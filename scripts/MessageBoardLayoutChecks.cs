// Offline checks: compile with app/KindleDisplay.cs and select this Main.
// Every form receives an isolated project root. Any backend subprocess is this
// test executable recording argv; no real Python backend or device is invoked.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using KindleDisplay;

internal static class MessageBoardLayoutChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static int assertions;
    private static string output;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 5 && Path.GetFileName(args[0]) == "pw2-todo-send.py" &&
                args[1] == "--project" && args[3] == "--text-file")
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(args[2], "captured-argv.json"), new JavaScriptSerializer().Serialize(args), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(args[2], "captured-text.txt"), File.ReadAllText(args[4], new UTF8Encoding(false, true)), new UTF8Encoding(false));
                Console.WriteLine(File.ReadAllText(Path.Combine(args[2], "fake-response.json"), Encoding.UTF8));
                return 0;
            }
            if (args.Length != 1) throw new ArgumentException("Supply an isolated output directory.");
            output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (MessageBoardProfile profile in Profiles())
            {
                CheckRenderer(profile);
                CheckForm(profile);
                CheckCorruptSettings(profile);
                CheckSavedFontAlias(profile);
            }
            CheckIndependentSettings();
            CheckTodoPush();
            Console.WriteLine("PASS: " + assertions + " assertions; no device connections or user drafts accessed.");
            Console.WriteLine("Artifacts: " + output);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static MessageBoardProfile[] Profiles()
    {
        return new MessageBoardProfile[] { MessageBoardProfile.Dx, MessageBoardProfile.Paperwhite2 };
    }

    private static string Name(MessageBoardProfile profile)
    {
        return profile.UsbStorage ? "pw2" : "dx";
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        assertions++;
    }

    private static T Field<T>(MessageBoardForm form, string name)
    {
        FieldInfo field = typeof(MessageBoardForm).GetField(name, PrivateInstance);
        if (field == null) throw new Exception("Missing form field: " + name);
        return (T)field.GetValue(form);
    }

    private static object Call(MessageBoardForm form, string name, params object[] arguments)
    {
        MethodInfo method = typeof(MessageBoardForm).GetMethod(name, PrivateInstance);
        if (method == null) throw new Exception("Missing form method: " + name);
        try { return method.Invoke(form, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    private static object CallStatic(string name, params object[] arguments)
    {
        MethodInfo method = typeof(MessageBoardForm).GetMethod(name, PrivateStatic);
        if (method == null) throw new Exception("Missing static form method: " + name);
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    private static void Reject(Action operation, string expectedText)
    {
        try { operation(); }
        catch (ArgumentException error)
        {
            Check(error.Message.Contains(expectedText), "overflow/validation identifies the cause: " + error.Message);
            return;
        }
        throw new Exception("Expected ArgumentException containing: " + expectedText);
    }

    private static MessageBoardLayout Layout(int rows, int size, bool autoFit, bool landscape)
    {
        return new MessageBoardLayout { Rows = rows, FontSize = size, FontFamily = "Microsoft YaHei",
            AutoFit = autoFit, Landscape = landscape };
    }

    private static string[] Lines(int count, string text)
    {
        string[] result = new string[count];
        for (int index = 0; index < count; index++) result[index] = text;
        return result;
    }

    private static Bitmap Render(MessageBoardProfile profile, MessageBoardLayout layout, string[] lines)
    {
        return MessageBoardRenderer.Render(lines, profile.Width, profile.Height, layout);
    }

    // Count broad gray strokes from the produced bitmap, without assuming row positions.
    private static int CountRules(Bitmap bitmap)
    {
        int count = 0;
        bool previous = false;
        for (int y = 0; y < bitmap.Height; y++)
        {
            int gray = 0;
            for (int x = bitmap.Width / 4; x < bitmap.Width * 9 / 10; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R >= 170 && pixel.R <= 210 && pixel.G == pixel.R && pixel.B == pixel.R) gray++;
            }
            bool current = gray >= bitmap.Width / 2;
            if (current && !previous) count++;
            previous = current;
        }
        return count;
    }

    private static Rectangle InkBounds(Bitmap text, Bitmap blank, out int pixels)
    {
        int minX = text.Width, minY = text.Height, maxX = -1, maxY = -1;
        pixels = 0;
        // Exclude heading and timestamp; compare against an otherwise identical blank cover.
        for (int y = text.Height * 14 / 100; y < text.Height * 89 / 100; y++)
            for (int x = text.Width / 10; x < text.Width * 95 / 100; x++)
                if (text.GetPixel(x, y).ToArgb() != blank.GetPixel(x, y).ToArgb())
                {
                    pixels++;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static int ChangedPixels(Bitmap first, Bitmap second, bool bodyOnly)
    {
        Check(first.Size == second.Size, "compared bitmap dimensions match");
        int count = 0;
        int top = bodyOnly ? first.Height * 14 / 100 : 0;
        int bottom = bodyOnly ? first.Height * 89 / 100 : first.Height;
        for (int y = top; y < bottom; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
    }

    private static void CheckRenderer(MessageBoardProfile profile)
    {
        foreach (bool landscape in new bool[] { false, true })
            foreach (int rows in new int[] { 1, 4, 10, 30 })
            {
                MessageBoardLayout layout = Layout(rows, 36, true, landscape);
                using (Bitmap cover = Render(profile, layout, Lines(rows, "")))
                {
                    Check(cover.Width == profile.Width && cover.Height == profile.Height,
                        Name(profile) + " export remains native dimensions");
                    Check(cover.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "opaque white background");
                    if (landscape) cover.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    Check(CountRules(cover) == rows, Name(profile) + " actual rule count equals " + rows);
                }
            }
        foreach (bool landscape in new bool[] { false, true })
            foreach (int rows in new int[] { 1, 10, 30 })
                using (Bitmap cover = Render(profile, Layout(rows, 42, true, landscape), Lines(rows, "中文 ABC")))
                    Check(cover.Height == profile.Height, "populated cover at " + rows + " rows, landscape=" + landscape);

        Rectangle smallBounds, largeBounds;
        int smallPixels, largePixels;
        string[] text = new string[] { "Ag国", "", "", "" };
        using (Bitmap small = Render(profile, Layout(4, 24, false, false), text))
        using (Bitmap blankSmall = Render(profile, Layout(4, 24, false, false), Lines(4, "")))
        using (Bitmap large = Render(profile, Layout(4, 48, false, false), text))
        using (Bitmap blankLarge = Render(profile, Layout(4, 48, false, false), Lines(4, "")))
        {
            smallBounds = InkBounds(small, blankSmall, out smallPixels);
            largeBounds = InkBounds(large, blankLarge, out largePixels);
            Check(smallPixels > 50, "sample glyphs rendered");
            Check(largeBounds.Height > smallBounds.Height * 1.5, "larger requested size increases actual ink height");
            Check(largePixels > smallPixels * 2, "larger requested size increases actual ink area");
            small.Save(Path.Combine(output, Name(profile) + "-font24.png"), ImageFormat.Png);
            large.Save(Path.Combine(output, Name(profile) + "-font48.png"), ImageFormat.Png);
        }
        MessageBoardLayout sans = Layout(4, 32, false, false);
        MessageBoardLayout serif = Layout(4, 32, false, false);
        sans.FontFamily = "Arial"; serif.FontFamily = "Times New Roman";
        using (Bitmap first = Render(profile, sans, new string[] { "Kindle sample", "", "", "" }))
        using (Bitmap second = Render(profile, serif, new string[] { "Kindle sample", "", "", "" }))
            Check(ChangedPixels(first, second, true) > 500, "font family changes actual glyph pixels");

        string[] wide = new string[] { new string('W', 30), "", "", "" };
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(4, 42, false, false), wide)) { } }, "第 1 行");
        using (Bitmap fitted = Render(profile, Layout(4, 42, true, false), wide))
            Check(fitted.Width == profile.Width, "automatic fit handles a long but displayable line");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(30, 120, false, false), Lines(30, "字"))) { } }, "第 1 行");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(1, 12, true, false), Lines(1, new string('长', 500)))) { } }, "第 1 行");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(1, 24, true, false), Lines(1, "换\n行"))) { } }, "第 1 行");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(0, 24, true, false), new string[0])) { } }, "行数");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(31, 24, true, false), new string[31])) { } }, "行数");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(1, 11, true, false), Lines(1, "字"))) { } }, "字号");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(1, 121, true, false), Lines(1, "字"))) { } }, "字号");
        Reject(delegate { using (Bitmap ignored = Render(profile, Layout(4, 24, true, false), Lines(3, "字"))) { } }, "行数");
        Console.WriteLine("PASS renderer: " + Name(profile));
    }

    private static MessageBoardForm Open(string projectRoot, MessageBoardProfile profile, string python = null)
    {
        MessageBoardForm form = new MessageBoardForm(projectRoot, python ?? Path.Combine(projectRoot, "never-executed-python.exe"), profile);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        Field<System.Windows.Forms.Timer>(form, "draftTimer").Stop();
        return form;
    }

    private static void Configure(MessageBoardForm form, int rows, int size, string font, bool fit, bool landscape)
    {
        using (FontFamily family = new FontFamily(font)) font = family.Name;
        Field<NumericUpDown>(form, "rowChoice").Value = rows;
        Field<NumericUpDown>(form, "fontSizeChoice").Value = size;
        Field<ComboBox>(form, "fontChoice").SelectedItem = font;
        Check(Field<ComboBox>(form, "fontChoice").SelectedItem as string == font, "selected font is installed");
        Field<CheckBox>(form, "autoFit").Checked = fit;
        Field<ComboBox>(form, "orientationChoice").SelectedIndex = landscape ? 1 : 0;
    }

    private static void Snapshot(MessageBoardForm form, string path)
    {
        Application.DoEvents();
        using (Bitmap snapshot = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(snapshot, new Rectangle(Point.Empty, snapshot.Size));
            snapshot.Save(path, ImageFormat.Png);
        }
    }

    private static void CheckForm(MessageBoardProfile profile)
    {
        string project = Path.Combine(output, Name(profile) + "-form");
        using (MessageBoardForm form = Open(project, profile))
        {
            TextBox editor = Field<TextBox>(form, "editor");
            PictureBox preview = Field<PictureBox>(form, "preview");
            MessageBoardLayout defaults = (MessageBoardLayout)Call(form, "ReadLayout");
            using (FontFamily family = new FontFamily("Microsoft YaHei"))
                Check(defaults.FontFamily == family.Name, "default remains Microsoft YaHei under localized font names");
            Check(defaults.Rows == 10 && defaults.FontSize == (profile.UsbStorage ? 36 : 42) && defaults.AutoFit && !defaults.Landscape,
                "new installation retains default ten-row portrait layout");
            Reject(delegate { Call(form, "CreatePreview", true); }, "至少填写");
            editor.Text = "第一行\r\n\r\n第三行\r\n";
            Configure(form, 3, 32, "Microsoft YaHei", false, false);
            string[] lines = (string[])Call(form, "ReadLines");
            Check(lines.Length == 3 && lines[1] == "" && lines[2] == "第三行", "blank rows and pasted trailing newline retained correctly");
            string png = (string)Call(form, "CreatePreview", true);
            Check(preview.Image.Width == profile.Width && preview.Image.Height == profile.Height, "portrait preview dimensions");
            Snapshot(form, Path.Combine(output, Name(profile) + "-window-portrait.png"));
            string original = editor.Text;
            Field<NumericUpDown>(form, "rowChoice").Value = 2;
            Check(editor.Text == original, "reducing rows never truncates draft");
            Check(!Field<Button>(form, "render").Enabled && !Field<Button>(form, "send").Enabled &&
                !Field<Button>(form, "wirelessSend").Enabled, "too many rows disable all preview/send entries");
            Check(preview.Image == null, "layout change clears stale preview");
            Reject(delegate { Call(form, "ReadLines"); }, "2 行");
            Call(form, "SaveLocalState");
            Check(File.ReadAllText(Path.Combine(project, "runtime", profile.RuntimeSubdirectory, "messageboard-draft.txt"), Encoding.UTF8) == original,
                "over-limit draft saved in full");

            Configure(form, 4, 32, "Microsoft YaHei", true, true);
            Check(Field<Button>(form, "send").Enabled, "increasing row count restores sending");
            png = (string)Call(form, "CreatePreview", true);
            Check(preview.Image.Width == profile.Height && preview.Image.Height == profile.Width, "landscape preview is displayed upright");
            using (Bitmap exported = new Bitmap(png))
            using (Bitmap displayed = new Bitmap(preview.Image))
            {
                Check(exported.Width == profile.Width && exported.Height == profile.Height, "landscape export keeps device dimensions");
                exported.RotateFlip(RotateFlipType.Rotate90FlipNone);
                Check(ChangedPixels(exported, displayed, false) == 0, "preview equals actual export rotated upright");
                exported.Save(Path.Combine(output, Name(profile) + "-landscape-upright.png"), ImageFormat.Png);
            }
            Snapshot(form, Path.Combine(output, Name(profile) + "-window-landscape.png"));
            Call(form, "SetBusy", true);
            foreach (string name in new string[] { "rowChoice", "fontSizeChoice", "fontChoice", "autoFit", "orientationChoice", "render", "send", "wirelessSend" })
                Check(!Field<Control>(form, name).Enabled, "busy disables " + name);
            Check(editor.ReadOnly, "busy protects text");
            Call(form, "SetBusy", false);
            foreach (string name in new string[] { "rowChoice", "fontSizeChoice", "fontChoice", "autoFit", "orientationChoice", "render", "send" })
                Check(Field<Control>(form, name).Enabled, "idle restores " + name);
            Check(!editor.ReadOnly, "idle restores editing");
            editor.Text = "新的内容";
            Check(preview.Image == null, "text change clears stale preview");
            Call(form, "CreatePreview", true);
            Field<NumericUpDown>(form, "fontSizeChoice").Value = 33;
            Check(preview.Image == null, "font change clears stale preview");
            form.Close();
        }
        Console.WriteLine("PASS UI: " + Name(profile));
    }

    private static void CheckIndependentSettings()
    {
        string project = Path.Combine(output, "shared-profile-root");
        using (MessageBoardForm dx = Open(project, MessageBoardProfile.Dx))
        using (MessageBoardForm pw = Open(project, MessageBoardProfile.Paperwhite2))
        {
            Configure(dx, 3, 32, "Arial", false, true);
            Configure(pw, 7, 24, "Times New Roman", true, false);
            Field<TextBox>(dx, "editor").Text = "DX draft";
            Field<TextBox>(pw, "editor").Text = "PW2 draft";
            dx.Close(); pw.Close();
        }
        using (MessageBoardForm dx = Open(project, MessageBoardProfile.Dx))
        using (MessageBoardForm pw = Open(project, MessageBoardProfile.Paperwhite2))
        {
            MessageBoardLayout dxLayout = (MessageBoardLayout)Call(dx, "ReadLayout");
            MessageBoardLayout pwLayout = (MessageBoardLayout)Call(pw, "ReadLayout");
            Check(dxLayout.Rows == 3 && dxLayout.FontSize == 32 && dxLayout.FontFamily == "Arial" && !dxLayout.AutoFit && dxLayout.Landscape,
                "DX saved settings reload independently");
            Check(pwLayout.Rows == 7 && pwLayout.FontSize == 24 && pwLayout.FontFamily == "Times New Roman" && pwLayout.AutoFit && !pwLayout.Landscape,
                "PW2 saved settings reload independently");
            Check(Field<TextBox>(dx, "editor").Text == "DX draft" && Field<TextBox>(pw, "editor").Text == "PW2 draft", "device drafts remain isolated");
            dx.Close(); pw.Close();
        }
        Console.WriteLine("PASS persistence and profile isolation");
    }

    private static void CheckCorruptSettings(MessageBoardProfile profile)
    {
        string[] invalid = new string[] { "{unreadable-json", "{\"Rows\":999,\"FontSize\":32,\"FontFamily\":\"Microsoft YaHei\",\"AutoFit\":true}" };
        for (int index = 0; index < invalid.Length; index++)
        {
            string project = Path.Combine(output, Name(profile) + "-corrupt-" + index);
            string runtime = Path.Combine(project, "runtime", profile.RuntimeSubdirectory);
            Directory.CreateDirectory(runtime);
            string path = Path.Combine(runtime, "messageboard-layout.json");
            File.WriteAllText(path, invalid[index], new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(runtime, "messageboard-draft.txt"), "保留草稿", new UTF8Encoding(false));
            using (MessageBoardForm form = Open(project, profile))
            {
                Check(Field<TextBox>(form, "editor").Text == "保留草稿", "bad settings do not prevent draft recovery");
                Check(Field<Label>(form, "notice").Text.Contains("未能读取上次排版"), "corrupt settings warning remains visible");
                Field<TextBox>(form, "editor").Text = "只修改文字";
                Call(form, "SaveLocalState");
                Check(File.ReadAllText(path, Encoding.UTF8) == invalid[index], "bad settings remain intact on ordinary save");
                form.Close();
            }
            Check(File.ReadAllText(path, Encoding.UTF8) == invalid[index], "bad settings remain intact on close");
            using (MessageBoardForm form = Open(project, profile))
            {
                Field<NumericUpDown>(form, "rowChoice").Value = 8;
                Call(form, "SaveLocalState");
                Check(File.ReadAllText(path, Encoding.UTF8) != invalid[index], "explicit settings edit replaces unusable saved layout");
                form.Close();
            }
        }
        Console.WriteLine("PASS corrupt settings: " + Name(profile));
    }

    private static void CheckSavedFontAlias(MessageBoardProfile profile)
    {
        string project = Path.Combine(output, Name(profile) + "-font-alias");
        string runtime = Path.Combine(project, "runtime", profile.RuntimeSubdirectory);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "messageboard-layout.json"),
            "{\"Rows\":4,\"FontSize\":32,\"FontFamily\":\"Microsoft YaHei\",\"AutoFit\":false,\"Landscape\":true}",
            new UTF8Encoding(false));
        using (MessageBoardForm form = Open(project, profile))
        {
            MessageBoardLayout layout = (MessageBoardLayout)Call(form, "ReadLayout");
            using (FontFamily family = new FontFamily("Microsoft YaHei"))
                Check(layout.FontFamily == family.Name, "English saved font name resolves to localized installed name");
            Check(layout.Rows == 4 && layout.FontSize == 32 && !layout.AutoFit && layout.Landscape,
                "font alias normalization preserves the rest of the saved layout");
            Check(!Field<Label>(form, "notice").Text.Contains("未能读取上次排版"), "valid English font alias does not report corruption");
            form.Close();
        }
        Console.WriteLine("PASS font alias: " + Name(profile));
    }

    private static void CheckTodoPush()
    {
        Check((int)CallStatic("ValidateTodoText", "\uFEFF \t第一项\f\r\n\t\r第二项\n") == 2,
            "todo validation matches BOM, ASCII trim, newline and empty-row rules");
        Check((int)CallStatic("ValidateTodoText", "\u3000\n\u0085") == 2,
            "non-ASCII whitespace is preserved consistently with backend");
        Check((int)CallStatic("ValidateTodoText", String.Join("\n", Lines(200, "待办"))) == 200, "200 todo items are accepted");
        string emoji = String.Concat(Lines(300, "\uD83D\uDE00"));
        Check((int)CallStatic("ValidateTodoText", emoji) == 1, "300 Unicode scalar values accepted, including surrogate pairs");
        Reject(delegate { CallStatic("ValidateTodoText", emoji + "字"); }, "300");
        Reject(delegate { CallStatic("ValidateTodoText", String.Join("\n", Lines(201, "事项"))); }, "200");
        Reject(delegate { CallStatic("ValidateTodoText", " \t\r\n\f"); }, "至少");
        Reject(delegate { CallStatic("ValidateTodoText", "事项\t内容"); }, "控制字符");
        Reject(delegate { CallStatic("ValidateTodoText", "事项\u0000内容"); }, "控制字符");
        Reject(delegate { CallStatic("ValidateTodoText", "\uD83D"); }, "无效字符");
        Reject(delegate { CallStatic("ValidateTodoText", new string(' ', 256 * 1024 + 1)); }, "256 KB");
        string delivered = "已送达 Kindle，打开离线待办后自动接收。";
        string response = new JavaScriptSerializer().Serialize(new { success = true, message = delivered });
        Check((string)CallStatic("TodoResultMessage", new MessageBoardResult { ExitCode = 0, Output = response, Error = "" }) == delivered,
            "backend message distinguishes delivery from device receipt");
        try { CallStatic("TodoResultMessage", new MessageBoardResult { ExitCode = 0, Output = "not json", Error = "" }); throw new Exception("Expected invalid JSON rejection"); }
        catch (InvalidDataException) { assertions++; }
        try { CallStatic("TodoResultMessage", new MessageBoardResult { ExitCode = 0, Output = "{\"success\":true}", Error = "" }); throw new Exception("Expected incomplete JSON rejection"); }
        catch (InvalidDataException) { assertions++; }
        try { CallStatic("TodoResultMessage", new MessageBoardResult { ExitCode = 1, Output = "", Error = "设备未唤醒" }); throw new Exception("Expected backend error"); }
        catch (InvalidOperationException error) { Check(error.Message == "设备未唤醒", "nonzero backend failure retains actionable message"); }

        string project = Path.Combine(output, "pw2 todo fake backend with spaces") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(project, "scripts"));
        File.WriteAllText(Path.Combine(project, "scripts", "pw2-todo-send.py"), "test recorder only", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(project, "fake-response.json"), response, new UTF8Encoding(false));
        using (MessageBoardForm form = Open(project, MessageBoardProfile.Paperwhite2, Assembly.GetExecutingAssembly().Location))
        {
            TextBox editor = Field<TextBox>(form, "editor");
            Button push = Field<Button>(form, "todoSend");
            Check(form.Controls.Contains(push) && !push.Enabled, "PW2 shows a separate todo button, disabled for empty text");
            string firstOutbox = (string)Call(form, "SaveTodoOutbox", "第一份待办");
            string secondOutbox = (string)Call(form, "SaveTodoOutbox", "另一份待办");
            Check(firstOutbox != secondOutbox, "concurrent requests receive independent outbox paths");
            Check(File.ReadAllText(firstOutbox, Encoding.UTF8) == "第一份待办" &&
                File.ReadAllText(secondOutbox, Encoding.UTF8) == "另一份待办", "separate outboxes preserve both request snapshots");
            string text = String.Join("\r\n", Lines(50, "事项含引号 \" 和字面量 $(nothing)"));
            editor.Text = text;
            Configure(form, 1, 120, "Microsoft YaHei", false, false);
            Check(push.Enabled && !Field<Button>(form, "render").Enabled && !Field<Button>(form, "wirelessSend").Enabled,
                "todo push bypasses cover row and font rendering limits");
            Check(push.Right <= form.ClientSize.Width && push.Bottom <= form.ClientSize.Height, "todo button fits window");
            Call(form, "SetBusy", true);
            Check(!push.Enabled, "device operation blocks concurrent todo push");
            Call(form, "SetBusy", false);
            Check(push.Enabled, "idle restores todo push despite cover overflow");
            Field<TextBox>(form, "wirelessAddress").Text = "192.168.1.25";
            System.Threading.Tasks.Task pending = (System.Threading.Tasks.Task)Call(form, "SendTodoAsync");
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
            Check(pending.IsCompleted, "local fake backend completed");
            pending.GetAwaiter().GetResult();
            Check(Field<Label>(form, "notice").Text == delivered, "push completes through actual process/JSON/UI flow");
            string[] captured = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(Path.Combine(project, "captured-argv.json"), Encoding.UTF8));
            Check(captured[2] == project, "Windows argv preserves spaces and trailing project backslash");
            string outbox = captured[4];
            Check(Path.GetDirectoryName(outbox) == Path.Combine(project, "runtime", "paperwhite2") &&
                Path.GetFileName(outbox).StartsWith("todo-outbox-"), "text-file argument identifies isolated outbox");
            Check(File.ReadAllText(Path.Combine(project, "captured-text.txt"), new UTF8Encoding(false, true)) == text,
                "backend receives exact original UTF-8 draft");
            Check(!File.Exists(outbox), "completed request removes only its own temporary text");
            Check(File.Exists(firstOutbox) && File.Exists(secondOutbox), "cleanup leaves other request snapshots intact");
            Check(File.ReadAllText(Path.Combine(project, "runtime", "paperwhite2", "todo-send.stdout.log"), Encoding.UTF8).Contains(delivered),
                "todo response logged independently");
            Check(push.Enabled && !editor.ReadOnly, "successful push restores editable controls");
            editor.Text = String.Join("\n", Lines(201, "事项"));
            Check(!push.Enabled, "201 entries disable todo push without discarding text");
            editor.Text = "读书三十分钟\r\n整理桌面";
            Configure(form, 10, 36, "Microsoft YaHei", true, false);
            Call(form, "TryPreview");
            Snapshot(form, Path.Combine(output, "pw2-window-todo-push.png"));
            form.Close();
        }
        using (MessageBoardForm dx = Open(Path.Combine(output, "dx-no-todo-push"), MessageBoardProfile.Dx))
        {
            Check(!dx.Controls.Contains(Field<Button>(dx, "todoSend")), "DX has no PW2 todo push action");
            dx.Close();
        }
        Console.WriteLine("PASS todo validation, UI gating and isolated backend process");
    }
}
