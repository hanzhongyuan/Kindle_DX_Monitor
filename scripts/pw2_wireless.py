"""Pinned-key SSH/SFTP transport for the PW2 message board.

Only /mnt/us/linkss is writable. This module never wakes, sleeps, mounts,
exports, reconfigures, or installs software on the Kindle.
"""
from __future__ import annotations

import errno
import hashlib
import ipaddress
import json
from pathlib import Path, PurePosixPath
import re
import socket
import stat
import threading
import time
import uuid


MAX_READ = 64 * 1024 * 1024
ROOT = PurePosixPath("/mnt/us")
PRIVATE_NETWORKS = tuple(ipaddress.IPv4Network(value) for value in
                         ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))
# A USB export removes the local vfat mount. On stock PW2 the user-facing
# directory is usually a FUSE overlay over /mnt/base-us, not vfat itself.
STORAGE_GUARD = r"""
awk 'function rw(opts) { return ("," opts ",") ~ /,rw,/ }
     $2 == "/mnt/us" && $3 == "vfat" && rw($4) { direct=1 }
     $2 == "/mnt/us" && ($3 == "fuse.fsp" || $3 == "fuse") && rw($4) { overlay=1 }
     $2 == "/mnt/base-us" && $3 == "vfat" && rw($4) { base=1 }
     END { exit !(direct || (overlay && base)) }' /proc/mounts || exit 31
connected=unknown
connected_file=$(kdb get system/driver/usb/SYS_CONNECTED 2>/dev/null)
if [ -n "$connected_file" ] && [ -r "$connected_file" ]; then
    connected=$(cat "$connected_file" 2>/dev/null)
fi
for item in /sys/devices/platform/*/gadget/*lun*/file \
            /sys/devices/platform/*/*/gadget/*lun*/file \
            /sys/kernel/config/usb_gadget/*/functions/mass_storage.*/lun.*/file; do
    [ -f "$item" ] || continue
    backing=$(cat "$item") || exit 33
    [ -z "$backing" ] && continue
    online=$(cat "${item%/file}/online" 2>/dev/null)
    case "$online" in 1) exit 32 ;; 0) continue ;; esac
    [ "$connected" = 0 ] || exit 32
done
"""
REFRESH = r"""
cd /mnt/us/extensions/linkss || exit 41
# Let the official stop/start finish even if SSH closes midway through it.
trap '' HUP TERM
/bin/sh ./bin/linkss.sh framework_restart </dev/null >/dev/null 2>&1 || exit 42
sync
status framework | grep -q '^framework start/running' || exit 43
"""


def local_file(path, maximum):
    path = Path(path)
    for item in reversed((path,) + tuple(path.parents)):
        try:
            info = item.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise RuntimeError("无线配置路径包含链接或重解析点，已停止。")
    if not path.is_file() or path.stat().st_size > maximum:
        raise RuntimeError("无线配置或配对密钥文件缺失、类型异常或过大。")
    return path


def safe_component(name):
    # The same files must remain accessible through Windows USB storage.
    if (not isinstance(name, str) or not name or name in {".", ".."}
            or re.search(r'[<>:"/\\|?*\x00-\x1f]', name)
            or name.endswith((" ", "."))):
        raise RuntimeError("设备文件名不适合安全的 USB/无线共享访问，已停止。")
    return name


def explain_error(error):
    """Translate transport errors without exposing keys or opaque SSH details."""
    try:
        import paramiko
    except ImportError:
        return "无线连接组件缺失，请重新解压完整程序后重试。"
    if isinstance(error, paramiko.BadHostKeyException):
        return "Kindle 的主机密钥与配对记录不一致，已拒绝连接。请接 USB 重新配对，不要手动跳过密钥检查。"
    if isinstance(error, (paramiko.AuthenticationException, paramiko.PasswordRequiredException)):
        return "无线私钥认证失败，可能是配对密钥丢失或已更换。请接 USB 重新配对。"
    if isinstance(error, (TimeoutError, socket.timeout, paramiko.ssh_exception.NoValidConnectionsError)):
        return "无线连接超时或无法连接。请手动唤醒 Kindle，确认与电脑在同一 Wi-Fi，并检查程序中的 Kindle IP。"
    if isinstance(error, paramiko.SSHException):
        if "not found in known_hosts" in str(error):
            return "缺少这台 Kindle 的已配对主机密钥，已拒绝连接。请接 USB 重新配对。"
        return "无线安全连接未能建立或已断开。请唤醒 Kindle 并检查同一 Wi-Fi 和 IP；若仍失败，请接 USB 重新配对。"
    if isinstance(error, (EOFError, ConnectionError)):
        return "无线连接已断开。请唤醒 Kindle，确认同一 Wi-Fi 和 IP 后重试；若上次写入中断，请先恢复之前的封面。"
    if isinstance(error, OSError):
        return "无法完成无线连接或设备文件操作。请唤醒 Kindle 并检查同一 Wi-Fi 和 IP，保留备份后重试。"
    if isinstance(error, (json.JSONDecodeError, UnicodeDecodeError)):
        return "无线配对配置无法读取，请接 USB 重新配对。"
    if isinstance(error, ImportError):
        return "无线连接组件缺失，请重新解压完整程序后重试。"
    return str(error)


