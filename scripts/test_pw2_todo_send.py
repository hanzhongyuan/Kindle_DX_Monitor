"""Exercise batch retries and the queue-only Wi-Fi capability without a device."""
import importlib.util
import json
from pathlib import Path, PurePosixPath
import tempfile
import unittest
from unittest.mock import Mock

SPEC = importlib.util.spec_from_file_location('pw2_todo_send', Path(__file__).with_name('pw2-todo-send.py'))
todo = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(todo)


class MemoryPath:
    def __init__(self, session, path):
        self.session, self.path = session, PurePosixPath(path)

    def __truediv__(self, value):
        return MemoryPath(self.session, self.path / value)

    @property
    def name(self):
        return self.path.name

    def exists(self):
        return str(self.path) in self.session.files

    def read_bytes(self):
        return self.session.files[str(self.path)]

    def read_text(self):
        return self.read_bytes().decode('utf-8')

    def stat(self):
        result = Mock()
        result.st_size = len(self.read_bytes())
        return result

    def mkdir(self, **kwargs):
        self.session.files.setdefault(str(self.path), b'')

    def write_exclusive(self, data):
        assert not self.exists()
        self.session.files[str(self.path)] = data

    def rename(self, other):
        assert not other.exists()
        self.session.published.append(str(other.path))
        self.session.files[str(other.path)] = self.session.files.pop(str(self.path))
        if self.session.fail_after_publish:
            raise ConnectionError('Disconnected after committing the batch')

    def unlink(self):
        self.session.files.pop(str(self.path))


class FakeSession:
    def __init__(self):
        self.files = {'/mnt/us/' + todo.PLUGIN + '/inbox.lua': b'Inbox.PROTOCOL = 1'}
        self.device = {'root': MemoryPath(self, '/mnt/us')}
        self.published = []
        self.fail_after_publish = False
        self.sync = Mock()


class ParseTests(unittest.TestCase):
    def test_normalizes_bom_crlf_blanks_and_unicode(self):
        self.assertEqual(todo.parse_items('\ufeff  事项😀  \r\n\r下一项\n'.encode()), ['事项😀', '下一项'])

    def test_rejects_invalid_input(self):
        for raw in [b'\xff', b'\n \t\r', b'a\tb', b'a\x00b', b'a\x7fb',
                    ('中' * 301).encode(), b'x\n' * 201, b'x' * (todo.MAX_BYTES + 1)]:
            with self.subTest(raw=raw[:20]), self.assertRaises(ValueError):
                todo.parse_items(raw)

    def test_accepts_200_items_and_300_unicode_scalars(self):
        self.assertEqual(len(todo.parse_items(('😀' * 300 + '\n') .encode() * 200)), 200)


class BatchTests(unittest.TestCase):
    def test_same_draft_reuses_id_changed_draft_gets_new_id(self):
        with tempfile.TemporaryDirectory() as directory:
            shared = todo.shared_backend(directory)
            shared.RUNTIME.mkdir(parents=True)
            first, data = todo.prepare_batch(shared, 'a' * 64, ['事项'])
            self.assertEqual((first, data), todo.prepare_batch(shared, 'a' * 64, ['事项']))
            changed, _ = todo.prepare_batch(shared, 'a' * 64, ['事项2'])
            self.assertNotEqual(first, changed)
            back, _ = todo.prepare_batch(shared, 'a' * 64, ['事项'])
            self.assertNotIn(back, [first, changed])
            other, _ = todo.prepare_batch(shared, 'b' * 64, ['事项'])
            self.assertNotEqual(back, other)

    def test_corrupt_local_record_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            shared = todo.shared_backend(directory)
            shared.RUNTIME.mkdir(parents=True)
            path = shared.RUNTIME / ('todo-outbox-' + 'a' * 64 + '.json')
            path.write_bytes(b'{broken')
            with self.assertRaises(ValueError):
                todo.prepare_batch(shared, 'a' * 64, ['事项'])
            self.assertEqual(path.read_bytes(), b'{broken')

    def test_write_scope_excludes_main_data_and_screensavers(self):
        session = todo.TodoWirelessSession('.', '.', set())
        session.guard = Mock()
        for path in [todo.INBOX, todo.INBOX / ('a' * 64 + '.json'),
                     todo.INBOX / ('a' * 64 + '.json.tmp-' + 'b' * 32)]:
            session.write_guard(todo.RemotePath(session, path))
        for path in ['/mnt/us/linkss/test', '/mnt/us/koreader/settings/kindle-todo.json',
                     '/mnt/us/koreader/plugins/kindletodo.koplugin/main.lua',
                     str(todo.INBOX / 'arbitrary.json'), str(todo.INBOX / ('a' * 64 + '.json') / 'nested')]:
            with self.subTest(path=path), self.assertRaises(RuntimeError):
                session.write_guard(todo.RemotePath(session, path))
        other = todo.TodoWirelessSession('.', '.', set())
        with self.assertRaises(RuntimeError):
            session.write_guard(todo.RemotePath(other, todo.INBOX))

    def test_retry_after_lost_ack_publishes_once_and_keeps_main_state(self):
        from unittest.mock import patch
        session = FakeSession()
        state_path = '/mnt/us/koreader/settings/kindle-todo.json'
        original = json.dumps({'items': [{'id': 1, 'text': '已完成', 'done': True}]}).encode()
        session.files[state_path] = original
        batch = 'a' * 64
        payload = todo.payload_for(['新事项'], batch)
        with patch.object(todo, 'RemotePath', MemoryPath):
            session.fail_after_publish = True
            with self.assertRaises(ConnectionError):
                todo.send_batch(session, batch, payload)
            session.fail_after_publish = False
            self.assertIn('已送达', todo.send_batch(session, batch, payload))
        self.assertEqual(len(session.published), 1)
        self.assertEqual(session.files[state_path], original)
        self.assertFalse(any('.tmp-' in path for path in session.files))

    def test_received_batch_does_not_write_and_preserves_completion(self):
        session = FakeSession()
        batch = 'a' * 64
        session.files['/mnt/us/koreader/settings/kindle-todo.json'] = json.dumps({
            'processed_batches': {batch: True}, 'items': [{'text': 'done', 'done': True}]}).encode()
        before = dict(session.files)
        self.assertIn('没有重复添加', todo.send_batch(session, batch, todo.payload_for(['done'], batch)))
        self.assertEqual(session.files, before)

    def test_conflicting_queue_is_never_overwritten(self):
        from unittest.mock import patch
        session = FakeSession()
        batch = 'a' * 64
        final = str(todo.INBOX / (batch + '.json'))
        session.files[final] = b'existing data'
        with patch.object(todo, 'RemotePath', MemoryPath), self.assertRaises(RuntimeError):
            todo.send_batch(session, batch, todo.payload_for(['new'], batch))
        self.assertEqual(session.files[final], b'existing data')

    def test_missing_receiver_refuses_writes(self):
        session = FakeSession()
        session.files.clear()
        with self.assertRaisesRegex(RuntimeError, '安装新版'):
            todo.send_batch(session, 'a' * 64, todo.payload_for(['new'], 'a' * 64))
        self.assertEqual(session.files, {})


if __name__ == '__main__':
    unittest.main()
