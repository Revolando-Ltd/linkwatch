using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// One-minute averages and events, one CSV per day under %APPDATA%\LinkWatch\history, kept 30 days.
public class MinuteRow { public DateTime T; public double Out, OutPeak, In, Rx, Tx; }
public class EventRow { public DateTime T; public string Kind, Text; }

public static class History {
    public static int KeepDays = 30;   // set from settings
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkWatch", "history"); } }

    // current minute being collected
    static DateTime curMin = DateTime.MinValue;
    static double sOut, sIn, sRx, sTx, peakOut; static int n;

    public static void AddSecond(DateTime now, double outB, double inB, double rx, double tx) {
        var m = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (m != curMin) { Flush(); curMin = m; }
        sOut += outB; sIn += inB; sRx += rx; sTx += tx; if (outB > peakOut) peakOut = outB; n++;
    }
    public static void Flush() {
        if (n > 0 && curMin != DateTime.MinValue) {
            string line = string.Join(",", curMin.ToString("yyyy-MM-ddTHH:mm", Inv),
                (sOut / n).ToString("F0", Inv), peakOut.ToString("F0", Inv), (sIn / n).ToString("F0", Inv),
                (sRx / n).ToString("F0", Inv), (sTx / n).ToString("F0", Inv));
            Append("rates_" + curMin.ToString("yyyy-MM-dd") + ".csv", line);
        }
        sOut = sIn = sRx = sTx = peakOut = 0; n = 0;
    }
    // kind: service-down, service-up, usb-on, usb-off, usb-speed, restart, app
    public static void Event(string kind, string text) {
        var now = DateTime.Now;
        Append("events_" + now.ToString("yyyy-MM-dd") + ".csv", now.ToString("yyyy-MM-ddTHH:mm:ss", Inv) + "," + kind + "," + text.Replace(",", ";"));
    }
    static void Append(string file, string line) {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, file), line + "\r\n"); } catch { }
    }
    public static void Cleanup() {
        try {
            var limit = DateTime.Today.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(Dir, "*.csv")) {
                DateTime d;
                string stamp = Path.GetFileNameWithoutExtension(f); stamp = stamp.Substring(stamp.IndexOf('_') + 1);
                if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d) && d < limit) File.Delete(f);
            }
        } catch { }
    }
    static IEnumerable<string> Lines(string prefix, DateTime from, DateTime to) {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) {
            string f = Path.Combine(Dir, prefix + d.ToString("yyyy-MM-dd") + ".csv");
            if (!File.Exists(f)) continue;
            string[] all;
            try { using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var r = new StreamReader(s)) all = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries); }
            catch { continue; }
            foreach (var l in all) yield return l;
        }
    }
    public static List<MinuteRow> Rates(DateTime from, DateTime to) {
        var list = new List<MinuteRow>();
        foreach (var l in Lines("rates_", from, to)) {
            var p = l.Split(','); DateTime t;
            if (p.Length < 6 || !DateTime.TryParseExact(p[0], "yyyy-MM-ddTHH:mm", Inv, DateTimeStyles.None, out t) || t < from || t > to) continue;
            list.Add(new MinuteRow { T = t, Out = double.Parse(p[1], Inv), OutPeak = double.Parse(p[2], Inv), In = double.Parse(p[3], Inv), Rx = double.Parse(p[4], Inv), Tx = double.Parse(p[5], Inv) });
        }
        return list;
    }
    public static List<EventRow> Events(DateTime from, DateTime to) {
        var list = new List<EventRow>();
        foreach (var l in Lines("events_", from, to)) {
            var p = l.Split(new[] { ',' }, 3); DateTime t;
            if (p.Length < 3 || !DateTime.TryParseExact(p[0], "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out t) || t < from || t > to) continue;
            list.Add(new EventRow { T = t, Kind = p[1], Text = p[2] });
        }
        return list;
    }
    public static Color KindColor(string kind) {
        switch (kind) {
            case "service-down": return Color.Red;
            case "service-up": return Color.LimeGreen;
            case "usb-speed": return Color.Orange;
            case "usb-off": return Color.Gray;
            case "usb-on": return Color.DeepSkyBlue;
            case "restart": return Color.Gold;
            default: return Color.FromArgb(120, 120, 120);
        }
    }
}

// History window: rate graph for a chosen range with event markers, and the event list below.
public class HistoryForm : Form {
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr h, string app, string idList);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    static readonly Color BgForm = Color.FromArgb(24, 24, 24), BgGraph = Color.FromArgb(16, 16, 16), BtnOff = Color.FromArgb(60, 60, 60);
    static readonly Font UiFont = new Font("Segoe UI", 9f);
    readonly PictureBox graph; readonly ListView list; readonly CheckBox chkWifi; readonly Label hover;
    readonly List<Button> rangeButtons = new List<Button>();
    TimeSpan range = TimeSpan.FromHours(24);
    DateTime from, to;
    List<MinuteRow> rows = new List<MinuteRow>(); List<EventRow> events = new List<EventRow>();
    EventRow selected; int hoverX = -1;
    const int PadL = 8, PadR = 8, PadT = 22, PadB = 22;

    public HistoryForm() {
        Text = "LinkWatch - History"; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); ClientSize = new Size(760, 520); MinimumSize = new Size(560, 420);
        BackColor = BgForm; ForeColor = Color.Gainsboro; Font = UiFont; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        HandleCreated += (s, e) => MainForm.DarkTitle(this);

        int x = 10;
        foreach (var r in new[] { Tuple.Create("1 h", 1.0), Tuple.Create("24 h", 24.0), Tuple.Create("7 d", 168.0), Tuple.Create("30 d", 720.0) }) {
            var b = new Button { Text = r.Item1, Location = new Point(x, 10), Size = new Size(64, 30), FlatStyle = FlatStyle.Flat, BackColor = BtnOff, Tag = r.Item2 };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            b.Click += (s, e) => { range = TimeSpan.FromHours((double)((Button)s).Tag); Reload(); };
            rangeButtons.Add(b); Controls.Add(b); x += 70;
        }
        chkWifi = new CheckBox { Text = "Show Wi-Fi", Location = new Point(x + 10, 15), Size = new Size(100, 22) };
        chkWifi.CheckedChanged += (s, e) => graph.Invalidate();
        var refresh = new Button { Text = "Refresh", Location = new Point(x + 120, 10), Size = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = BtnOff };
        refresh.Click += (s, e) => Reload();
        hover = new Label { Location = new Point(x + 210, 8), Size = new Size(300, 36), ForeColor = Color.Gray, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.AddRange(new Control[] { chkWifi, refresh, hover });

        graph = new PictureBox { Location = new Point(10, 50), Size = new Size(740, 270), BackColor = BgGraph, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        graph.Paint += (s, e) => Draw(e.Graphics);
        graph.Resize += (s, e) => graph.Invalidate();
        graph.MouseMove += (s, e) => { hoverX = e.X; UpdateHover(); graph.Invalidate(); };
        graph.MouseLeave += (s, e) => { hoverX = -1; hover.Text = ""; graph.Invalidate(); };
        Controls.Add(graph);

        list = new ListView { Location = new Point(10, 330), Size = new Size(740, 180), View = View.Details, FullRowSelect = true, HideSelection = false,
            BackColor = BgGraph, ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        list.Columns.Add("Time", 150); list.Columns.Add("Event", 560);
        // dark column header (the default one is white)
        list.OwnerDraw = true;
        list.DrawColumnHeader += (s, e) => {
            using (var b = new SolidBrush(Color.FromArgb(38, 38, 38))) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, UiFont, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        list.DrawItem += (s, e) => e.DrawDefault = true;
        list.DrawSubItem += (s, e) => e.DrawDefault = true;
        // Windows' own dark theme for the scrollbar and the empty header area
        list.HandleCreated += (s, e) => {
            SetWindowTheme(list.Handle, "DarkMode_Explorer", null);
            IntPtr header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
            if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
        };
        list.SelectedIndexChanged += (s, e) => { selected = list.SelectedItems.Count > 0 ? (EventRow)list.SelectedItems[0].Tag : null; graph.Invalidate(); };
        Controls.Add(list);
        // last column fills the rest of the header, so no blank header strip is left
        Resize += (s, e) => { list.Columns[1].Width = -2; };
        Shown += (s, e) => { list.Columns[1].Width = -2; };
        Reload();
    }

    void Reload() {
        History.Flush();
        to = DateTime.Now; from = to - range;
        rows = History.Rates(from, to); events = History.Events(from, to);
        foreach (var b in rangeButtons) { bool on = (double)b.Tag == range.TotalHours; b.BackColor = on ? Color.LightGreen : BtnOff; b.ForeColor = on ? Color.Black : Color.Gainsboro; }
        list.BeginUpdate(); list.Items.Clear();
        foreach (var ev in events.OrderByDescending(e => e.T)) {
            var it = new ListViewItem(ev.T.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = ev, ForeColor = History.KindColor(ev.Kind) };
            it.SubItems.Add(ev.Text); list.Items.Add(it);
        }
        list.EndUpdate();
        selected = null; graph.Invalidate();
    }

    float X(DateTime t, int w) { return PadL + (float)((t - from).TotalSeconds / (to - from).TotalSeconds) * (w - PadL - PadR); }

    // values per pixel column (average of the minutes in it) so 30 days still fit
    double[] Bucket(Func<MinuteRow, double> f, int w, bool max) {
        int cols = Math.Max(1, w - PadL - PadR);
        var sum = new double[cols]; var cnt = new int[cols];
        foreach (var r in rows) {
            int c = (int)(X(r.T, w) - PadL); if (c < 0 || c >= cols) continue;
            double v = f(r); if (max) sum[c] = Math.Max(sum[c], v); else sum[c] += v; cnt[c]++;
        }
        var res = new double[cols];
        for (int i = 0; i < cols; i++) res[i] = cnt[i] == 0 ? double.NaN : (max ? sum[i] : sum[i] / cnt[i]);
        return res;
    }

    void Draw(Graphics g) {
        int w = graph.Width, h = graph.Height;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var grid = new Pen(Color.FromArgb(42, 42, 42))) {
            for (int i = 1; i < 4; i++) { float y = PadT + (h - PadT - PadB) * i / 4f; g.DrawLine(grid, PadL, y, w - PadR, y); }
            for (int i = 0; i <= 6; i++) {
                var t = from + TimeSpan.FromTicks((to - from).Ticks * i / 6);
                float x = X(t, w); g.DrawLine(grid, x, PadT, x, h - PadB);
                string lab = range.TotalHours <= 24 ? t.ToString("HH:mm") : t.ToString("dd MMM HH:mm");
                var sz = g.MeasureString(lab, UiFont);
                g.DrawString(lab, UiFont, Brushes.Gray, Math.Min(Math.Max(0, x - sz.Width / 2), w - sz.Width), h - PadB + 3);
            }
        }
        var keys = new List<Tuple<Func<MinuteRow, double>, Color, bool>> {
            Tuple.Create<Func<MinuteRow, double>, Color, bool>(r => r.OutPeak, Color.FromArgb(70, 50, 205, 50), true),
            Tuple.Create<Func<MinuteRow, double>, Color, bool>(r => r.Out, Color.LimeGreen, false),
            Tuple.Create<Func<MinuteRow, double>, Color, bool>(r => r.In, Color.MediumOrchid, false) };
        if (chkWifi.Checked) {
            keys.Add(Tuple.Create<Func<MinuteRow, double>, Color, bool>(r => r.Rx, Color.DeepSkyBlue, false));
            keys.Add(Tuple.Create<Func<MinuteRow, double>, Color, bool>(r => r.Tx, Color.Orange, false));
        }
        var data = keys.Select(k => Bucket(k.Item1, w, k.Item3)).ToList();
        double peak = 1024;
        foreach (var d in data) foreach (var v in d) if (!double.IsNaN(v) && v > peak) peak = v;
        float plotH = h - PadT - PadB;
        for (int k = 0; k < data.Count; k++) {
            using (var pen = new Pen(keys[k].Item2, k == 0 ? 1f : 1.6f)) {
                var seg = new List<PointF>();
                Action drawSeg = () => { if (seg.Count > 1) g.DrawLines(pen, seg.ToArray()); else if (seg.Count == 1) g.DrawRectangle(pen, seg[0].X, seg[0].Y, 1, 1); seg.Clear(); };
                for (int i = 0; i < data[k].Length; i++) {
                    double v = data[k][i];
                    if (double.IsNaN(v)) { drawSeg(); continue; }   // gap: monitor was not running
                    seg.Add(new PointF(PadL + i, (float)(PadT + plotH - plotH * v / peak)));
                }
                drawSeg();
            }
        }
        // event markers
        foreach (var ev in events) {
            float x = X(ev.T, w);
            using (var p = new Pen(History.KindColor(ev.Kind), ev == selected ? 3f : 1f) { DashStyle = ev == selected ? DashStyle.Solid : DashStyle.Dot })
                g.DrawLine(p, x, PadT, x, h - PadB);
        }
        if (hoverX >= PadL && hoverX <= w - PadR) using (var p = new Pen(Color.FromArgb(110, 110, 110))) g.DrawLine(p, hoverX, PadT, hoverX, h - PadB);
        g.DrawString("peak: " + MainForm.Rate(peak) + "    green: to Quest (avg, light = 1 s peak)   purple: from Quest (~)" + (chkWifi.Checked ? "   blue/orange: Wi-Fi" : ""),
            UiFont, Brushes.Gray, PadL, 3);
        if (rows.Count == 0) g.DrawString("No data recorded in this range yet.", UiFont, Brushes.Gray, w / 2 - 110, h / 2 - 8);
    }

    void UpdateHover() {
        int w = graph.Width;
        if (rows.Count == 0 || hoverX < PadL) { hover.Text = ""; return; }
        var t = from + TimeSpan.FromSeconds((to - from).TotalSeconds * (hoverX - PadL) / (w - PadL - PadR));
        var r = rows.OrderBy(x => Math.Abs((x.T - t).TotalSeconds)).First();
        if (Math.Abs((r.T - t).TotalMinutes) > Math.Max(2, range.TotalMinutes / (w - PadL - PadR) * 2)) { hover.Text = t.ToString("dd MMM HH:mm") + "   no data"; return; }
        hover.Text = r.T.ToString("dd MMM HH:mm") + "   to Quest " + MainForm.Rate(r.Out) + " (peak " + MainForm.Rate(r.OutPeak) + ")\r\nfrom Quest ~" + MainForm.Rate(r.In) +
            "   Wi-Fi " + MainForm.Rate(r.Rx) + " / " + MainForm.Rate(r.Tx);
    }
}
