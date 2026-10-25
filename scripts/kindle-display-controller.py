#!/usr/bin/env python3
"""Keep the USB Kindle display connected and apply an explicit screen rotation.

Only the existing pinned SSH host key and the Kindle's existing empty root
password are used. Home restarts the viewer; the GUI Disconnect button stops the
session by creating runtime/kindle-display.stop. --once-status is read-only.
"""
from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import re
import select
import shutil
import socket
import subprocess
import sys
import threading
import time
import uuid

PROJECT = Path(__file__).resolve().parent.parent
RUNTIME = PROJECT / "runtime"
sys.path.insert(0, str(RUNTIME / "ssh-libs"))
HOST = "192.168.2.2"
FORWARD_HOST = "127.0.0.1"
FORWARD_PORT = 5905
SIZE_COMMAND = "cat /sys/class/graphics/fb0/virtual_size"
DRIVER_STATE_COMMAND = (
    "printf '%s %s %s\\n' "
    '"$(cat /sys/class/graphics/fb0/virtual_size)" '
    '"$(cat /sys/module/eink_fb_hal_broads/parameters/bs_orientation)" '
    '"$(cat /sys/module/eink_fb_hal_broads/parameters/bs_upside_down)"'
)
# DXG 2.5.8 factory /opt/eink/scripts/diags uses command 14 and these
# einkfb orientation enums. Readback uses the broadsheet driver's two flags;
# generic fb0/rotate does not identify all four hardware orientations.
ROTATIONS = {0: (0, (824, 1200)), 90: (2, (1200, 824)),
             180: (1, (824, 1200)), 270: (3, (1200, 824))}
START_COMMAND = "sh /mnt/us/kindlevncviewer/kindle-display.sh 127.0.0.1:5"
# Check the PID's command line before signalling: never kill an unrelated PID.
STOP_COMMAND = r'''pidfile=/tmp/kindle-display.pid
[ -f "$pidfile" ] || exit 0
pid=$(cat "$pidfile")
case "$pid" in ''|*[!0-9]*) echo 'Invalid viewer pidfile' >&2; exit 2;; esac
kill -0 "$pid" 2>/dev/null || exit 0
cmd=$(tr '\000' ' ' < "/proc/$pid/cmdline")
case "$cmd" in *'/mnt/us/kindlevncviewer/kindle-display.sh'*) ;; *) echo 'Viewer PID owner mismatch' >&2; exit 3;; esac
kill -TERM "$pid" || exit 4
n=0
while kill -0 "$pid" 2>/dev/null; do
    n=$((n+1))
    [ "$n" -lt 6 ] || { echo 'Viewer did not stop' >&2; exit 5; }
    sleep 1
done
'''
ALLOWED_SIZES = {(824, 1200), (1200, 824)}
LOG = logging.getLogger("kindle-display")


def powershell_path():
    config_path = PROJECT / "kindle-display.local.json"
    config = json.loads(config_path.read_text(encoding="utf-8-sig")) if config_path.is_file() else {}
    if not isinstance(config, dict):
        raise ValueError("kindle-display.local.json must contain a JSON object")
    configured = config.get("PowerShellPath")
    if configured is not None and not isinstance(configured, str):
        raise ValueError("PowerShellPath must be a string")
    if configured and configured.strip():
        candidate = Path(os.path.expandvars(configured))
        if not candidate.is_absolute():
            candidate = PROJECT / candidate
        if not candidate.is_file():
            raise RuntimeError("Configured PowerShellPath does not exist: " + str(candidate))
        return str(candidate.resolve())
    candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "PowerShell/7/pwsh.exe"
    if candidate.is_file():
        return str(candidate)
    discovered = shutil.which("pwsh")
    if discovered:
        return discovered
    raise RuntimeError("PowerShell 7 was not found. Install it or set PowerShellPath in kindle-display.local.json.")


class Stopping(Exception):
    pass


class OrientationRequestError(ValueError):
    def __init__(self, message, request_id=None):
        super().__init__(message)
        self.request_id = request_id


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def process_start_ticks():
    """Windows process creation time in .NET UTC ticks, for PID reuse checks."""
    if os.name != "nt":
        return None
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.GetProcessTimes.restype = wintypes.BOOL
    times = [wintypes.FILETIME() for _ in range(4)]
    if not kernel.GetProcessTimes(kernel.GetCurrentProcess(), *(ctypes.byref(t) for t in times)):
        raise ctypes.WinError(ctypes.get_last_error())
    creation = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
    return str(creation + 504911232000000000)