class RemotePath:
    """Small pathlib-compatible surface used by the existing backup journal."""

    def __init__(self, session, path):
        self.session = session
        self.path = PurePosixPath(path)
        if self.path != ROOT and ROOT not in self.path.parents:
            raise RuntimeError("远程路径超出 Kindle 存储目录。")
        for part in self.path.relative_to(ROOT).parts:
            safe_component(part)

    def __str__(self):
        return str(self.path)

    def __truediv__(self, relative):
        if not isinstance(relative, str) or relative.startswith("/"):
            raise RuntimeError("远程子路径不正确。")
        for part in relative.split("/"):
            safe_component(part)
        return RemotePath(self.session, self.path / relative)

    @property
    def name(self):
        return self.path.name

    @property
    def suffix(self):
        return self.path.suffix

    @property
    def parent(self):
        return RemotePath(self.session, self.path.parent)

    def with_name(self, name):
        return RemotePath(self.session, self.path.with_name(safe_component(name)))

    def _info(self):
        try:
            return self.session.sftp.lstat(str(self))
        except OSError as error:
            if error.errno == errno.ENOENT:
                return None
            raise

    def exists(self):
        return self._info() is not None

    def is_dir(self):
        info = self._info()
        return info is not None and stat.S_ISDIR(info.st_mode)

    def is_file(self):
        info = self._info()
        return info is not None and stat.S_ISREG(info.st_mode)

    def plain(self, *, directory=False, missing=False):
        for candidate in reversed((self.path,) + tuple(self.path.parents)):
            try:
                info = self.session.sftp.lstat(str(candidate))
            except OSError as error:
                if error.errno == errno.ENOENT:
                    continue
                raise
            if stat.S_ISLNK(info.st_mode):
                raise RuntimeError("设备路径包含符号链接，已停止：" + str(candidate))
            if candidate != self.path and not stat.S_ISDIR(info.st_mode):
                raise RuntimeError("设备目录类型异常，已停止。")
        info = self._info()
        if info is None:
            if missing:
                return self
            raise RuntimeError("缺少设备文件或目录：" + str(self))
        if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
            raise RuntimeError("设备文件类型不符合预期：" + str(self))
        return self

    def stat(self):
        self.plain(directory=self.is_dir())
        return self.session.sftp.lstat(str(self))

    def iterdir(self):
        self.plain(directory=True)
        for name in self.session.sftp.listdir(str(self)):
            yield self / safe_component(name)

    def read_bytes(self):
        self.plain()
        if self.stat().st_size > MAX_READ:
            raise RuntimeError("设备文件过大，已停止读取。")
        with self.session.sftp.open(str(self), "rb") as stream:
            data = stream.read(MAX_READ + 1)
        if len(data) > MAX_READ:
            raise RuntimeError("读取期间设备文件超过大小限制。")
        return data

    def read_text(self, encoding="utf-8"):
        return self.read_bytes().decode(encoding)

    def mkdir(self, *, parents=False, exist_ok=False):
        self.session.write_guard(self)
        self.plain(directory=True, missing=True)
        if self.exists():
            if exist_ok:
                return
            raise FileExistsError(str(self))
        if parents and not self.parent.exists():
            self.parent.mkdir(parents=True, exist_ok=True)
        self.parent.plain(directory=True)
        self.session.sftp.mkdir(str(self))

    def unlink(self):
        self.session.write_guard(self)
        self.plain()
        self.session.sftp.remove(str(self))

    def rename(self, destination):
        if not isinstance(destination, RemotePath) or destination.session is not self.session:
            raise RuntimeError("不能跨设备移动备份。")
        self.session.write_guard(self)
        self.session.write_guard(destination)
        self.plain(directory=self.is_dir())
        destination.parent.plain(directory=True)
        if destination.exists():
            raise RuntimeError("备份目标已存在，未覆盖。")
        # Standard SFTP rename refuses an existing destination, unlike POSIX
        # rename; this is the same publish-without-overwrite rule as USB.
        self.session.sftp.rename(str(self), str(destination))
        self.session.sync()

    def write_exclusive(self, data):
        self.session.write_guard(self)
        self.parent.plain(directory=True)
        self.plain(missing=True)
        with self.session.sftp.open(str(self), "wx") as stream:
            for offset in range(0, len(data), 65536):
                stream.write(data[offset:offset + 65536])
            stream.flush()

    def atomic_bytes(self, data):
        self.session.write_guard(self)
        self.parent.plain(directory=True)
        self.plain(missing=True)
        temporary = self.with_name(self.name + ".tmp-" + uuid.uuid4().hex)
        try:
            temporary.write_exclusive(data)
            if temporary.read_bytes() != data:
                raise RuntimeError("无线临时文件校验失败，未替换目标。")
            self.session.write_guard(self)
            self.plain(missing=True)
            # Never emulate replacement by deleting the journal first. The
            # paired OpenSSH SFTP server must support its atomic extension.
            self.session.sftp.posix_rename(str(temporary), str(self))
            self.session.sync()
        finally:
            try:
                if temporary.exists():
                    temporary.unlink()
            except (OSError, EOFError, RuntimeError):
                # A lost connection may leave a known .tmp-UUID file; the
                # existing backup journal can inspect/resume these later.
                pass


