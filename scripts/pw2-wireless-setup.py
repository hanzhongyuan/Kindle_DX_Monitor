#!/usr/bin/env python3
"""Pair a USB-connected PW2 for private-key Wi-Fi sending; also repairs lost keys.

Install NiLuJe's PW2 USBNetwork package first. Keep flight mode enabled during
pairing, then safely eject, unplug, and restart before enabling Wi-Fi. This tool
does not install firmware, change books, send a message, or restart the Kindle.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import importlib.util
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

PROJECT = Path(__file__).resolve().parent.parent
RUNTIME = PROJECT / "runtime" / "paperwhite2"
REMOTE_HOST_KEY = "/mnt/us/usbnet/etc/pw2_messageboard_host_ed25519"
NETWORKS = tuple(ipaddress.ip_network(value) for value in
                 ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))


def private_host(value):
    address = ipaddress.ip_address(value.strip())
    if address.version != 4 or not any(address in network for network in NETWORKS):
        raise ValueError("请输入 Kindle 的局域网 IPv4 地址，例如 192.168.1.123。")
    return str(address)


def backend_module():
    spec = importlib.util.spec_from_file_location(
        "pw2_usb_pairing_backend", PROJECT / "scripts" / "kindle-pw2-messageboard.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def restrict_directory(path):
    """Create a local key directory accessible to this Windows user and SYSTEM."""
    result = subprocess.run(["whoami.exe", "/user", "/fo", "csv", "/nh"],
                            capture_output=True, timeout=10,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    rows = list(csv.reader(result.stdout.decode("utf-8", errors="replace").splitlines()))
    sid = rows[-1][-1].strip() if result.returncode == 0 and rows else ""
    if not re.fullmatch(r"S-1-(?:\d+-)*\d+", sid):
        raise RuntimeError("无法确认 Windows 用户，尚未创建登录密钥。")
    path.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        ["icacls.exe", str(path), "/inheritance:r", "/grant:r",
         "*" + sid + ":(OI)(CI)F", "*S-1-5-18:(OI)(CI)F"],
        capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError("无法保护本机密钥目录，请使用支持 Windows 权限的本地磁盘。")


def key_bytes(key):
    return key.private_bytes(serialization.Encoding.PEM,
                             serialization.PrivateFormat.OpenSSH,
                             serialization.NoEncryption())


def public_bytes(key):
    return key.public_key().public_bytes(serialization.Encoding.OpenSSH,
                                        serialization.PublicFormat.OpenSSH)


def load_or_generate_key(path, board):
    board.plain_path(path, missing=True)
    if path.exists():
        if path.stat().st_size > 16384:
            raise RuntimeError("已有密钥文件大小异常，已保留文件，请先核对。")
        key = serialization.load_ssh_private_key(path.read_bytes(), password=None)
        if not isinstance(key, Ed25519PrivateKey):
            raise RuntimeError("已有专用密钥格式不同，已保留文件，请先核对。")
        return key, False
    return Ed25519PrivateKey.generate(), True


def configure_usbnet(text):
    for name in ("USE_WIFI", "USE_WIFI_SSHD_ONLY", "USE_OPENSSH"):
        pattern = r"(?m)^" + name + r"=.*$"
        if len(re.findall(pattern, text)) != 1:
            raise RuntimeError("USBNetwork 配置不符合预期，请先核对：" + name)
        text = re.sub(pattern, name + '="true"', text)
    return text


def configure_sshd(text):
    if re.search(r"(?im)^\s*(?:Match|Include)\s", text):
        raise RuntimeError("已有 SSH 配置包含自定义分支，已保留配置，请先核对后再配对。")
    settings = {
        "HostKey": REMOTE_HOST_KEY,
        "Port": "22",
        "AddressFamily": "inet",
        "PubkeyAuthentication": "yes",
        "AuthenticationMethods": "publickey",
        "PasswordAuthentication": "no",
        "KbdInteractiveAuthentication": "no",
        "ChallengeResponseAuthentication": "no",
        "PermitEmptyPasswords": "no",
        "PermitRootLogin": "prohibit-password",
        "AuthorizedKeysFile": "/mnt/us/usbnet/etc/authorized_keys",
        "StrictModes": "no",  # Official Kindle port uses a FAT user partition.
        "AllowUsers": "root",
        "AllowTcpForwarding": "no",
        "AllowAgentForwarding": "no",
        "X11Forwarding": "no",
        "UseDNS": "no",
        "Subsystem": "sftp /mnt/us/usbnet/libexec/sftp-server",
    }
    names = {name.lower() for name in settings}
    kept = []
    for line in text.splitlines():
        words = line.strip().split()
        if words and not words[0].startswith("#") and words[0].lower() in names:
            continue
        if line != "# PW2 messageboard USB pairing settings":
            kept.append(line)
    lines = ["# PW2 messageboard USB pairing settings"]
    lines += [name + " " + value for name, value in settings.items()]
    return "\n".join(lines + kept).rstrip() + "\n"


def pair(host=None):
    if os.name != "nt":
        raise RuntimeError("请在连接 Kindle USB 存储盘的 Windows 电脑上运行配对。")
    board = backend_module()
    device = board.discover()
    board.MessageBoard(device).inspect()
    root = device["root"]
    etc = root / "usbnet" / "etc"
    for relative in ("usbnet/etc/config", "usbnet/etc/sshd_config",
                     "usbnet/sbin/sshd", "usbnet/libexec/sftp-server"):
        board.plain_path(root / relative)
    config_path = RUNTIME / "wireless.json"
    config = board.read_json(config_path) if config_path.exists() else {}
    if config.get("device_id_sha256") not in (None, device["id"]):
        raise RuntimeError("本机无线配置属于另一台 Kindle，未覆盖；请先保留原配置。")
    selected_host = host or config.get("host", "")
    if selected_host:
        selected_host = private_host(selected_host)
    key_dir = RUNTIME / "wireless" / device["id"]
    board.plain_path(key_dir, directory=True, missing=True)
    restrict_directory(key_dir)
    client_path = key_dir / "client_ed25519"
    known_hosts_path = key_dir / "known_hosts"
    host_path = etc / "pw2_messageboard_host_ed25519"
    client_key, client_created = load_or_generate_key(client_path, board)
    host_key, host_created = load_or_generate_key(host_path, board)
    tag = "kindle-pw2-messageboard-" + device["id"][:16]
    alias = "pw2-" + device["id"][:16]
    auth_path = etc / "authorized_keys"
    board.plain_path(auth_path, missing=True)
    if auth_path.exists() and auth_path.stat().st_size > 1024 * 1024:
        raise RuntimeError("已有授权文件过大，已停止并保留原文件。")
    authorized = auth_path.read_text(encoding="utf-8") if auth_path.exists() else ""
    lines = [line for line in authorized.splitlines()
             if not line.split() or line.split()[-1] != tag]
    lines.append(public_bytes(client_key).decode("ascii") + " " + tag)
    usbnet_config = configure_usbnet((etc / "config").read_text(encoding="utf-8"))
    sshd_config = configure_sshd((etc / "sshd_config").read_text(encoding="utf-8"))
    auto = root / "usbnet" / "auto"
    board.plain_path(auto, missing=True)
    if auto.exists() and auto.stat().st_size:
        raise RuntimeError("USBNetwork 自动启动标志不是空文件，已保留并停止。")
    # Back up the settings only, never book content. This directory inherits the
    # restricted local key directory ACL, including copies of any existing key.
    saved = key_dir / ("before-pairing-" + dt.datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
    saved.mkdir()
    for name in ("config", "sshd_config", "authorized_keys",
                 "pw2_messageboard_host_ed25519", "pw2_messageboard_host_ed25519.pub"):
        path = etc / name
        board.plain_path(path, missing=True)
        if path.exists():
            board.atomic_bytes(saved / name, path.read_bytes())
    if config_path.exists():
        board.atomic_bytes(saved / "wireless.json", config_path.read_bytes())
    if client_created:
        board.atomic_bytes(client_path, key_bytes(client_key))
    if host_created:
        board.atomic_bytes(host_path, key_bytes(host_key))
    host_public = public_bytes(host_key)
    board.atomic_bytes(etc / "pw2_messageboard_host_ed25519.pub", host_public + b"\n")
    board.atomic_bytes(auth_path, ("\n".join(lines) + "\n").encode("utf-8"))
    # Configure key-only authentication before enabling Wi-Fi SSH or autostart.
    board.atomic_bytes(etc / "sshd_config", sshd_config.encode("utf-8"))
    board.atomic_bytes(known_hosts_path, alias.encode("ascii") + b" " + host_public + b"\n")
    config.update(host=selected_host, port=22, device_id_sha256=device["id"],
                  private_key=client_path.relative_to(PROJECT).as_posix(),
                  known_hosts=known_hosts_path.relative_to(PROJECT).as_posix(),
                  paired_at=dt.datetime.now(dt.timezone.utc).isoformat())
    board.atomic_json(config_path, config)
    board.atomic_bytes(etc / "config", usbnet_config.encode("utf-8"))
    board.atomic_bytes(auto, b"")
    if ((etc / "sshd_config").read_bytes() != sshd_config.encode("utf-8")
            or (etc / "config").read_bytes() != usbnet_config.encode("utf-8")
            or serialization.load_ssh_private_key(host_path.read_bytes(), None)
                .public_key().public_bytes(serialization.Encoding.OpenSSH,
                                           serialization.PublicFormat.OpenSSH) != host_public):
        raise RuntimeError("配对写入校验未通过，请保持飞行模式并重新接线配对。")
    print("USB 无线配对已完成。请安全弹出并拔掉数据线，在 Kindle 设置中完整重启一次。"
          "重启后开启 Wi-Fi，与电脑连接同一网络，在留言板填入 Kindle 的 IP 地址。")
    print("以后先唤醒 Kindle，再点“无线发送”。密钥丢失时可接回 USB，再运行此配对程序。")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=private_host, help="Kindle LAN IPv4 address; can also be entered in the app later")
    args = parser.parse_args()
    try:
        pair(args.host)
        return 0
    except Exception as error:
        print("PW2 无线配对未完成：" + str(error)
              + "\n请保持飞行模式，保留 runtime/paperwhite2 和设备原文件，再通过 USB 处理。", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
