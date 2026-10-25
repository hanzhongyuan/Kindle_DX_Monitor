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

    internal static class MessageBoardRenderer
    {
        internal static Bitmap Render(string[] lines)
        {
            return Render(lines, 824, 1200);
        }

        internal static Bitmap Render(string[] lines, int width, int height)
        {
            if (lines == null || lines.Length != 10) throw new ArgumentException("留言板需要十行文本。");
            if (!((width == 824 && height == 1200) || (width == 758 && height == 1024)))
                throw new ArgumentException("不支持的留言板尺寸。");
            float horizontalScale = width / 824F, verticalScale = height / 1200F;
            float fontScale = Math.Min(horizontalScale, verticalScale);
            float left = 52 * horizontalScale, right = width - left, textLeft = 108 * horizontalScale;
            Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (Font title = new Font("Microsoft YaHei", 48F * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font number = new Font("Microsoft YaHei", 24F * fontScale, FontStyle.Regular, GraphicsUnit.Pixel))
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
                    for (int index = 0; index < 10; index++)
                    {
                        string text = lines[index] ?? "";
                        if (text.IndexOfAny(new char[] { '\r', '\n', '\t' }) >= 0)
                            throw new ArgumentException("第 " + (index + 1) + " 行包含换行或制表符，请按一行一条填写。");
                        float top = (158 + index * 91) * verticalScale;
                        graphics.DrawString((index + 1).ToString("00", CultureInfo.InvariantCulture), number,
                            Brushes.DimGray, new PointF(left, top + 14 * verticalScale));
                        using (Font lineFont = FitFont(graphics, text, format, index + 1, right - textLeft, fontScale))
                            graphics.DrawString(text, lineFont, Brushes.Black, new PointF(textLeft,
                                top + (60 * verticalScale - lineFont.GetHeight(graphics)) / 2), format);
                        graphics.DrawLine(rule, textLeft, top + 69 * verticalScale, right, top + 69 * verticalScale);
                    }
                    graphics.DrawString("更新于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), footer,
                        Brushes.DimGray, new PointF(left, 1135 * verticalScale));
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static Font FitFont(Graphics graphics, string text, StringFormat format, int lineNumber,
            float availableWidth, float fontScale)
        {
            for (float size = 42; size >= 26; size -= 1)
            {
                Font font = new Font("Microsoft YaHei", size * fontScale, FontStyle.Regular, GraphicsUnit.Pixel);
                if (graphics.MeasureString(text, font, Int32.MaxValue, format).Width <= availableWidth) return font;
                font.Dispose();
            }
            throw new ArgumentException("第 " + lineNumber + " 行太长，缩小字体后仍放不下。请拆到下一行或缩短文字。");
        }
    }

    internal sealed class MessageBoardForm : Form
    {
        private readonly string root, python, runtime;
        private readonly MessageBoardProfile profile;
        private readonly TextBox editor = new TextBox();
        private readonly Label lineCount = new Label();
        private readonly PictureBox preview = new PictureBox();
        private readonly Label notice = new Label();
        private readonly Button render = new Button(), send = new Button(), restore = new Button(), close = new Button();
        private readonly System.Windows.Forms.Timer draftTimer = new System.Windows.Forms.Timer();
        private bool busy, loading, draftLoadFailed;

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
                ? "仅限 Paperwhite 2；首次需按指南配置设备。最多十行，编辑自动保存。\r\n连接 Kindle 存储盘发送；发送后请安全弹出，按下方提示重载封面。"
                : "最多十行，支持整段粘贴并自动保存；扩展屏运行时需先手动断开。\r\n休眠后可拔线；Kindle 重启后需重新发送。",
                Location = new Point(26, 58), Size = new Size(555, 48), ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F) });
            editor.Location = new Point(26, 118);
            editor.Font = new Font("Microsoft YaHei UI", 16F);
            editor.Multiline = true;
            editor.AcceptsReturn = true;
            editor.AcceptsTab = false;
            editor.WordWrap = false;
            editor.ScrollBars = ScrollBars.Both;
            editor.ShortcutsEnabled = true;
            editor.MaxLength = Int32.MaxValue;
            editor.Size = new Size(538, editor.Font.Height * 10 + SystemInformation.HorizontalScrollBarHeight + 12);
            editor.AccessibleName = "留言内容，每行一条，最多十行";
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
            Controls.Add(new Label { Text = "竖向封面 · " + profile.Width + " × " + profile.Height,
                Location = new Point(594, preview.Bottom + 10), Size = new Size(310, 24),
                ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleCenter });
            Button[] buttons = { render, send, restore, close };
            string[] captions = { "预览", "发送到 Kindle", profile.UsbStorage ? "恢复之前的封面" : "恢复原休眠封面", "关闭" };
            int[] widths = { 98, 152, 170, 92 };
            int x = 26;
            int buttonsTop = Math.Max(505, lineCount.Bottom + 12);
            for (int index = 0; index < buttons.Length; index++)
            {
                buttons[index].Text = captions[index];
                buttons[index].Location = new Point(x, buttonsTop);
                buttons[index].Size = new Size(widths[index], 38);
                buttons[index].UseVisualStyleBackColor = true;
                Controls.Add(buttons[index]);
                x += widths[index] + 10;
            }
            notice.Location = new Point(26, buttonsTop + 60);
            notice.Size = new Size(878, 77);
            ClientSize = new Size(930, Math.Max(665, notice.Bottom + 23));
            notice.ForeColor = Color.DimGray;
            notice.Text = profile.UsbStorage
                ? "先编辑并预览，再连接 Kindle 存储盘发送。Paperwhite 2 草稿单独保存在本机。"
                : "先编辑并预览，再连接 USB 数据线发送。草稿只保存在本机。";
            Controls.Add(notice);
            render.Click += delegate { TryPreview(); };
            send.Click += async delegate { await SendAsync(false); };
            restore.Click += async delegate { await SendAsync(true); };
            close.Click += delegate { Close(); };
            draftTimer.Interval = 600;
            draftTimer.Tick += delegate
            {
                draftTimer.Stop();
                try { SaveDraft(); }
                catch (Exception error) { SetNotice("草稿未能保存：" + error.Message, true); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (busy) { args.Cancel = true; SetNotice("正在传输，请等待当前操作完成后关闭。", false); return; }
                draftTimer.Stop();
                try { SaveDraft(); }
                catch (Exception error) { SetNotice("草稿未能保存：" + error.Message, true); args.Cancel = true; }
            };
            FormClosed += delegate
            {
                draftTimer.Stop(); draftTimer.Dispose();
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
            };
            LoadDraft();
            UpdateLineCount();
            if (!draftLoadFailed) TryPreview();
        }

        private static string[] SplitText(string text)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length == 0) return new string[0];
            string[] result = normalized.Split(new char[] { '\n' }, StringSplitOptions.None);
            // A final line terminator from a copied text file does not add an
            // eleventh line. Any preceding empty lines are still preserved.
            if (normalized.EndsWith("\n", StringComparison.Ordinal)) Array.Resize(ref result, result.Length - 1);
            return result;
        }

        private void UpdateLineCount()
        {
            int count = SplitText(editor.Text).Length;
            bool tooMany = count > 10;
            lineCount.Text = count.ToString(CultureInfo.InvariantCulture) + " / 10 行 · " +
                (tooMany ? "已保留全部文字，请缩减到十行后再预览或发送。" : "自动保存到本机，下次打开继续编辑。Ctrl+A 可全选。");
            lineCount.ForeColor = tooMany ? Color.Firebrick : Color.DimGray;
            render.Enabled = send.Enabled = !busy && !tooMany;
        }

        private string[] ReadLines()
        {
            string[] content = SplitText(editor.Text);
            if (content.Length > 10) throw new ArgumentException("留言最多十行。全部文字已保留，请缩减后再发送。");
            string[] result = new string[10];
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
                // Save even an unfinished draft with more than ten lines. Only
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
            Bitmap rendered = MessageBoardRenderer.Render(text, profile.Width, profile.Height);
            try
            {
                Directory.CreateDirectory(runtime);
                string path = Path.Combine(runtime, "messageboard-preview.png");
                rendered.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
                SaveDraft(); CreatePreview(false);
                SetNotice(profile.UsbStorage
                    ? "预览已更新。请连接 Kindle，确认电脑能看到 Kindle 存储盘，再点击“发送到 Kindle”。"
                    : "预览已更新。确认文字后，连接 USB 并点击“发送到 Kindle”。", false);
            }
            catch (Exception error) { SetNotice(error.Message, true); }
        }

        private async Task SendAsync(bool restoring)
        {
            if (busy) return;
            string imagePath = null;
            try
            {
                SaveDraft();
                if (!restoring) imagePath = CreatePreview(true);
                string script = Path.Combine(root, "scripts", profile.BackendScript);
                if (!File.Exists(python)) throw new FileNotFoundException("未找到 Python，请检查本机配置。", python);
                if (!File.Exists(script)) throw new FileNotFoundException("未找到留言板后台程序，请保留完整程序目录。", script);
                SetBusy(true);
                SetNotice(restoring
                    ? (profile.UsbStorage ? "正在恢复之前的封面，请保持 USB 连接…" : "正在恢复原休眠封面，请保持 USB 连接…")
                    : "正在发送留言，请保持 USB 连接…", false);
                MessageBoardResult result = await Task.Run(() => RunBackend(restoring, imagePath));
                if (result.ExitCode != 0)
                {
                    string error = String.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(error) ? "留言板操作未完成，请检查 USB 连接后重试。" : error.Trim());
                }
                string output = result.Output == null ? "" : result.Output.Trim();
                string fallback = profile.UsbStorage
                    ? (restoring ? "之前的封面已写回。请安全弹出 Kindle，并按指南重载封面。"
                        : "留言已写入 Kindle。请安全弹出 Kindle，并按指南重载封面。")
                    : (restoring ? "原休眠封面已恢复。"
                        : "留言已发送，Kindle 已进入休眠显示留言。可拔线保留；Kindle 完整重启后需重新发送。");
                SetNotice(output.Length > 0 ? output : fallback, false);
            }
            catch (Exception error) { SetNotice(error.Message, true); }
            finally { SetBusy(false); }
        }

        private MessageBoardResult RunBackend(bool restoring, string imagePath)
        {
            using (DeviceOperationGuard guard = new DeviceOperationGuard())
            {
                if (!profile.UsbStorage) AssertDisplayStopped();
                return RunBackendCore(restoring, imagePath);
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

        private MessageBoardResult RunBackendCore(bool restoring, string imagePath)
        {
            string script = Path.Combine(root, "scripts", profile.BackendScript);
            string arguments = "\"" + script + "\" --action " + (restoring ? "restore" : "install --image \"" + imagePath + "\"");
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
                File.WriteAllText(Path.Combine(runtime, "messageboard.stdout.log"), result.Output, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(runtime, "messageboard.stderr.log"), result.Error, new UTF8Encoding(false));
                return result;
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            render.Enabled = send.Enabled = restore.Enabled = close.Enabled = !value;
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