def parse_size(output):
    match = re.fullmatch(r"\s*(\d{1,4}),(\d{1,4})\s*", output)
    if not match:
        raise ValueError("Unexpected Kindle framebuffer size: " + repr(output[:100]))
    size = tuple(map(int, match.groups()))
    if size not in ALLOWED_SIZES:
        raise ValueError("Unsupported Kindle framebuffer size: " + repr(size))
    return size


def parse_driver_state(output):
    match = re.fullmatch(r"\s*(\d{1,4},\d{1,4}) ([01]) ([01])\s*", output)
    if not match:
        raise ValueError("Unexpected Kindle driver state: " + repr(output[:100]))
    size = parse_size(match.group(1))
    landscape, inverted = map(int, match.groups()[1:])
    rotation = {(0, 0): 0, (1, 0): 90, (0, 1): 180, (1, 1): 270}[landscape, inverted]
    if size != ROTATIONS[rotation][1]:
        raise ValueError("Kindle driver orientation and framebuffer size disagree")
    return rotation, size


def parse_orientation_request(text):
    request_id = None
    try:
        request = json.loads(text)
        if not isinstance(request, dict):
            raise ValueError("Request must be a JSON object")
        request_id = request.get("request_id")
        if not isinstance(request_id, str) or len(request_id) != 36:
            raise ValueError("Request ID must be a UUID")
        uuid.UUID(request_id)
        rotation = request.get("rotation")
        if type(rotation) is not int or rotation not in ROTATIONS:
            raise ValueError("Rotation must be 0, 90, 180, or 270")
        requested_at = request.get("requested_at")
        timestamp = dt.datetime.fromisoformat(requested_at.replace("Z", "+00:00"))
        if timestamp.tzinfo is None:
            raise ValueError("Request timestamp must include its time zone")
        return dict(rotation=rotation, requested_at=requested_at, request_id=request_id)
    except (ValueError, TypeError, AttributeError) as exc:
        raise OrientationRequestError("Invalid orientation request: " + str(exc), request_id) from exc


def open_ssh():
    import paramiko
    client = paramiko.SSHClient()
    try:
        client.load_host_keys(str(RUNTIME / "kindle_known_hosts"))
        client.set_missing_host_key_policy(paramiko.RejectPolicy())
        client.connect(HOST, port=22, username="root", password="",
                       look_for_keys=False, allow_agent=False, timeout=5,
                       banner_timeout=8, auth_timeout=8, channel_timeout=8)
        transport = client.get_transport()
        transport.set_keepalive(10)
        # Detect a physically missing USB link without executing remote polls.
        # Windows sends TCP keepalive probes after 10 s, then every 3 s.
        transport.sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
        if hasattr(socket, "SIO_KEEPALIVE_VALS"):
            transport.sock.ioctl(socket.SIO_KEEPALIVE_VALS, (1, 10000, 3000))
        return client
    except BaseException:
        client.close()
        raise


def exec_request(channel, text, timeout=5, stop_check=None):
    """Bound exec acknowledgement: Channel.settimeout only covers data I/O."""
    done, errors = threading.Event(), []
    def request():
        try:
            channel.exec_command(text)
        except Exception as exc:
            errors.append(exc)
        finally:
            done.set()
    threading.Thread(target=request, daemon=True, name="kindle-exec-request").start()
    deadline = time.monotonic() + timeout
    while not done.wait(0.1):
        if stop_check and stop_check():
            channel.get_transport().close()
            raise Stopping()
        if time.monotonic() >= deadline:
            # Closing our dedicated transport wakes Paramiko's unbounded
            # channel event wait and lets the existing reconnect loop recover.
            channel.get_transport().close()
            raise TimeoutError("SSH exec request timed out")
    if errors:
        raise errors[0]


