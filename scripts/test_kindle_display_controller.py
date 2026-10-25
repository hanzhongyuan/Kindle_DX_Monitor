"""Offline regression tests: no real SSH, network listener, or display changes."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("controller", Path(__file__).with_name("kindle-display-controller.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class ControllerTests(unittest.TestCase):
    def test_configured_powershell_relative_to_project_and_bom_supported(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            shell = project / "tools" / "pwsh.exe"
            shell.parent.mkdir()
            shell.touch()
            (project / "kindle-display.local.json").write_text(
                '{"PowerShellPath":"tools/pwsh.exe"}', encoding="utf-8-sig")
            with patch.object(c, "PROJECT", project):
                self.assertEqual(c.powershell_path(), str(shell.resolve()))

    def test_missing_configured_powershell_does_not_fall_back_to_other_executable(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            (project / "kindle-display.local.json").write_text(
                '{"PowerShellPath":"missing/pwsh.exe"}', encoding="utf-8")
            with patch.object(c, "PROJECT", project), patch.object(c.shutil, "which") as which:
                with self.assertRaises(RuntimeError):
                    c.powershell_path()
                which.assert_not_called()

    def test_powershell_config_rejects_arrays_and_non_string_values(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            for content in ('[]', '{"PowerShellPath":1}'):
                (project / "kindle-display.local.json").write_text(content, encoding="utf-8")
                with patch.object(c, "PROJECT", project), self.assertRaises(ValueError):
                    c.powershell_path()

    def test_framebuffer_values_are_strict(self):
        self.assertEqual(c.parse_size("824,1200\n"), (824, 1200))
        self.assertEqual(c.parse_size("1200,824\n"), (1200, 824))
        for text in ("800,600", "824,1200\n0", "824;1200", "99999,1200"):
            with self.assertRaises(ValueError):
                c.parse_size(text)

    def test_restart_stops_before_resizing_and_starting(self):
        controller = c.Controller()
        order = []
        controller.status = Mock()
        controller.stopping = Mock(return_value=False)
        controller.stop_viewer = lambda: order.append("stop")
        controller.resize = lambda size: order.append(("resize", size))
        with patch.object(c, "Viewer", side_effect=lambda client, **kwargs: order.append("start")):
            controller.restart_viewer((1200, 824))
        self.assertEqual(order, ["stop", ("resize", (1200, 824)), "start"])

    def test_stop_during_resize_never_starts_viewer(self):
        controller = c.Controller()
        controller.status = Mock()
        controller.stop_viewer = Mock()
        controller.resize = Mock()
        controller.stopping = Mock(side_effect=[False, True])
        with patch.object(c, "Viewer") as viewer, self.assertRaises(c.Stopping):
            controller.restart_viewer((1200, 824))
        viewer.assert_not_called()

    def test_connected_loop_never_polls_orientation(self):
        controller = c.Controller()
        controller.stopping = Mock(return_value=False)
        controller.status = Mock()
        controller.handle_requests = Mock(side_effect=lambda size: size)
        controller.take_orientation_request = Mock(return_value=None)
        client = Mock()
        count = [0]
        viewer = Mock()
        viewer.done.is_set.return_value = False
        restarted = []
        def apply(rotation, request, previous):
            restarted.append(rotation)
            controller.viewer = viewer
            return (824, 1200)
        def wait(seconds):
            count[0] += 1
            if count[0] > 3:
                raise c.Stopping()
        controller.apply_rotation = apply
        controller.wait = wait
        with patch.object(c, "open_ssh", return_value=client), patch.object(c, "ReverseForward"), \
                patch.object(c, "command", return_value="824,1200 0 0") as command, \
                self.assertRaises(c.Stopping):
            controller.connected()
        self.assertEqual(restarted, [controller.desired_rotation])
        self.assertEqual(command.call_count, 1)  # Initial connection only.
        self.assertEqual(controller.handle_requests.call_count, 3)

    def request_controller(self, folder):
        controller = c.Controller()
        controller.orientation_path = Path(folder) / "orientation"
        controller.refresh_path = Path(folder) / "refresh"
        controller.settings_path = Path(folder) / "settings.json"
        controller.status = Mock(side_effect=lambda **updates: controller.state.update(updates))
        controller.restart_viewer = Mock()
        controller.stopping = Mock(return_value=False)
        return controller

    def orientation_request(self, rotation):
        return dict(rotation=rotation, requested_at=c.utc_now(), request_id=str(c.uuid.uuid4()))

    def test_four_driver_states_distinguish_inverted_directions(self):
        for text, expected in [("824,1200 0 0", 0), ("824,1200 0 1", 180),
                               ("1200,824 1 0", 90), ("1200,824 1 1", 270)]:
            self.assertEqual(c.parse_driver_state(text)[0], expected)
        with self.assertRaises(ValueError):
            c.parse_driver_state("1200,824 0 1")

    def test_orientation_json_rejects_invalid_rotation(self):
        for rotation in (True, "90", 45, -90):
            with self.assertRaises(c.OrientationRequestError):
                c.parse_orientation_request(c.json.dumps(self.orientation_request(rotation)))

    def test_manual_same_size_180_request_is_applied(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            request = self.orientation_request(180)
            controller.orientation_path.write_text(c.json.dumps(request))
            controller.apply_rotation = Mock(return_value=(824, 1200))
            self.assertEqual(controller.handle_requests((824, 1200)), (824, 1200))
            controller.apply_rotation.assert_called_once_with(180, request)
            self.assertFalse(controller.orientation_path.exists())

    def test_manual_orientation_success_persists_only_confirmed_rotation(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            request = self.orientation_request(270)
            controller.configure_rotation = Mock(return_value=(1200, 824))
            self.assertEqual(controller.apply_rotation(270, request, previous=(0, (824, 1200))), (1200, 824))
            self.assertEqual(controller.state["rotation"], 270)
            self.assertEqual(controller.state["orientation_request_id"], request["request_id"])
            self.assertIsNotNone(controller.state["orientation_applied"])
            self.assertEqual(c.json.loads(controller.settings_path.read_text())["rotation"], 270)
            self.assertEqual(controller.desired_rotation, 270)

    def test_failed_direction_rolls_back_driver_display_and_viewer(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            request = self.orientation_request(270)
            controller.configure_rotation = Mock(side_effect=[RuntimeError("host resize failed"), (824, 1200)])
            controller.save_rotation = Mock()
            self.assertEqual(controller.apply_rotation(270, request, previous=(180, (824, 1200))), (824, 1200))
            self.assertEqual([call.args[0] for call in controller.configure_rotation.call_args_list], [270, 180])
            controller.save_rotation.assert_not_called()
            self.assertEqual(controller.state["connection"], "connected")
            self.assertEqual(controller.state["rotation"], 180)
            self.assertIsNone(controller.state["orientation_applied"])
            self.assertEqual(controller.state["orientation_request_id"], request["request_id"])
            self.assertIn("host resize failed", controller.state["last_error"])

    def test_direction_and_rollback_failure_stays_in_error_for_manual_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            controller.configure_rotation = Mock(side_effect=RuntimeError("device unavailable"))
            controller.apply_rotation(90, self.orientation_request(90), previous=(0, (824, 1200)))
            self.assertEqual(controller.state["connection"], "error")
            self.assertIsNone(controller.state["rotation"])
            self.assertEqual(controller.configure_rotation.call_count, 2)

    def test_direction_failure_before_mutation_keeps_viewer_and_request_id(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            request = self.orientation_request(90)
            controller.orientation_path.write_text(c.json.dumps(request))
            controller.stop_viewer = Mock()
            with patch.object(c, "read_driver_state", side_effect=TimeoutError("driver read failed")):
                controller.handle_requests((824, 1200))
            controller.stop_viewer.assert_not_called()
            self.assertEqual(controller.state["orientation_request_id"], request["request_id"])
            self.assertIsNone(controller.state["orientation_applied"])

    def test_rotation_is_set_and_verified_before_windows_resize_and_viewer(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            calls = []
            controller.wait = Mock()
            controller.stop_viewer = lambda: calls.append("stop")
            controller.set_driver_rotation = lambda angle: (calls.append(("driver", angle)) or (824, 1200))
            controller.resize = lambda size: calls.append(("resize", size))
            viewer = Mock()
            viewer.done.is_set.return_value = False
            with patch.object(c, "Viewer", side_effect=lambda *a, **kw: (calls.append("viewer") or viewer)):
                controller.configure_rotation(180)
            self.assertEqual(calls, ["stop", ("driver", 180), ("resize", (824, 1200)), "viewer"])

    def test_set_driver_rotation_verifies_inversion_even_at_same_size(self):
        controller = c.Controller()
        controller.wait = Mock()
        controller.stopping = Mock(return_value=False)
        with patch.object(c, "command") as command, \
                patch.object(c, "read_driver_state", side_effect=[(0, (824, 1200)), (180, (824, 1200))]) as read:
            self.assertEqual(controller.set_driver_rotation(180), (824, 1200))
        self.assertIn("14 1", command.call_args.args[1])
        self.assertEqual(read.call_count, 2)

    def test_stop_restores_original_driver_orientation_after_stopping_viewer(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            controller.client = Mock()
            controller.original_rotation = 270
            calls = []
            controller.stop_viewer = lambda: calls.append("stop")
            controller.set_driver_rotation = lambda rotation, **kw: calls.append(rotation)
            with patch.object(c, "command"):
                controller.disconnect(restore_original=True)
            self.assertEqual(calls, ["stop", 270])
            self.assertTrue(controller.state["original_orientation_restored"])

    def test_refresh_reloads_without_resizing_or_reading_orientation(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            controller.refresh_path.touch()
            with patch.object(c, "read_size") as read_size:
                self.assertEqual(controller.handle_requests((824, 1200)), (824, 1200))
            read_size.assert_not_called()
            controller.restart_viewer.assert_called_once_with((824, 1200), resize=False)
            self.assertFalse(controller.refresh_path.exists())
            self.assertIn("refresh_completed", controller.status.call_args.kwargs)

    def test_refresh_and_orientation_change_share_one_restart(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = self.request_controller(folder)
            controller.refresh_path.touch()
            request = self.orientation_request(90)
            controller.orientation_path.write_text(c.json.dumps(request))
            controller.apply_rotation = Mock(return_value=(1200, 824))
            controller.handle_requests((824, 1200))
            controller.apply_rotation.assert_called_once_with(90, request)
            controller.restart_viewer.assert_not_called()

    def test_existing_stop_file_is_never_removed(self):
        with tempfile.TemporaryDirectory() as folder:
            controller = c.Controller()
            controller.stop_path = Path(folder) / "stop"
            controller.status_path = Path(folder) / "state.json"
            controller.stop_path.write_text("stop")
            with patch.object(c, "open_ssh") as ssh:
                self.assertEqual(controller.run(), 0)
            ssh.assert_not_called()
            self.assertTrue(controller.stop_path.exists())
            self.assertEqual(controller.state["connection"], "stopped")

    def test_forward_rejects_other_interfaces_and_origins(self):
        forwarding = c.ReverseForward(Mock())
        for origin, server in [(("192.168.2.2", 10), ("127.0.0.1", 5905)),
                               (("127.0.0.1", 10), ("0.0.0.0", 5905)),
                               (("127.0.0.1", 10), ("127.0.0.1", 5906))]:
            channel = Mock()
            with patch.object(c.threading, "Thread") as thread:
                forwarding.accept(channel, origin, server)
            channel.close.assert_called_once()
            thread.assert_not_called()

    def test_forward_callback_returns_after_launching_thread(self):
        forwarding = c.ReverseForward(Mock())
        channel = Mock()
        with patch.object(c.threading, "Thread") as thread:
            forwarding.accept(channel, ("127.0.0.1", 42), ("127.0.0.1", 5905))
        thread.assert_called_once_with(target=forwarding.relay, args=(channel,), daemon=True, name="kindle-rfb-forward")
        thread.return_value.start.assert_called_once()

    def test_forward_request_uses_only_loopback_and_propagates_failure(self):
        transport = Mock()
        forwarding = c.ReverseForward(transport)
        forwarding.start()
        transport.request_port_forward.assert_called_once_with("127.0.0.1", 5905, handler=forwarding.accept)
        transport.request_port_forward.side_effect = RuntimeError("listener occupied")
        with self.assertRaisesRegex(RuntimeError, "listener occupied"):
            forwarding.start()

    def test_forward_timeout_closes_dedicated_transport(self):
        transport = Mock()
        forwarding = c.ReverseForward(transport)
        # Leave the request worker unstarted to simulate a missing SSH reply.
        with patch.object(c.threading, "Thread"), patch.object(c.time, "monotonic", side_effect=[0, 9]), \
                self.assertRaisesRegex(TimeoutError, "request timed out"):
            forwarding.start()
        transport.close.assert_called_once()

    def test_exec_request_success_and_failure_are_preserved(self):
        channel = Mock()
        c.exec_request(channel, "cat size")
        channel.exec_command.assert_called_once_with("cat size")
        channel.get_transport.assert_not_called()
        channel.exec_command.side_effect = RuntimeError("exec rejected")
        with self.assertRaisesRegex(RuntimeError, "exec rejected"):
            c.exec_request(channel, "cat size")

    def test_exec_request_timeout_closes_transport(self):
        channel = Mock()
        with patch.object(c.threading, "Thread"), patch.object(c.time, "monotonic", side_effect=[0, 6]), \
                self.assertRaisesRegex(TimeoutError, "SSH exec request timed out"):
            c.exec_request(channel, "cat size")
        channel.get_transport.return_value.close.assert_called_once()

    def test_exec_timeout_releases_blocked_request_worker(self):
        channel = Mock()
        released, exited = c.threading.Event(), c.threading.Event()
        def blocked_exec(text):
            try:
                if not released.wait(1):
                    raise AssertionError("request worker was not released")
                raise RuntimeError("transport closed")
            finally:
                exited.set()
        channel.exec_command.side_effect = blocked_exec
        channel.get_transport.return_value.close.side_effect = released.set
        with self.assertRaises(TimeoutError):
            c.exec_request(channel, "cat size", timeout=0.01)
        self.assertTrue(released.is_set())
        self.assertTrue(exited.wait(1))

    def test_exec_request_stop_closes_transport(self):
        channel = Mock()
        with patch.object(c.threading, "Thread"), self.assertRaises(c.Stopping):
            c.exec_request(channel, "cat size", stop_check=lambda: True)
        channel.get_transport.return_value.close.assert_called_once()

    def test_command_and_viewer_use_bounded_exec(self):
        client = Mock()
        channel = client.get_transport.return_value.open_session.return_value
        channel.recv_ready.return_value = False
        channel.recv_stderr_ready.return_value = False
        channel.exit_status_ready.return_value = True
        channel.recv_exit_status.return_value = 0
        stop_check = Mock(return_value=False)
        with patch.object(c, "exec_request") as request:
            self.assertEqual(c.command(client, "cat size", stop_check=stop_check), "")
            request.assert_called_once_with(channel, "cat size", timeout=5, stop_check=stop_check)
        with patch.object(c, "exec_request", side_effect=TimeoutError("missing acknowledgement")) as request, \
                patch.object(c.threading, "Thread") as thread, self.assertRaises(TimeoutError):
            c.Viewer(client, stop_check=stop_check)
        request.assert_called_once_with(channel, c.START_COMMAND, stop_check=stop_check)
        thread.assert_not_called()

    def test_size_line_does_not_wait_for_exit_status(self):
        client = Mock()
        channel = client.get_transport.return_value.open_session.return_value
        channel.recv_ready.side_effect = [True, False]
        channel.recv.return_value = b"1200,824\n"
        channel.recv_stderr_ready.return_value = False
        with patch.object(c, "exec_request"):
            self.assertEqual(c.read_size(client), (1200, 824))
        channel.exit_status_ready.assert_not_called()
        channel.recv_exit_status.assert_not_called()
        channel.close.assert_called_once()

    def test_restart_without_resize_preserves_host_display(self):
        controller = c.Controller()
        controller.status = Mock()
        controller.stop_viewer = Mock()
        controller.resize = Mock()
        controller.stopping = Mock(return_value=False)
        with patch.object(c, "Viewer") as viewer:
            controller.restart_viewer((1200, 824), resize=False)
        controller.stop_viewer.assert_called_once()
        controller.resize.assert_not_called()
        viewer.assert_called_once_with(controller.client, stop_check=controller.stopping)

    def test_ssh_uses_tcp_keepalive_and_existing_pinned_identity(self):
        paramiko = Mock()
        client = paramiko.SSHClient.return_value
        transport = client.get_transport.return_value
        with patch.dict(c.sys.modules, {"paramiko": paramiko}):
            self.assertIs(c.open_ssh(), client)
        client.load_host_keys.assert_called_once_with(str(c.RUNTIME / "kindle_known_hosts"))
        client.set_missing_host_key_policy.assert_called_once_with(paramiko.RejectPolicy.return_value)
        self.assertEqual(client.connect.call_args.kwargs["password"], "")
        self.assertFalse(client.connect.call_args.kwargs["look_for_keys"])
        self.assertFalse(client.connect.call_args.kwargs["allow_agent"])
        transport.sock.setsockopt.assert_called_once_with(c.socket.SOL_SOCKET, c.socket.SO_KEEPALIVE, 1)
        if hasattr(c.socket, "SIO_KEEPALIVE_VALS"):
            transport.sock.ioctl.assert_called_once_with(c.socket.SIO_KEEPALIVE_VALS, (1, 10000, 3000))

    def test_forward_relays_only_fixed_local_endpoint(self):
        forwarding = c.ReverseForward(Mock())
        forwarding.slots.acquire()
        channel, peer = Mock(), Mock()
        channel.closed = False
        peer.recv.side_effect = [b"server-frame", b""]
        channel.recv.return_value = b"client-request"
        with patch.object(c.socket, "create_connection", return_value=peer) as connect, \
                patch.object(c.select, "select", side_effect=[([peer, channel], [], []), ([peer], [], [])]):
            forwarding.relay(channel)
        connect.assert_called_once_with(("127.0.0.1", 5905), timeout=3)
        channel.sendall.assert_called_once_with(b"server-frame")
        peer.sendall.assert_called_once_with(b"client-request")
        channel.close.assert_called_once()
        peer.close.assert_called_once()

    def test_read_only_status_never_starts_or_writes_controller(self):
        client = Mock()
        with patch.object(c, "open_ssh", return_value=client), patch.object(c, "command", return_value="1200,824 1 0\n") as command, \
                patch.object(c, "Controller") as controller, patch.object(c.sys, "argv", ["controller", "--once-status"]), \
                patch("builtins.print") as output:
            self.assertEqual(c.main(), 0)
        controller.assert_not_called()
        command.assert_called_once_with(client, c.DRIVER_STATE_COMMAND, stop_check=None, line_validator=c.parse_driver_state)
        client.close.assert_called_once()
        self.assertIn('"width": 1200', output.call_args.args[0])


if __name__ == "__main__":
    unittest.main()
