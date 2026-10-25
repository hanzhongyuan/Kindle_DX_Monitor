#!/usr/bin/env python3
"""Write a PW2 message image through USB storage and the existing linkss plugin.

No SSH, firmware installation, device restart, or drive ejection is performed.
Original images are backed up before any replacement. Disconnecting during a
write leaves a journal; Restore can recover only files belonging to that change.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import uuid

PROJECT = Path(__file__).resolve().parent.parent
RUNTIME = PROJECT / "runtime" / "paperwhite2"
BACKUP_NAME = "pw2-messageboard-backup"
IMAGE_NAME = "bg_ss00.png"
MAX_IMAGE_BYTES = 5 * 1024 * 1024
MAX_LIBRARY_BYTES = 64 * 1024 * 1024
# Legacy serial prefixes: KindleTool's PW2 device IDs, never the DX B009 ID.
PW2_IDS = {"D4", "5A", "D5", "D6", "D7", "D8", "F2", "17", "60", "62", "61", "5F"}
DISCOVER_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$records = @(foreach ($disk in Get-CimInstance Win32_DiskDrive) {
    if ($disk.PNPDeviceID -notlike 'USBSTOR\*' -or $disk.Model -notmatch 'Kindle') { continue }
    foreach ($partition in Get-CimAssociatedInstance -InputObject $disk -Association Win32_DiskDriveToDiskPartition) {
        foreach ($volume in Get-CimAssociatedInstance -InputObject $partition -Association Win32_LogicalDiskToPartition) {
            [pscustomobject]@{ drive = $volume.DeviceID; serial = ([string]$disk.SerialNumber).Trim() }
        }
    }
})
ConvertTo-Json -InputObject $records -Compress
"""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def plain_path(path, *, directory=False, missing=False):
    """Refuse junctions/symlinks, including every ancestor of a managed path."""
    path = Path(path)
    for item in reversed((path,) + tuple(path.parents)):
        try:
            info = item.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise RuntimeError("路径包含符号链接或重解析点，已停止：" + str(item))
    if not path.exists():
        if missing:
            return path
        raise RuntimeError("缺少所需文件或目录：" + str(path))
    if path.is_dir() != directory:
        raise RuntimeError("文件类型不符合预期：" + str(path))
    return path


def read_json(path):
    plain_path(path)
    if path.stat().st_size > 1024 * 1024:
        raise ValueError("记录文件过大，已停止。")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("留言板记录格式不正确。")
    return value