def command(client, text, timeout=8, stop_check=None, line_validator=None):
    channel = client.get_transport().open_session(timeout=5)
    channel.settimeout(5)
    output, errors = bytearray(), bytearray()
    deadline = time.monotonic() + timeout
    try:
        exec_request(channel, text, timeout=min(5, timeout), stop_check=stop_check)
        while True:
            if stop_check and stop_check():
                raise Stopping()
            for ready, receive, target in ((channel.recv_ready, channel.recv, output),
                                           (channel.recv_stderr_ready, channel.recv_stderr, errors)):
                while ready():
                    data = receive(4096)
                    if not data:
                        break
                    target.extend(data)
                    if len(output) + len(errors) > 16384:
                        raise RuntimeError("SSH command output exceeded 16 KiB")
            if line_validator is not None and b"\n" in output:
                line = output.split(b"\n", 1)[0].decode("utf-8", "replace")
                line_validator(line)
                return line
            if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready():
                status = channel.recv_exit_status()
                if status != 0:
                    raise RuntimeError("Remote command failed (%s): %s" %
                                       (status, errors.decode("utf-8", "replace")[:1000]))
                return output.decode("utf-8", "replace")
            if time.monotonic() >= deadline:
                raise TimeoutError("Remote command timed out: stdout=%r stderr=%r eof=%s closed=%s exit_status=%s" %
                                   (bytes(output[:160]), bytes(errors[:160]), channel.eof_received,
                                    channel.closed, channel.exit_status_ready()))
            time.sleep(0.05)
    finally:
        channel.close()


def read_size(client, stop_check=None):
    # Old Dropbear can delay short-channel exit status while forwarding RFB.
    # A validated complete sysfs line is sufficient for this read-only query.
    return parse_size(command(client, SIZE_COMMAND, stop_check=stop_check, line_validator=parse_size))


def read_driver_state(client, stop_check=None):
    return parse_driver_state(command(client, DRIVER_STATE_COMMAND, stop_check=stop_check,
                                      line_validator=parse_driver_state))


def require_rotation_ack(line):
    if line != "rotation-written":
        raise ValueError("Kindle did not acknowledge the orientation command")


class ReverseForward:
    """Bounded, fixed loopback-to-loopback forwarding; no general proxy."""
    def __init__(self, transport):
        self.transport = transport
        self.stop = threading.Event()
        self.slots = threading.BoundedSemaphore(4)
        self.lock = threading.Lock()
        self.channels = set()

    def start(self, stop_check=None):
        # Paramiko global requests have no timeout. Bound the wait so a USB
        # removal at precisely this point cannot strand the controller.
        done, errors = threading.Event(), []
        def request():
            try:
                self.transport.request_port_forward(FORWARD_HOST, FORWARD_PORT, handler=self.accept)
            except Exception as exc:
                errors.append(exc)
            finally:
                done.set()
        threading.Thread(target=request, daemon=True, name="kindle-forward-request").start()
        deadline = time.monotonic() + 8
        while not done.wait(0.1):
            if stop_check and stop_check():
                self.transport.close()
                raise Stopping()
            if time.monotonic() >= deadline:
                self.transport.close()
                raise TimeoutError("SSH reverse forwarding request timed out")
        if errors:
            raise errors[0]

    def accept(self, channel, origin, server):
        if (self.stop.is_set() or tuple(server) != (FORWARD_HOST, FORWARD_PORT)
                or origin[0] != FORWARD_HOST or not self.slots.acquire(blocking=False)):
            channel.close()
            return
        with self.lock:
            self.channels.add(channel)
        threading.Thread(target=self.relay, args=(channel,), daemon=True,
                         name="kindle-rfb-forward").start()

    def relay(self, channel):
        peer = None
        try:
            peer = socket.create_connection((FORWARD_HOST, FORWARD_PORT), timeout=3)
            peer.settimeout(3)
            channel.settimeout(3)
            while not self.stop.is_set() and not channel.closed:
                readable, _, _ = select.select([peer, channel], [], [], 0.5)
                for source in readable:
                    data = source.recv(16384)
                    if not data:
                        return
                    (channel if source is peer else peer).sendall(data)
        except (OSError, EOFError) as exc:
            if not self.stop.is_set():
                LOG.info("RFB connection ended: %s", exc)
        finally:
            channel.close()
            if peer is not None:
                peer.close()
            with self.lock:
                self.channels.discard(channel)
            self.slots.release()

    def close(self):
        self.stop.set()
        with self.lock:
            for channel in self.channels:
                channel.close()
        # The controller immediately closes this dedicated SSH transport,
        # which removes its listener. Avoid an unbounded cancel global request
        # when the USB cable has already been unplugged.