class WirelessSession:
    def __init__(self, project, runtime, allowed_ids):
        self.project = Path(project)
        self.runtime = Path(runtime)
        self.allowed_ids = allowed_ids
        self.client = None
        self.sftp = None
        self.sock = None

    def __enter__(self):
        try:
            self.connect()
            return self
        except BaseException:
            self.close()
            raise

    def __exit__(self, *_):
        self.close()

    def close(self):
        for connection in (self.sftp, self.client, self.sock):
            if connection is not None:
                try:
                    connection.close()
                except Exception:
                    pass

    def connect(self):
        import paramiko

        config_path = self.runtime / "wireless.json"
        if not config_path.exists():
            raise RuntimeError("还没有无线配对配置，请先完成 Paperwhite 2 无线连接设置。")
        config = json.loads(local_file(config_path, 65536).read_text(encoding="utf-8-sig"))
        if not isinstance(config, dict):
            raise RuntimeError("无线配置格式不正确。")
        self.host = config.get("host")
        port = config.get("port", 22)
        device_id = config.get("device_id_sha256")
        try:
            address = ipaddress.IPv4Address(self.host) if isinstance(self.host, str) else None
        except ipaddress.AddressValueError:
            address = None
        if address is None or not any(address in network for network in PRIVATE_NETWORKS):
            raise RuntimeError("请输入 Kindle 在同一 Wi-Fi 下的局域网 IPv4 地址（10.x、172.16–31.x 或 192.168.x）。")
        if (type(port) is not int or not 1 <= port <= 65535
                or not isinstance(device_id, str) or not re.fullmatch(r"[0-9a-f]{64}", device_id)):
            raise RuntimeError("无线地址、端口或设备配对标识不正确。")
        paths = {}
        for name in ("private_key", "known_hosts"):
            value = config.get(name)
            if not isinstance(value, str) or not value or "\x00" in value:
                raise RuntimeError("无线配置缺少私钥或已配对主机密钥文件。")
            path = Path(value)
            try:
                paths[name] = local_file(path if path.is_absolute() else self.project / path, 1024 * 1024)
            except (OSError, RuntimeError) as error:
                raise RuntimeError("无线配对密钥文件缺失或无法安全读取，请接 USB 重新配对。") from error
        alias = "pw2-" + device_id[:16]
        self.client = paramiko.SSHClient()
        self.client.load_host_keys(str(paths["known_hosts"]))
        self.client.set_missing_host_key_policy(paramiko.RejectPolicy())
        if not self.client.get_host_keys().lookup(alias):
            raise RuntimeError("缺少这台 Kindle 的已配对主机密钥，请通过 USB 重新配对；不会自动信任新主机。")
        self.sock = socket.create_connection((self.host, port), timeout=8)
        # The stable alias pins identity across DHCP changes. The socket already
        # connects to the configured port, so host-key lookup always uses alias.
        self.client.connect(hostname=alias, sock=self.sock, username="root",
                            key_filename=str(paths["private_key"]),
                            allow_agent=False, look_for_keys=False,
                            timeout=8, banner_timeout=8, auth_timeout=8,
                            channel_timeout=10)
        self.client.get_transport().set_keepalive(5)
        if self.run("id -u", timeout=8).strip() != "0":
            raise RuntimeError("无线账号缺少屏保目录所需权限。")
        serial = self.run("cat /proc/usid", timeout=8).strip().upper()
        if (not re.fullmatch(r"[A-Z0-9]{16}", serial) or serial[:2] not in {"B0", "90"}
                or serial[2:4] not in self.allowed_ids
                or hashlib.sha256(serial.encode("ascii")).hexdigest() != device_id):
            raise RuntimeError("无线连接到的设备与已配对的 Paperwhite 2 不一致，未写入。")
        self.guard()
        self.sftp = self.bounded(self.client.open_sftp, 10)
        self.sftp.get_channel().settimeout(12)
        root = RemotePath(self, ROOT)
        version_file = root / "system/version.txt"
        if version_file.stat().st_size > 4096:
            raise RuntimeError("设备版本文件异常，已停止。")
        version = version_file.read_text(encoding="utf-8-sig").strip()
        if not re.match(r"^Kindle 5\.12\.2\.2(?:\s|$)", version):
            raise RuntimeError("无线留言板仅支持 Paperwhite 2 的 5.12.2.2 系统。")
        self.device = {"root": root, "id": device_id, "prefix": serial[:4],
                       "firmware": version, "transport": "wireless", "host": self.host}

    def bounded(self, callback, timeout):
        # Paramiko's exec/subsystem acknowledgement waits are not governed by
        # Channel.settimeout. Close the transport if a server never acknowledges.
        ready = threading.Event()
        result = []

        def request():
            try:
                result.append((True, callback()))
            except BaseException as error:
                result.append((False, error))
            finally:
                ready.set()

        threading.Thread(target=request, daemon=True).start()
        if not ready.wait(timeout):
            self.client.close()
            raise TimeoutError("Kindle 没有及时响应连接请求，请唤醒后重试。")
        succeeded, value = result[0]
        if not succeeded:
            raise value
        return value

    def run(self, command, *, timeout=12):
        """Run only module-owned commands, with bounded output and total time."""
        transport = self.client.get_transport()
        if transport is None or not transport.is_active():
            raise RuntimeError("无线连接已断开，请唤醒 Kindle 后重试。")
        channel = transport.open_session(timeout=8)
        channel.settimeout(8)
        output = bytearray()
        errors = bytearray()
        deadline = time.monotonic() + timeout
        try:
            self.bounded(lambda: channel.exec_command(command), min(timeout, 8))
            while True:
                while channel.recv_ready():
                    output.extend(channel.recv(8192))
                    if len(output) > 131072:
                        raise RuntimeError("设备命令输出异常，已停止。")
                while channel.recv_stderr_ready():
                    errors.extend(channel.recv_stderr(8192))
                    if len(errors) > 131072:
                        raise RuntimeError("设备命令错误输出异常，已停止。")
                if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready():
                    result = channel.recv_exit_status()
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError("Kindle 响应超时，请确认设备已唤醒并连接同一网络。")
                time.sleep(0.05)
            if result != 0:
                if command == STORAGE_GUARD:
                    raise RuntimeError("Kindle 存储盘正在通过 USB 导出或尚未就绪。请安全弹出、拔线并唤醒 Kindle 后重试无线发送。")
                raise RuntimeError("Kindle 命令未完成（返回码 %d）。" % result)
            return output.decode("utf-8", errors="replace")
        finally:
            channel.close()

    def guard(self):
        self.run(STORAGE_GUARD, timeout=8)

    def write_guard(self, path):
        if path.session is not self or PurePosixPath("/mnt/us/linkss") not in path.path.parents:
            raise RuntimeError("拒绝写入留言板管理范围以外的设备路径。")
        self.guard()

    def sync(self):
        self.run("sync", timeout=15)

    def refresh(self):
        self.guard()
        (self.device["root"] / "extensions/linkss/bin/linkss.sh").plain()
        # This is the official KUAL action, including stop + start (not restart),
        # so linkss receives its stopped event and rebuilds the live bind mount.
        self.run(REFRESH, timeout=55)
