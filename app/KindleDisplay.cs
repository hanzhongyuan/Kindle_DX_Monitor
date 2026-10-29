using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KindleDisplay
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // The GUI always runs asInvoker. Only explicit button actions can
            // launch this restricted worker with Windows' normal UAC prompt.
            if (args.Length != 0)
            {
                if (args.Length == 2 && args[0] == "--session" && (args[1] == "Start" || args[1] == "Stop"))
                    return SessionWorker.Run(args[1]);
                return 64;
            }
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex mutex = new Mutex(false, "Local\\KindleView-Gui-" + sid))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        MessageBox.Show("Kindle 显示器已经打开，请从任务栏切换到该窗口。", "Kindle 显示器",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new DisplayForm(sid));
                }
                catch (Exception error)
                {
                    MessageBox.Show("程序未能正常打开：" + error.Message, "Kindle 显示器",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }

    internal sealed class RuntimePaths
    {
        internal readonly string PowerShellPath, PythonPath, PythonHostPath, VncServerPath;

        internal RuntimePaths(string root)
        {
            Dictionary<string, object> config = ReadConfiguration(root);
            PowerShellPath = ConfiguredPath(config, "PowerShellPath", FindPowerShell(), root);
            PythonPath = ConfiguredPath(config, "PythonPath", Path.Combine(root, @".venv\Scripts\python.exe"), root);
            PythonHostPath = ConfiguredPath(config, "PythonHostPath", FindPythonHost(PythonPath), root);
            VncServerPath = ConfiguredPath(config, "VncServerPath",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"TightVNC\tvnserver.exe"), root);
        }

        // The standalone message board only needs Python. In particular it
        // must not resolve or require any virtual-display/VNC/PowerShell setup.
        internal static string ReadPythonPath(string root)
        {
            return ConfiguredPath(ReadConfiguration(root), "PythonPath", Path.Combine(root, @".venv\Scripts\python.exe"), root);
        }

        internal static string ReadPythonHostPath(string root, string python)
        {
            return ConfiguredPath(ReadConfiguration(root), "PythonHostPath", FindPythonHost(python), root);
        }

        private static Dictionary<string, object> ReadConfiguration(string root)
        {
            Dictionary<string, object> config = new Dictionary<string, object>();
            string configPath = Path.Combine(root, "kindle-display.local.json");
            if (File.Exists(configPath))
            {
                config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(configPath, Encoding.UTF8));
                if (config == null) throw new InvalidDataException("kindle-display.local.json must contain a JSON object.");
            }
            return config;
        }

        private static string ConfiguredPath(Dictionary<string, object> config, string name, string fallback, string root)
        {
            object value;
            string configured = null;
            if (config.TryGetValue(name, out value) && value != null)
            {
                configured = value as string;
                if (configured == null) throw new InvalidDataException(name + " must be a path string.");
            }
            string path = Environment.ExpandEnvironmentVariables(String.IsNullOrWhiteSpace(configured) ? fallback : configured);
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }

        private static string FindPowerShell()
        {
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
            if (File.Exists(standard)) return standard;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), "pwsh.exe");
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            // Keep the expected location so a missing prerequisite is reported
            // by the worker, without preventing the control window from opening.
            return standard;
        }

        private static string FindPythonHost(string python)
        {
            string directory = Path.GetDirectoryName(python);
            string environmentRoot = Path.GetDirectoryName(directory);
            if (String.IsNullOrEmpty(environmentRoot)) return python;
            string configPath = Path.Combine(environmentRoot, "pyvenv.cfg");
            if (!File.Exists(configPath)) return python;
            foreach (string line in File.ReadAllLines(configPath))
            {
                int equals = line.IndexOf('=');
                if (equals < 0 || !String.Equals(line.Substring(0, equals).Trim(), "home", StringComparison.OrdinalIgnoreCase)) continue;
                string home = Environment.ExpandEnvironmentVariables(line.Substring(equals + 1).Trim());
                if (home.Length == 0) break;
                if (!Path.IsPathRooted(home)) home = Path.Combine(environmentRoot, home);
                return Path.GetFullPath(Path.Combine(home, "python.exe"));
            }
            return python;
        }
    }

    internal static class MessageBoardProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            return MessageBoardLauncher.Run(args, MessageBoardProfile.Dx);
        }
    }

    internal static class Paperwhite2MessageBoardProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            return MessageBoardLauncher.Run(args, MessageBoardProfile.Paperwhite2);
        }
    }

    internal sealed class MessageBoardProfile
    {
        internal static readonly MessageBoardProfile Dx = new MessageBoardProfile(false);
        internal static readonly MessageBoardProfile Paperwhite2 = new MessageBoardProfile(true);
        internal readonly bool UsbStorage;
        internal readonly string Title, MutexName, RuntimeSubdirectory, BackendScript;
        internal readonly int Width, Height;

        private MessageBoardProfile(bool paperwhite2)
        {
            UsbStorage = paperwhite2;
            Title = paperwhite2 ? "Kindle Paperwhite 2 封面留言板" : "Kindle 休眠留言板";
            MutexName = paperwhite2 ? "KindleView-Paperwhite2-MessageBoard-Gui-" : "KindleView-MessageBoard-Gui-";
            RuntimeSubdirectory = paperwhite2 ? "paperwhite2" : "";
            BackendScript = paperwhite2 ? "kindle-pw2-messageboard.py" : "kindle-messageboard.py";
            Width = paperwhite2 ? 758 : 824;
            Height = paperwhite2 ? 1024 : 1200;
        }
    }

    internal static class MessageBoardLauncher
    {
        internal static int Run(string[] args, MessageBoardProfile profile)
        {
            // This independent executable never dispatches display workers.
            if (args.Length != 0) return 64;
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex mutex = new Mutex(false, "Local\\" + profile.MutexName + sid))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        MessageBox.Show(profile.Title + "已经打开，请从任务栏切换到该窗口。", profile.Title,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }
                    string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                    string python = RuntimePaths.ReadPythonPath(root);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MessageBoardForm(root, python, profile));
                }
                catch (Exception error)
                {
                    MessageBox.Show("留言板未能正常打开：" + error.Message, profile.Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }

    internal static class SessionWorker
    {
        internal static bool IsAdministrator()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static int Run(string action)
        {
            // No other command, file path, or script can be passed to a worker.
            if ((action != "Start" && action != "Stop") || !IsAdministrator()) return 740;
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string runtime = Path.Combine(root, "runtime");
            try
            {
                Directory.CreateDirectory(runtime);
                RuntimePaths paths = new RuntimePaths(root);
                string script = Path.Combine(root, "scripts", "kindle-display-session.ps1");
                ProcessStartInfo info = new ProcessStartInfo(paths.PowerShellPath,
                    "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Action " + action);
                info.WorkingDirectory = root;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                StringBuilder output = new StringBuilder(), error = new StringBuilder();
                object outputLock = new object(), errorLock = new object();
                using (Process process = new Process { StartInfo = info })
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs line)
                    {
                        if (line.Data != null) lock (outputLock) output.AppendLine(line.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line)
                    {
                        if (line.Data != null) lock (errorLock) error.AppendLine(line.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    // Descendant background processes may inherit the pipe's
                    // write handles. Wait only for this PowerShell process;
                    // the parameterless overload and ReadToEnd wait for EOF.
                    if (!process.WaitForExit(Int32.MaxValue)) throw new TimeoutException("Session worker did not exit.");
                    try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                    try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
                    string outputText, errorText;
                    lock (outputLock) outputText = output.ToString();
                    lock (errorLock) errorText = error.ToString();
                    File.WriteAllText(Path.Combine(runtime, "gui-session.stdout.log"), outputText, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(runtime, "gui-session.stderr.log"), errorText, new UTF8Encoding(false));
                    return process.ExitCode;
                }
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(runtime, "gui-session.stderr.log"), error.ToString(), new UTF8Encoding(false)); }
                catch (Exception) { }
                return 1;
            }
        }
    }

    internal sealed class Snapshot
    {
        public bool Running, NeedsCleanup;
        public string Connection = "connecting";
        public int Width, Height;
        public int? Rotation, SavedRotation;
        public string OrientationRequestId, LastError;
        public DateTime OrientationApplied, RefreshCompleted;
    }

    internal sealed class ControllerIdentity
    {
        public int Pid;
        public long StartedTicks;
    }

    internal sealed class OperationResult
    {
        public int ExitCode;
        public Snapshot State;
    }

    internal sealed class DeviceOperationGuard : IDisposable
    {
        private readonly Mutex mutex;
        private bool owned;

        internal DeviceOperationGuard()
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            mutex = new Mutex(false, "Local\\KindleView-DeviceOperation-" + sid);
            try
            {
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new InvalidOperationException("另一个 Kindle 程序正在操作设备，请等待它完成后再试。");
            }
            catch { mutex.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (owned) { owned = false; mutex.ReleaseMutex(); }
            mutex.Dispose();
        }
    }

    internal sealed class DisplayForm : Form
    {
        private readonly string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        private readonly string sid;
        private readonly string python;
        // The venv launcher creates this verified base-interpreter child; the
        // controller's JSON records the child's PID and exact creation time.
        private readonly string pythonHost;
        private readonly string vncServer;
        private readonly Label status = new Label(), detail = new Label();
        private readonly Button connect = new Button(), disconnect = new Button(), refresh = new Button(),
            orientation = new Button(), exit = new Button();
        private readonly ComboBox rotationChoice = new ComboBox();
        private static readonly int[] RotationValues = { 0, 90, 180, 270 };
        private static readonly string[] RotationLabels = { "0° · 竖屏（正向）", "90° · 横屏一",
            "180° · 竖屏（倒置）", "270° · 横屏二" };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private Snapshot snapshot = new Snapshot();
        private bool known, polling, busy, closingRequested, allowClose;
        private bool choiceInitialized, choiceDirty, settingChoice;
        private int stateGeneration;
        private int requestedRotation;
        private string orientationRequestId;
        private DateTime noticeUntil, orientationRequested, refreshRequested;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW", SetLastError = true)]
        private static extern bool MoveFileEx(string existingPath, string newPath, uint flags);

        public DisplayForm(string ownerSid)
        {
            sid = ownerSid;
            RuntimePaths paths = new RuntimePaths(root);
            python = paths.PythonPath;
            pythonHost = paths.PythonHostPath;
            vncServer = paths.VncServerPath;
            Text = "Kindle 显示器";
            ClientSize = new Size(620, 320);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            Label title = new Label { Text = "Kindle 显示器", Location = new Point(24, 20), Size = new Size(570, 34),
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold) };
            status.Location = new Point(26, 68);
            status.Size = new Size(570, 25);
            status.Text = "正在检查连接…";
            detail.Location = new Point(26, 98);
            detail.Size = new Size(570, 42);
            detail.ForeColor = Color.DimGray;
            detail.Text = "请稍候。";
            Controls.AddRange(new Control[] { title, status, detail });
            Controls.Add(new Label { Text = "画面方向", Location = new Point(26, 155), Size = new Size(78, 26) });
            rotationChoice.Location = new Point(108, 151);
            rotationChoice.Size = new Size(334, 30);
            rotationChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            rotationChoice.Items.AddRange(RotationLabels);
            rotationChoice.SelectedIndex = 0;
            rotationChoice.SelectedIndexChanged += delegate
            {
                if (!settingChoice) { choiceInitialized = true; choiceDirty = true; }
            };
            orientation.Text = "应用方向";
            orientation.Location = new Point(458, 148);
            orientation.Size = new Size(138, 38);
            orientation.UseVisualStyleBackColor = true;
            Controls.AddRange(new Control[] { rotationChoice, orientation });
            Button[] buttons = { connect, disconnect, refresh, exit };
            string[] captions = { "连接", "断开连接", "刷新", "退出" };
            int[] left = { 24, 170, 316, 462 };
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Text = captions[i];
                buttons[i].Location = new Point(left[i], 204);
                buttons[i].Size = new Size(134, 38);
                buttons[i].UseVisualStyleBackColor = true;
                Controls.Add(buttons[i]);
            }
            Controls.Add(new Label { Text = "先选方向再连接；连接后点击“应用方向”调整。\r\n可最小化到任务栏；退出或关闭窗口会先断开显示连接。",
                Location = new Point(26, 260), Size = new Size(570, 43), ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F) });
            connect.Click += async delegate
            {
                if (snapshot.Running) { Notice("已接管当前连接", "Kindle 已在后台连接，无需重复启动。", false); return; }
                if (snapshot.NeedsCleanup) { Notice("请先断开上次连接", "点击“断开连接”清理后，再点击“连接”。", true); return; }
                try { using (DeviceOperationGuard guard = new DeviceOperationGuard()) WriteOrientationRequest(); }
                catch (InvalidOperationException error) { Notice("连接暂未启动", error.Message, true); return; }
                catch (Exception) { Notice("方向设置未保存", "请稍后重试；尚未启动连接。", true); return; }
                await ExecuteActionAsync("Start");
            };
            disconnect.Click += async delegate { await ExecuteActionAsync("Stop"); };
            refresh.Click += delegate { SendRefreshRequest(); };
            orientation.Click += delegate { ApplyOrientation(); };
            exit.Click += async delegate { await RequestExitAsync(); };
            FormClosing += async delegate(object sender, FormClosingEventArgs args)
            {
                if (allowClose) return;
                args.Cancel = true;
                await RequestExitAsync();
            };
            Shown += async delegate
            {
                BeginInvoke(new Action(delegate { ShowWindow(Handle, 4); }));
                await PollAsync();
            };
            timer.Interval = 2000;
            timer.Tick += async delegate { await PollAsync(); };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
            UpdateButtons();
            timer.Start();
        }

        private string Runtime(string name) { return Path.Combine(root, "runtime", name); }

        private static object Value(Dictionary<string, object> data, string name)
        {
            object value;
            return data != null && data.TryGetValue(name, out value) ? value : null;
        }

        private static string TextValue(Dictionary<string, object> data, string name)
        {
            return Convert.ToString(Value(data, name), CultureInfo.InvariantCulture);
        }

        internal static long Number(Dictionary<string, object> data, string name)
        {
            long value;
            return Int64.TryParse(TextValue(data, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static DateTime Timestamp(Dictionary<string, object> data, string name)
        {
            DateTimeOffset value;
            return DateTimeOffset.TryParse(TextValue(data, name), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out value) ? value.UtcDateTime : DateTime.MinValue;
        }

        private static int? Rotation(Dictionary<string, object> data)
        {
            object raw = Value(data, "rotation");
            if (!(raw is int) && !(raw is long)) return null;
            long value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return value == 0 || value == 90 || value == 180 || value == 270 ? (int?)value : null;
        }

        internal static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        }

        // Limited-query access can inspect an elevated controller without
        // elevating the GUI. The state PID, exact image and 100ns creation time
        // must all agree. Actual Stop still uses the session script's independent
        // executable + complete command-line + PID/start-time verification.
        internal static ControllerIdentity ReadControllerIdentity(int pid, string python, string pythonHost)
        {
            if (pid <= 0) return null;
            IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
            if (handle == IntPtr.Zero) return null;
            try
            {
                uint exitCode;
                if (!GetExitCodeProcess(handle, out exitCode) || exitCode != 259) return null;
                StringBuilder path = new StringBuilder(32768);
                int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size) ||
                    !(String.Equals(path.ToString(), python, StringComparison.OrdinalIgnoreCase) ||
                      String.Equals(path.ToString(), pythonHost, StringComparison.OrdinalIgnoreCase))) return null;
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user)) return null;
                return new ControllerIdentity { Pid = pid, StartedTicks = DateTime.FromFileTimeUtc(created).Ticks };
            }
            finally { CloseHandle(handle); }
        }

        private Snapshot ReadSnapshot()
        {
            Dictionary<string, object> state = ReadJson(Runtime("kindle-display-controller.json"));
            Dictionary<string, object> vnc = ReadJson(Runtime("kindle-vnc-server.json"));
            Dictionary<string, object> settings = ReadJson(Runtime("kindle-display-settings.json"));
            ControllerIdentity controller = ReadControllerIdentity((int)Number(state, "pid"), python, pythonHost);
            bool pending = String.Equals(TextValue(vnc, "OwnerSid"), sid, StringComparison.Ordinal) &&
                String.Equals(TextValue(vnc, "ServerExe"), vncServer, StringComparison.OrdinalIgnoreCase) &&
                Object.Equals(Value(vnc, "ConfigActive"), true);
            // A stale JSON file must never label a different PID as connected.
            bool matchesState = controller != null && controller.StartedTicks == Number(state, "starttimeUtcTicks");
            Snapshot result = new Snapshot { Running = matchesState, NeedsCleanup = matchesState || pending,
                SavedRotation = Rotation(settings) };
            if (matchesState)
            {
                result.Connection = TextValue(state, "connection");
                result.Width = (int)Number(state, "width");
                result.Height = (int)Number(state, "height");
                result.Rotation = Rotation(state);
                result.OrientationApplied = Timestamp(state, "orientation_applied");
                result.OrientationRequestId = TextValue(state, "orientation_request_id");
                result.LastError = TextValue(state, "last_error");
                result.RefreshCompleted = Timestamp(state, "refresh_completed");
            }
            return result;
        }

        private async Task PollAsync()
        {
            if (polling || busy || closingRequested || allowClose || IsDisposed) return;
            polling = true;
            int generation = stateGeneration;
            try
            {
                Snapshot current = await Task.Run(() => ReadSnapshot());
                if (IsDisposed || busy || closingRequested || allowClose || generation != stateGeneration) return;
                AcceptSnapshot(current);
                known = true;
                CheckRequestCompletion();
                if (DateTime.UtcNow >= noticeUntil) RenderState();
            }
            catch (Exception)
            {
                if (!IsDisposed && !busy && !closingRequested && !allowClose && generation == stateGeneration)
                {
                    known = false;
                    Notice("暂时无法读取连接状态", "稍后会自动重试；退出时仍会先尝试断开连接。", true);
                }
            }
            finally
            {
                polling = false;
                if (!IsDisposed) UpdateButtons();
            }
        }

        private OperationResult RunSession(string action)
        {
            // Acquire and release on this synchronous worker thread, never
            // across an await. The other GUI cannot send while Start/Stop runs.
            using (DeviceOperationGuard guard = new DeviceOperationGuard())
                return RunSessionCore(action);
        }

        private OperationResult RunSessionCore(string action)
        {
            if (action != "Start" && action != "Stop") throw new ArgumentException("Unsupported session action.");
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, "--session " + action);
            info.WorkingDirectory = root;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            if (SessionWorker.IsAdministrator())
            {
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
            }
            else
            {
                info.UseShellExecute = true;
                info.Verb = "runas"; // Requested only by the user's session button action.
            }
            int code;
            using (Process process = Process.Start(info))
            {
                process.WaitForExit(); // This method runs only on a worker thread.
                code = process.ExitCode;
            }
            return new OperationResult { ExitCode = code, State = ReadSnapshot() };
        }

        private async Task<bool> ExecuteActionAsync(string action)
        {
            if (busy || allowClose) return false;
            busy = true;
            stateGeneration++;
            UpdateButtons();
            Notice(action == "Start" ? "正在连接…" : "正在断开连接…",
                action == "Start" ? "如出现权限提示，请允许启用本项目的虚拟显示器。" : "如出现权限提示，请允许关闭本项目的虚拟显示器。", false);
            bool success = false;
            try
            {
                OperationResult result = await Task.Run(() => RunSession(action));
                AcceptSnapshot(result.State);
                known = true;
                // A duplicate Start can fail while the already-running matching
                // controller is healthy. Attach to it; never start another one.
                success = action == "Start" ? snapshot.Running : result.ExitCode == 0 && !snapshot.NeedsCleanup;
                if (success)
                {
                    if (action == "Stop") { orientationRequestId = null; orientationRequested = DateTime.MinValue; }
                    Notice(action == "Start" ? "连接已启动" : "已断开连接",
                        action == "Start" ? "正在读取 Kindle 的显示状态。" : "电脑显示设置已恢复。", false);
                }
                else
                    Notice(action == "Start" ? "连接未完成" : "断开尚未完成",
                        "请检查 USB 连接后重试；窗口会保留。", true);
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223)
                    Notice("已取消操作", "未授予设备管理权限，当前连接保持不变。", false);
                else { known = false; Notice("操作未能启动", "请稍后重试；窗口会保留。", true); }
            }
            catch (InvalidOperationException error)
            {
                Notice("操作暂未执行", error.Message, true);
            }
            catch (Exception)
            {
                known = false;
                Notice("操作未完成", "请检查 USB 连接后重试；窗口会保留。", true);
            }
            finally { busy = false; UpdateButtons(); }

            if (closingRequested)
            {
                if (action == "Start")
                {
                    if (!known || snapshot.NeedsCleanup) await ExecuteActionAsync("Stop");
                    else CloseAfterCleanup();
                }
                else if (success) CloseAfterCleanup();
                else { closingRequested = false; UpdateButtons(); }
            }
            else await PollAsync();
            return success;
        }

        private async Task RequestExitAsync()
        {
            if (closingRequested || allowClose) return;
            closingRequested = true;
            stateGeneration++;
            UpdateButtons();
            if (busy) { Notice("正在准备退出…", "当前操作结束后会自动断开连接并关闭窗口。", false); return; }
            if (known && !snapshot.NeedsCleanup) CloseAfterCleanup();
            else await ExecuteActionAsync("Stop");
        }

        private void CloseAfterCleanup()
        {
            allowClose = true;
            BeginInvoke(new Action(Close)); // Avoid recursively closing inside FormClosing.
        }

        private void AcceptSnapshot(Snapshot current)
        {
            bool firstConnection = !snapshot.Running && current.Running;
            snapshot = current;
            // Polling must never replace a selection the user has not applied.
            if (!choiceInitialized || (firstConnection && !choiceDirty && orientationRequestId == null))
            {
                int initial = current.Running && current.Rotation.HasValue ? current.Rotation.Value : current.SavedRotation ?? 0;
                settingChoice = true;
                try { rotationChoice.SelectedIndex = Array.IndexOf(RotationValues, initial); }
                finally { settingChoice = false; }
                choiceInitialized = true;
                choiceDirty = false;
            }
        }

        private int SelectedRotation()
        {
            int index = rotationChoice.SelectedIndex;
            if (index < 0 || index >= RotationValues.Length) throw new InvalidOperationException("Select a supported rotation.");
            int value = RotationValues[index];
            if (value != 0 && value != 90 && value != 180 && value != 270) throw new InvalidOperationException("Unsupported rotation.");
            return value;
        }

        private void WriteOrientationRequest()
        {
            int rotation = SelectedRotation();
            DateTime requested = DateTime.UtcNow;
            string id = Guid.NewGuid().ToString("D");
            Dictionary<string, object> request = new Dictionary<string, object> {
                { "rotation", rotation }, { "requested_at", requested.ToString("o", CultureInfo.InvariantCulture) }, { "request_id", id } };
            Directory.CreateDirectory(Path.Combine(root, "runtime"));
            string temporary = Runtime(".kindle-display.orientation." + id + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(request), new UTF8Encoding(false));
                // Same-directory atomic rename, replacing an unconsumed request.
                // The controller can only observe complete JSON documents.
                if (!MoveFileEx(temporary, Runtime("kindle-display.orientation"), 0x1 | 0x8))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            orientationRequestId = id;
            orientationRequested = requested;
            requestedRotation = rotation;
        }

        private void ApplyOrientation()
        {
            if (!snapshot.Running || busy || closingRequested) return;
            try
            {
                using (DeviceOperationGuard guard = new DeviceOperationGuard()) WriteOrientationRequest();
                Notice("正在应用方向…", RotationLabel(requestedRotation) + "，请等待十几秒。", false);
            }
            catch (InvalidOperationException error) { Notice("方向请求未发送", error.Message, true); }
            catch (Exception) { Notice("方向请求未发送", "请稍后再试。", true); }
        }

        private void SendRefreshRequest()
        {
            if (!snapshot.Running || busy || closingRequested) return;
            try
            {
                DateTime requested = DateTime.UtcNow;
                using (DeviceOperationGuard guard = new DeviceOperationGuard())
                    File.WriteAllText(Runtime("kindle-display.refresh"),
                        requested.ToString("o", CultureInfo.InvariantCulture), new UTF8Encoding(false));
                refreshRequested = requested;
                Notice("已请求刷新", "画面将重新载入，可能短暂切换页面。", false);
            }
            catch (InvalidOperationException error) { Notice("请求未发送", error.Message, true); }
            catch (Exception) { Notice("请求未发送", "请稍后再试。", true); }
        }

        private void CheckRequestCompletion()
        {
            if (orientationRequestId != null && String.Equals(snapshot.OrientationRequestId, orientationRequestId, StringComparison.Ordinal))
            {
                if (snapshot.OrientationApplied >= orientationRequested && snapshot.Rotation == requestedRotation)
                {
                    orientationRequestId = null;
                    orientationRequested = DateTime.MinValue;
                    choiceDirty = SelectedRotation() != requestedRotation;
                    Notice("方向已应用", SizeText(), false);
                }
                else if (!String.IsNullOrEmpty(snapshot.LastError))
                {
                    orientationRequestId = null;
                    orientationRequested = DateTime.MinValue;
                    Notice("方向未能应用", "请检查 USB 连接后重新点击“应用方向”。", true);
                }
            }
            if (refreshRequested != DateTime.MinValue && snapshot.RefreshCompleted >= refreshRequested)
            {
                refreshRequested = DateTime.MinValue;
                Notice("画面已刷新", "可继续使用 Kindle 显示器。", false);
            }
        }

        private string SizeText()
        {
            if (snapshot.Width <= 0 || snapshot.Height <= 0) return "正在读取显示状态。";
            string direction = snapshot.Rotation.HasValue ? RotationLabel(snapshot.Rotation.Value) : (snapshot.Width > snapshot.Height ? "横屏" : "竖屏");
            return direction + " · " + snapshot.Width + " × " + snapshot.Height;
        }

        private static string RotationLabel(int rotation)
        {
            int index = Array.IndexOf(RotationValues, rotation);
            return index >= 0 ? RotationLabels[index] : "方向未知";
        }

        private void RenderState()
        {
            status.ForeColor = snapshot.Running ? Color.FromArgb(30, 110, 70) : Color.DimGray;
            if (!snapshot.Running)
            {
                status.Text = "未连接";
                detail.Text = snapshot.NeedsCleanup ? "上次连接尚未清理，请先点击“断开连接”。" : "插好 Kindle 的 USB 数据线后，点击“连接”。";
                return;
            }
            if (orientationRequestId != null && snapshot.Connection == "connected")
            {
                status.Text = "正在应用方向…";
                detail.Text = RotationLabel(requestedRotation) + "，请等待十几秒。";
                return;
            }
            switch (snapshot.Connection)
            {
                case "connected": status.Text = "已连接"; detail.Text = SizeText(); break;
                case "reconnecting": status.Text = "正在重新连接…"; detail.Text = "请保持 USB 连接，恢复后会自动继续显示。"; break;
                case "resizing": status.Text = "正在调整横竖屏…"; detail.Text = "屏幕方向切换需要十几秒，请稍候。"; break;
                case "refreshing": status.Text = "正在刷新画面…"; detail.Text = "画面将重新载入，请稍候。"; break;
                case "error": status.Text = "显示设置未完成"; detail.Text = "请重新应用方向，或断开连接后重试。"; status.ForeColor = Color.Firebrick; break;
                case "stopping": case "stopped": status.Text = "正在断开连接…"; detail.Text = "正在恢复电脑显示设置，请稍候。"; break;
                default: status.Text = "正在连接…"; detail.Text = "连接成功后会自动显示电脑画面。"; break;
            }
        }

        private void Notice(string text, string description, bool failure)
        {
            status.Text = text;
            detail.Text = description;
            status.ForeColor = failure ? Color.Firebrick : Color.FromArgb(35, 70, 110);
            noticeUntil = DateTime.UtcNow.AddSeconds(failure ? 12 : 4);
        }

        private void UpdateButtons()
        {
            bool idle = !busy && !closingRequested;
            connect.Enabled = idle && known;
            disconnect.Enabled = idle && known && snapshot.NeedsCleanup;
            refresh.Enabled = idle && known && snapshot.Running;
            orientation.Enabled = idle && known && snapshot.Running && orientationRequestId == null;
            rotationChoice.Enabled = idle;
            exit.Enabled = !closingRequested;
        }
    }

    internal sealed class MessageBoardResult
    {
        internal int ExitCode;
        internal string Output, Error;
    }

    internal sealed class MessageBoardLayout
    {
        internal const int MinRows = 1, MaxRows = 30, MinFontSize = 12, MaxFontSize = 120;
        public int Rows { get; set; }
        public string FontFamily { get; set; }
        public int FontSize { get; set; }
        public bool AutoFit { get; set; }
        public bool Landscape { get; set; }

        public MessageBoardLayout()
        {
            Rows = 10; FontFamily = "Microsoft YaHei"; FontSize = 42; AutoFit = true;
        }

        internal static MessageBoardLayout DefaultFor(int height)
        {
            return new MessageBoardLayout { FontSize = (int)Math.Round(42F * height / 1200F) };
        }

        internal void Validate()
        {
            if (Rows < MinRows || Rows > MaxRows) throw new ArgumentException("显示行数须在 1 到 30 之间。");
            if (FontSize < MinFontSize || FontSize > MaxFontSize) throw new ArgumentException("字号须在 12 到 120 像素之间。");
            if (String.IsNullOrWhiteSpace(FontFamily)) throw new ArgumentException("请选择字体。");
            using (FontFamily family = new FontFamily(FontFamily))
                if (!family.IsStyleAvailable(FontStyle.Regular)) throw new ArgumentException("所选字体不支持常规字形，请换一种字体。");
        }
    }

    internal static class MessageBoardRenderer
    {
        internal static Bitmap Render(string[] lines)
        {
            return Render(lines, 824, 1200);
        }

        internal static Bitmap Render(string[] lines, int width, int height)
        {
            return Render(lines, width, height, MessageBoardLayout.DefaultFor(height));
        }

        internal static Bitmap Render(string[] lines, int width, int height, MessageBoardLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            layout.Validate();
            if (lines == null || lines.Length != layout.Rows) throw new ArgumentException("留言行数与显示行数设置不一致。");
            if (!((width == 824 && height == 1200) || (width == 758 && height == 1024)))
                throw new ArgumentException("不支持的留言板尺寸。");
            // Compose in reading orientation, then rotate back to the device's
            // native raster. The existing USB/wireless image contract stays intact.
            if (layout.Landscape) { int nativeWidth = width; width = height; height = nativeWidth; }
            float horizontalScale = width / 824F, verticalScale = height / 1200F;
            float fontScale = Math.Min(horizontalScale, verticalScale);
            float left = 52 * horizontalScale, right = width - left, textLeft = 108 * horizontalScale;
            float rowHeight = 910 * verticalScale / layout.Rows;
            float textHeight = rowHeight * 0.84F;
            Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (Font title = new Font("Microsoft YaHei", 48F * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font number = new Font("Microsoft YaHei", Math.Min(24F * fontScale, textHeight / 1.6F), FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font footer = new Font("Microsoft YaHei", 21F * fontScale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Pen rule = new Pen(Color.FromArgb(190, 190, 190), 1F))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    graphics.Clear(Color.White);
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                    format.Trimming = StringTrimming.None;
                    graphics.DrawString("留言板", title, Brushes.Black, new PointF(left, 48 * verticalScale));
                    graphics.DrawLine(Pens.Black, left, 126 * verticalScale, right, 126 * verticalScale);
                    for (int index = 0; index < layout.Rows; index++)
                    {
                        string text = lines[index] ?? "";
                        if (text.IndexOfAny(new char[] { '\r', '\n', '\t' }) >= 0)
                            throw new ArgumentException("第 " + (index + 1) + " 行包含换行或制表符，请按一行一条填写。");
                        float top = 158 * verticalScale + index * rowHeight;
                        graphics.DrawString((index + 1).ToString("00", CultureInfo.InvariantCulture), number,
                            Brushes.DimGray, new PointF(left, top + (textHeight - number.GetHeight(graphics)) / 2));
                        using (Font lineFont = FitFont(graphics, text, format, index + 1, right - textLeft, textHeight, layout))
                            graphics.DrawString(text, lineFont, Brushes.Black, new PointF(textLeft,
                                top + (textHeight - lineFont.GetHeight(graphics)) / 2), format);
                        graphics.DrawLine(rule, textLeft, top + rowHeight * 0.92F, right, top + rowHeight * 0.92F);
                    }
                    graphics.DrawString("更新于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), footer,
                        Brushes.DimGray, new PointF(left, 1135 * verticalScale));
                }
                if (layout.Landscape) bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static Font FitFont(Graphics graphics, string text, StringFormat format, int lineNumber,
            float availableWidth, float availableHeight, MessageBoardLayout layout)
        {
            // Blank rows still draw their rule but do not constrain the chosen font.
            if (String.IsNullOrWhiteSpace(text)) return new Font(layout.FontFamily, MessageBoardLayout.MinFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            int minimum = layout.AutoFit ? MessageBoardLayout.MinFontSize : layout.FontSize;
            for (int size = layout.FontSize; size >= minimum; size--)
            {
                Font font = new Font(layout.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
                if (font.GetHeight(graphics) <= availableHeight &&
                    graphics.MeasureString(text, font, Int32.MaxValue, format).Width <= availableWidth) return font;
                font.Dispose();
            }
            throw new ArgumentException("第 " + lineNumber + " 行在当前行数和字号下放不下。请缩短文字、减小字号或减少显示行数。" +
                (layout.AutoFit ? "自动缩小至 12 像素后仍需调整。" : "也可勾选“自动缩小字号”。"));
        }
    }

    internal sealed class MessageBoardForm : Form
    {
        private readonly string root, python, runtime;
        private readonly MessageBoardProfile profile;
        private readonly TextBox editor = new TextBox();
        private readonly NumericUpDown rowChoice = new NumericUpDown(), fontSizeChoice = new NumericUpDown();
        private readonly ComboBox fontChoice = new ComboBox();
        private readonly ComboBox orientationChoice = new ComboBox();
        private readonly CheckBox autoFit = new CheckBox();
        private readonly Label lineCount = new Label();
        private readonly PictureBox preview = new PictureBox();
        private readonly Label previewCaption = new Label();
        private readonly Label notice = new Label();
        private readonly Button render = new Button(), send = new Button(), restore = new Button(), close = new Button();
        private readonly Button wirelessSend = new Button(), wirelessRestore = new Button(), saveWirelessAddress = new Button();
        private readonly Button todoSend = new Button();
        private readonly TextBox wirelessAddress = new TextBox();
        private readonly System.Windows.Forms.Timer draftTimer = new System.Windows.Forms.Timer();
        private bool busy, loading, draftLoadFailed, layoutLoadFailed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW", SetLastError = true)]
        private static extern bool MoveFileEx(string existingPath, string newPath, uint flags);

        internal MessageBoardForm(string projectRoot, string pythonPath)
            : this(projectRoot, pythonPath, MessageBoardProfile.Dx)
        {
        }

        internal MessageBoardForm(string projectRoot, string pythonPath, MessageBoardProfile selectedProfile)
        {
            root = projectRoot;
            python = pythonPath;
            profile = selectedProfile;
            runtime = Path.Combine(root, "runtime", profile.RuntimeSubdirectory);
            loading = true;
            Text = profile.Title;
            ClientSize = new Size(930, 665);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            Controls.Add(new Label { Text = "把提醒留在墨水屏上", Location = new Point(24, 18), Size = new Size(540, 32),
                Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold) });
            Controls.Add(new Label { Text = profile.UsbStorage
                ? "封面排版可调，也可把每行文字无线推送为待办。\r\n无线操作前请先唤醒 Kindle，让电脑与 Kindle 连接同一 Wi-Fi。"
                : "行数、字体、字号和方向可调；发送前请先断开扩展屏。\r\n文字与设置自动保存。休眠后可拔线；Kindle 重启后需重新发送。",
                Location = new Point(26, 58), Size = new Size(555, 48), ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F) });
            Controls.Add(new Label { Text = "行数", Location = new Point(26, 115), Size = new Size(40, 24) });
            rowChoice.Location = new Point(66, 112);
            rowChoice.Size = new Size(58, 28);
            rowChoice.Minimum = MessageBoardLayout.MinRows; rowChoice.Maximum = MessageBoardLayout.MaxRows;
            rowChoice.Value = 10;
            rowChoice.AccessibleName = "封面显示行数";
            Controls.Add(rowChoice);
            Controls.Add(new Label { Text = "字体", Location = new Point(136, 115), Size = new Size(40, 24) });
            fontChoice.Location = new Point(176, 112);
            fontChoice.Size = new Size(205, 28);
            fontChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            fontChoice.DropDownWidth = 300;
            fontChoice.Sorted = true;
            fontChoice.AccessibleName = "封面正文字体";
            foreach (FontFamily family in FontFamily.Families)
            {
                using (family)
                    if (family.IsStyleAvailable(FontStyle.Regular)) fontChoice.Items.Add(family.Name);
            }
            using (FontFamily defaultFamily = new FontFamily("Microsoft YaHei")) fontChoice.SelectedItem = defaultFamily.Name;
            if (fontChoice.SelectedIndex < 0)
                using (FontFamily fallbackFamily = FontFamily.GenericSansSerif) fontChoice.SelectedItem = fallbackFamily.Name;
            if (fontChoice.SelectedIndex < 0 && fontChoice.Items.Count > 0) fontChoice.SelectedIndex = 0;
            Controls.Add(fontChoice);
            Controls.Add(new Label { Text = "字号", Location = new Point(393, 115), Size = new Size(40, 24) });
            fontSizeChoice.Location = new Point(435, 112);
            fontSizeChoice.Size = new Size(68, 28);
            fontSizeChoice.Minimum = MessageBoardLayout.MinFontSize; fontSizeChoice.Maximum = MessageBoardLayout.MaxFontSize;
            fontSizeChoice.Value = MessageBoardLayout.DefaultFor(profile.Height).FontSize;
            fontSizeChoice.AccessibleName = "封面正文字号（像素）";
            Controls.Add(fontSizeChoice);
            Controls.Add(new Label { Text = "像素", Location = new Point(511, 115), Size = new Size(48, 24) });
            Controls.Add(new Label { Text = "方向", Location = new Point(26, 150), Size = new Size(40, 24) });
            orientationChoice.Location = new Point(66, 147);
            orientationChoice.Size = new Size(98, 28);
            orientationChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            orientationChoice.Items.AddRange(new object[] { "纵向", "横向" });
            orientationChoice.SelectedIndex = 0;
            orientationChoice.AccessibleName = "封面显示方向";
            Controls.Add(orientationChoice);
            autoFit.Text = "自动缩小字号";
            autoFit.Location = new Point(176, 147);
            autoFit.Size = new Size(144, 26);
            autoFit.Checked = true;
            Controls.Add(autoFit);
            Controls.Add(new Label { Text = "按行宽和行高缩小；\r\n取消勾选可固定字号。",
                Location = new Point(330, 146), Size = new Size(234, 43), ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F) });
            rowChoice.ValueChanged += LayoutChanged;
            fontChoice.SelectedIndexChanged += LayoutChanged;
            fontSizeChoice.ValueChanged += LayoutChanged;
            autoFit.CheckedChanged += LayoutChanged;
            orientationChoice.SelectedIndexChanged += LayoutChanged;
            editor.Location = new Point(26, 194);
            editor.Font = new Font("Microsoft YaHei UI", 16F);
            editor.Multiline = true;
            editor.AcceptsReturn = true;
            editor.AcceptsTab = false;
            editor.WordWrap = false;
            editor.ScrollBars = ScrollBars.Both;
            editor.ShortcutsEnabled = true;
            editor.MaxLength = Int32.MaxValue;
            editor.Size = new Size(538, 249);
            editor.AccessibleName = profile.UsbStorage ? "留言或待办内容，每行一条" : "留言内容，每行一条，行数可在上方设置";
            editor.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Control && args.KeyCode == Keys.A)
                {
                    editor.SelectAll();
                    args.SuppressKeyPress = true;
                }
            };
            editor.TextChanged += delegate
            {
                if (loading) return;
                draftLoadFailed = false;
                UpdateLineCount();
                ClearPreview();
                draftTimer.Stop();
                draftTimer.Start();
            };
            Controls.Add(editor);
            lineCount.Location = new Point(26, editor.Bottom + 8);
            lineCount.Size = new Size(538, 45);
            lineCount.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(lineCount);
            preview.Location = new Point(594, 64);
            preview.Size = new Size(310, profile.UsbStorage ? 419 : 452);
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BorderStyle = BorderStyle.FixedSingle;
            preview.BackColor = Color.FromArgb(245, 245, 245);
            Controls.Add(preview);
            previewCaption.Location = new Point(594, preview.Bottom + 10);
            previewCaption.Size = new Size(310, 44);
            previewCaption.ForeColor = Color.DimGray;
            previewCaption.TextAlign = ContentAlignment.TopCenter;
            previewCaption.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(previewCaption);
            Button[] buttons = profile.UsbStorage
                ? new Button[] { render, send, wirelessSend, todoSend, restore, wirelessRestore, close }
                : new Button[] { render, send, restore, close };
            string[] captions = profile.UsbStorage
                ? new string[] { "预览", "USB发送", "无线发送", "推送待办", "USB恢复封面", "无线恢复封面", "关闭" }
                : new string[] { "预览", "发送到 Kindle", "恢复原休眠封面", "关闭" };
            int[] widths = profile.UsbStorage ? new int[] { 86, 118, 118, 118, 146, 146, 86 } : new int[] { 98, 152, 170, 92 };
            int x = 26;
            int buttonsTop = Math.Max(505, lineCount.Bottom + 12);
            if (profile.UsbStorage)
            {
                int addressTop = Math.Max(buttonsTop, previewCaption.Bottom + 8);
                Controls.Add(new Label { Text = "Kindle IP 地址", Location = new Point(26, addressTop + 6), Size = new Size(108, 24) });
                wirelessAddress.Location = new Point(140, addressTop + 3);
                wirelessAddress.Size = new Size(182, 28);
                wirelessAddress.MaxLength = 15;
                wirelessAddress.AccessibleName = "Kindle 在当前 Wi-Fi 下的 IP 地址";
                Controls.Add(wirelessAddress);
                saveWirelessAddress.Text = "保存地址";
                saveWirelessAddress.Location = new Point(334, addressTop);
                saveWirelessAddress.Size = new Size(98, 32);
                saveWirelessAddress.UseVisualStyleBackColor = true;
                saveWirelessAddress.Click += delegate
                {
                    try { SaveWirelessAddress(); SetNotice("地址已保存。先唤醒 Kindle 并拔掉 USB，再选择“无线发送”封面或“推送待办”。", false); }
                    catch (Exception error) { SetNotice(error.Message, true); }
                };
                Controls.Add(saveWirelessAddress);
                Controls.Add(new Label { Text = "无线使用时请拔掉 USB；电脑与 Kindle 连接同一 Wi-Fi。",
                    Location = new Point(450, addressTop + 4), Size = new Size(454, 36), ForeColor = Color.DimGray,
                    Font = new Font("Microsoft YaHei UI", 9F) });
                buttonsTop = addressTop + 44;
            }
            for (int index = 0; index < buttons.Length; index++)
            {
                buttons[index].Text = captions[index];
                buttons[index].Location = new Point(x, buttonsTop);
                buttons[index].Size = new Size(widths[index], 38);
                buttons[index].UseVisualStyleBackColor = true;
                Controls.Add(buttons[index]);
                x += widths[index] + 10;
            }
            notice.Location = new Point(26, Math.Max(buttonsTop + 60, previewCaption.Bottom + 12));
            notice.Size = new Size(878, 77);
            ClientSize = new Size(930, Math.Max(665, notice.Bottom + 23));
            notice.ForeColor = Color.DimGray;
            notice.Text = profile.UsbStorage
                ? "“无线发送”更新屏保封面；“推送待办”将每行追加到 KOReader 清单，保留原有完成状态。相同草稿重发不重复；改了草稿再推送会追加整份草稿。首次无线使用需先完成设备配置。"
                : "先编辑并预览，再连接 USB 数据线发送。草稿只保存在本机。";
            Controls.Add(notice);
            render.Click += delegate { TryPreview(); };
            send.Click += async delegate { await SendAsync(false); };
            restore.Click += async delegate { await SendAsync(true); };
            wirelessSend.Click += async delegate { await SendAsync(false, true); };
            wirelessRestore.Click += async delegate { await SendAsync(true, true); };
            todoSend.AccessibleName = "通过 Wi-Fi 将每行文字追加到 KOReader 离线待办";
            todoSend.Click += async delegate { await SendTodoAsync(); };
            close.Click += delegate { Close(); };
            draftTimer.Interval = 600;
            draftTimer.Tick += delegate
            {
                draftTimer.Stop();
                try { SaveLocalState(); }
                catch (Exception error) { SetNotice("文字或排版设置未能保存：" + error.Message, true); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (busy) { args.Cancel = true; SetNotice("正在传输，请等待当前操作完成后关闭。", false); return; }
                draftTimer.Stop();
                try { SaveLocalState(); }
                catch (Exception error) { SetNotice("文字或排版设置未能保存：" + error.Message, true); args.Cancel = true; }
            };
            FormClosed += delegate
            {
                draftTimer.Stop(); draftTimer.Dispose();
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
            };
            LoadLayout();
            LoadDraft();
            loading = false;
            UpdateLineCount();
            if (!draftLoadFailed && !layoutLoadFailed) TryPreview();
            if (profile.UsbStorage) LoadWirelessAddress();
        }

        private MessageBoardLayout ReadLayout()
        {
            return new MessageBoardLayout { Rows = (int)rowChoice.Value,
                FontFamily = fontChoice.SelectedItem as string, FontSize = (int)fontSizeChoice.Value,
                AutoFit = autoFit.Checked, Landscape = orientationChoice.SelectedIndex == 1 };
        }

        private void LayoutChanged(object sender, EventArgs args)
        {
            if (loading) return;
            layoutLoadFailed = false;
            UpdateLineCount();
            ClearPreview();
            draftTimer.Stop();
            draftTimer.Start();
            SetNotice("排版已更改，点击“预览”查看新的行数、文字大小和方向。", false);
        }

        private void ClearPreview()
        {
            Image previous = preview.Image;
            preview.Image = null;
            if (previous != null) previous.Dispose();
        }

        private void LoadLayout()
        {
            string path = Path.Combine(runtime, "messageboard-layout.json");
            if (!File.Exists(path)) return;
            try
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("排版设置文件过大。");
                MessageBoardLayout saved = new JavaScriptSerializer().Deserialize<MessageBoardLayout>(File.ReadAllText(path, Encoding.UTF8));
                if (saved == null) throw new InvalidDataException("排版设置为空。");
                saved.Validate();
                int fontIndex;
                using (FontFamily family = new FontFamily(saved.FontFamily)) fontIndex = fontChoice.FindStringExact(family.Name);
                if (fontIndex < 0) throw new InvalidDataException("本机未安装已保存的字体，请重新选择。");
                rowChoice.Value = saved.Rows;
                fontSizeChoice.Value = saved.FontSize;
                fontChoice.SelectedIndex = fontIndex;
                autoFit.Checked = saved.AutoFit;
                orientationChoice.SelectedIndex = saved.Landscape ? 1 : 0;
            }
            catch (Exception error)
            {
                layoutLoadFailed = true;
                SetNotice("未能读取上次排版：" + error.Message + " 原文件已保留；当前使用默认排版，调整设置后保存新排版。", true);
            }
        }

        private void SaveLocalState()
        {
            SaveDraft();
            if (layoutLoadFailed) return;
            Directory.CreateDirectory(runtime);
            string path = Path.Combine(runtime, "messageboard-layout.json");
            string temporary = Path.Combine(runtime, ".messageboard-layout." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(ReadLayout()), new UTF8Encoding(false));
                if (!MoveFileEx(temporary, path, 0x1 | 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private Dictionary<string, object> ReadWirelessSettings()
        {
            string path = Path.Combine(runtime, "wireless.json");
            if (!File.Exists(path)) return new Dictionary<string, object>();
            try
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
                Dictionary<string, object> settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (settings == null) throw new InvalidDataException();
                return settings;
            }
            catch (Exception)
            {
                throw new InvalidDataException("无法读取已保存的无线设置，原文件已保留。请按指南检查首次无线配置。");
            }
        }

        private void LoadWirelessAddress()
        {
            try
            {
                object value;
                Dictionary<string, object> settings = ReadWirelessSettings();
                if (settings.TryGetValue("host", out value) && value != null)
                {
                    string address = value as string;
                    if (address == null) throw new InvalidDataException("已保存的 Kindle IP 地址格式不正确，请重新填写。");
                    wirelessAddress.Text = address;
                }
            }
            catch (Exception error) { SetNotice(error.Message, true); }
        }

        private static string ValidateWirelessAddress(string text)
        {
            string[] parts = (text ?? "").Trim().Split('.');
            byte[] values = new byte[4];
            if (parts.Length != 4) throw new ArgumentException("请填写 Kindle 当前的 IP 地址，例如 192.168.1.25。");
            for (int index = 0; index < parts.Length; index++)
                if (!Byte.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                    throw new ArgumentException("IP 地址需要四组 0 到 255 的数字，例如 192.168.1.25。");
            bool local = values[0] == 10 || (values[0] == 172 && values[1] >= 16 && values[1] <= 31) ||
                (values[0] == 192 && values[1] == 168);
            if (!local) throw new ArgumentException("请填写同一 Wi-Fi 下的 Kindle 内网地址；本程序不通过外网发送。");
            return values[0].ToString(CultureInfo.InvariantCulture) + "." + values[1].ToString(CultureInfo.InvariantCulture) + "." +
                values[2].ToString(CultureInfo.InvariantCulture) + "." + values[3].ToString(CultureInfo.InvariantCulture);
        }

        private void SaveWirelessAddress()
        {
            if (!profile.UsbStorage) return;
            string address = ValidateWirelessAddress(wirelessAddress.Text);
            Dictionary<string, object> settings = ReadWirelessSettings();
            // Change only the displayed address. Pairing and connection details
            // installed for this device remain intact, including unknown fields.
            settings["host"] = address;
            if (!settings.ContainsKey("port")) settings["port"] = 22;
            Directory.CreateDirectory(runtime);
            string path = Path.Combine(runtime, "wireless.json");
            string temporary = Path.Combine(runtime, ".wireless." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(settings), new UTF8Encoding(false));
                if (!MoveFileEx(temporary, path, 0x1 | 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
                wirelessAddress.Text = address;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string[] SplitText(string text)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length == 0) return new string[0];
            string[] result = normalized.Split(new char[] { '\n' }, StringSplitOptions.None);
            // A final line terminator from a copied text file does not add an
            // extra row. Any preceding empty lines are still preserved.
            if (normalized.EndsWith("\n", StringComparison.Ordinal)) Array.Resize(ref result, result.Length - 1);
            return result;
        }

        private void UpdateLineCount()
        {
            int count = SplitText(editor.Text).Length;
            int rows = (int)rowChoice.Value;
            bool landscape = orientationChoice.SelectedIndex == 1;
            previewCaption.Text = (landscape ? "横向封面 · " : "纵向封面 · ") +
                (landscape ? profile.Height : profile.Width) + " × " + (landscape ? profile.Width : profile.Height) +
                (landscape ? "\r\n将 Kindle 顺时针旋转 90° 阅读" : "");
            bool tooMany = count > rows;
            lineCount.Text = count.ToString(CultureInfo.InvariantCulture) + " / " + rows.ToString(CultureInfo.InvariantCulture) + " 行 · " +
                (tooMany ? "已保留全部文字，请增加显示行数或精简内容后再预览、发送。" : "文字与排版自动保存。调整后点“预览”查看；Ctrl+A 可全选。");
            lineCount.ForeColor = tooMany ? Color.Firebrick : Color.DimGray;
            render.Enabled = send.Enabled = wirelessSend.Enabled = !busy && !tooMany;
            int todoCount = 0;
            if (profile.UsbStorage)
            {
                foreach (string line in SplitTodoText(editor.Text)) if (line.Length != 0) todoCount++;
                lineCount.Text = count + " / " + rows + " 封面行 · " +
                    (tooMany ? "封面需增加行数或精简内容。" : "文字与排版自动保存。") +
                    "\r\n待办 " + todoCount + " / 200 项（每项最多300字），不受封面排版限制。";
                if (todoCount > 200) lineCount.ForeColor = Color.Firebrick;
            }
            todoSend.Enabled = profile.UsbStorage && !busy && todoCount > 0 && todoCount <= 200;
        }

        private static int ValidateTodoText(string text)
        {
            try
            {
                if (new UTF8Encoding(false, true).GetByteCount(text ?? "") > 256 * 1024)
                    throw new ArgumentException("待办文本超过 256 KB，请分批推送。全部草稿已保留。");
            }
            catch (EncoderFallbackException) { throw new ArgumentException("待办中含有无效字符，请重新输入。"); }
            int count = 0, lineNumber = 0;
            foreach (string item in SplitTodoText(text))
            {
                lineNumber++;
                if (item.Length == 0) continue;
                if (++count > 200) throw new ArgumentException("每次最多推送 200 项待办，请减少内容后再推送。全部草稿已保留。");
                int characters = 0;
                for (int index = 0; index < item.Length; index++)
                {
                    char character = item[index];
                    if (character < 32 || character == 127)
                        throw new ArgumentException("第 " + lineNumber + " 行包含制表符或控制字符，请调整后再推送。");
                    if (Char.IsHighSurrogate(character))
                    {
                        if (index + 1 >= item.Length || !Char.IsLowSurrogate(item[index + 1]))
                            throw new ArgumentException("第 " + lineNumber + " 行含有无效字符，请重新输入。");
                        index++;
                    }
                    else if (Char.IsLowSurrogate(character))
                        throw new ArgumentException("第 " + lineNumber + " 行含有无效字符，请重新输入。");
                    if (++characters > 300)
                        throw new ArgumentException("第 " + lineNumber + " 行超过 300 个字符，请缩短后再推送。全部草稿已保留。");
                }
            }
            if (count == 0) throw new ArgumentException("请至少填写一项待办后再推送。");
            return count;
        }

        private static string[] SplitTodoText(string text)
        {
            text = text ?? "";
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            string[] lines = SplitText(text);
            for (int index = 0; index < lines.Length; index++) lines[index] = lines[index].Trim(' ', '\t', '\v', '\f');
            return lines;
        }

        private string SaveTodoOutbox(string text)
        {
            Directory.CreateDirectory(runtime);
            string token = Guid.NewGuid().ToString("N");
            string path = Path.Combine(runtime, "todo-outbox-" + token + ".txt");
            string temporary = Path.Combine(runtime, ".todo-outbox-" + token + ".tmp");
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false, true));
                if (!MoveFileEx(temporary, path, 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }

        private string[] ReadLines()
        {
            string[] content = SplitText(editor.Text);
            int rows = (int)rowChoice.Value;
            if (content.Length > rows) throw new ArgumentException(profile.UsbStorage
                ? "封面文字超过设置的 " + rows + " 行。草稿已保留，请调整封面行数，或直接点“推送待办”追加清单。"
                : "文字超过设置的 " + rows + " 行。全部文字已保留，请增加显示行数或精简内容后再发送。");
            string[] result = new string[rows];
            for (int index = 0; index < result.Length; index++)
                result[index] = index < content.Length ? content[index].Replace("\t", "    ") : "";
            return result;
        }

        private void LoadDraft()
        {
            string path = Path.Combine(runtime, "messageboard-draft.txt");
            string legacyPath = Path.Combine(runtime, "messageboard-draft.json");
            if (!File.Exists(path) && !File.Exists(legacyPath)) return;
            loading = true;
            try
            {
                string text;
                if (File.Exists(path)) text = File.ReadAllText(path, new UTF8Encoding(false, true));
                else
                {
                    // Migrate the former ten-input draft without modifying its
                    // original JSON file. New edits are kept as plain UTF-8 text.
                    string[] saved = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(legacyPath, Encoding.UTF8));
                    if (saved == null || saved.Length != 10) throw new InvalidDataException("旧草稿行数不正确。");
                    text = String.Join(Environment.NewLine, saved);
                }
                editor.Text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
            }
            catch (Exception error) { draftLoadFailed = true; SetNotice("未能读取上次草稿：" + error.Message + " 原文件已保留；编辑后会保存新草稿。", true); }
            finally { loading = false; }
        }

        private void SaveDraft()
        {
            if (draftLoadFailed) return;
            Directory.CreateDirectory(runtime);
            string path = Path.Combine(runtime, "messageboard-draft.txt");
            string temporary = Path.Combine(runtime, ".messageboard-draft." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Save even an unfinished draft exceeding the selected rows. Only
                // preview/send enforce the display limit; typing never loses data.
                File.WriteAllText(temporary, editor.Text, new UTF8Encoding(false));
                if (!MoveFileEx(temporary, path, 0x1 | 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private string CreatePreview(bool requireText)
        {
            string[] text = ReadLines();
            bool any = false;
            foreach (string line in text) if (!String.IsNullOrWhiteSpace(line)) { any = true; break; }
            if (requireText && !any) throw new ArgumentException("请至少填写一行留言后再发送。");
            Bitmap rendered = MessageBoardRenderer.Render(text, profile.Width, profile.Height, ReadLayout());
            try
            {
                Directory.CreateDirectory(runtime);
                string path = Path.Combine(runtime, "messageboard-preview.png");
                rendered.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                // Show the reading orientation on the PC, while the saved PNG
                // above is always the native device size required by the backend.
                if (orientationChoice.SelectedIndex == 1) rendered.RotateFlip(RotateFlipType.Rotate90FlipNone);
                Image previous = preview.Image;
                preview.Image = rendered;
                rendered = null;
                if (previous != null) previous.Dispose();
                return path;
            }
            finally { if (rendered != null) rendered.Dispose(); }
        }

        private void TryPreview()
        {
            try
            {
                SaveLocalState(); CreatePreview(false);
                SetNotice(profile.UsbStorage
                    ? "封面预览已更新。“USB发送”或“无线发送”更新屏保；“推送待办”按次追加整份草稿，保留原有完成状态。相同草稿重发不重复；改了草稿再推送会追加整份草稿。无线操作前先唤醒 Kindle，并拔掉 USB。"
                    : "预览已更新。确认文字后，连接 USB 并点击“发送到 Kindle”。", false);
            }
            catch (Exception error) { ClearPreview(); SetNotice(error.Message, true); }
        }

        private Task SendAsync(bool restoring)
        {
            return SendAsync(restoring, false);
        }

        private async Task SendTodoAsync()
        {
            if (busy || !profile.UsbStorage) return;
            string textPath = null;
            try
            {
                draftTimer.Stop();
                SaveLocalState();
                ValidateTodoText(editor.Text);
                SaveWirelessAddress();
                string script = Path.Combine(root, "scripts", "pw2-todo-send.py");
                if (!File.Exists(python)) throw new FileNotFoundException("未找到 Python，请检查本机配置。", python);
                if (!File.Exists(script)) throw new FileNotFoundException("未找到待办推送程序，请保留完整程序目录。", script);
                textPath = SaveTodoOutbox(editor.Text);
                SetBusy(true);
                SetNotice("正在通过 Wi-Fi 推送待办，请让 Kindle 保持唤醒。原有事项和完成状态会保留。", false);
                MessageBoardResult result = await Task.Run(() => RunTodoBackend(script, textPath));
                SetNotice(TodoResultMessage(result), false);
            }
            catch (Exception error) { SetNotice(error.Message, true); }
            finally
            {
                try
                {
                    if (textPath != null && File.Exists(textPath) &&
                        (File.GetAttributes(textPath) & FileAttributes.ReparsePoint) == 0) File.Delete(textPath);
                }
                catch (Exception error) { SetNotice(notice.Text + "\r\n本机发送临时文本未能清理：" + error.Message, true); }
                SetBusy(false);
            }
        }

        private static string TodoResultMessage(MessageBoardResult result)
        {
            if (result.ExitCode != 0)
            {
                string error = String.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(error)
                    ? "待办推送未完成。请先唤醒 Kindle，确认同一 Wi-Fi 并拔掉 USB，再重试。" : error.Trim());
            }
            Dictionary<string, object> response;
            try { response = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(result.Output); }
            catch (Exception) { throw new InvalidDataException("无法识别待办推送结果，请查看 runtime/paperwhite2/todo-send.stdout.log。"); }
            object success, message;
            if (response == null || !response.TryGetValue("success", out success) || !(success is bool) ||
                !response.TryGetValue("message", out message) || !(message is string) || String.IsNullOrWhiteSpace((string)message))
                throw new InvalidDataException("待办推送返回结果不完整，请查看 runtime/paperwhite2/todo-send.stdout.log。");
            if (!(bool)success) throw new InvalidOperationException((string)message);
            return ((string)message).Trim();
        }

        private static string QuoteProcessArgument(string value)
        {
            // ProcessStartInfo.Arguments follows Windows argv quoting, including
            // doubling a trailing backslash before the closing quote.
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(character);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            return quoted.Append('"').ToString();
        }

        private MessageBoardResult RunTodoBackend(string script, string textPath)
        {
            using (DeviceOperationGuard guard = new DeviceOperationGuard())
                return RunPythonBackend(QuoteProcessArgument(script) + " --project " + QuoteProcessArgument(root) +
                    " --text-file " + QuoteProcessArgument(textPath), "todo-send");
        }

        private async Task SendAsync(bool restoring, bool wireless)
        {
            if (busy) return;
            string imagePath = null;
            try
            {
                draftTimer.Stop();
                SaveLocalState();
                if (!restoring) imagePath = CreatePreview(true);
                if (wireless)
                {
                    if (!profile.UsbStorage) throw new InvalidOperationException("此入口不支持无线发送。");
                    SaveWirelessAddress();
                }
                string script = Path.Combine(root, "scripts", profile.BackendScript);
                if (!File.Exists(python)) throw new FileNotFoundException("未找到 Python，请检查本机配置。", python);
                if (!File.Exists(script)) throw new FileNotFoundException("未找到留言板后台程序，请保留完整程序目录。", script);
                SetBusy(true);
                SetNotice(wireless
                    ? (restoring ? "正在无线恢复之前的封面，请让 Kindle 保持唤醒…" : "正在无线发送，请让 Kindle 保持唤醒…")
                    : (restoring
                        ? (profile.UsbStorage ? "正在恢复之前的封面，请保持 USB 连接…" : "正在恢复原休眠封面，请保持 USB 连接…")
                        : "正在发送留言，请保持 USB 连接…"), false);
                MessageBoardResult result = await Task.Run(() => RunBackend(restoring, imagePath, wireless));
                if (result.ExitCode != 0)
                {
                    string error = String.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(error)
                        ? (wireless ? "无线操作未完成。请先唤醒 Kindle，确认同一 Wi-Fi 并拔掉 USB，再重试。"
                            : "留言板操作未完成，请检查 USB 连接后重试。") : error.Trim());
                }
                string output = result.Output == null ? "" : result.Output.Trim();
                string fallback = wireless
                    ? (restoring ? "之前的封面已通过无线恢复。等 Kindle 回到书库，再短按电源显示封面。"
                        : "留言已通过无线发送。等 Kindle 回到书库，再短按电源显示新封面。")
                    : profile.UsbStorage
                    ? (restoring ? "之前的封面已写回。请安全弹出 Kindle，并按指南重载封面。"
                        : "留言已写入 Kindle。请安全弹出 Kindle，并按指南重载封面。")
                    : (restoring ? "原休眠封面已恢复。"
                        : "留言已发送，Kindle 已进入休眠显示留言。可拔线保留；Kindle 完整重启后需重新发送。");
                SetNotice(output.Length > 0 ? output : fallback, false);
            }
            catch (Exception error) { SetNotice(error.Message, true); }
            finally { SetBusy(false); }
        }

        private MessageBoardResult RunBackend(bool restoring, string imagePath, bool wireless)
        {
            using (DeviceOperationGuard guard = new DeviceOperationGuard())
            {
                if (!profile.UsbStorage) AssertDisplayStopped();
                return RunBackendCore(restoring, imagePath, wireless);
            }
        }

        private void AssertDisplayStopped()
        {
            Dictionary<string, object> state = DisplayForm.ReadJson(Path.Combine(runtime, "kindle-display-controller.json"));
            if (state == null) return;
            long rawPid = DisplayForm.Number(state, "pid");
            long started = DisplayForm.Number(state, "starttimeUtcTicks");
            if (rawPid <= 0 || rawPid > Int32.MaxValue || started <= 0)
                throw new InvalidOperationException("无法确认显示器是否已断开，请先在显示器窗口完成断开操作。");
            int pid = (int)rawPid;
            string pythonHost = RuntimePaths.ReadPythonHostPath(root, python);
            ControllerIdentity identity = DisplayForm.ReadControllerIdentity(pid, python, pythonHost);
            if (identity != null)
            {
                if (identity.StartedTicks == started)
                    throw new InvalidOperationException("请先在显示器窗口点击断开连接，再发送或恢复留言板。");
                return; // A reused PID is not the saved controller.
            }
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != started) return;
                    throw new InvalidOperationException("无法核实仍在运行的显示器进程，请先在显示器窗口完成断开操作。");
                }
            }
            catch (ArgumentException) { } // The saved process has exited.
        }

        private MessageBoardResult RunBackendCore(bool restoring, string imagePath, bool wireless)
        {
            string script = Path.Combine(root, "scripts", profile.BackendScript);
            string action = (wireless ? "wireless-" : "") + (restoring ? "restore" : "install");
            string arguments = "\"" + script + "\" --action " + action + (restoring ? "" : " --image \"" + imagePath + "\"");
            return RunPythonBackend(arguments, "messageboard");
        }

        private MessageBoardResult RunPythonBackend(string arguments, string logPrefix)
        {
            ProcessStartInfo info = new ProcessStartInfo(python, arguments);
            info.WorkingDirectory = root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (Process process = new Process { StartInfo = info })
            {
                process.Start();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                MessageBoardResult result = new MessageBoardResult { ExitCode = process.ExitCode,
                    Output = output.GetAwaiter().GetResult(), Error = error.GetAwaiter().GetResult() };
                File.WriteAllText(Path.Combine(runtime, logPrefix + ".stdout.log"), result.Output, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(runtime, logPrefix + ".stderr.log"), result.Error, new UTF8Encoding(false));
                return result;
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            render.Enabled = send.Enabled = restore.Enabled = close.Enabled = wirelessSend.Enabled = wirelessRestore.Enabled = saveWirelessAddress.Enabled = todoSend.Enabled = !value;
            wirelessAddress.Enabled = !value;
            rowChoice.Enabled = fontChoice.Enabled = fontSizeChoice.Enabled = autoFit.Enabled = orientationChoice.Enabled = !value;
            editor.ReadOnly = value;
            UpdateLineCount();
            UseWaitCursor = value;
        }

        private void SetNotice(string text, bool failure)
        {
            notice.Text = text.Length > 480 ? text.Substring(0, 480) +
                (profile.UsbStorage ? "…（详情见 runtime/paperwhite2 中的留言板日志）" : "…（详情见 runtime 中的留言板日志）") : text;
            notice.ForeColor = failure ? Color.Firebrick : Color.FromArgb(35, 70, 110);
        }
    }
}
