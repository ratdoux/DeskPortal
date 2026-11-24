using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace DeskLink;

sealed class PerformanceForm : Form
{
    readonly Label connection = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    readonly Label summary = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
    readonly BandwidthChart chart = new();
    readonly ListView traffic = Table("Traffic by type", ("Traffic", 150), ("Send Mbps", 105), ("Receive Mbps", 115),
        ("Total sent", 115), ("Total received", 115));
    readonly ListView streams = Table("Active shared windows", ("Window", 158), ("Direction", 78), ("Codec", 154),
        ("Last slow stage", 232), ("Click ms", 68), ("Pixels", 94), ("FPS", 52), ("Present FPS", 82), ("Mbps", 62), ("Codec ms", 78), ("Queue ms", 78), ("Setup ms", 78));
    readonly Label windowHint = new() { AutoSize = true, Dock = DockStyle.Fill };

    public PerformanceForm(DeskAppearance? appearance = null)
    {
        SuspendLayout();
        Text = "DeskPortal performance";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 518);
        MinimumSize = new Size(610, 395);
        ShowInTaskbar = false;
        var body = Panel();
        body.Controls.Add(connection, 0, 0);
        body.Controls.Add(summary, 0, 1);
        var tabs = new DeskPages();
        body.RowStyles.Add(new(SizeType.AutoSize)); body.RowStyles.Add(new(SizeType.AutoSize));
        body.RowStyles.Add(new(SizeType.Percent, 100));
        body.Controls.Add(tabs, 0, 2);
        Controls.Add(body);

        var network = Panel();
        network.RowStyles.Add(new(SizeType.AutoSize));
        network.RowStyles.Add(new(SizeType.Percent, 55));
        network.RowStyles.Add(new(SizeType.Percent, 45));
        network.RowStyles.Add(new(SizeType.AutoSize));
        network.Controls.Add(new Label { Text = "Bandwidth over the last 60 seconds", AutoSize = true }, 0, 0);
        network.Controls.Add(chart, 0, 1);
        network.Controls.Add(traffic, 0, 2);
        network.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text =
            "Network RTT is Windows’ smoothed TCP estimate for the input connection. DeskPortal delay measures the local send queue and the peer’s input dispatch and reply queue. Input round trip times the full probe.\nTraffic counts DeskPortal message bytes, including framing." }, 0, 3);
        var windows = Panel();
        windows.RowStyles.Add(new(SizeType.AutoSize));
        windows.RowStyles.Add(new(SizeType.Percent, 100));
        windows.RowStyles.Add(new(SizeType.AutoSize));
        windows.Controls.Add(windowHint, 0, 0);
        windows.Controls.Add(streams, 0, 1);
        windows.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text =
            "Codec and queue times are averages per frame. Last slow stage retains the most recent measured operation of at least 100 ms while the window is shared. Every one is also appended to slow-stages.log in the app data folder. Present FPS counts new frames submitted to DXGI or drawn as bitmaps." }, 0, 2);
        tabs.AddPage("Network", network); tabs.AddPage("Shared windows", windows);
        connection.Font = new Font("Consolas", 10, FontStyle.Bold); connection.Tag = Tone.Primary;
        connection.Padding = new Padding(7, 4, 7, 4);
        DeskStyle.Apply(this, appearance ?? DeskAppearance.Initial);
        ResumeLayout(true);
    }

    static TableLayoutPanel Panel() => new() { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
    static ListView Table(string name, params (string Text, int Width)[] columns)
    {
        var table = new ListView { AccessibleName = name, View = View.Details, Dock = DockStyle.Fill,
            FullRowSelect = true, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        foreach (var (title, width) in columns) table.Columns.Add(title, width);
        return table;
    }
    static void SetRow(ListView table, string key, string[] cells, string tip = "")
    {
        if (table.Items[key] is not { } item)
        {
            table.Items.Add(new ListViewItem(cells) { Name = key, ToolTipText = tip });
            return;
        }
        for (int i = 0; i < cells.Length; i++)
            if (item.SubItems[i].Text != cells[i]) item.SubItems[i].Text = cells[i];
        item.ToolTipText = tip;
    }
    static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B", < 1048576 => $"{bytes / 1024d:N1} KiB",
        < 1073741824 => $"{bytes / 1048576d:N1} MiB", _ => $"{bytes / 1073741824d:N2} GiB"
    };
    static string Rate(double value) => value switch
    {
        0 => "0", < 0.001 => "<0.001", < 1 => value.ToString("0.000"), _ => value.ToString("0.0")
    };
    static string Metric(double? value) => value?.ToString("0.0") ?? "—";

    public void Render(SessionPerformance? session)
    {
        connection.Text = session == null ? "Pair a computer to see session performance." :
            $"{Environment.MachineName}  ↔  {session.Computer}     ·     " +
            (session.IndependentConnections ? "Separate input, video and transfer connections" : "Legacy shared connection");
        var sample = session?.Latest;
        chart.SetHistory(session?.History);
        windowHint.Text = sample?.Windows.Length > 0 ? "Click ms measures input arrival to fresh capture on the sender, and click release to fresh video on the receiver." : "No active window streams";
        if (sample == null)
        {
            summary.Text = session == null ? "" : "Measuring the connection…";
            traffic.Items.Clear(); streams.Items.Clear(); return;
        }
        summary.Text = $"Network RTT  {Metric(sample.NetworkRttMs)} ms     DeskPortal delay  {Metric(sample.DeskLinkDelayMs)} ms     " +
            $"Input round trip  {Metric(sample.RoundTripMs)} ms\nSend  {Rate(sample.SendRate)} Mbps     Receive  {Rate(sample.ReceiveRate)} Mbps     " +
            $"DeskPortal CPU  {sample.CpuPercent:F1}%     Memory  {Bytes(sample.WorkingSet)}" +
            (sample.Transport == null ? "" : $"\n{sample.Transport}");
        void Category(string label, long send, long receive, long sent, long received) =>
            SetRow(traffic, label, [label, Rate(PerformanceSample.Mbps(send, sample.Seconds)), Rate(PerformanceSample.Mbps(receive, sample.Seconds)), Bytes(sent), Bytes(received)]);
        var s = sample.SentDelta; var r = sample.ReceivedDelta;
        var st = sample.Sent; var rt = sample.Received;
        traffic.BeginUpdate();
        Category("Window video", s.Video, r.Video, st.Video, rt.Video);
        Category("Files", s.Files, r.Files, st.Files, rt.Files);
        Category("Clipboard", s.Clipboard, r.Clipboard, st.Clipboard, rt.Clipboard);
        Category("Input & control", s.Control, r.Control, st.Control, rt.Control);
        Category("Total", s.Total, r.Total, st.Total, rt.Total);
        traffic.EndUpdate();
        streams.BeginUpdate();
        var active = new HashSet<string>();
        foreach (var w in sample.Windows)
        {
            string key = w.Id.ToString();
            active.Add(key);
            SetRow(streams, key, [w.Title, w.Sending ? "Send" : "Receive", w.Codec.Split('·')[0].Trim(),
                w.LastSlowStage ?? "—", Metric(w.ClickMs), w.Pixels.IsEmpty ? "—" : $"{w.Pixels.Width}×{w.Pixels.Height}", Metric(w.Fps), Metric(w.PaintedFps),
                Rate(w.Mbps), Metric(w.CodecMs), Metric(w.QueueMs), Metric(w.SetupMs)], w.Title + " · " + w.Codec);
        }
        for (int i = streams.Items.Count - 1; i >= 0; i--)
            if (!active.Contains(streams.Items[i].Name)) streams.Items.RemoveAt(i);
        streams.EndUpdate();
    }
}

