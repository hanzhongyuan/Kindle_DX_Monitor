"""Run the real store under Lua 5.1 and LuaJIT; never access a Kindle.

Optional test runtime (kept outside production dependencies):
  python -m pip install --target runtime/test-deps lupa
  python -m unittest discover -s scripts -p test_pw2_todo_store.py -v

The JSON codec is dependency-injected using Python's JSON implementation, with
rapidjson's array markers. On Windows, os.replace provides Kindle/POSIX rename
semantics. All Lua file reads, writes, validation and transactions are real.
"""
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "runtime" / "test-deps"))
try:
    from lupa.lua51 import LuaRuntime as Lua51
    from lupa.luajit21 import LuaRuntime as LuaJIT
except ImportError:
    Lua51 = LuaJIT = None

SOURCE = PROJECT / "device/pw2-todo/koreader/plugins/kindletodo.koplugin/store.lua"
INBOX_SOURCE = SOURCE.with_name("inbox.lua")


class StoreChecks:
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="kindle-todo-test-")
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "kindle-todo.json"
        self.backup = Path(str(self.path) + ".bak")
        self.lua = self.runtime(encoding=None, unpack_returned_tuples=True)
        self.get_meta = self.lua.eval(b"getmetatable")
        self.set_meta = self.lua.eval(b"setmetatable")
        self.lua.globals().py_encode = self.encode
        self.lua.globals().py_decode = self.decode
        self.lua.globals().py_replace = self.replace
        self.lua.execute(b"""
            codec = {
                encode = function(value, options) return py_encode(value) end,
                decode = function(value) return py_decode(value) end,
            }
            package.loaded.rapidjson = codec
            -- The KOReader-only ffi helper is a stub, so tests do not claim to
            -- validate power-loss durability or the Kindle kernel's fsync.
            sync_calls = 0
            package.loaded['ffi/util'] = {
                fsyncOpenedFile = function(file) sync_calls = sync_calls + 1; return true end,
                fsyncDirectory = function(path) return true end,
            }
            os.rename = function(source, target) return py_replace(source, target) end
        """)
        self.codec = self.lua.globals().codec
        self.module = self.lua.execute(SOURCE.read_bytes())
        self.store = self.open_store()

    @staticmethod
    def replace(source, target):
        try:
            os.replace(os.fsdecode(source), os.fsdecode(target))
            return True
        except OSError as error:
            return None, str(error).encode()

    def to_lua(self, value):
        if isinstance(value, dict):
            result = self.lua.table()
            for key, child in value.items():
                result[key.encode()] = self.to_lua(child)
            return self.set_meta(result, self.lua.table_from({b"__jsontype": b"object"}))
        if isinstance(value, list):
            result = self.lua.table()
            for index, child in enumerate(value, 1):
                result[index] = self.to_lua(child)
            return self.set_meta(result, self.lua.table_from({b"__jsontype": b"array"}))
        if isinstance(value, str):
            return value.encode("utf-8")
        if value is None:
            return self.lua.table_from({b"__json_null": True})
        return value

    def to_python(self, value):
        if isinstance(value, bytes):
            return value.decode("utf-8")
        if hasattr(value, "items"):
            meta = self.get_meta(value)
            if meta is not None and meta[b"__jsontype"] == b"array":
                return [self.to_python(value[index]) for index in range(1, len(value) + 1)]
            return {self.to_python(key): self.to_python(child) for key, child in value.items()}
        return value

    def encode(self, value):
        return json.dumps(self.to_python(value), ensure_ascii=False, allow_nan=False).encode("utf-8")

    def decode(self, value):
        return self.to_lua(json.loads(value))

    def open_store(self, injected=True):
        result = self.module.open(str(self.path).encode(), self.codec if injected else None)
        if isinstance(result, tuple):
            self.fail(f"Open failed: {result}")
        return result

    def operation(self, name, *arguments, store=None):
        target = store if store is not None else self.store
        converted = [arg.encode("utf-8") if isinstance(arg, str) else arg for arg in arguments]
        return target[name.encode()](target, *converted)

    def ok(self, name, *arguments):
        self.assertIs(self.operation(name, *arguments), True)

    def rejected(self, name, *arguments, contains=None):
        result = self.operation(name, *arguments)
        self.assertIsInstance(result, tuple, result)
        self.assertIsNone(result[0])
        self.assertIsInstance(result[1], bytes)
        if contains:
            self.assertIn(contains, result[1].decode("utf-8"))

    def state(self):
        return {
            "items": self.to_python(self.store[b"items"]),
            "font_size": self.store.font_size,
            "compact_mode": self.store.compact_mode,
            "next_id": self.store.next_id,
        }

    def disk(self):
        return json.loads(self.path.read_bytes())

    def test_new_store_and_default_rapidjson_loading(self):
        self.assertEqual(self.state(), {"items": [], "font_size": 28, "compact_mode": False, "next_id": 1})
        self.assertFalse(self.path.exists())
        self.assertEqual(self.open_store(injected=False).font_size, 28)
        self.ok("set_font_size", 18)
        self.assertEqual(self.disk()["items"], [])
        self.assertGreater(self.lua.globals().sync_calls, 0)

    def test_crud_persists_ids_completion_and_exact_previous_backup(self):
        self.ok("add", "  买牛奶 🥛  ")
        first = self.path.read_bytes()
        self.assertFalse(self.backup.exists())
        self.assertEqual(self.disk()["items"], [{"id": 1, "text": "买牛奶 🥛", "done": False}])
        self.ok("add", "读书")
        self.assertEqual(self.backup.read_bytes(), first)
        before = self.path.read_bytes()
        self.ok("toggle", 1)
        self.assertEqual(self.backup.read_bytes(), before)
        self.ok("edit", 1, "买两瓶牛奶")
        self.assertTrue(self.disk()["items"][0]["done"])
        self.ok("remove", 1)
        self.ok("add", "散步")
        self.assertEqual([item["id"] for item in self.disk()["items"]], [2, 3])
        reloaded = self.open_store()
        self.assertEqual(self.to_python(reloaded[b"items"]), self.state()["items"])
        self.assertEqual(reloaded.next_id, 4)

    def test_deleting_last_item_persists_an_empty_json_array(self):
        self.ok("add", "任务")
        self.ok("remove", 1)
        self.assertEqual(self.disk()["items"], [])
        self.assertEqual(len(self.open_store()[b"items"]), 0)

    def test_import_appends_bom_crlf_cr_and_ignores_blank_lines(self):
        self.ok("add", "已有事项")
        self.ok("import_text", "\ufeff第一项\r\n\r\n   \n第二项\r第三项\n")
        self.assertEqual([item["text"] for item in self.disk()["items"]], ["已有事项", "第一项", "第二项", "第三项"])
        self.assertTrue(all(not item["done"] for item in self.disk()["items"]))
        self.rejected("import_text", " \r\n\t\n", contains="没有")

    def test_count_limit_and_atomic_import_validation(self):
        self.ok("import_text", "\n".join(f"任务 {i}" for i in range(199)))
        before, memory = self.path.read_bytes(), self.state()
        self.rejected("import_text", "第200项\n第201项", contains="200")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.state(), memory)
        self.rejected("import_text", "先有效\n" + "长" * 301, contains="300")
        self.assertEqual(self.state(), memory)
        self.ok("add", "最后一项")
        self.rejected("add", "超出", contains="200")
        self.assertEqual(len(self.disk()["items"]), 200)

    def test_utf8_codepoint_boundary_and_invalid_sequences(self):
        self.ok("add", "中" * 300)
        self.ok("add", "😀" * 300)
        for value in ("中" * 301, "😀" * 301):
            self.rejected("add", value, contains="300")
        for value in (b"\xc0\xaf", b"\xed\xa0\x80", b"\xf4\x90\x80\x80", b"\x80", b"\xe4\xb8"):
            self.rejected("add", value, contains="UTF-8")
        for value in ("", "   ", "first\nsecond", "tab\ttext", "nul\x00text"):
            self.rejected("add", value)
        self.assertEqual(len(self.disk()["items"]), 2)

    def test_font_size_and_unknown_ids_reject_without_writing(self):
        self.ok("add", "内容")
        self.ok("set_font_size", 48)
        before, memory = self.path.read_bytes(), self.state()
        for value in (17, 49, 18.5, "28", False, float("inf"), float("nan")):
            self.rejected("set_font_size", value, contains="字号")
        for name, args in (("toggle", (999,)), ("edit", (999, "编辑")), ("remove", (999,))):
            self.rejected(name, *args, contains="未找到")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.state(), memory)

    def test_compact_mode_legacy_default_and_enable_disable_survive_reopening(self):
        legacy = {"schema": 1, "font_size": 32, "next_id": 2,
                  "items": [{"id": 1, "text": "保留已完成事项", "done": True}]}
        self.path.write_text(json.dumps(legacy), encoding="utf-8")
        before = self.path.read_bytes()
        self.store = self.open_store()
        self.assertIs(self.store.compact_mode, False)
        self.assertEqual(self.path.read_bytes(), before)
        for enabled in (True, False):
            self.ok("set_compact_mode", enabled)
            self.assertIs(self.disk()["compact_mode"], enabled)
            self.store = self.open_store()
            self.assertIs(self.store.compact_mode, enabled)
            self.assertEqual(self.disk()["items"], legacy["items"])
            self.assertEqual(self.store.font_size, 32)

    def test_compact_mode_survives_edits_imports_font_changes_and_wifi_batches(self):
        self.ok("set_compact_mode", True)
        for name, arguments in (("add", ("待办",)), ("toggle", (1,)), ("edit", (1, "修改后",)),
                                ("import_text", ("导入一\n导入二",)), ("set_font_size", (40,)), ("remove", (2,))):
            self.ok(name, *arguments)
            self.assertIs(self.disk()["compact_mode"], True)
            self.store = self.open_store()
            self.assertIs(self.store.compact_mode, True)
        self.assertEqual(self.batch(1, ["批次导入"]), (True, 1))
        self.setup_inbox()
        self.queue(2, ["Wi-Fi收件"])
        self.assertEqual(self.receive(), (1, None, 1))
        self.store = self.open_store()
        self.assertIs(self.store.compact_mode, True)
        self.assertTrue(self.disk()["items"][0]["done"])
        self.assertEqual(self.store.font_size, 40)
        self.assertEqual(self.batch(1, ["批次导入"]), (True, 0))
        self.assertIs(self.disk()["compact_mode"], True)

    def test_compact_mode_failed_commit_preserves_disk_memory_and_can_retry(self):
        self.ok("add", "任务")
        self.lua.globals().main_path = str(self.path).encode()
        self.lua.execute(b"""
            saved_rename = os.rename
            os.rename = function(source, target)
                if reject_compact and target == main_path then return nil, 'injected compact commit failure' end
                return saved_rename(source, target)
            end
        """)
        for enabled in (True, False):
            before, memory = self.path.read_bytes(), self.state()
            self.lua.globals().reject_compact = True
            self.rejected("set_compact_mode", enabled, contains="未保存")
            self.assertEqual(self.path.read_bytes(), before)
            self.assertEqual(self.state(), memory)
            self.assertIs(self.open_store().compact_mode, not enabled)
            self.lua.globals().reject_compact = False
            self.ok("set_compact_mode", enabled)
            self.assertIs(self.open_store().compact_mode, enabled)

    def test_compact_mode_rejects_non_boolean_input_and_saved_values(self):
        self.ok("set_compact_mode", True)
        before, memory = self.path.read_bytes(), self.state()
        for value in (None, 0, 1, "true", "false", self.to_lua({})):
            self.rejected("set_compact_mode", value, contains="布尔值")
            self.assertEqual(self.path.read_bytes(), before)
            self.assertEqual(self.state(), memory)
        invalid = self.disk()
        invalid["compact_mode"] = "true"
        self.path.write_text(json.dumps(invalid), encoding="utf-8")
        invalid_bytes = self.path.read_bytes()
        opened, error = self.module.open(str(self.path).encode(), self.codec)
        self.assertIsNone(opened)
        self.assertIn("布尔值", error.decode())
        self.assertEqual(self.path.read_bytes(), invalid_bytes)

    def test_corrupt_primary_is_never_replaced_or_automatically_restored(self):
        self.ok("add", "原事项")
        self.ok("toggle", 1)
        saved_backup = self.backup.read_bytes()
        for invalid in (b"", b"{bad", b"null", b"false", b"[]"):
            self.path.write_bytes(invalid)
            result = self.module.open(str(self.path).encode(), self.codec)
            self.assertIsNone(result[0])
            self.assertIn("原文件未改动", result[1].decode())
            self.assertEqual(self.path.read_bytes(), invalid)
            self.assertEqual(self.backup.read_bytes(), saved_backup)

    def test_invalid_schema_items_and_next_id_are_rejected(self):
        base = {"schema": 1, "font_size": 28, "next_id": 2, "items": [{"id": 1, "text": "条目", "done": False}]}
        variations = [
            {"schema": 2}, {"font_size": 49}, {"next_id": 1}, {"next_id": 2.5},
            {"items": {}}, {"items": [None]}, {"items": [{"id": 1, "text": "", "done": False}]},
            {"items": [{"id": 1, "text": "内容", "done": "false"}]},
            {"items": base["items"] * 2},
        ]
        for replacement in variations:
            self.path.write_text(json.dumps(dict(base, **replacement)), encoding="utf-8")
            before = self.path.read_bytes()
            self.assertIsNone(self.module.open(str(self.path).encode(), self.codec)[0])
            self.assertEqual(self.path.read_bytes(), before)

    def test_missing_primary_with_backup_requires_recovery(self):
        self.backup.write_text("previous contents", encoding="utf-8")
        result = self.module.open(str(self.path).encode(), self.codec)
        self.assertIsNone(result[0])
        self.assertIn("备份", result[1].decode())
        self.assertFalse(self.path.exists())

    def test_unreadable_file_does_not_become_an_empty_store(self):
        self.lua.execute(b"io.open = function() return nil, 'permission denied', 13 end")
        result = self.module.open(str(self.path).encode(), self.codec)
        self.assertIsNone(result[0])
        self.assertIn(b"permission denied", result[1])

    def test_external_changes_and_stale_second_store_are_not_overwritten(self):
        self.ok("add", "原始")
        stale = self.open_store()
        self.ok("add", "更新")
        latest = self.path.read_bytes()
        self.assertIsNone(self.operation("toggle", 1, store=stale)[0])
        self.assertEqual(self.path.read_bytes(), latest)
        memory = self.state()
        self.path.write_bytes(b"externally corrupted")
        self.rejected("toggle", 1, contains="修改或损坏")
        self.assertEqual(self.state(), memory)
        self.assertEqual(self.path.read_bytes(), b"externally corrupted")

    def test_encoder_failure_preserves_memory_and_primary(self):
        self.ok("add", "保留")
        before, memory = self.path.read_bytes(), self.state()
        self.lua.execute(b"codec.encode = function() return nil, 'encoder failure' end")
        self.rejected("toggle", 1, contains="编码")
        self.assertEqual(self.state(), memory)
        self.assertEqual(self.path.read_bytes(), before)

    def test_io_failures_preserve_memory_primary_and_clean_temporary_files(self):
        self.ok("add", "保留")
        self.ok("add", "另一个")
        before, memory = self.path.read_bytes(), self.state()
        self.lua.globals().target_path = str(self.path).encode()
        self.lua.globals().old_items = self.store[b"items"]
        self.lua.execute(b"""
            original_open, original_rename = io.open, os.rename
            io.open = function(path, mode)
                if mode ~= 'wb' then return original_open(path, mode) end
                if fault == 'open' then return nil, 'injected open failure', 28 end
                if fault == 'backup' and path:find('.bak.tmp-', 1, true) then
                    return nil, 'injected backup failure', 28
                end
                local file, err = original_open(path, mode)
                if not file then return nil, err end
                return {
                    write = function(self, data)
                        if fault == 'write' then file:write(data:sub(1, 5)); return nil, 'injected write failure' end
                        return file:write(data)
                    end,
                    flush = function(self)
                        if fault == 'flush' then return nil, 'injected flush failure' end
                        return file:flush()
                    end,
                    close = function(self)
                        local ok, close_err = file:close()
                        if fault == 'close' then return nil, 'injected close failure' end
                        return ok, close_err
                    end,
                }
            end
            os.rename = function(source, target)
                if fault == 'rename' and target == target_path then return nil, 'injected rename failure' end
                if fault == 'backup_rename' and target == target_path .. '.bak' then return nil, 'injected backup rename failure' end
                if fault == 'throw' then error('injected rename exception') end
                return original_rename(source, target)
            end
            package.loaded['ffi/util'].fsyncOpenedFile = function()
                if fault == 'fsync' then return false, 'injected sync failure' end
                return true
            end
            package.loaded['ffi/util'].fsyncDirectory = function()
                if fault == 'backup_sync' then return false, 'injected directory sync failure' end
                return true
            end
        """)
        for fault in ("open", "write", "flush", "close", "fsync", "backup", "backup_rename", "backup_sync", "rename", "throw"):
            with self.subTest(fault=fault):
                self.lua.globals().fault = fault.encode()
                self.rejected("toggle", 1)
                self.assertEqual(self.state(), memory)
                self.assertEqual(self.path.read_bytes(), before)
                self.lua.globals().current_items = self.store[b"items"]
                self.assertTrue(self.lua.eval(b"old_items == current_items"))
                self.assertEqual(list(self.path.parent.glob("*.tmp-*")), [])
        self.lua.globals().fault = None
        self.ok("toggle", 1)
        self.assertTrue(self.disk()["items"][0]["done"])

    @staticmethod
    def batch_id(number):
        return f"{number:064x}"

    def batch(self, number, items):
        return self.operation("import_batch", self.batch_id(number), self.to_lua(items))

    def test_batch_appends_preserving_completion_font_and_persistent_deduplication(self):
        self.ok("add", "已有完成事项")
        self.ok("toggle", 1)
        self.ok("set_font_size", 42)
        self.assertEqual(self.batch(1, ["电脑事项", "买牛奶 🥛"]), (True, 2))
        self.assertEqual(self.disk()["items"][0], {"id": 1, "text": "已有完成事项", "done": True})
        self.assertEqual(self.disk()["font_size"], 42)
        self.assertTrue(self.disk()["processed_batches"][self.batch_id(1)])
        self.ok("remove", 2)
        self.ok("toggle", 3)
        self.ok("import_text", "本机新增")
        self.store = self.open_store()
        before, memory = self.path.read_bytes(), self.state()
        self.assertEqual(self.batch(1, ["电脑事项", "买牛奶 🥛"]), (True, 0))
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.state(), memory)
        self.assertEqual(self.batch(2, ["电脑事项"]), (True, 1))
        self.assertTrue(self.disk()["items"][0]["done"])
        self.assertTrue(self.disk()["items"][1]["done"])
        self.assertFalse(self.disk()["items"][-1]["done"])

    def test_legacy_store_accepts_batches_and_rejects_invalid_packets_atomically(self):
        self.path.write_text(json.dumps({"schema": 1, "font_size": 28, "next_id": 2,
            "items": [{"id": 1, "text": "旧版任务", "done": True}]}), encoding="utf-8")
        self.store = self.open_store()
        before, memory = self.path.read_bytes(), self.state()
        for batch_id, items in (("a", ["事项"]), ("A" * 64, ["事项"]), (self.batch_id(1), []),
                                (self.batch_id(1), {}), (self.batch_id(1), ["先有效", "长" * 301]),
                                (self.batch_id(1), ["事项"] * 201), (self.batch_id(1), [{"text": "事项"}])):
            self.rejected("import_batch", batch_id, self.to_lua(items))
            self.assertEqual(self.path.read_bytes(), before)
            self.assertEqual(self.state(), memory)
        self.assertEqual(self.batch(1, ["新任务"]), (True, 1))
        self.assertTrue(self.disk()["items"][0]["done"])

    def test_batch_commit_failure_does_not_consume_id_and_can_retry(self):
        self.ok("add", "完成项")
        self.ok("toggle", 1)
        before, memory = self.path.read_bytes(), self.state()
        self.lua.globals().main_path = str(self.path).encode()
        self.lua.execute(b"""
            saved_rename = os.rename
            os.rename = function(source, target)
                if target == main_path then return nil, 'injected commit failure' end
                return saved_rename(source, target)
            end
        """)
        self.rejected("import_batch", self.batch_id(1), self.to_lua(["新任务"]))
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.state(), memory)
        self.assertIsNone(self.store._processed_batches[self.batch_id(1).encode()])
        self.lua.execute(b"os.rename = saved_rename")
        self.assertEqual(self.batch(1, ["新任务"]), (True, 1))
        self.assertTrue(self.disk()["items"][0]["done"])

    def test_batch_history_limit_never_discards_retry_ids(self):
        history = {self.batch_id(index): True for index in range(1, 4097)}
        self.path.write_text(json.dumps({"schema": 1, "font_size": 28, "next_id": 2,
            "items": [{"id": 1, "text": "保留", "done": True}], "processed_batches": history}), encoding="utf-8")
        self.store = self.open_store()
        before = self.path.read_bytes()
        self.assertEqual(self.batch(1, ["旧批重试"]), (True, 0))
        self.rejected("import_batch", self.batch_id(4097), self.to_lua(["新批次"]), contains="4096")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(len(self.disk()["processed_batches"]), 4096)
        self.path.write_bytes(b"corrupted after loading")
        self.rejected("import_batch", self.batch_id(1), self.to_lua(["旧批重试"]), contains="修改或损坏")

    def setup_inbox(self):
        self.inbox_path = self.path.parent / "kindle-todo-inbox"
        self.lua.globals().py_lstat = self.lstat
        self.lua.globals().py_listdir = lambda path: self.to_lua(sorted(os.listdir(os.fsdecode(path))))
        self.lua.execute(b"""
            package.loaded['libs/libkoreader-lfs'] = {
                symlinkattributes = function(path) return py_lstat(path) end,
                dir = function(path)
                    local names, index = py_listdir(path), 0
                    local state = { close = function(self) self.closed = true end }
                    return function(state)
                        if state.closed then return nil end
                        index = index + 1
                        return names[index]
                    end, state
                end,
            }
        """)
        self.inbox = self.lua.execute(INBOX_SOURCE.read_bytes())

    def lstat(self, path):
        try:
            info = os.lstat(os.fsdecode(path))
        except OSError as error:
            return None, str(error).encode(), error.errno
        mode = "link" if stat.S_ISLNK(info.st_mode) else "directory" if stat.S_ISDIR(info.st_mode) else "file" if stat.S_ISREG(info.st_mode) else "other"
        # Windows can expose 128-bit file IDs; strings preserve equality without
        # overflowing Lua 5.1's number conversion in this filesystem adapter.
        return self.to_lua({"mode": mode, "size": info.st_size, "dev": str(info.st_dev),
                            "ino": str(info.st_ino), "modification": info.st_mtime})

    def queue(self, number, items):
        self.inbox_path.mkdir(exist_ok=True)
        path = self.inbox_path / (self.batch_id(number) + ".json")
        path.write_text(json.dumps({"schema": 1, "batch_id": self.batch_id(number), "items": items},
                                   ensure_ascii=False), encoding="utf-8")
        return path

    def receive(self, limit=None):
        return self.inbox.receive(self.store, self.inbox_path.as_posix().encode(), limit)

    def test_inbox_missing_directory_and_sorted_bounded_processing(self):
        self.setup_inbox()
        self.assertEqual(self.inbox.PROTOCOL, 1)
        self.assertEqual(self.receive(), (0, None, 0))
        for number in (3, 1, 2):
            self.queue(number, [f"批次{number}"])
        (self.inbox_path / "upload.tmp").write_text("unfinished", encoding="utf-8")
        self.assertEqual(self.receive(2), (2, None, 2))
        self.assertEqual([item["text"] for item in self.disk()["items"]], ["批次1", "批次2"])
        self.assertTrue((self.inbox_path / (self.batch_id(3) + ".json")).exists())
        self.assertEqual(self.receive(), (1, None, 1))
        self.assertEqual(self.receive(), (0, None, 0))
        self.assertTrue((self.inbox_path / "upload.tmp").exists())

    def test_inbox_delete_failure_retries_without_duplicates_or_losing_completion(self):
        self.setup_inbox()
        packet = self.queue(1, ["推送事项"])
        self.lua.globals().packet_path = packet.as_posix().encode()
        self.lua.execute(b"""
            original_remove = os.remove
            os.remove = function(path)
                if path == packet_path then return nil, 'injected unlink failure' end
                return original_remove(path)
            end
        """)
        added, error, processed = self.receive()
        self.assertEqual((added, processed), (1, 0))
        self.assertIn("重试不会重复", error.decode())
        self.assertTrue(packet.exists())
        self.ok("toggle", 1)
        self.store = self.open_store()
        self.lua.execute(b"os.remove = original_remove")
        self.assertEqual(self.receive(), (0, None, 1))
        self.assertFalse(packet.exists())
        self.assertEqual(self.disk()["items"], [{"id": 1, "text": "推送事项", "done": True}])

    def test_inbox_storage_failure_retains_packet_and_recovery_appends_once(self):
        self.setup_inbox()
        self.ok("add", "原任务")
        self.ok("toggle", 1)
        packet = self.queue(1, ["新任务"])
        before, memory = self.path.read_bytes(), self.state()
        self.lua.execute(b"""
            original_open = io.open
            io.open = function(path, mode)
                if mode == 'wb' then return nil, 'injected disk full', 28 end
                return original_open(path, mode)
            end
        """)
        added, error, processed = self.receive()
        self.assertEqual((added, processed), (0, 0))
        self.assertIn(b"injected disk full", error)
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.state(), memory)
        self.assertTrue(packet.exists())
        self.lua.execute(b"io.open = original_open")
        self.assertEqual(self.receive(), (1, None, 1))
        self.assertTrue(self.disk()["items"][0]["done"])

    def test_inbox_errors_preserve_partial_success_and_invalid_packet(self):
        self.setup_inbox()
        first = self.queue(1, ["正常事项"])
        second = self.queue(2, ["不会导入"])
        second.write_bytes(b"{invalid json")
        added, error, processed = self.receive()
        self.assertEqual((added, processed), (1, 1))
        self.assertIn("JSON", error.decode())
        self.assertFalse(first.exists())
        self.assertTrue(second.exists())
        self.assertEqual(len(self.disk()["items"]), 1)
        self.queue(2, ["修复后重试"])
        self.assertEqual(self.receive(), (1, None, 1))

    def test_inbox_rejects_oversized_mismatched_and_non_file_entries(self):
        self.setup_inbox()
        packet = self.queue(1, ["任务"])
        for contents in (b"x" * (256 * 1024 + 1), b"", json.dumps({"schema": 2, "batch_id": self.batch_id(1), "items": ["任务"]}).encode(),
                         json.dumps({"schema": 1, "batch_id": self.batch_id(2), "items": ["任务"]}).encode()):
            packet.write_bytes(contents)
            added, error, processed = self.receive()
            self.assertEqual((added, processed), (0, 0))
            self.assertIsNotNone(error)
            self.assertEqual(packet.read_bytes(), contents)
            self.assertFalse(self.path.exists())
        packet.unlink()
        packet.mkdir()
        self.assertIn("普通文件", self.receive()[1].decode())
        self.assertTrue(packet.is_dir())

    def test_inbox_rejects_symlink_directory_and_file_before_reading(self):
        self.setup_inbox()
        packet = self.queue(1, ["不得导入"])
        self.lua.globals().blocked_path = self.inbox_path.as_posix().encode()
        self.lua.execute(b"""
            local lfs = package.loaded['libs/libkoreader-lfs']
            local original = lfs.symlinkattributes
            lfs.symlinkattributes = function(path)
                if path == blocked_path then return {mode='link'} end
                return original(path)
            end
        """)
        self.assertIn("符号链接", self.receive()[1].decode())
        self.lua.globals().blocked_path = packet.as_posix().encode()
        self.assertIn("符号链接", self.receive()[1].decode())
        self.assertTrue(packet.exists())
        self.assertFalse(self.path.exists())

    def test_inbox_task_limit_keeps_batch_until_space_is_available(self):
        self.setup_inbox()
        self.ok("import_text", "\n".join(f"任务{i}" for i in range(200)))
        self.ok("toggle", 200)
        packet = self.queue(1, ["排队事项"])
        self.assertIn("200", self.receive()[1].decode())
        self.assertTrue(packet.exists())
        self.ok("remove", 1)
        self.assertEqual(self.receive(), (1, None, 1))
        self.assertEqual(len(self.disk()["items"]), 200)
        self.assertTrue(self.disk()["items"][-2]["done"])


@unittest.skipIf(Lua51 is None, "Install lupa into runtime/test-deps to run the actual Lua store")
class Lua51StoreTests(StoreChecks, unittest.TestCase):
    runtime = Lua51


@unittest.skipIf(LuaJIT is None, "Install lupa into runtime/test-deps to run the actual Lua store")
class LuaJITStoreTests(StoreChecks, unittest.TestCase):
    runtime = LuaJIT


if __name__ == "__main__":
    unittest.main()
