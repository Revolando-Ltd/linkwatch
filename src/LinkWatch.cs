// LinkWatch: watches OVRService, restarts it with one click, draws the Quest cable and graphs the
// Quest Link USB data rate with Windows' own ETW, plus optional Wi-Fi traffic. No third-party code.
// Closing the window hides it to the tray; Exit in the tray menu quits.
// Args: --hidden (start in tray)  --restart-service  --apply-task (internal, elevated helpers)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

// shown in the exe's Properties > Details tab
[assembly: AssemblyTitle("LinkWatch")]
[assembly: AssemblyProduct("LinkWatch")]
[assembly: AssemblyDescription("Quest Link diagnostic tool: cable speed, real USB data rate, history, service restart")]
[assembly: AssemblyCompany("REVOLANDO LTD")]
[assembly: AssemblyCopyright("Copyright 2026 REVOLANDO LTD. PolyForm Noncommercial 1.0.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class AboutInfo {
    public const string Version = "1.0.0 beta 1";
    public const string TermsVersion = "2";   // bump when the terms text changes, users are asked again
    public const string Author = "Aydın Özon";
    public const string Company = "REVOLANDO LTD", CompanyUrl = "https://revolando.com";
    public const string RepoUrl = "https://github.com/Revolando-Ltd/linkwatch";
    public const string LicenseName = "PolyForm Noncommercial 1.0.0";
    public const string LicenseUrl = "https://polyformproject.org/licenses/noncommercial/1.0.0";
}

// Shown once (and again when the terms change): use at your own risk, what the app does to the system, license.
class TermsForm : Form {
    public const string Text1 =
        "LinkWatch is provided \"AS IS\", without warranty of any kind, express or implied, including fitness for a particular purpose. You use it at your own risk.\r\n\r\n" +
        "What LinkWatch does on your PC:\r\n" +
        "  - Re-Start stops and starts the Windows service OVRService. This disconnects any running Quest Link session and VR application.\r\n" +
        "  - It reads USB transfer events through Windows' own Event Tracing (ETW). It does not install drivers and does not change USB settings.\r\n" +
        "  - In administrator mode it creates a Windows scheduled task that starts it with administrator rights.\r\n" +
        "  - It stores settings and history only on this PC (%APPDATA%\\LinkWatch).\r\n\r\n" +
        "Privacy: LinkWatch does not collect, store or transmit any personal data or usage data. It has no telemetry, no analytics, no accounts, no update check and makes no network connections. The only time anything opens on the internet is when you click a link in the About window, which opens your own browser.\r\n\r\n" +
        "To the maximum extent permitted by law, REVOLANDO LTD and the authors are not liable for any damage or loss arising from the use of LinkWatch, including interrupted sessions, lost work, data loss or hardware issues. Nothing in these terms limits liability that cannot be limited by law.\r\n\r\n" +
        "License: PolyForm Noncommercial 1.0.0. Free for noncommercial use. Commercial use, sale or resale requires a license from REVOLANDO LTD (revolando.com).\r\n\r\n" +
        "LinkWatch is not affiliated with, endorsed by or sponsored by Meta Platforms, Inc. Meta, Meta Quest, Quest and Oculus are trademarks of Meta Platforms, Inc.";
    public TermsForm(Icon icon) {
        Text = "LinkWatch - Terms of use"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(520, 460); TopMost = true;
        BackColor = Color.FromArgb(24, 24, 24); ForeColor = Color.Gainsboro; Font = new Font("Segoe UI", 9f); Icon = icon;
        HandleCreated += (s, e) => MainForm.DarkTitle(this);
        var head = new Label { Text = "Please read and accept before using LinkWatch", Location = new Point(16, 14), AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
        var box = new TextBox { Text = Text1, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 44), Size = new Size(488, 356),
            BackColor = Color.FromArgb(16, 16, 16), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle, TabStop = false };
        box.HandleCreated += (s, e) => HistoryForm.SetWindowTheme(box.Handle, "DarkMode_Explorer", null);
        var agree = new Button { Text = "I agree", Location = new Point(316, 414), Size = new Size(90, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.LightGreen, ForeColor = Color.Black, DialogResult = DialogResult.OK };
        var exit = new Button { Text = "Exit", Location = new Point(414, 414), Size = new Size(90, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), DialogResult = DialogResult.Cancel };
        exit.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        Controls.AddRange(new Control[] { head, box, agree, exit });
        AcceptButton = agree; CancelButton = exit;
        Shown += (s, e) => { box.SelectionLength = 0; agree.Focus(); };
    }
}

// About window: who made it, the license, where the code lives
class AboutForm : Form {
    public AboutForm(Icon icon) {
        Text = "About LinkWatch"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; ClientSize = new Size(380, 330);
        BackColor = Color.FromArgb(24, 24, 24); ForeColor = Color.Gainsboro; Font = new Font("Segoe UI", 9f); Icon = icon;
        HandleCreated += (s, e) => MainForm.DarkTitle(this);
        var logo = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48) };
        logo.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; e.Graphics.FillEllipse(Brushes.LimeGreen, 4, 4, 40, 40); };
        int y = 20;
        Func<string, Font, Color, Label> add = (t, f, c) => { var l = new Label { Text = t, Font = f, ForeColor = c, Location = new Point(84, y), AutoSize = true }; Controls.Add(l); y += f.Height + 6; return l; };
        add("LinkWatch", new Font("Segoe UI", 14f, FontStyle.Bold), Color.Gainsboro);
        add("Version " + AboutInfo.Version, Font, Color.Gray);
        y += 6;
        add("Quest Link diagnostic tool", Font, Color.Gainsboro);
        add("Created by " + AboutInfo.Author, Font, Color.Gainsboro);
        add("© 2026 " + AboutInfo.Company, Font, Color.Gainsboro);
        y += 8;
        Func<string, string, LinkLabel> link = (t, url) => {
            var l = new LinkLabel { Text = t, Location = new Point(84, y), AutoSize = true, LinkColor = Color.LightGreen, ActiveLinkColor = Color.White, VisitedLinkColor = Color.LightGreen };
            if (url != null) l.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            Controls.Add(l); y += Font.Height + 8; return l;
        };
        link("revolando.com", AboutInfo.CompanyUrl);
        link("Source code on GitHub", AboutInfo.RepoUrl);
        link("License: " + AboutInfo.LicenseName, AboutInfo.LicenseUrl);
        var terms = link("Terms of use", null);
        terms.LinkClicked += (s, e) => MessageBox.Show(this, TermsForm.Text1, "LinkWatch - Terms of use");
        var note = new Label { Text = "Free for noncommercial use. Commercial use and sale only with a license from REVOLANDO LTD.", Location = new Point(20, y + 4), Size = new Size(340, 34), ForeColor = Color.Gray };
        var ok = new Button { Text = "OK", Location = new Point(280, 292), Size = new Size(80, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), DialogResult = DialogResult.OK };
        ok.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        Controls.AddRange(new Control[] { logo, note, ok }); AcceptButton = ok;
    }
}

