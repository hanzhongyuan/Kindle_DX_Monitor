"""Offline safety and rollback tests; never connect to a real Kindle."""
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from PIL import Image

spec = importlib.util.spec_from_file_location("messageboard", Path(__file__).with_name("kindle-messageboard.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
TOKEN = "a" * 32


class ImageTests(unittest.TestCase):
    def test_rgb_is_converted_to_opaque_grayscale_without_changing_dimensions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "message.png"
            Image.new("RGB", (824, 1200), "white").save(path)
            payload = m.prepare_image(path)
            with Image.open(io.BytesIO(payload)) as result:
                self.assertEqual(result.size, (824, 1200))
                self.assertEqual(result.mode, "L")
                self.assertEqual(result.getpixel((0, 0)), 255)

    def test_transparent_and_wrong_size_images_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "message.png"
            for picture in (Image.new("RGBA", (824, 1200), (255, 255, 255, 0)),
                            Image.new("RGB", (1200, 824), "white")):
                picture.save(path)
                with self.assertRaises(ValueError):
                    m.prepare_image(path)

    def test_non_png_is_rejected_even_with_png_extension(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "message.png"
            Image.new("RGB", (824, 1200), "white").save(path, "JPEG")
            with self.assertRaises(ValueError):
                m.prepare_image(path)

    def test_oversized_input_is_rejected_before_decoding(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "message.png"
            path.write_bytes(b"x" * (m.MAX_IMAGE_BYTES + 1))
            with self.assertRaises(ValueError):
                m.prepare_image(path)


class OwnershipTests(unittest.TestCase):
    def test_framework_flags_accept_dxg_hex_format_and_reject_starting_states(self):
        self.assertTrue(m.framework_ready("00000000\n00000000\n00000001\n"))
        self.assertTrue(m.framework_ready("0 0 1"))
        for flags in ("00000001 00000000 00000000", "00000000 00000001 00000000",
                      "0 0 0", "0 0 2", "0 0", "0 0 1 extra", "0 0 nope"):
            self.assertFalse(m.framework_ready(flags))

    def make_board(self, mounted=False, marker=TOKEN, model="B009", linkss=False):
        board = m.MessageBoard(Mock(), {"owner_token": TOKEN})
        def run(script, timeout=12):
            if script == "cat /etc/prettyversion.txt":
                return "Kindle 2.5.8 (~~otaVersion~~)\n"
            if script.startswith("dd if=/proc/usid"):
                return model
            if "/mnt/us/linkss" in script:
                return "present" if linkss else ""
            if script == "cat /proc/mounts":
                return "fsp " + m.TARGET + " fuse.fsp rw 0 0\n" if mounted else ""
            if m.MARKER in script:
                return marker
            if script == "lipc-get-prop com.lab126.powerd state":
                return "active"
            raise AssertionError(script)
        board.run = Mock(side_effect=run)
        return board

    def test_bind_mount_with_fsp_source_is_recognized_by_both_markers(self):
        board = self.make_board(mounted=True)
        self.assertTrue(board.inspect()["mounted"])
        self.assertEqual(sum(m.MARKER in call.args[0] for call in board.run.call_args_list), 2)

    def test_foreign_mount_and_other_models_are_rejected(self):
        for board in (self.make_board(mounted=True, marker="other"),
                      self.make_board(model="B008"), self.make_board(linkss=True)):
            with self.assertRaises(RuntimeError):
                board.inspect()

    def test_stacked_mounts_are_rejected(self):
        board = self.make_board(mounted=True)
        original = board.run.side_effect
        board.run.side_effect = lambda script, **kwargs: original(script, **kwargs) * (2 if script == "cat /proc/mounts" else 1)
        with self.assertRaises(RuntimeError):
            board.inspect()

    def test_live_viewer_prevents_any_framework_change(self):
        board = m.MessageBoard(Mock(), {})
        board.run = Mock(return_value="active")
        with self.assertRaises(RuntimeError):
            board.ensure_viewer_stopped()
        self.assertEqual(board.run.call_count, 1)

    def test_arbitrary_owner_token_cannot_become_shell_text(self):
        for bad in ("'; reboot", "a" * 31, 3, ["a"]):
            with self.assertRaises(ValueError):
                m.owner_token({"owner_token": bad})


class TransactionTests(unittest.TestCase):
    def board(self):
        board = m.MessageBoard(Mock(), {"owner_token": TOKEN})
        board.inspect = Mock(return_value={"mounted": False})
        board.ensure_viewer_stopped = Mock()
        board.ensure_owned_directory = Mock()
        board.upload = Mock(return_value="digest")
        board.run = Mock(return_value="")
        board.stop_framework = Mock()
        board.start_framework = Mock()
        board.sleep = Mock()
        return board

    def test_install_stops_framework_before_changing_visible_picture(self):
        board = self.board()
        board.inspect.side_effect = [{"mounted": False}, {"mounted": True}, {"mounted": True}]
        calls = Mock()
        for name in ("run", "stop_framework", "start_framework", "sleep"):
            calls.attach_mock(getattr(board, name), name)
        with patch.object(m, "save_state"):
            board.install(b"image")
        sequence = [call[0] for call in calls.mock_calls]
        self.assertLess(sequence.index("stop_framework"), next(i for i, call in enumerate(calls.mock_calls)
                       if call[0] == "run" and call.args[0].startswith("mv ")))
        self.assertLess(sequence.index("start_framework"), sequence.index("sleep"))
        board.run.assert_any_call("mount --bind " + m.SOURCE + " " + m.TARGET)

    def test_update_does_not_stack_a_second_bind_mount(self):
        board = self.board()
        board.inspect.return_value = {"mounted": True}
        with patch.object(m, "save_state"):
            board.install(b"image")
        self.assertFalse(any(call.args[0].startswith("mount ") for call in board.run.call_args_list))

    def test_failed_new_install_unmounts_owned_overlay_and_restarts_framework(self):
        board = self.board()
        board.inspect.side_effect = [{"mounted": False}, {"mounted": True}, {"mounted": True}]
        board.start_framework.side_effect = [RuntimeError("startup failed"), None]
        with patch.object(m, "save_state"), self.assertRaisesRegex(RuntimeError, "startup failed"):
            board.install(b"image")
        board.run.assert_any_call("umount " + m.TARGET)
        self.assertEqual(board.start_framework.call_count, 2)

    def test_rollback_starts_framework_even_when_stopping_or_mount_checks_fail(self):
        board = self.board()
        board.stop_framework.side_effect = RuntimeError("stop failure")
        failures = board.rollback(False, False)
        board.start_framework.assert_called_once()
        self.assertEqual(failures, ["stop failure"])

    def test_restore_without_owned_mount_does_not_restart_framework(self):
        board = self.board()
        with patch.object(m, "save_state"):
            board.restore()
        board.stop_framework.assert_not_called()
        board.start_framework.assert_not_called()
        self.assertFalse(board.state["enabled"])

    def test_sleep_wakes_and_checks_state_before_single_button_toggle(self):
        board = self.board()
        board.sleep = m.MessageBoard.sleep.__get__(board)
        board.wait_for = Mock()
        calls = Mock()
        calls.attach_mock(board.run, "run")
        calls.attach_mock(board.wait_for, "wait_for")
        board.sleep()
        self.assertEqual(calls.mock_calls[0].args[0], "lipc-get-prop com.lab126.powerd state")
        self.assertEqual(calls.mock_calls[1].args[0], "lipc-set-prop -i com.lab126.powerd wakeUp 1")
        self.assertEqual(calls.mock_calls[2].args[1], "active")
        self.assertEqual(calls.mock_calls[3].args[0], "powerd_test -p")
        self.assertEqual(calls.mock_calls[4].args[1], "screenSaver")

    def test_sleep_already_active_skips_wakeup_and_toggles_once(self):
        board = self.board()
        board.sleep = m.MessageBoard.sleep.__get__(board)
        board.run.return_value = "active\n"
        board.wait_for = Mock()
        board.sleep()
        self.assertEqual([call.args[0] for call in board.run.call_args_list],
                         ["lipc-get-prop com.lab126.powerd state", "powerd_test -p"])
        board.wait_for.assert_called_once_with("lipc-get-prop com.lab126.powerd state", "screenSaver", 15,
                                               "Kindle 尚未进入休眠封面。")

    def test_wakeup_failure_is_not_ignored_or_followed_by_toggle(self):
        board = self.board()
        board.sleep = m.MessageBoard.sleep.__get__(board)
        board.run.side_effect = ["screenSaver", RuntimeError("wake failed")]
        board.wait_for = Mock()
        with self.assertRaisesRegex(RuntimeError, "wake failed"):
            board.sleep()
        self.assertFalse(any(call.args[0] == "powerd_test -p" for call in board.run.call_args_list))

    def test_upload_checksum_mismatch_is_rejected(self):
        board = m.MessageBoard(Mock(), {"owner_token": TOKEN})
        channel = board.client.get_transport.return_value.open_session.return_value
        channel.send.return_value = 3
        channel.exit_status_ready.return_value = True
        channel.recv_exit_status.return_value = 0
        board.run = Mock(return_value="0" * 32 + "  upload.png")
        with patch.object(m.display, "exec_request"), self.assertRaisesRegex(RuntimeError, "校验失败"):
            board.upload(b"abc")
        channel.close.assert_called_once()
        self.assertNotEqual("0" * 32, hashlib.md5(b"abc").hexdigest())


if __name__ == "__main__":
    unittest.main()
