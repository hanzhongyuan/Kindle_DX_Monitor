#!/usr/bin/env python3
"""Use an owned, reversible bind mount for a DXG 2.5.8 sleep message board.

No firmware files, startup scripts, passwords, or original screensavers change.
The mount survives sleep/disconnection, but a full Kindle reboot removes it.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import time
import uuid

PROJECT = Path(__file__).resolve().parent.parent
RUNTIME = PROJECT / "runtime"
STATE = RUNTIME / "messageboard-state.json"
BASE = "/mnt/us/kindle-messageboard"
SOURCE = BASE + "/screensavers"
TARGET = "/opt/amazon/screen_saver/824x1200"
MARKER = ".kindle-messageboard-owner"
IMAGE = SOURCE + "/messageboard.png"
UPLOAD = BASE + "/upload.png"
PREVIOUS = BASE + "/previous.png"
MAX_IMAGE_BYTES = 5 * 1024 * 1024

_spec = importlib.util.spec_from_file_location(
    "messageboard_display", Path(__file__).with_name("kindle-display-controller.py"))
display = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(display)


def prepare_image(path):
    from PIL import Image
    path = Path(path)
    if not path.is_file() or path.stat().st_size > MAX_IMAGE_BYTES:
        raise ValueError("留言图片必须是小于 5 MiB 的 PNG 文件。")
    with Image.open(path) as picture:
        if picture.format != "PNG" or picture.size != (824, 1200):
            raise ValueError("留言图片必须是 824×1200 的 PNG。")
        if getattr(picture, "n_frames", 1) != 1:
            raise ValueError("留言图片必须是静态图片。")
        picture.load()
        rgba = picture.convert("RGBA")
        if rgba.getchannel("A").getextrema() != (255, 255):
            raise ValueError("留言图片必须使用不透明的白色背景。")
        result = io.BytesIO()
        rgba.convert("L").save(result, format="PNG", optimize=True)
    return result.getvalue()


def load_state():
    if not STATE.exists():
        return {}
    value = json.loads(STATE.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("留言板本地状态文件损坏。")
    return value


def save_state(state):
    RUNTIME.mkdir(parents=True, exist_ok=True)
    temporary = STATE.with_suffix(".tmp")
    temporary.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, STATE)


def owner_token(state):
    token = state.get("owner_token")
    if token is not None and (not isinstance(token, str) or not re.fullmatch(r"[0-9a-f]{32}", token)):
        raise ValueError("留言板所有权记录损坏，已停止以保护现有封面。")
    return token


def mounts_at(text, target=TARGET):
    mounts = []
    for line in text.splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[1] == target:
            mounts.append(parts)
    return mounts


def framework_ready(text):
    # DXG /proc/bd renders flags as eight hexadecimal digits, not decimal "0"/"1".
    values = text.split()
    return (len(values) == 3 and
            all(re.fullmatch(r"[0-9a-fA-F]{1,8}", value) for value in values) and
            tuple(int(value, 16) for value in values) == (0, 0, 1))


class MessageBoard:
    def __init__(self, client, state=None):
        self.client = client
        self.state = load_state() if state is None else state
        self.token = owner_token(self.state)

    def run(self, script, timeout=12):
        return display.command(self.client, script, timeout=timeout)

    def read_marker(self, path):
        # All paths used here are constants, never user input.
        return self.run("if [ -f " + path + "/" + MARKER + " ]; then cat " +
                        path + "/" + MARKER + "; fi").strip()

    def inspect(self):
        firmware = self.run("cat /etc/prettyversion.txt").strip()
        model = self.run("dd if=/proc/usid bs=1 count=4 2>/dev/null").strip()
        if model != "B009" or not firmware.startswith("Kindle 2.5.8"):
            raise RuntimeError("此留言板仅验证了 Kindle DX Graphite（B009）2.5.8，已停止操作。")
        if self.run("if [ -e /mnt/us/linkss ]; then echo present; fi").strip():
            raise RuntimeError("检测到其他休眠封面组件 linkss，已停止，避免覆盖其配置。")
        entries = mounts_at(self.run("cat /proc/mounts"))
        if len(entries) > 1:
            raise RuntimeError("休眠图片目录存在多层挂载，已停止操作。")
        mounted = bool(entries)
        if mounted:
            if not self.token or self.read_marker(SOURCE) != self.token or self.read_marker(TARGET) != self.token:
                raise RuntimeError("休眠图片目录被其他组件使用，不能替换或卸载。")
        mode = self.run("lipc-get-prop com.lab126.powerd state").strip()
        return {"model": model, "firmware": firmware, "mounted": mounted,
                "power_state": mode, "requires_resend_after_reboot": True}

    def ensure_viewer_stopped(self):
        active = self.run(r'''if [ -f /tmp/kindle-display.pid ]; then
pid=$(cat /tmp/kindle-display.pid)
case "$pid" in ''|*[!0-9]*) echo invalid;; *)
if kill -0 "$pid" 2>/dev/null; then echo active; fi;; esac
fi''').strip()
        if active:
            raise RuntimeError("请先在显示器窗口点击断开连接，再发送留言。")
        if self.run("lipc-get-prop com.lab126.powerd preventScreenSaver").strip() != "0":
            raise RuntimeError("Kindle 的休眠被其他程序阻止，请先退出该程序。")

    def ensure_owned_directory(self):
        exists = self.run("if [ -e " + BASE + " ]; then echo present; fi").strip()
        if exists:
            if not self.token or self.read_marker(BASE) != self.token or self.read_marker(SOURCE) != self.token:
                raise RuntimeError("Kindle 留言板目录与本机记录不匹配，已停止以保护现有文件。")
            if self.run("if [ -L " + BASE + " ] || [ -L " + SOURCE + " ]; then echo symlink; fi").strip():
                raise RuntimeError("留言板目录不能是符号链接。")
            return
        if not self.token:
            self.token = uuid.uuid4().hex
            self.state["owner_token"] = self.token
            save_state(self.state)
        self.run("mkdir " + BASE + " && mkdir " + SOURCE +
                 " && printf '%s\\n' '" + self.token + "' > " + BASE + "/" + MARKER +
                 " && printf '%s\\n' '" + self.token + "' > " + SOURCE + "/" + MARKER)

    def upload(self, payload):
        # A fixed filename is fed through stdin; text/image contents never enter a shell command.
        channel = self.client.get_transport().open_session(timeout=5)
        channel.settimeout(5)
        deadline = time.monotonic() + 30
        try:
            display.exec_request(channel, "cat > " + UPLOAD, timeout=5)
            offset = 0
            while offset < len(payload):
                if time.monotonic() >= deadline:
                    raise TimeoutError("上传留言图片超时。")
                count = channel.send(payload[offset:offset + 16384])
                if count <= 0:
                    raise RuntimeError("上传留言图片时连接已断开。")
                offset += count
            channel.shutdown_write()
            errors = bytearray()
            while not channel.exit_status_ready():
                while channel.recv_ready():
                    channel.recv(4096)
                while channel.recv_stderr_ready():
                    errors.extend(channel.recv_stderr(4096))
                if len(errors) > 4096 or time.monotonic() >= deadline:
                    raise TimeoutError("留言图片上传未完成。")
                time.sleep(0.05)
            if channel.recv_exit_status() != 0:
                raise RuntimeError("留言图片上传失败：" + errors.decode("utf-8", "replace")[:500])
        finally:
            channel.close()
        expected = hashlib.md5(payload).hexdigest()
        actual = self.run("/usr/bin/md5sum " + UPLOAD).split()[0]
        if actual != expected:
            raise RuntimeError("留言图片传输校验失败，原封面未改变。")
        return expected

    def wait_for(self, script, expected, seconds, label):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            actual = self.run(script).strip()
            if expected(actual) if callable(expected) else actual == expected:
                return
            time.sleep(0.5)
        raise TimeoutError(label)

    def stop_framework(self):
        self.run("/etc/init.d/framework stop >/tmp/kindle-messageboard-framework.log 2>&1 </dev/null", timeout=20)
        self.wait_for("pidof cvm || true", "", 25, "Kindle 阅读界面尚未停止。")

    def start_framework(self):
        self.run("/etc/init.d/framework start >/tmp/kindle-messageboard-framework.log 2>&1 </dev/null", timeout=20)
        flags = "printf '%s %s %s' \"$(cat /proc/bd/framework_started)\" \"$(cat /proc/bd/framework_stopped)\" \"$(cat /proc/bd/framework_running)\""
        self.wait_for(flags, framework_ready, 75, "Kindle 阅读界面启动超时，请检查设备。")

    def sleep(self):
        # On DXG 2.5.8, wakeUp can fail with 0x100 when already active.
        if self.run("lipc-get-prop com.lab126.powerd state").strip() != "active":
            self.run("lipc-set-prop -i com.lab126.powerd wakeUp 1")
            self.wait_for("lipc-get-prop com.lab126.powerd state", "active", 12, "Kindle 尚未唤醒。")
        self.run("powerd_test -p")
        self.wait_for("lipc-get-prop com.lab126.powerd state", "screenSaver", 15, "Kindle 尚未进入休眠封面。")

    def rollback(self, previously_mounted, previous_image):
        errors = []
        try:
            self.stop_framework()
            current = self.inspect()["mounted"]
            if current and not previously_mounted:
                self.run("umount " + TARGET)
            if previous_image:
                self.run("cp " + PREVIOUS + " " + IMAGE)
            if previously_mounted and not current:
                self.run("mount --bind " + SOURCE + " " + TARGET)
        except Exception as exc:
            errors.append(str(exc))
        finally:
            try:
                self.start_framework()
            except Exception as exc:
                errors.append(str(exc))
        return errors

    def install(self, payload):
        before = self.inspect()
        self.ensure_viewer_stopped()
        self.ensure_owned_directory()
        digest = self.upload(payload)
        previous_image = self.run("if [ -f " + IMAGE + " ]; then echo present; fi").strip() == "present"
        if previous_image:
            self.run("cp " + IMAGE + " " + PREVIOUS)
        # Stop before replacing any image visible to the framework's image cache.
        try:
            self.stop_framework()
            self.run("mv " + UPLOAD + " " + IMAGE)
            if not before["mounted"]:
                self.run("mount --bind " + SOURCE + " " + TARGET)
            if not self.inspect()["mounted"]:
                raise RuntimeError("未能挂载留言图片。")
            self.start_framework()
            self.sleep()
        except Exception as exc:
            recovery = self.rollback(before["mounted"], previous_image)
            if recovery:
                raise RuntimeError(str(exc) + "；恢复检查：" + "; ".join(recovery)) from exc
            raise
        self.state.update(enabled=True, image_md5=digest, requires_resend_after_reboot=True,
                          updated=display.utc_now())
        save_state(self.state)
        return self.inspect()

    def restore(self):
        before = self.inspect()
        self.ensure_viewer_stopped()
        if before["mounted"]:
            try:
                self.stop_framework()
                # Reconfirm ownership immediately before unmounting.
                if not self.inspect()["mounted"]:
                    raise RuntimeError("留言板挂载在恢复过程中发生变化。")
                self.run("umount " + TARGET)
                self.start_framework()
                self.sleep()
            except Exception as exc:
                recovery = self.rollback(True, False)
                if recovery:
                    raise RuntimeError(str(exc) + "；恢复检查：" + "; ".join(recovery)) from exc
                raise
        self.state.update(enabled=False, updated=display.utc_now())
        save_state(self.state)
        return self.inspect()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--action", choices=("install", "restore", "status"), required=True)
    parser.add_argument("--image")
    args = parser.parse_args()
    if args.action == "install" and not args.image:
        parser.error("--action install requires --image")
    if args.action != "install" and args.image:
        parser.error("--image is only used with --action install")
    client = None
    try:
        payload = prepare_image(args.image) if args.action == "install" else None
        client = display.open_ssh()
        board = MessageBoard(client)
        if args.action == "status":
            print(json.dumps(board.inspect(), ensure_ascii=False))
        elif args.action == "install":
            board.install(payload)
            print("留言板已设为休眠封面，可拔掉 USB 保留显示。Kindle 完整重启后需要重新发送一次。")
        else:
            board.restore()
            print("已恢复原来的 Kindle 休眠封面。")
        return 0
    except Exception as exc:
        print("留言板操作失败：" + str(exc), file=sys.stderr)
        return 1
    finally:
        if client is not None:
            client.close()


if __name__ == "__main__":
    raise SystemExit(main())