static class App {
    public const string Name = "LinkWatch", TaskName = "LinkWatch", ServiceName = "OVRService";
    public const string ShowEventName = @"Local\LinkWatch_Show", ShotEventName = @"Local\LinkWatch_Shot";
    public static string ExePath { get { return Application.ExecutablePath; } }
    public static bool IsAdmin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }

    public static int Run(string file, string args) {
        var si = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(si)) { p.WaitForExit(); return p.ExitCode; }
    }
    // Runs this exe elevated with the given args (one UAC prompt). False if the user said no.
    public static bool RunElevated(string args) {
        try {
            var si = new ProcessStartInfo(ExePath, args) { UseShellExecute = true, Verb = "runas" };
            using (var p = Process.Start(si)) { p.WaitForExit(); return p.ExitCode == 0; }
        } catch (Win32Exception) { return false; }
    }
    public static bool TaskExists() { return Run("schtasks.exe", "/query /tn \"" + TaskName + "\"") == 0; }

    public static void RestartService() {
        using (var sc = new ServiceController(ServiceName)) {
            if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    // Creates (with or without a logon trigger) or deletes the elevated task. Must run as admin.
    public static int ApplyTask(bool want, bool logon) {
        // remove the task left by the old name ("OVR Monitor"), if any
        if (Run("schtasks.exe", "/query /tn \"OVR Monitor\"") == 0) Run("schtasks.exe", "/delete /tn \"OVR Monitor\" /f");
        if (!want) return TaskExists() ? Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f") : 0;
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string trig = logon ? "<LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger>" : "";
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<Triggers>" + trig + "</Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(ExePath) + "</Command><Arguments>--hidden</Arguments></Exec></Actions></Task>";
        string tmp = Path.Combine(Path.GetTempPath(), "linkwatch_task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        int r = Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f");
        File.Delete(tmp);
        return r;
    }

    static bool SignalShow(int waitMs) {
        var until = DateTime.Now.AddMilliseconds(waitMs);
        do {
            try { using (var e = EventWaitHandle.OpenExisting(ShowEventName)) { e.Set(); return true; } } catch { }
            if (waitMs > 0) Thread.Sleep(300);
        } while (DateTime.Now < until);
        return false;
    }

    [STAThread]
    static int Main(string[] args) {
        bool hidden = args.Contains("--hidden");
        // the app used to be called "OVR Monitor": carry its settings and history over once
        try {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string oldDir = Path.Combine(appData, "OVRMonitor"), newDir = Path.Combine(appData, "LinkWatch");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir)) Directory.Move(oldDir, newDir);
        } catch { }
        if (args.Contains("--restart-service")) { try { RestartService(); return 0; } catch { return 1; } }
        // --screenshot: ask the running instance to save window captures to %TEMP%\LinkWatch_*.png
        if (args.Contains("--screenshot")) {
            try { using (var e = EventWaitHandle.OpenExisting(ShotEventName)) { e.Set(); return 0; } } catch { return 1; }
        }
        if (args.Contains("--apply-task")) {
            var s = Settings.Load();
            return ApplyTask(s.AdminMode, s.Autostart);
        }

        // already running: just bring the window up
        try { using (var e = EventWaitHandle.OpenExisting(ShowEventName)) { if (!hidden) e.Set(); return 0; } } catch { }

        // admin mode: the elevated instance comes from the scheduled task, no prompt
        var cfg = Settings.Load();
        if (!IsAdmin && cfg.AdminMode && TaskExists()) {
            Run("schtasks.exe", "/run /tn \"" + TaskName + "\"");
            if (!hidden) SignalShow(20000);
            return 0;
        }

        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        bool created;
        using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out created, sec))
        using (var shotEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShotEventName, out created, sec)) {
            MainForm.ShotEvent = shotEvent;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // terms of use: accepted once per terms version, otherwise the app does not start
            if (cfg.AcceptedTerms != AboutInfo.TermsVersion) {
                using (var tf = new TermsForm(Icon.ExtractAssociatedIcon(ExePath)))
                    if (tf.ShowDialog() != DialogResult.OK) return 0;
                cfg.AcceptedTerms = AboutInfo.TermsVersion; cfg.Save();
            }
            var f = new MainForm(cfg, showEvent, hidden);
            Application.Run(f.Context);
        }
        return 0;
    }
}

class Settings {
    public string Measure = "ETW", AcceptedTerms = "";
    public bool AdminMode = false, Autostart = true, Wifi = false;
    public int CheckSec = 30, HistoryDays = 30;
    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkWatch"); } }
    static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

