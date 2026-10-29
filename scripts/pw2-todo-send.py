#!/usr/bin/env python3
"""Append a batch to the paired PW2's KOReader todo inbox over Wi-Fi."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import re
import sys
import uuid

from pw2_wireless import RemotePath, WirelessSession, explain_error, local_file

PROJECT = Path(__file__).resolve().parent.parent
INBOX = PurePosixPath('/mnt/us/koreader/settings/kindle-todo-inbox')
PLUGIN = 'koreader/plugins/kindletodo.koplugin'
MAX_BYTES = 256 * 1024
BATCH_ID = re.compile(r'[0-9a-f]{64}')
QUEUE_NAME = re.compile(r'[0-9a-f]{64}\.json(?:\.tmp-[0-9a-f]{32})?')


def shared_backend(project):
    spec = importlib.util.spec_from_file_location('pw2_messageboard_shared',
                                                Path(__file__).with_name('kindle-pw2-messageboard.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.PROJECT = Path(project)
    module.RUNTIME = module.PROJECT / 'runtime' / 'paperwhite2'
    return module


class TodoWirelessSession(WirelessSession):
    """Reuse pinned identity; grant writes only to this plugin's batch inbox."""
    def write_guard(self, path):
        if (not isinstance(path, RemotePath) or path.session is not self
                or not (path.path == INBOX or (path.path.parent == INBOX
                         and QUEUE_NAME.fullmatch(path.name)))):
            raise RuntimeError('拒绝写入待办收件箱以外的设备路径。')
        self.guard()


def parse_items(raw):
    if len(raw) > MAX_BYTES:
        raise ValueError('待办文本超过 256 KB，请分批发送。')
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeDecodeError as error:
        raise ValueError('待办文本必须使用 UTF-8 编码。') from error
    items = []
    # Keep the exact same newline and ASCII whitespace rules as the Lua store.
    for line in text.replace('\r\n', '\n').replace('\r', '\n').split('\n'):
        line = line.strip(' \t\v\f')
        if not line:
            continue
        if any(ord(char) < 32 or ord(char) == 127 for char in line):
            raise ValueError('待办事项中不能含制表符或其他控制字符。')
        if len(line) > 300:
            raise ValueError('每项待办最多 300 个字符，请缩短后重试。')
        items.append(line)
    if not items:
        raise ValueError('请先输入待办事项，每行一项。')
    if len(items) > 200:
        raise ValueError('每批最多发送 200 项待办。')
    return items


def payload_for(items, batch_id):
    return (json.dumps({'schema': 1, 'batch_id': batch_id, 'items': items},
                       ensure_ascii=False, separators=(',', ':')) + '\n').encode('utf-8')


def prepare_batch(shared, device_id, items):
    """Persist before connecting writes so an uncertain retry keeps its ID."""
    path = shared.RUNTIME / ('todo-outbox-' + device_id + '.json')
    shared.plain_path(path, missing=True)
    if path.exists():
        try:
            old = json.loads(local_file(path, MAX_BYTES).read_text(encoding='utf-8'))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ValueError('本机待办发送记录损坏，请保留 runtime/paperwhite2 中的记录后检查。') from error
        if (not isinstance(old, dict) or old.get('schema') != 1
                or not isinstance(old.get('batch_id'), str)
                or not BATCH_ID.fullmatch(old['batch_id'])
                or not isinstance(old.get('items'), list)):
            raise ValueError('本机待办发送记录损坏，请保留 runtime/paperwhite2 中的记录后检查。')
        if old['items'] == items:
            return old['batch_id'], payload_for(items, old['batch_id'])
    batch_id = hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    payload = payload_for(items, batch_id)
    if len(payload) > MAX_BYTES:
        raise ValueError('这批待办编码后超过 256 KB，请分批发送。')
    shared.atomic_bytes(path, payload)
    return batch_id, payload


def already_received(root, batch_id):
    path = root / 'koreader/settings/kindle-todo.json'
    if not path.exists():
        return False
    if path.stat().st_size > 2 * 1024 * 1024:
        raise RuntimeError('设备待办记录过大，请先在 Kindle 上检查。')
    try:
        state = json.loads(path.read_text())
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RuntimeError('设备待办记录无法读取，请先在 Kindle 上检查，原文件未修改。') from error
    if not isinstance(state, dict):
        raise RuntimeError('设备待办记录格式不正确，请先在 Kindle 上检查。')
    batches = state.get('processed_batches', [])
    return isinstance(batches, dict) and batches.get(batch_id) is True


def send_batch(session, batch_id, payload):
    root = session.device['root']
    receiver = root / (PLUGIN + '/inbox.lua')
    if (not receiver.exists() or receiver.stat().st_size > 128 * 1024
            or 'Inbox.PROTOCOL = 1' not in receiver.read_text()):
        raise RuntimeError('Kindle 上的待办还没有无线接收功能，请先安装新版 KindlePaperwhite2Todo.zip，并重新启动 KOReader。')
    if already_received(root, batch_id):
        return '这份草稿已接收，没有重复添加。Kindle 上的完成状态已保留。'
    inbox = RemotePath(session, INBOX)
    inbox.mkdir(exist_ok=True)
    final = inbox / (batch_id + '.json')
    if final.exists():
        # The receiver may consume this between lstat and open. Only tolerate
        # disappearance when its transaction is visible in the primary store.
        try:
            same = final.read_bytes() == payload
        except (OSError, RuntimeError):
            if already_received(root, batch_id):
                return '这份草稿已接收，没有重复添加。Kindle 上的完成状态已保留。'
            raise
        if not same:
            raise RuntimeError('设备已有同名但内容不同的待办批次，未覆盖。请保留收件箱检查。')
    else:
        # Upload under an ignored suffix, verify, sync, then publish without
        # overwrite. The device only sees complete *.json batches.
        temporary = inbox / (batch_id + '.json.tmp-' + uuid.uuid4().hex)
        try:
            temporary.write_exclusive(payload)
            if temporary.read_bytes() != payload:
                raise RuntimeError('无线待办文件校验失败，请重试。')
            session.sync()
            temporary.rename(final)
        finally:
            try:
                if temporary.exists():
                    temporary.unlink()
            except (OSError, EOFError, RuntimeError):
                pass
    if already_received(root, batch_id):
        return '待办已接收，已有事项和完成状态已保留。'
    return ('待办已送达 Kindle。打开“离线待办”后自动接收；若已打开，约 5 秒刷新。'
            '这次会追加整份草稿；连续重发同一草稿不会重复添加。')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=PROJECT)
    parser.add_argument('--text-file', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        items = parse_items(local_file(args.text_file, MAX_BYTES).read_bytes())
        shared = shared_backend(args.project)
        with TodoWirelessSession(args.project, shared.RUNTIME, shared.PW2_IDS) as session:
            with shared.operation_lock(session.device['id']):
                batch_id, payload = prepare_batch(shared, session.device['id'], items)
                message = send_batch(session, batch_id, payload)
        print(json.dumps({'success': True, 'message': message}, ensure_ascii=False))
        return 0
    except Exception as error:
        # Todo never changes screensavers, so avoid their recovery advice.
        if isinstance(error, (EOFError, ConnectionError)):
            detail = '无线连接中断。请唤醒 Kindle 并重试同一草稿，系统会防止重复添加。'
        else:
            detail = explain_error(error)
        print('待办推送未完成：' + detail, file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