sealed class BandwidthChart : Control
{
    IReadOnlyCollection<BandwidthPoint>? history;
    public BandwidthChart()
    {
        Dock = DockStyle.Fill; DoubleBuffered = true; BackColor = SystemColors.Window;
        AccessibleName = "Sent and received bandwidth over the last minute";
    }
    public void SetHistory(IReadOnlyCollection<BandwidthPoint>? points) { history = points; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int line = Font.Height + 3;
        var plot = Rectangle.FromLTRB(4, line + 6, Width - 6, Height - line - 4);
        if (plot.Width < 60 || plot.Height < 10) return;
        var palette = DeskStyle.PaletteFor(this);
        // Accent swatches carry their own contrasting text. For thin plot lines,
        // mix the hue toward the foreground until it stands out on this canvas.
        Color Trace(Color color)
        {
            double background = DeskPalette.Luminance(BackColor);
            for (int i = 0; i < 6; i++)
            {
                double lineLight = DeskPalette.Luminance(color);
                if ((Math.Max(lineLight, background) + .05) / (Math.Min(lineLight, background) + .05) >= 3) break;
                color = Color.FromArgb((color.R + ForeColor.R) / 2, (color.G + ForeColor.G) / 2, (color.B + ForeColor.B) / 2);
            }
            return color;
        }
        using var send = new Pen(Trace(palette.Primary), 2);
        using var receive = new Pen(Trace(palette.Complement), 2) { DashStyle = DashStyle.Dash };
        int Legend(string name, Pen pen, int x)
        {
            g.DrawLine(pen, x, line / 2, x + 20, line / 2);
            TextRenderer.DrawText(g, name, Font, new Point(x + 22, 0), ForeColor);
            return x + 32 + TextRenderer.MeasureText(g, name, Font).Width;
        }
        int next = Legend("Send", send, 4);
        Legend("Receive", receive, next);
        double peak = history?.Count > 0 ? Math.Max(1, Math.Ceiling(history.Max(p => Math.Max(p.Send, p.Receive)))) : 1;
        void Right(string text, int y) => TextRenderer.DrawText(g, text, Font, new Rectangle(0, y, Width - 4, line), ForeColor, TextFormatFlags.Right);
        Right($"{peak:N0} Mbps", 0);
        TextRenderer.DrawText(g, "60 s ago", Font, new Point(1, plot.Bottom + 1), ForeColor);
        Right("Now", plot.Bottom + 1);
        g.DrawRectangle(SystemPens.ControlDark, plot);
        if (history == null || history.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        long latest = history.Last().Timestamp;
        PointF? previousSend = null, previousReceive = null;
        foreach (var point in history)
        {
            double age = Stopwatch.GetElapsedTime(point.Timestamp, latest).TotalSeconds;
            float x = plot.Right - (float)(Math.Min(age, 60) / 60 * plot.Width);
            PointF At(double rate) => new(x, plot.Bottom - (float)(rate / peak * plot.Height));
            var sent = At(point.Send); var received = At(point.Receive);
            if (previousSend is { } a && previousReceive is { } b)
            { g.DrawLine(send, a, sent); g.DrawLine(receive, b, received); }
            else { g.DrawEllipse(send, sent.X - 1, sent.Y - 1, 2, 2); g.DrawEllipse(receive, received.X - 1, received.Y - 1, 2, 2); }
            previousSend = sent; previousReceive = received;
        }
    }
}