    public static Settings Load() {
        var s = new Settings();
        try {
            foreach (Match m in Regex.Matches(File.ReadAllText(FilePath), "\"(\\w+)\"\\s*:\\s*\"?([^\",\\r\\n}]*)\"?")) {
                string k = m.Groups[1].Value, v = m.Groups[2].Value.Trim();
                switch (k) {
                    case "Measure": s.Measure = v; break;
                    case "AcceptedTerms": s.AcceptedTerms = v; break;
                    case "AdminMode": s.AdminMode = v == "true"; break;
                    case "Autostart": s.Autostart = v == "true"; break;
                    case "Wifi": s.Wifi = v == "true"; break;
                    case "CheckSec": int.TryParse(v, out s.CheckSec); break;
                    case "HistoryDays": int.TryParse(v, out s.HistoryDays); break;
                }
            }
        } catch { }
        if (s.CheckSec < 5) s.CheckSec = 30;
        if (s.HistoryDays < 1) s.HistoryDays = 30;
        return s;
    }
    public void Save() {
        Directory.CreateDirectory(Dir);
        Func<bool, string> b = x => x ? "true" : "false";
        File.WriteAllText(FilePath, "{\r\n  \"Measure\": \"" + Measure + "\",\r\n  \"AcceptedTerms\": \"" + AcceptedTerms + "\",\r\n  \"AdminMode\": " + b(AdminMode) + ",\r\n  \"Autostart\": " + b(Autostart) +
            ",\r\n  \"CheckSec\": " + CheckSec + ",\r\n  \"HistoryDays\": " + HistoryDays + ",\r\n  \"Wifi\": " + b(Wifi) + "\r\n}\r\n");
    }
}