class Viewer:
    def __init__(self, client, stop_check=None):
        self.channel = client.get_transport().open_session(timeout=5)
        self.channel.settimeout(5)
        self.done = threading.Event()
        self.closed = threading.Event()
        self.exit_code = None
        try:
            exec_request(self.channel, START_COMMAND, stop_check=stop_check)
        except BaseException:
            self.channel.close()
            raise
        threading.Thread(target=self.drain, daemon=True, name="kindle-viewer-log").start()

    def drain(self):
        try:
            while not self.closed.is_set():
                for ready, receive in ((self.channel.recv_ready, self.channel.recv),
                                       (self.channel.recv_stderr_ready, self.channel.recv_stderr)):
                    while ready():
                        chunk = receive(4096)
                        if not chunk:
                            break
                        LOG.info("viewer: %s", chunk.decode("utf-8", "replace").rstrip())
                if self.channel.exit_status_ready() or self.channel.closed:
                    self.exit_code = self.channel.recv_exit_status()
                    return
                self.closed.wait(0.1)
        except Exception as exc:
            LOG.info("Viewer channel ended: %s", exc)
        finally:
            self.done.set()

    def close(self):
        self.closed.set()
        self.channel.close()


class Controller:
    def __init__(self):
        self.stop_path = RUNTIME / "kindle-display.stop"
        self.orientation_path = RUNTIME / "kindle-display.orientation"
        self.refresh_path = RUNTIME / "kindle-display.refresh"
        self.settings_path = RUNTIME / "kindle-display-settings.json"
        self.status_path = RUNTIME / "kindle-display-controller.json"
        self.stop_event = threading.Event()
        self.state = dict(pid=os.getpid(), starttime=utc_now(), starttimeUtcTicks=process_start_ticks(),
                          connection="connecting", width=None, height=None, reconnects=0,
                          viewer_restarts=0, last_error=None, stop_method="KindleDisplay.exe Disconnect",
                          orientation_mode="manual", orientation_checked=None, refresh_completed=None,
                          rotation=None, orientation_applied=None, orientation_request_id=None)
        self.client = self.forward = self.viewer = None
        self.original_rotation = None
        self.desired_rotation = 0
        try:
            saved = json.loads(self.settings_path.read_text(encoding="utf-8-sig"))
            if type(saved.get("rotation")) is int and saved["rotation"] in ROTATIONS:
                self.desired_rotation = saved["rotation"]
        except (OSError, ValueError, AttributeError):
            pass

    def stopping(self):
        return self.stop_event.is_set() or self.stop_path.exists()

    def wait(self, seconds):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            if self.stopping():
                raise Stopping()
            self.stop_event.wait(min(0.2, max(0, until - time.monotonic())))

    def status(self, **updates):
        self.state.update(updates)
        self.state["updated"] = utc_now()
        temp = self.status_path.with_name(self.status_path.name + ".%s.tmp" % os.getpid())
        temp.write_text(json.dumps(self.state, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(temp, self.status_path)

    def stop_viewer(self):
        command(self.client, STOP_COMMAND, timeout=9)
        if self.viewer:
            self.viewer.close()
            self.viewer = None

    def take_orientation_request(self):
        # Claim by rename so a newer GUI request cannot be deleted after reading.
        claimed = self.orientation_path.with_name(self.orientation_path.name + ".%s.processing" % os.getpid())
        try:
            os.replace(self.orientation_path, claimed)
        except FileNotFoundError:
            return None
        try:
            if claimed.stat().st_size > 4096:
                raise OrientationRequestError("Orientation request exceeds 4 KiB")
            return parse_orientation_request(claimed.read_text(encoding="utf-8-sig"))
        finally:
            claimed.unlink(missing_ok=True)

    def save_rotation(self, rotation):
        temp = self.settings_path.with_name(self.settings_path.name + ".%s.tmp" % os.getpid())
        temp.write_text(json.dumps(dict(rotation=rotation, updated=utc_now())), encoding="utf-8")
        os.replace(temp, self.settings_path)

    def set_driver_rotation(self, rotation, allow_stopping=False):
        mode, expected = ROTATIONS[rotation]
        stop_check = None if allow_stopping else self.stopping
        text = "printf '14 %d\\n' > /proc/eink_fb/update_display && printf 'rotation-written\\n'" % mode
        command(self.client, text, stop_check=stop_check, line_validator=require_rotation_ack)
        deadline = time.monotonic() + 5
        while True:
            actual_rotation, actual_size = read_driver_state(self.client, stop_check=stop_check)
            if actual_rotation == rotation and actual_size == expected:
                return actual_size
            if time.monotonic() >= deadline:
                raise RuntimeError("Kindle orientation readback did not match %s degrees" % rotation)
            if allow_stopping:
                time.sleep(0.25)
            else:
                self.wait(0.25)

    def configure_rotation(self, rotation):
        self.stop_viewer()
        if self.stopping():
            raise Stopping()
        size = self.set_driver_rotation(rotation)
        self.resize(size)
        if self.stopping():
            raise Stopping()
        self.viewer = Viewer(self.client, stop_check=self.stopping)
        self.wait(0.5)
        if self.viewer.done.is_set():
            raise RuntimeError("Kindle viewer exited while applying orientation")
        return size

    def apply_rotation(self, rotation, request=None, previous=None):
        request_id = request["request_id"] if request else None
        old_rotation, old_size = previous if previous is not None else read_driver_state(self.client, self.stopping)
        self.status(connection="resizing", orientation_request_id=request_id,
                    orientation_applied=None, last_error=None)
        try:
            size = self.configure_rotation(rotation)
            self.save_rotation(rotation)
            self.desired_rotation = rotation
            self.status(connection="connected", rotation=rotation, width=size[0], height=size[1],
                        orientation_applied=utc_now(), orientation_checked=utc_now(),
                        orientation_request_id=request_id, last_error=None,
                        viewer_restarts=self.state["viewer_restarts"] + 1)
            LOG.info("Applied orientation %s degrees; request=%s", rotation, request_id)
            return size
        except Stopping:
            raise
        except Exception as exc:
            error = "Orientation %s failed: %s" % (rotation, exc)
            LOG.warning(error, exc_info=True)
            try:
                self.configure_rotation(old_rotation)
                self.desired_rotation = old_rotation
                self.status(connection="connected", rotation=old_rotation,
                            width=old_size[0], height=old_size[1])
                LOG.info("Restored prior orientation %s degrees", old_rotation)
            except Stopping:
                raise
            except Exception as recovery_exc:
                error += "; restoring the previous orientation failed: " + str(recovery_exc)
                self.status(connection="error", rotation=None)
                LOG.error(error)
            self.status(orientation_request_id=request_id, orientation_applied=None, last_error=error[:2000])
            return old_size

    def resize(self, size):
        pwsh = powershell_path()
        args = [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", str(PROJECT / "scripts/kindle-vnc-server.ps1"),
                "-Action", "Resize", "-Width", str(size[0]), "-Height", str(size[1])]
        LOG.info("Synchronizing Windows display to %sx%s", *size)
        proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        deadline = time.monotonic() + 90
        try:
            while True:
                try:
                    output, _ = proc.communicate(timeout=0.2)
                    break
                except subprocess.TimeoutExpired:
                    if self.stopping():
                        raise Stopping()
                    if time.monotonic() > deadline:
                        raise TimeoutError("Windows display resize exceeded 90 seconds")
            LOG.info("Windows resize result: %s", output.decode("utf-8", "replace")[-8192:].strip())
            if proc.returncode:
                raise RuntimeError("Windows display resize failed (exit %s)" % proc.returncode)
        finally:
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.communicate(timeout=3)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.communicate()

    def restart_viewer(self, size, resize=True):
        # The old client caches geometry: it must exit BEFORE host resizing.
        self.status(connection="resizing" if resize else "refreshing")
        self.stop_viewer()
        if self.stopping():
            raise Stopping()
        if resize:
            self.resize(size)
        if self.stopping():
            raise Stopping()
        self.viewer = Viewer(self.client, stop_check=self.stopping)
        self.status(connection="connected", width=size[0], height=size[1], last_error=None,
                    viewer_restarts=self.state["viewer_restarts"] + 1)

    def handle_requests(self, size):
        restarted = False
        if self.orientation_path.exists():
            request = None
            try:
                request = self.take_orientation_request()
                if request is not None:
                    size = self.apply_rotation(request["rotation"], request)
                    restarted = True
            except Stopping:
                raise
            except Exception as exc:
                request_id = request["request_id"] if request else getattr(exc, "request_id", None)
                LOG.warning("Requested orientation failed: %s", exc)
                self.status(orientation_request_id=request_id, orientation_applied=None,
                            last_error="Orientation request failed: " + str(exc)[:1000])
        if self.refresh_path.exists():
            self.refresh_path.unlink(missing_ok=True)
            if not restarted:
                # Reload the old viewer to request a complete RFB frame. Its
                # release has no external full-refresh API; VDD need not resize.
                self.restart_viewer(size, resize=False)
            self.status(refresh_completed=utc_now(), last_error=None)
            LOG.info("Requested display reload completed")
        return size

    def connected(self):
        self.client = open_ssh()
        previous = read_driver_state(self.client, stop_check=self.stopping)
        if self.original_rotation is None:
            self.original_rotation = previous[0]
            self.status(original_rotation=self.original_rotation)
        self.forward = ReverseForward(self.client.get_transport())
        self.forward.start(stop_check=self.stopping)
        try:
            request = self.take_orientation_request()
        except OrientationRequestError as exc:
            self.status(orientation_request_id=exc.request_id, orientation_applied=None, last_error=str(exc))
            request = None
        rotation = request["rotation"] if request else self.desired_rotation
        size = self.apply_rotation(rotation, request, previous=previous)
        while True:
            self.wait(1.0)
            if not self.client.get_transport().is_active():
                raise ConnectionError("USB SSH connection lost")
            size = self.handle_requests(size)
            if self.state["connection"] != "error" and self.viewer is not None and self.viewer.done.is_set():
                LOG.info("Viewer exited (%s); reconnecting automatically", self.viewer.exit_code)
                self.wait(5.0)
                size = self.handle_requests(size)
                if self.viewer is None or not self.viewer.done.is_set():
                    continue
                self.restart_viewer(size, resize=False)

    def disconnect(self, restore_original=False):
        if self.client:
            try:
                if self.client.get_transport() and self.client.get_transport().is_active():
                    self.stop_viewer()
                    if restore_original and self.original_rotation is not None:
                        self.set_driver_rotation(self.original_rotation, allow_stopping=True)
                        command(self.client, "if [ -w /proc/keypad ]; then echo 'send 139' > /proc/keypad; "
                                "echo 'send 139' > /proc/keypad; fi")
                        self.status(original_orientation_restored=True)
            except Exception as exc:
                LOG.info("Viewer cleanup: %s", exc)
        if self.viewer:
            self.viewer.close()
            self.viewer = None
        if self.forward:
            self.forward.close()
            self.forward = None
        if self.client:
            self.client.close()
            self.client = None

    def run(self):
        self.status()
        backoff = 1.0
        try:
            while not self.stopping():
                try:
                    self.status(connection="connecting")
                    self.connected()
                except Stopping:
                    break
                except KeyboardInterrupt:
                    self.stop_event.set()
                    break
                except Exception as exc:
                    LOG.warning("Connection cycle failed: %s", exc, exc_info=True)
                    self.status(connection="reconnecting", last_error=str(exc)[:1000],
                                reconnects=self.state["reconnects"] + 1)
                finally:
                    self.disconnect(restore_original=self.stopping())
                self.wait(backoff)
                backoff = min(10.0, backoff * 2)
        except (Stopping, KeyboardInterrupt):
            pass
        finally:
            self.status(connection="stopping")
            self.disconnect(restore_original=True)
            self.status(connection="stopped")
            LOG.info("Controller stopped")
        return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--once-status", action="store_true", help="Read Kindle geometry over pinned SSH; make no changes")
    args = parser.parse_args()
    if args.once_status:
        client = open_ssh()
        try:
            rotation, (width, height) = read_driver_state(client)
            print(json.dumps(dict(host=HOST, connection="connected", width=width, height=height, rotation=rotation)))
        finally:
            client.close()
        return 0
    RUNTIME.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(RUNTIME / "kindle-display-controller.log", maxBytes=2_000_000,
                                  backupCount=2, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    LOG.addHandler(handler)
    LOG.setLevel(logging.INFO)
    return Controller().run()


if __name__ == "__main__":
    raise SystemExit(main())