def atomic_bytes(path, data):
    plain_path(path.parent, directory=True)
    plain_path(path, missing=True)
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def atomic_json(path, value):
    atomic_bytes(path, (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def discover():
    if os.name != "nt":
        raise RuntimeError("此版本通过 Windows 的 USB 存储盘发送，请在 Windows 上使用。")
    powershell = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    result = subprocess.run(
        [str(powershell), "-NoProfile", "-NonInteractive", "-Command", DISCOVER_SCRIPT],
        capture_output=True, timeout=25, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    if result.returncode:
        raise RuntimeError("无法读取 USB 设备清单，请重新插线后重试。")
    records = json.loads(result.stdout.decode("utf-8-sig"))
    if not isinstance(records, list):
        raise RuntimeError("USB 设备清单格式不正确。")
    devices = []
    for record in records:
        serial = str(record.get("serial", "")).strip().upper()
        drive = str(record.get("drive", "")).upper()
        if not re.fullmatch(r"[A-Z]:", drive) or not re.fullmatch(r"[A-Z0-9]{16}", serial):
            continue
        if serial[:2] not in {"B0", "90"} or serial[2:4] not in PW2_IDS:
            continue
        root = Path(drive + "\\")
        version_path = plain_path(root / "system/version.txt")
        if version_path.stat().st_size > 4096:
            raise RuntimeError("设备版本文件异常，已停止。")
        version = version_path.read_text(encoding="utf-8-sig").strip()
        if not re.match(r"^Kindle 5\.12\.2\.2(?:\s|$)", version):
            raise RuntimeError("此版本仅面向 Paperwhite 2 的 5.12.2.2 系统，当前为：" + version[:120])
        devices.append({"root": root, "id": digest(serial.encode("ascii")),
                        "prefix": serial[:4], "firmware": version})
    if not devices:
        raise RuntimeError("未找到 Paperwhite 2 存储盘。请接数据线，保持 USB 存储模式；不要开启 USBNetwork。")
    if len(devices) != 1:
        raise RuntimeError("检测到多台 Paperwhite 2，请只连接本次要设置的那一台。")
    return devices[0]


def prepare_image(path):
    from PIL import Image
    path = plain_path(Path(path))
    if path.stat().st_size > MAX_IMAGE_BYTES:
        raise ValueError("留言图片不能超过 5 MiB。")
    with Image.open(path) as picture:
        if picture.format != "PNG" or picture.size != (758, 1024) or getattr(picture, "n_frames", 1) != 1:
            raise ValueError("PW2 留言图片必须是 758×1024 的静态 PNG。")
        picture.load()
        rgba = picture.convert("RGBA")
        if rgba.getchannel("A").getextrema() != (255, 255):
            raise ValueError("留言图片不能含透明背景。")
        output = io.BytesIO()
        gray = rgba.convert("L")
        gray.info.clear()  # Do not carry a source ICC profile into Kindle PNGs.
        gray.save(output, format="PNG", optimize=True)
        return output.getvalue()


def read_images(folder):
    plain_path(folder, directory=True)
    images = {}
    total = 0
    for path in sorted(folder.iterdir()):
        plain_path(path)
        if path.suffix != ".png":
            raise RuntimeError("屏保目录含有非 PNG 文件，请先备份并整理后重试：" + path.name)
        total += path.stat().st_size
        if total > MAX_LIBRARY_BYTES:
            raise RuntimeError("原封面图片总量超过 64 MiB，请先在电脑备份并整理。")
        images[path.name] = path.read_bytes()
    return images


def hashes(images):
    return Counter(digest(data) for data in images.values())


class MessageBoard:
    def __init__(self, device):
        self.device = device
        self.root = device["root"]
        self.plugin = self.root / "linkss"
        self.images = self.plugin / "screensavers"
        self.backup = self.plugin / BACKUP_NAME
        self.manifest_path = self.backup / "manifest.json"
        self.local = RUNTIME / "devices" / device["id"]
        self.local_owner = self.local / "owner.json"

    def inspect(self):
        # Installed plugin code is only checked for presence; never executed here.
        if not self.plugin.is_dir():
            raise RuntimeError("尚未安装 PW2 的 linkss 屏保插件。请先按 docs/PAPERWHITE2_MESSAGEBOARD.zh-CN.md 完成设备准备，再发送留言。")
        for relative in ("bin/linkss", "bin/shuffless", "bin/libkh5"):
            plain_path(self.plugin / relative)
        plain_path(self.images, directory=True)
        if not (self.plugin / "auto").is_file():
            raise RuntimeError("请先在 KUAL 的 Screen Savers 中启用屏保插件，并重启 Kindle 一次。")
        for mode in ("cover", "last", "random", "shuffle"):
            if (self.plugin / mode).exists():
                raise RuntimeError("屏保当前不是普通图片模式。请在 KUAL → Screen Savers → Screen Savers Behavior 选择 Image Cycle (Default)，再点 Restart framework now。")
        for flag in ("auto", "autoreboot", "reboot"):
            plain_path(self.plugin / flag, missing=True)
        return {"model": "Kindle Paperwhite 2", "serial_prefix": self.device["prefix"],
                "firmware": self.device["firmware"], "drive": str(self.root),
                "requires_safe_eject": True, "backup_present": self.backup.exists()}

    def load_manifest(self):
        owner = read_json(self.local_owner) if self.local_owner.exists() else None
        if owner is not None and owner.get("phase") == "preparing":
            return self.resume_backup(owner)
        if not self.backup.exists():
            return None
        plain_path(self.backup, directory=True)
        manifest = read_json(self.manifest_path)
        if owner is None:
            raise RuntimeError("缺少本机封面备份记录，请找回原项目的 runtime/paperwhite2。")
        if (manifest.get("version") != 1 or manifest.get("device_id") != self.device["id"]
                or owner.get("device_id") != self.device["id"]
                or not re.fullmatch(r"[0-9a-f]{32}", str(owner.get("token", "")))
                or manifest.get("token") != owner.get("token")
                or owner.get("phase") not in {None, "ready"}):
            raise RuntimeError("设备备份与本机记录不匹配，请保留原项目 runtime/paperwhite2，不能覆盖别人的备份。")
        original = read_images(self.backup / "original")
        expected = manifest.get("original")
        if not isinstance(expected, dict) or {name: digest(data) for name, data in original.items()} != expected:
            raise RuntimeError("原封面备份已被改动，已停止以保护现有文件。")
        if manifest.get("phase") not in {"inactive", "active", "pending"}:
            raise RuntimeError("设备备份记录不完整，请保留现有文件。")
        if type(manifest.get("autoreboot_was_enabled")) is not bool:
            raise RuntimeError("原自动重载设置记录损坏，已停止。")
        for key in ("autoreboot_added", "reboot_added"):
            if key in manifest and type(manifest[key]) is not bool:
                raise RuntimeError("自动重载设置归属记录损坏，已停止。")
        for key in ("current_hash", "pending_hash"):
            value = manifest.get(key)
            if value is not None and not re.fullmatch(r"[0-9a-f]{64}", str(value)):
                raise RuntimeError("留言图片记录损坏，已停止。")
        return manifest

    def resume_backup_files(self, folder, desired, directories=()):
        """Resume only journal-owned files with matching complete/prefix bytes."""
        plain_path(folder, directory=True)
        temporary = []
        for path in folder.iterdir():
            if path.name in directories:
                plain_path(path, directory=True)
                continue
            plain_path(path)
            name = path.name
            is_temporary = False
            if name not in desired:
                candidate = name.rsplit(".tmp-", 1)
                if (len(candidate) != 2 or candidate[0] not in desired
                        or not re.fullmatch(r"[0-9a-f]{32}", candidate[1])):
                    raise RuntimeError("未完成备份中有未知文件，已保留并停止：" + name)
                name = candidate[0]
                is_temporary = True
            payload = desired[name]
            if path.stat().st_size > len(payload) or not payload.startswith(path.read_bytes()):
                raise RuntimeError("未完成备份中的文件已被改动，已保留并停止：" + path.name)
            if is_temporary:
                temporary.append(path)
        # Inspect every entry before removing only our verified atomic leftovers.
        for path in temporary:
            path.unlink()
        for name, payload in desired.items():
            path = folder / name
            if not path.exists() or path.read_bytes() != payload:
                atomic_bytes(path, payload)

    def resume_backup(self, owner):
        expected = owner.get("original")
        if (owner.get("phase") != "preparing" or owner.get("device_id") != self.device["id"]
                or not re.fullmatch(r"[0-9a-f]{32}", str(owner.get("token", "")))
                or not isinstance(expected, dict)
                or type(owner.get("autoreboot_was_enabled")) is not bool):
            raise RuntimeError("未完成备份的本机记录不完整，已停止，原封面未修改。")
        for name, checksum in expected.items():
            if (not isinstance(name, str) or Path(name).name != name or Path(name).suffix != ".png"
                    or not re.fullmatch(r"[0-9a-f]{64}", str(checksum))):
                raise RuntimeError("未完成备份的原图片记录异常，已停止。")
        if len({name.casefold() for name in expected}) != len(expected):
            raise RuntimeError("原图片记录包含重复文件名，已停止。")
        current = read_images(self.images)
        if hashes(current) != Counter(expected.values()):
            raise RuntimeError("首次备份尚未完成，而当前封面已改变；已保留全部文件，不会覆盖新图片。")
        # linkss can rename identical images after USB disconnects. Preserve the
        # originally journaled names while requiring exactly the same contents.
        by_hash = {digest(data): data for data in current.values()}
        originals = {name: by_hash[checksum] for name, checksum in expected.items()}
        manifest = dict(device_id=owner["device_id"], token=owner["token"], version=1,
                        phase="inactive", current_hash=None, original=expected,
                        autoreboot_was_enabled=owner["autoreboot_was_enabled"])
        staging = self.plugin / (BACKUP_NAME + ".preparing-" + owner["token"])
        if self.backup.exists():
            # Only a fully published backup with this exact transaction identity
            # can survive the final local ready-record write being interrupted.
            plain_path(self.backup, directory=True)
            if staging.exists() or {path.name for path in self.backup.iterdir()} != {"manifest.json", "original"}:
                raise RuntimeError("正式备份与未完成事务冲突，已保留文件，不会接管已有目录。")
            if read_json(self.manifest_path) != manifest:
                raise RuntimeError("已有备份不属于本次准备事务，已停止。")
            if {name: digest(data) for name, data in read_images(self.backup / "original").items()} != expected:
                raise RuntimeError("已发布的原封面备份与准备记录不匹配，已停止。")
        else:
            plain_path(staging, directory=True, missing=True)
            staging.mkdir(exist_ok=True)
            manifest_bytes = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
            self.resume_backup_files(staging, {"manifest.json": manifest_bytes}, directories={"original"})
            original_folder = staging / "original"
            plain_path(original_folder, directory=True, missing=True)
            original_folder.mkdir(exist_ok=True)
            self.resume_backup_files(original_folder, originals)
            if {name: digest(data) for name, data in read_images(original_folder).items()} != expected:
                raise RuntimeError("原封面备份校验失败，原图片未改变。")
            if hashes(read_images(self.images)) != Counter(expected.values()):
                raise RuntimeError("备份期间封面内容发生变化，已停止，未修改当前封面。")
            plain_path(self.backup, directory=True, missing=True)
            if self.backup.exists():
                raise RuntimeError("正式备份目录已被其他操作创建，未覆盖。")
            # This publishes only our backup. The live screensavers directory is
            # never renamed, and Windows refuses an existing destination here.
            staging.rename(self.backup)
        atomic_json(self.local_owner, dict(owner, phase="ready"))
        return manifest

    def create_backup(self, current):
        if self.local_owner.exists():
            owner = read_json(self.local_owner)
            if owner.get("phase") == "preparing":
                return self.resume_backup(owner)
            raise RuntimeError("本机已有此设备记录但设备备份丢失，请先找回备份，不要重新接管。")
        if self.backup.exists():
            raise RuntimeError("备份目录已存在，不能覆盖。")
        plain_path(self.local, directory=True, missing=True)
        self.local.mkdir(parents=True, exist_ok=True)
        plain_path(self.local, directory=True)
        owner = {"device_id": self.device["id"], "token": uuid.uuid4().hex, "phase": "preparing",
                 "original": {name: digest(data) for name, data in current.items()},
                 "autoreboot_was_enabled": (self.plugin / "autoreboot").is_file()}
        # Persist recovery identity and the untouched source library before the
        # first device write. Interrupted preparation can safely resume later.
        atomic_json(self.local_owner, owner)
        return self.resume_backup(owner)

    def validate_current(self, manifest, current, *, recovering=False):
        observed = hashes(current)
        if manifest["phase"] == "pending":
            if not recovering:
                raise RuntimeError("上次发送或恢复中断，请先点击“恢复之前的封面”，不要删除备份。")
            # Partial writes are recoverable only when every file has a recorded
            # name and matching bytes (or is a prefix of our staged image).
            allowed = set(manifest["original"].values())
            allowed.update(h for h in (manifest.get("current_hash"), manifest.get("pending_hash")) if h)
            staged = self.backup / "staged.png"
            staged_bytes = b""
            if manifest.get("pending_hash") is not None:
                plain_path(staged)
                if staged.stat().st_size > MAX_IMAGE_BYTES:
                    raise RuntimeError("待发送图片异常，已停止。")
                staged_bytes = staged.read_bytes()
            if staged_bytes and digest(staged_bytes) != manifest.get("pending_hash"):
                raise RuntimeError("待发送图片记录不匹配，已停止。")
            original = read_images(self.backup / "original")
            for name, data in current.items():
                if digest(data) in allowed:
                    continue
                if name == IMAGE_NAME and staged_bytes and staged_bytes.startswith(data):
                    continue
                if name in original and original[name].startswith(data):
                    continue
                raise RuntimeError("屏保目录出现了不属于本次操作的图片，已停止，避免覆盖你的新文件。")
        elif manifest["phase"] == "active":
            if observed != Counter({manifest["current_hash"]: 1}):
                raise RuntimeError("留言封面已被其他操作修改，请先备份并核对；程序不会覆盖新增图片。")
        elif observed != Counter(manifest["original"].values()):
            raise RuntimeError("之前的封面已被改动，请先保留现有图片和备份后处理。")

    def replace_images(self, current, desired):
        # Keep the directory inode: linkss bind-mounts this exact directory.
        if hashes(read_images(self.images)) != hashes(current):
            raise RuntimeError("操作前屏保目录发生变化，已停止。")
        for name, data in current.items():
            path = plain_path(self.images / name)
            if digest(path.read_bytes()) != digest(data):
                raise RuntimeError("操作前屏保文件发生变化，已停止。")
            path.unlink()
        for name, data in desired.items():
            path = self.images / name
            plain_path(path, missing=True)
            with path.open("xb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
        if hashes(read_images(self.images)) != hashes(desired):
            raise RuntimeError("屏保写入校验失败，请重新连接后点击“恢复之前的封面”。")

    def reload_notice(self, mode):
        if mode == "manual":
            return "请安全弹出 Kindle 并拔线，在 KUAL → Screen Savers 点击 Restart framework now，或在 Kindle 设置菜单完整重启一次。等书籍列表回来，再短按电源显示封面。"
        if mode == "first":
            return "请安全弹出 Kindle 并拔线，然后在 Kindle 的设置菜单选择“重启”一次，让自动重载生效。以后发送后弹出并拔线，约 10 秒会重新载入界面，再短按电源显示封面。"
        return "请安全弹出 Kindle 并拔线，约 10 秒后插件会重新载入界面，再短按电源显示封面。若未更新，打开 KUAL → Screen Savers → Restart framework now；首次配置也可完整重启一次。"

    def request_reload(self, manifest):
        font_reload = self.root / "linkfonts/autoreboot"
        if font_reload.exists():
            plain_path(font_reload)
            return "manual"  # Its watchdog owns USB events; leave it unchanged.
        autoreboot = self.plugin / "autoreboot"
        first_enable = not autoreboot.exists()
        if first_enable:
            manifest["autoreboot_added"] = True
            atomic_json(self.manifest_path, manifest)
            atomic_bytes(autoreboot, b"")
            # The initial manual restart loads this image and starts the
            # watchdog. A reboot marker now would survive until an unrelated
            # later USB disconnect, because the watchdog missed this event.
            return "first"
        # Upstream's watchdog consumes this marker after USB is disconnected.
        if not (self.plugin / "reboot").exists():
            manifest["reboot_added"] = True
            atomic_json(self.manifest_path, manifest)
            atomic_bytes(self.plugin / "reboot", b"")
        return "auto"

    def restore_reload_settings(self, manifest):
        if manifest.get("autoreboot_added") and not manifest["autoreboot_was_enabled"]:
            for name, was_added in (("autoreboot", True), ("reboot", manifest.get("reboot_added"))):
                path = self.plugin / name
                if was_added and path.exists():
                    plain_path(path)
                    if path.stat().st_size:
                        raise RuntimeError("自动重载标志已被其他操作修改，请保留文件并手动处理：" + name)
                    path.unlink()
            manifest.update(autoreboot_added=False, reboot_added=False)
            atomic_json(self.manifest_path, manifest)
            # A full plugin stop/start is needed to stop its existing watchdog.
            return "manual"
        if not (self.plugin / "autoreboot").exists():
            return "manual"
        return self.request_reload(manifest)

    def install(self, payload):
        self.inspect()
        current = read_images(self.images)
        manifest = self.load_manifest()
        if manifest is None:
            manifest = self.create_backup(current)
        self.validate_current(manifest, current)
        get_drive_type = ctypes.windll.kernel32.GetDriveTypeW
        get_drive_type.argtypes = [ctypes.c_wchar_p]
        get_drive_type.restype = ctypes.c_uint
        if get_drive_type(str(self.root)) not in (2, 3):
            raise RuntimeError("Kindle 存储盘已断开，请重新连接。")
        atomic_bytes(self.backup / "staged.png", payload)
        manifest.update(phase="pending", pending_hash=digest(payload), pending_action="install")
        atomic_json(self.manifest_path, manifest)
        self.replace_images(current, {IMAGE_NAME: payload})
        manifest.update(phase="active", current_hash=digest(payload), pending_hash=None,
                        pending_action=None, updated=dt.datetime.now(dt.timezone.utc).isoformat())
        atomic_json(self.manifest_path, manifest)
        mode = self.request_reload(manifest)
        return "留言图片已保存到 Paperwhite 2，原封面已备份。" + self.reload_notice(mode)

    def restore(self):
        self.inspect()
        manifest = self.load_manifest()
        if manifest is None:
            return "这台 Paperwhite 2 没有本程序的封面备份，未修改设备。"
        current = read_images(self.images)
        self.validate_current(manifest, current, recovering=True)
        original = read_images(self.backup / "original")
        manifest.update(phase="pending", pending_action="restore")
        atomic_json(self.manifest_path, manifest)
        self.replace_images(current, original)
        manifest.update(phase="inactive", current_hash=None, pending_hash=None,
                        pending_action=None, updated=dt.datetime.now(dt.timezone.utc).isoformat())
        atomic_json(self.manifest_path, manifest)
        mode = self.restore_reload_settings(manifest)
        return "已恢复本程序接管前的屏保图片，并还原本程序新增的自动重载设置。备份仍保留；这不会卸载屏保插件。" + self.reload_notice(mode)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--action", choices=("install", "restore", "status"), required=True)
    parser.add_argument("--image")
    args = parser.parse_args()
    if (args.action == "install") != bool(args.image):
        parser.error("only --action install requires --image")
    try:
        payload = prepare_image(args.image) if args.action == "install" else None
        board = MessageBoard(discover())
        if args.action == "status":
            print(json.dumps(board.inspect(), ensure_ascii=False))
        else:
            print(board.install(payload) if args.action == "install" else board.restore())
        return 0
    except Exception as error:
        print("PW2 留言板操作未完成：" + str(error) + "\n请保留设备上的 linkss/" + BACKUP_NAME + " 及其 preparing 准备目录、本机 runtime/paperwhite2，不要删除备份排错。", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
