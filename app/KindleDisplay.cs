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
            Dictionary<string, object> config = new Dictionary<string, object>();
            string configPath = Path.Combine(root, "kindle-display.local.json");
            if (File.Exists(configPath))
            {
                config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(configPath, Encoding.UTF8));
                if (config == null) throw new InvalidDataException("kindle-display.local.json must contain a JSON object.");
            }
            PowerShellPath = ConfiguredPath(config, "PowerShellPath", FindPowerShell(), root);
            PythonPath = ConfiguredPath(config, "PythonPath", Path.Combine(root, @".venv\Scripts\python.exe"), root);
            PythonHostPath = ConfiguredPath(config, "PythonHostPath", FindPythonHost(PythonPath), root);
            VncServerPath = ConfiguredPath(config, "VncServerPath",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"TightVNC\tvnserver.exe"), root);
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
            Controls.Add(new Label { Text = "先选方向再连接；连接后点击“应用方向”调整。\r\n可最小化到任务栏；退出或关闭窗口会先断开连接。",
                Location = new Point(26, 260), Size = new Size(570, 43), ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F) });
            connect.Click += async delegate
            {
                if (snapshot.Running) { Notice("已接管当前连接", "Kindle 已在后台连接，无需重复启动。", false); return; }
                if (snapshot.NeedsCleanup) { Notice("请先断开上次连接", "点击“断开连接”清理后，再点击“连接”。", true); return; }
                try { WriteOrientationRequest(); }
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

        private static long Number(Dictionary<string, object> data, string name)
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

        private static Dictionary<string, object> ReadJson(string path)
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
        private ControllerIdentity ReadControllerIdentity(int pid)
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
            ControllerIdentity controller = ReadControllerIdentity((int)Number(state, "pid"));
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

        private async Task ExecuteActionAsync(string action)
        {
            if (busy || allowClose) return;
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
                WriteOrientationRequest();
                Notice("正在应用方向…", RotationLabel(requestedRotation) + "，请等待十几秒。", false);
            }
            catch (Exception) { Notice("方向请求未发送", "请稍后再试。", true); }
        }

        private void SendRefreshRequest()
        {
            if (!snapshot.Running || busy || closingRequested) return;
            try
            {
                DateTime requested = DateTime.UtcNow;
                File.WriteAllText(Runtime("kindle-display.refresh"),
                    requested.ToString("o", CultureInfo.InvariantCulture), new UTF8Encoding(false));
                refreshRequested = requested;
                Notice("已请求刷新", "画面将重新载入，可能短暂切换页面。", false);
            }
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
}