class MainForm : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    public static void DarkTitle(Form f) { int one = 1; DwmSetWindowAttribute(f.Handle, 20, ref one, 4); }

    public ApplicationContext Context = new ApplicationContext();
    readonly Settings cfg; readonly EventWaitHandle showEvent;
    readonly EtwUsb etw = new EtwUsb();
    bool quitting;
    const int Samples = 60;

    static readonly Color BgForm = Color.FromArgb(24, 24, 24), BgPanel = Color.FromArgb(38, 38, 38), BgGraph = Color.FromArgb(16, 16, 16), BtnOff = Color.FromArgb(60, 60, 60);
    static readonly Font UiFont = new Font("Segoe UI", 9f), BoldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
    readonly Dictionary<string, List<double>> series = new Dictionary<string, List<double>>();
    readonly Dictionary<string, Color> colors = new Dictionary<string, Color> {
        { "out", Color.LimeGreen }, { "in", Color.MediumOrchid }, { "rx", Color.DeepSkyBlue }, { "tx", Color.Orange } };
    readonly Icon iconGreen = Dot(Color.LimeGreen), iconRed = Dot(Color.Red), iconYellow = Dot(Color.Gold);

    Label lblStatus, lblTime, lblUsbRate, lblConn, lblNetRate;
    Button btnWifi, btnRestart, btnSettings, btnHistory;
    UsbLink lastLink; int tick; DateTime lastCleanup = DateTime.Today;
    Panel secUsb, secNet;
    PictureBox picCable, graphUsb, graphNet;
    ComboBox cmbNet;
    NotifyIcon tray;
    System.Windows.Forms.Timer timerSvc, timerRate;
    UsbLink usb = new UsbLink();
    string lastState;
    DateTime prevT; double prevRx, prevTx; bool havePrev;

    static Icon Dot(Color c) {
        using (var bmp = new Bitmap(16, 16))
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
    public static string Rate(double bytesPerSec) {
        double bit = bytesPerSec * 8;
        if (bit >= 1e9) return (bit / 1e9).ToString("N2") + " Gbps";
        if (bit >= 1e6) return (bit / 1e6).ToString("N1") + " Mbps";
        if (bit >= 1e3) return (bit / 1e3).ToString("N0") + " Kbps";
        return bit.ToString("N0") + " bps";
    }
    static Label L(string text, int x, int y, int w, int h) { return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h) }; }
    static Button B(string text, int x, int w) {
        var b = new Button { Text = text, Location = new Point(x, 12), Size = new Size(w, 40), FlatStyle = FlatStyle.Flat, BackColor = BtnOff, ForeColor = Color.Gainsboro };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90); return b;
    }
    // Horizontal ink extent of a text as TextRenderer draws it (measured sizes include side bearings,
    // which made the gap after "To Quest" look wider than the one before "From Quest").
    static void InkSpan(string text, Font f, out int left, out int right, out int width) {
        width = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding).Width + 4;
        left = width; right = 0;
        using (var bmp = new Bitmap(width, 24)) {
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Black); TextRenderer.DrawText(g, text, f, new Point(0, 2), Color.White, Color.Black, TextFormatFlags.NoPadding); }
            for (int x = 0; x < width; x++) for (int y = 0; y < 24; y++) if (bmp.GetPixel(x, y).R > 60) { if (x < left) left = x; if (x > right) right = x; break; }
        }
        if (right < left) { left = 0; right = width - 1; }
    }
    // colored text legend (the color says which line it is) with a drawn chevron: "<" before or ">" after.
    // Text and chevron share one vertical centre and keep the same 6 px gap to the text ink on either side.
    void Legend(Panel p, string text, Color c, int x, string arrowBefore = null, string arrowAfter = null) {
        const int H = 20, ArrowW = 5, Gap = 6, Pad = 2;   // chevron 5 px wide, pen 2.4 px (round caps)
        int inkL, inkR, tw; InkSpan(text, UiFont, out inkL, out inkR, out tw);
        bool before = arrowBefore != null, after = arrowAfter != null;
        // text origin so that its ink starts right after the chevron + gap
        int tx = before ? Pad + ArrowW + Gap - inkL : Pad - inkL;
        float ax = tx + inkR + Gap;                       // left end of the ">" chevron
        int w = after ? (int)Math.Ceiling(ax + ArrowW + Pad) : tx + inkR + Pad + 1;
        var box = new PictureBox { Location = new Point(x, 4), Size = new Size(w, H), BackColor = BgPanel };
        box.Paint += (s, e) => {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float cy = H / 2f;
            TextRenderer.DrawText(g, text, UiFont, new Rectangle(tx, 0, tw, H), c, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            using (var pen = new Pen(c, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                if (before) g.DrawLines(pen, new[] { new PointF(Pad + ArrowW, cy - 4.5f), new PointF(Pad, cy), new PointF(Pad + ArrowW, cy + 4.5f) });
                if (after) g.DrawLines(pen, new[] { new PointF(ax, cy - 4.5f), new PointF(ax + ArrowW, cy), new PointF(ax, cy + 4.5f) });
            }
        };
        p.Controls.Add(box);
    }
    Panel Section(string title, int y, int h, out Label titleLabel) {
        var p = new Panel { Location = new Point(8, y), Size = new Size(491, h), BackColor = BgPanel };
        titleLabel = L(title, 8, 6, 200, 20); titleLabel.Font = BoldFont; p.Controls.Add(titleLabel); return p;
    }
    PictureBox Graph(Panel p, string[] keys, int y, int h) {
        var g = new PictureBox { Location = new Point(8, y), Size = new Size(475, h), BackColor = BgGraph };
        g.Paint += (s, e) => DrawGraph(e.Graphics, g.Width, g.Height, keys);
        p.Controls.Add(g); return g;
    }

    public MainForm(Settings cfg, EventWaitHandle showEvent, bool hidden) {
        this.cfg = cfg; this.showEvent = showEvent;
        foreach (var k in new[] { "out", "in", "rx", "tx" }) series[k] = new List<double>();

        Text = App.Name; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        Icon = iconYellow; BackColor = BgForm; ForeColor = Color.Gainsboro; Font = UiFont;
        HandleCreated += (s, e) => DarkTitle(this);

        lblStatus = L("OVRService: checking...", 12, 12, 196, 24); lblStatus.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        lblTime = L("", 12, 38, 196, 18); lblTime.ForeColor = Color.Gray;
        btnWifi = B("Wi-Fi", 214, 80); btnRestart = B("Re-Start", 300, 96);
        btnHistory = B("", 402, 43); btnHistory.Paint += (s, e) => DrawClock(e.Graphics, btnHistory.Width / 2f, btnHistory.Height / 2f);
        btnSettings = B("\u2699", 452, 43); btnSettings.Font = new Font("Segoe UI Symbol", 14f);

        Label t;
        secUsb = Section("USB cable (Quest Link)", 62, 288, out t);
        // same wording and places as the Wi-Fi box, seen from the PC: Received (from Quest) | Sent (to Quest)
        Legend(secUsb, "Received (~)", colors["in"], 300, "<", null);
        Legend(secUsb, "Sent", colors["out"], 412, null, ">");
        picCable = new PictureBox { Location = new Point(8, 30), Size = new Size(475, 68), BackColor = BgGraph };
        picCable.Paint += (s, e) => DrawCable(e.Graphics, picCable.Width);
        secUsb.Controls.Add(picCable);
        graphUsb = Graph(secUsb, new[] { "out", "in" }, 104, 130);
        lblUsbRate = L("", 8, 240, 475, 20);
        lblConn = L("", 8, 262, 475, 20); lblConn.ForeColor = Color.Gray;
        secUsb.Controls.Add(lblUsbRate); secUsb.Controls.Add(lblConn);

        Label netTitle;
        secNet = Section("Network", 358, 182, out netTitle);
        Legend(secNet, "Received", colors["rx"], 300, "<", null);
        Legend(secNet, "Sent", colors["tx"], 412, null, ">");
        graphNet = Graph(secNet, new[] { "rx", "tx" }, 30, 120);
        lblNetRate = L("", 8, 156, 475, 20); secNet.Controls.Add(lblNetRate);
        cmbNet = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Location = new Point(150, 4), Size = new Size(140, 22), BackColor = BgPanel, ForeColor = Color.Gainsboro };
        foreach (var a in Adapters()) cmbNet.Items.Add(a.Name);
        if (cmbNet.Items.Count > 0) cmbNet.SelectedIndex = 0;
        cmbNet.Visible = cmbNet.Items.Count > 1;
        netTitle.Text = cmbNet.Items.Count == 1 ? "Network (" + cmbNet.Items[0] + ")" : "Network";
        cmbNet.SelectedIndexChanged += (s, e) => { havePrev = false; series["rx"].Clear(); series["tx"].Clear(); };
        secNet.Controls.Add(cmbNet);

        Controls.AddRange(new Control[] { lblStatus, lblTime, btnWifi, btnRestart, btnHistory, btnSettings, secUsb, secNet });
        btnHistory.Click += (s, e) => ShowHistory();
        new ToolTip().SetToolTip(btnHistory, "History");
        btnWifi.Click += (s, e) => { SetWifi(!cfg.Wifi); cfg.Save(); };
        btnRestart.Click += (s, e) => RestartOvr();
        btnSettings.Click += (s, e) => ShowSettings();
        SetWifi(cfg.Wifi);

        tray = new NotifyIcon { Icon = iconYellow, Text = App.Name, Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open window", null, (s, e) => ShowMain());
        menu.Items.Add("Re-Start OVRService", null, (s, e) => RestartOvr());
        menu.Items.Add("Check now", null, (s, e) => { CheckService(); UpdateConn(); });
        menu.Items.Add("History", null, (s, e) => { ShowMain(); ShowHistory(); });
        menu.Items.Add("Settings", null, (s, e) => { ShowMain(); ShowSettings(); });
        menu.Items.Add("About LinkWatch", null, (s, e) => { ShowMain(); ShowAbout(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Quit());
        tray.ContextMenuStrip = menu;
        tray.BalloonTipClicked += (s, e) => { if (lastState != "Running") RestartOvr(); else ShowMain(); };
        tray.DoubleClick += (s, e) => ShowMain();
        FormClosing += (s, e) => { if (!quitting) { e.Cancel = true; Hide(); } };

        timerSvc = new System.Windows.Forms.Timer { Interval = cfg.CheckSec * 1000 };
        timerSvc.Tick += (s, e) => { CheckService(); UpdateConn(); if (cfg.Measure == "ETW" && App.IsAdmin && !etw.Running) StartUsb(); };
        timerRate = new System.Windows.Forms.Timer { Interval = 1000 };
        timerRate.Tick += (s, e) => UpdateRates();

        History.KeepDays = cfg.HistoryDays; History.Cleanup();
        History.Event("app", "LinkWatch started (" + (App.IsAdmin ? "administrator" : "normal user") + ")");
        CheckService(); UpdateConn(); StartUsb();
        timerSvc.Start(); timerRate.Start();
        if (!hidden) ShowMain();
    }

    void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Quit() {
        quitting = true; History.Flush(); History.Event("app", "LinkWatch closed"); etw.Stop(); timerSvc.Stop(); timerRate.Stop(); tray.Visible = false;
        Close(); Context.ExitThread();
    }
    void StartUsb() { etw.Stop(); if (cfg.Measure == "ETW" && App.IsAdmin) etw.Start(); }

    void SetWifi(bool on) {
        cfg.Wifi = on; secNet.Visible = on;
        btnWifi.BackColor = on ? Color.LightGreen : BtnOff; btnWifi.ForeColor = on ? Color.Black : Color.Gainsboro;
        ClientSize = new Size(507, on ? 548 : 358);
    }

    static IEnumerable<NetworkInterface> Adapters() {
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
    }

    // ---------- service ----------
    void CheckService() {
        string state;
        try { using (var sc = new ServiceController(App.ServiceName)) state = sc.Status.ToString(); } catch { state = "Not found"; }
        string time = DateTime.Now.ToString("HH:mm:ss");
        lblTime.Text = "Last check: " + time + " (every " + cfg.CheckSec + " s)";
        if (state == "Running") {
            tray.Icon = iconGreen; Icon = iconGreen; tray.Text = "OVRService running (" + time + ")";
            lblStatus.Text = "OVRService: running"; lblStatus.ForeColor = Color.LimeGreen;
            if (lastState != null && lastState != "Running") tray.ShowBalloonTip(3000, App.Name, "OVRService is running again.", ToolTipIcon.Info);
        } else {
            tray.Icon = iconRed; Icon = iconRed; tray.Text = "OVRService: " + state + " (" + time + ")";
            lblStatus.Text = "OVRService: " + state; lblStatus.ForeColor = Color.Red;
            tray.ShowBalloonTip(10000, "OVRService is not running", "State: " + state + ". Click to restart.", ToolTipIcon.Error);
            System.Media.SystemSounds.Exclamation.Play();
        }
        if (state != lastState) History.Event(state == "Running" ? "service-up" : "service-down", "OVRService: " + state);
        lastState = state;
        if (DateTime.Today != lastCleanup) { lastCleanup = DateTime.Today; History.Cleanup(); }
    }
    void RestartOvr() {
        // while the service runs, a restart drops the live Link session: ask first
        if (lastState == "Running") {
            if (!Visible) ShowMain();
            var answer = MessageBox.Show(this,
                "Restarting OVRService will disconnect the current Quest Link connection.\n\n" +
                "The headset leaves Link and any running VR app (e.g. Unreal VR Preview) loses the headset.\n\nRestart anyway?",
                "Re-Start OVRService", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        History.Event("restart", "Re-Start pressed");
        tray.Icon = iconYellow; lblStatus.Text = "OVRService: restarting..."; lblStatus.ForeColor = Color.Gold; Refresh();
        bool ok;
        if (App.IsAdmin) { try { App.RestartService(); ok = true; } catch { ok = false; } }
        else ok = App.RunElevated("--restart-service");
        if (!ok) tray.ShowBalloonTip(5000, App.Name, "Restart failed or was cancelled.", ToolTipIcon.Warning);
        CheckService();
    }

    void UpdateConn() {
        try { usb = UsbInfo.Get(); } catch { usb = new UsbLink(); }
        LogLinkChange(lastLink, usb); lastLink = usb;
        picCable.Invalidate();
        string t = usb.Connected ? usb.Speed : "Quest not connected by cable";
        lblConn.Text = "Link: " + t;
        lblConn.ForeColor = t.Contains("USB 3") ? Color.LimeGreen : Regex.IsMatch(t, "USB [12]") ? Color.Orange : Color.Gray;
    }

    // ---------- rates ----------
    void Add(string k, double v) { var l = series[k]; l.Add(v); while (l.Count > Samples) l.RemoveAt(0); }
    void UpdateRates() {
        if (showEvent.WaitOne(0)) ShowMain();
        if (ShotEvent != null && ShotEvent.WaitOne(0)) TakeScreenshots();
        if (++tick % 5 == 0) UpdateConn();   // cable changes show up within 5 s
        var nic = Adapters().FirstOrDefault(n => n.Name == (string)cmbNet.SelectedItem);
        double rxNow = 0, txNow = 0;
        if (nic != null) { var st = nic.GetIPStatistics(); rxNow = st.BytesReceived; txNow = st.BytesSent; }
        var now = DateTime.UtcNow;
        long[] u = etw.Take();
        if (havePrev) {
            double dt = (now - prevT).TotalSeconds; if (dt <= 0) dt = 1;
            double rx = Math.Max(0, (rxNow - prevRx) / dt), tx = Math.Max(0, (txNow - prevTx) / dt);
            double o = u[0] / dt, i = u[1] / dt;
            Add("out", o); Add("in", i); Add("rx", rx); Add("tx", tx);
            History.AddSecond(DateTime.Now, o, i, rx, tx);
            lblUsbRate.ForeColor = Color.Gainsboro;
            if (etw.Running) lblUsbRate.Text = "Received: ~" + Rate(i) + "      Sent: " + Rate(o);
            else if (cfg.Measure != "ETW") lblUsbRate.Text = "USB measurement is off (settings)";
            else if (!App.IsAdmin) { lblUsbRate.ForeColor = Color.Orange; lblUsbRate.Text = "USB measurement needs admin mode (settings)"; }
            else { lblUsbRate.ForeColor = Color.Orange; lblUsbRate.Text = "USB measurement could not start: " + etw.LastError; }
            lblNetRate.Text = "Received: " + Rate(rx) + "      Sent: " + Rate(tx);
            if (Visible) { graphUsb.Invalidate(); if (cfg.Wifi) graphNet.Invalidate(); }
        }
        prevT = now; prevRx = rxNow; prevTx = txNow; havePrev = true;
    }

    // ---------- drawing ----------
    void DrawGraph(Graphics g, int w, int h, string[] keys) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var grid = new Pen(Color.FromArgb(42, 42, 42))) {
            for (int i = 1; i < 4; i++) g.DrawLine(grid, 0, h * i / 4, w, h * i / 4);
            for (int i = 1; i < 6; i++) g.DrawLine(grid, w * i / 6, 0, w * i / 6, h);
        }
        double peak = 1024;
        foreach (var k in keys) foreach (var v in series[k]) if (v > peak) peak = v;
        foreach (var k in keys) {
            var l = series[k]; if (l.Count < 2) continue;
            var pts = new PointF[l.Count];
            for (int i = 0; i < l.Count; i++)
                pts[i] = new PointF((float)((w - 1) * (Samples - l.Count + i) / (double)(Samples - 1)), (float)((h - 2) - (h - 6) * l[i] / peak));
            using (var p = new Pen(colors[k], 1.6f)) g.DrawLines(p, pts);
        }
        g.DrawString("peak: " + Rate(peak), UiFont, Brushes.Gray, 4, 2);
        g.DrawString("last " + Samples + " s", UiFont, Brushes.Gray, w - 62, h - 18);
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r) {
        var p = new GraphicsPath(); float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    // desktop PC, about 44 x 28 px around (cx, cy)
    static void DrawPc(Graphics g, Pen pen, float cx, float cy) {
        float x = cx - 22, y = cy - 14;
        g.DrawPath(pen, RoundRect(x, y, 29, 20, 2.5f));
        g.DrawLine(pen, x + 14.5f, y + 20, x + 14.5f, y + 25);
        g.DrawLine(pen, x + 8, y + 26, x + 21, y + 26);
        g.DrawPath(pen, RoundRect(x + 33, y, 11, 27, 2.5f));
        g.DrawLine(pen, x + 36, y + 5, x + 41, y + 5);
        g.DrawLine(pen, x + 36, y + 9, x + 41, y + 9);
        using (var b = new SolidBrush(pen.Color)) g.FillEllipse(b, x + 37.3f, y + 20, 2.6f, 2.6f);
    }
    // VR headset around (cx, cy), geometry scaled, pen width kept
    static void DrawHeadset(Graphics g, Pen pen, float cx, float cy, float scale) {
        float x = cx - 19, y = cy - 10, bw = 38, bh = 21, r = 6;
        var m = new Matrix(); m.Translate(cx, cy); m.Scale(scale, scale); m.Translate(-cx, -cy);
        var p = new GraphicsPath();
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + bw - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + bw - 2 * r, y + bh - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddBezier(x + bw - r, y + bh, cx + 6, y + bh, cx + 5, y + bh - 5, cx, y + bh - 5);
        p.AddBezier(cx, y + bh - 5, cx - 5, y + bh - 5, cx - 6, y + bh, x + r, y + bh);
        p.AddArc(x, y + bh - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        foreach (var path in new[] { p, RoundRect(x - 5, y + 5, 5, 11, 2), RoundRect(x + bw, y + 5, 5, 11, 2), RoundRect(cx - 4.5f, y + 4, 9, 3.5f, 1.7f) }) {
            path.Transform(m); g.DrawPath(pen, path);
        }
    }
    static void DrawPlug(Graphics g, float x, float y, string type, Pen pen, Brush brush) {
        if (type == "USB-C") {
            var p = new GraphicsPath();
            p.AddArc(x - 12, y - 6, 12, 12, 90, 180); p.AddArc(x, y - 6, 12, 12, 270, 180); p.CloseFigure();
            g.FillPath(brush, p); g.DrawPath(pen, p);
        } else { g.FillRectangle(brush, x - 14, y - 7, 28, 14); g.DrawRectangle(pen, x - 14, y - 7, 28, 14); }
    }
    void DrawCable(Graphics g, int w) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var sf = new StringFormat { Alignment = StringAlignment.Center };
        float cy = 26, rx = w - 36;
        using (var icon = new Pen(Color.Gainsboro, 2) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) {
            DrawPc(g, icon, 36, 24);
            DrawHeadset(g, icon, rx, 24, 0.9f);
        }
        g.DrawString("PC", BoldFont, Brushes.Gainsboro, new RectangleF(0, 44, 72, 16), sf);
        g.DrawString(usb.Connected ? usb.Device : "no device", BoldFont, Brushes.Gainsboro, new RectangleF(rx - 36, 44, 72, 16), sf);
        if (!usb.Connected) {
            using (var dash = new Pen(Color.FromArgb(90, 90, 90), 3) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, 84, cy, w - 84, cy);
            g.DrawString("not connected by cable", UiFont, Brushes.Gray, new RectangleF(84, cy + 8, w - 168, 16), sf);
            return;
        }
        Color col = usb.Speed.Contains("USB 3") ? Color.LimeGreen : Regex.IsMatch(usb.Speed, "USB [12]") ? Color.Orange : Color.Gray;
        float x1 = 84, x2 = w - 84;
        using (var cable = new Pen(col, 4)) g.DrawLine(cable, x1 + 14, cy, x2 - 12, cy);
        using (var plugPen = new Pen(col, 3))
        using (var plugBrush = new SolidBrush(Color.FromArgb(70, 70, 70)))
        using (var colBrush = new SolidBrush(col)) {
            DrawPlug(g, x1, cy, usb.PcPort, plugPen, plugBrush);
            if (usb.DevicePort != "fixed") DrawPlug(g, x2, cy, usb.DevicePort, plugPen, plugBrush);   // Rift: cable is part of the headset
            g.DrawString(usb.PcPort, UiFont, Brushes.Gray, x1 - 18, cy + 10);
            if (usb.DevicePort != "fixed") g.DrawString(usb.DevicePort, UiFont, Brushes.Gray, x2 - 20, cy + 10);
            if (usb.Hubs.Count > 0) {
                float mx = (x1 + x2) / 2;
                g.FillRectangle(plugBrush, mx - 22, cy - 8, 44, 16); g.DrawRectangle(plugPen, mx - 22, cy - 8, 44, 16);
                g.DrawString("hub", UiFont, Brushes.Gainsboro, new RectangleF(mx - 22, cy - 8, 44, 16), sf);
            }
            string shortSpeed = Regex.Replace(usb.Speed, @"\s*\(device is.*$", "");
            g.DrawString(shortSpeed, UiFont, colBrush, new RectangleF(x1, cy - 22, x2 - x1, 16), sf);
            if (usb.Speed.Contains("device is USB 3 capable"))
                g.DrawString("device supports USB 3: check cable / port", UiFont, Brushes.Orange, new RectangleF(x1, cy + 24, x2 - x1, 16), sf);
            else if (usb.Hubs.Count > 0)
                g.DrawString("via: " + string.Join(", ", usb.Hubs), UiFont, Brushes.Gray, new RectangleF(x1, cy + 24, x2 - x1, 16), sf);
        }
    }

    // ---------- screenshots (for the README) ----------
    public static EventWaitHandle ShotEvent;
    static void CaptureWindow(Form f, string file) {
        f.TopMost = true; f.Activate(); f.Refresh(); Application.DoEvents(); Thread.Sleep(400);
        using (var b = new Bitmap(f.Width, f.Height)) {
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
            b.Save(Path.Combine(Path.GetTempPath(), file), System.Drawing.Imaging.ImageFormat.Png);
        }
        f.TopMost = false;
    }
    void TakeScreenshots() {
        ShowMain(); CaptureWindow(this, "LinkWatch_main.png");
        ShowHistory(); CaptureWindow(historyForm, "LinkWatch_history.png");
        // settings is modal: capture and cancel it from a timer once it is on screen
        var t = new System.Windows.Forms.Timer { Interval = 900 };
        t.Tick += (s, e) => { t.Stop(); var d = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Settings"); if (d != null) { CaptureWindow(d, "LinkWatch_settings.png"); d.DialogResult = DialogResult.Cancel; } };
        t.Start(); ShowSettings();
    }

    // ---------- history ----------
    HistoryForm historyForm;
    void ShowHistory() {
        if (historyForm == null || historyForm.IsDisposed) historyForm = new HistoryForm();
        if (!historyForm.Visible) historyForm.Show(this); else historyForm.Activate();
    }
    static void LogLinkChange(UsbLink before, UsbLink now) {
        if (before == null) { if (now.Connected) History.Event("usb-on", now.Device + " connected: " + now.Speed + " on " + now.PcPort); return; }
        if (before.Connected && !now.Connected) History.Event("usb-off", before.Device + " disconnected");
        else if (!before.Connected && now.Connected) History.Event(now.Speed.Contains("USB 3") ? "usb-on" : "usb-speed", now.Device + " connected: " + now.Speed + " on " + now.PcPort);
        else if (now.Connected && before.Speed != now.Speed) History.Event("usb-speed", "Link speed changed: " + before.Speed + " -> " + now.Speed);
    }
    // clock glyph for the History button, same 2 px stroke as the other icons
    static void DrawClock(Graphics g, float cx, float cy) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Color.Gainsboro, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
            g.DrawEllipse(p, cx - 9, cy - 9, 18, 18);
            g.DrawLine(p, cx, cy, cx, cy - 6);
            g.DrawLine(p, cx, cy, cx + 4.5f, cy + 2.5f);
        }
    }

    // ---------- settings ----------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static void SetRunEntry(bool on) {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
            if (on) k.SetValue(App.Name, "\"" + App.ExePath + "\" --hidden");
            else if (k.GetValue(App.Name) != null) k.DeleteValue(App.Name);
            if (k.GetValue("OVR Monitor") != null) k.DeleteValue("OVR Monitor");   // old name
        }
        // shortcut left by the old PowerShell version
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "OVR Izleyici.lnk");
        if (File.Exists(lnk)) File.Delete(lnk);
    }
    // Brings the task and the Run entry in line with the settings. False if the UAC prompt was refused.
    bool ApplyStartup() {
        bool ok = true;
        bool taskNeeded = cfg.AdminMode;
        if (taskNeeded || App.TaskExists()) ok = App.IsAdmin ? App.ApplyTask(cfg.AdminMode, cfg.Autostart) == 0 : App.RunElevated("--apply-task");
        if (!ok) cfg.AdminMode = App.TaskExists();
        SetRunEntry(cfg.Autostart && !cfg.AdminMode);
        return ok;
    }

    // No white boxes: dark colours plus Windows' own dark theme for the native parts (drop-downs, spin buttons)
    static void DarkControls(Control root) {
        foreach (Control c in root.Controls) {
            var cb = c as ComboBox; var nu = c as NumericUpDown; var bt = c as Button;
            if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.BackColor = BgPanel; cb.ForeColor = Color.Gainsboro;
                cb.HandleCreated += (s, e) => HistoryForm.SetWindowTheme(((Control)s).Handle, "DarkMode_CFD", null); }
            if (nu != null) { nu.BackColor = BgPanel; nu.ForeColor = Color.Gainsboro; nu.BorderStyle = BorderStyle.FixedSingle;
                nu.HandleCreated += (s, e) => { foreach (Control k in ((Control)s).Controls) HistoryForm.SetWindowTheme(k.Handle, "DarkMode_Explorer", null); }; }
            if (bt != null) { bt.BackColor = BtnOff; bt.ForeColor = Color.Gainsboro; bt.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90); }
        }
    }

    void ShowAbout() { using (var a = new AboutForm(Icon)) a.ShowDialog(this); }

    void ShowSettings() {
        var d = new Form { Text = "Settings", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 284), BackColor = BgForm, ForeColor = Color.Gainsboro, Font = UiFont, ShowInTaskbar = false };
        d.HandleCreated += (s, e) => DarkTitle(d);
        // two-way switch instead of a drop-down (no native white parts): ETW (Windows built-in) | Off
        string measure = cfg.Measure;
        var btnEtw = new Button { Text = "ETW (built-in)", Location = new Point(170, 12), Size = new Size(100, 28), FlatStyle = FlatStyle.Flat };
        var btnOff = new Button { Text = "Off", Location = new Point(274, 12), Size = new Size(66, 28), FlatStyle = FlatStyle.Flat };
        Action paintSwitch = () => {
            foreach (var b in new[] { btnEtw, btnOff }) {
                bool on = (b == btnEtw) == (measure == "ETW");
                b.BackColor = on ? Color.LightGreen : BtnOff; b.ForeColor = on ? Color.Black : Color.Gainsboro;
                b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            }
        };
        btnEtw.Click += (s, e) => { measure = "ETW"; paintSwitch(); };
        btnOff.Click += (s, e) => { measure = "Off"; paintSwitch(); };        var chkAdmin = new CheckBox { Text = "Run as administrator (asks only once)", Location = new Point(16, 52), Size = new Size(330, 22), Checked = cfg.AdminMode };
        var note = L("Needed for USB measurement and for Re-Start without a prompt.", 34, 74, 310, 32); note.ForeColor = Color.Gray;
        var chkAuto = new CheckBox { Text = "Start with Windows (in the tray)", Location = new Point(16, 112), Size = new Size(330, 22), Checked = cfg.Autostart };
        var num = new NumericUpDown { Location = new Point(170, 147), Size = new Size(70, 22), Minimum = 5, Maximum = 600, Value = cfg.CheckSec };
        var numDays = new NumericUpDown { Location = new Point(170, 181), Size = new Size(70, 22), Minimum = 1, Maximum = 365, Value = cfg.HistoryDays };
        var state = L("Now running as " + (App.IsAdmin ? "administrator" : "normal user"), 16, 216, 150, 20); state.ForeColor = Color.Gray;
        var about = new LinkLabel { Text = "About LinkWatch", Location = new Point(16, 252), AutoSize = true, LinkColor = Color.LightGreen, ActiveLinkColor = Color.White, VisitedLinkColor = Color.LightGreen };
        about.LinkClicked += (s, e) => { using (var a = new AboutForm(Icon)) a.ShowDialog(d); };
        d.Controls.Add(about);
        var ok = new Button { Text = "OK", Location = new Point(176, 246), Size = new Size(80, 28), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(264, 246), Size = new Size(80, 28), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        d.AcceptButton = ok; d.CancelButton = cancel;
        d.Controls.AddRange(new Control[] { L("USB measurement", 16, 18, 140, 20), btnEtw, btnOff, chkAdmin, note, chkAuto, L("Service check every (s)", 16, 150, 150, 20), num,
            L("Keep history (days)", 16, 184, 150, 20), numDays, state, ok, cancel });
        DarkControls(d); paintSwitch(); d.ActiveControl = ok;
        if (d.ShowDialog(this) != DialogResult.OK) return;

        bool oldAdmin = cfg.AdminMode, oldAuto = cfg.Autostart;
        cfg.Measure = measure;
        cfg.AdminMode = chkAdmin.Checked; cfg.Autostart = chkAuto.Checked; cfg.CheckSec = (int)num.Value;
        cfg.HistoryDays = (int)numDays.Value; History.KeepDays = cfg.HistoryDays; History.Cleanup();
        timerSvc.Interval = cfg.CheckSec * 1000;
        cfg.Save();   // the elevated --apply-task helper reads these
        if (oldAdmin != cfg.AdminMode || oldAuto != cfg.Autostart) {
            if (!ApplyStartup()) MessageBox.Show(this, "Administrator permission was not given, admin mode was not changed.", App.Name);
            cfg.Save();
        }
        // switched on admin mode while running as a normal user: hand over to the elevated instance
        if (cfg.AdminMode && !App.IsAdmin && App.TaskExists()) {
            Quit(); showEvent.Close();
            Process.Start(App.ExePath);
            return;
        }
        StartUsb(); CheckService();
    }
}

