using System.Net;
using System.Collections.Concurrent;
namespace DeskLink;

static class ProjectionChecks
{
    static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    static async Task Until(Func<bool> condition, string reason)
    {
        var end = Environment.TickCount64 + 12000;
        while (!condition())
        {
            if (Environment.TickCount64 >= end) throw new TimeoutException(reason);
            await Task.Delay(20);
        }
    }

    // Three isolated loopback interfaces run the production listener, two-sided comparison,
    // split TLS channels, per-peer policy and disconnect handling. No firewall rule is installed.
    public static int Run(bool capture, bool recordInput = false)
    {
        Exception? failure = null;
        using var owner = new Form { ShowInTaskbar = false, Opacity = 0, Size = new(50, 50) };
        owner.Shown += async (_, _) =>
        {
            try { await Check(owner, capture, recordInput); }
            catch (Exception ex) { failure = ex; }
            finally { owner.Close(); }
        };
        Application.Run(owner);
        if (failure == null) return 0;
        Console.Error.WriteLine(failure); return 1;
    }
    static async Task Check(Form owner, bool capture, bool recordInput)
    {
        var a = IPAddress.Loopback;
        var b = IPAddress.Parse("127.0.0.2"); var c = IPAddress.Parse("127.0.0.3");
        var codes = new List<string>();
        var initial = new DeskSettings { Stream = new(FrameCodec.H264, 12, Mbps: 6) };
        var rightSettings = initial;
        var reports = recordInput ? new ConcurrentQueue<MonitorInput.Input>() : null;
        using var input = new MonitorInput(reports == null ? null : reports.Enqueue);
        ScreenPeers Node(Func<DeskSettings> settings) => new(owner, Guid.NewGuid(), settings, input, Console.WriteLine, () => { },
            (_, code, _) => { codes.Add(code); return Task.FromResult(true); });
        using var hub = Node(() => initial); using var left = Node(() => initial); using var right = Node(() => rightSettings);
        int port = hub.ListenOn(new(a, 0), [b, c]);
        left.ListenOn(new(b, port), [a]); right.ListenOn(new(c, port), [a]);
        var first = hub.Connect(b); var second = hub.Connect(c);
        try
        {
            await Until(() => hub.Sessions.Count() == 2 && left.Sessions.Any() && right.Sessions.Any(), "Two simultaneous screen pairs");
            Require(codes.Count == 4 && codes.GroupBy(s => s).All(g => g.Count() % 2 == 0), "Independent confirmation codes differed");
            var choices = hub.Sessions.SelectMany(s => s.Choices).ToArray();
            Require(choices.Length == Screen.AllScreens.Length * 2, "Each peer contributes its own monitor inventory");
            var work = Screen.PrimaryScreen!.WorkingArea;
            using (var picker = new MonitorPicker(work, initial.Appearance, _ => { }))
            {
                picker.UpdateChoices(choices); picker.Show();
                Require(Math.Abs(picker.Left + picker.Width / 2 - (work.Left + work.Width / 2)) <= 1 &&
                    Math.Abs(picker.Top + picker.Height / 2 - (work.Top + work.Height / 2)) <= 1, "Picker center");
                picker.Close();
            }
            rightSettings = initial with { Features = Sharing.Mouse };
            right.SettingsChanged();
            await Until(() => hub.Sessions.Count(s => s.Available) == 1, "Permission change should affect only one peer");
            var session = hub.Sessions.Single(s => s.Available);
            if (capture) await CheckCapture(session, left.Sessions.Single(), reports);
            left.Disconnect(a);
            await Until(() => hub.Sessions.Count() == 1, "Disconnect one of two peers");
            rightSettings = initial; right.SettingsChanged();
            await Until(() => hub.Sessions.Single().Available, "Remaining peer can resume sharing");
            Console.WriteLine("PASS  Three loopback PCs paired concurrently with matching codes, separate monitor inventories, a centered picker and independent permission and disconnect handling.");
        }
        finally { hub.DisconnectAll(); left.DisconnectAll(); right.DisconnectAll(); await Task.WhenAll(first, second); }
        await CheckMainPair();
    }
    static async Task CheckMainPair()
    {
        using var form = new DeskForm(false) { Opacity = 0, ShowInTaskbar = false };
        form.UseSettings(new DeskSettings { Features = Sharing.Mouse | Sharing.Windows });
        form.Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var invitation = new Invitation(IPAddress.Loopback, 0);
        var accept = invitation.Accept(timeout.Token);
        using var remote = await Invitation.Join("127.0.0.1", invitation.Code, timeout.Token, invitation.ListeningPort);
        using var local = await accept;
        var requests = new ConcurrentQueue<MonitorRequest>();
        var returns = new ConcurrentQueue<ReturnInfo>();
        var mouse = new ConcurrentQueue<MouseSample>();
        var reader = Task.Run(() => remote.Run(p =>
        {
            if (p.Kind == Kind.MonitorRequest) requests.Enqueue(p.Read<MonitorRequest>());
            if (p.Kind == Kind.ReturnWindow) returns.Enqueue(p.Read<ReturnInfo>());
            if (p.Kind == Kind.Mouse) mouse.Enqueue(p.Read<MouseSample>());
            return Task.CompletedTask;
        }));
        var session = form.RunSession(local, timeout.Token);
        try
        {
            var screen = new DisplayInfo("Test display", new(0, 0, 1280, 720), true);
            remote.Send(Kind.Hello, new DeskHello(2, "ScreenTest", 1280, 720, true, Sharing.Mouse | Sharing.Windows,
                Displays: [screen], LayoutNode: Guid.NewGuid(), MonitorProjection: true, WindowIcons: true));
            await Until(() => form.ScreenLayout != null, "Main peer hello");
            remote.Send(Kind.MonitorPicker, true);
            await Until(() => !requests.IsEmpty, "Forwarded shortcut on the pointer's PC");
            Require(requests.TryDequeue(out var req) && req.Display == screen.Id, "Open sole monitor directly");
            remote.Send(Kind.MonitorClose, req!.Id);
            var id = Guid.NewGuid();
            var desktop = SystemInformation.VirtualScreen;
            var area = Screen.PrimaryScreen!.WorkingArea;
            var position = new Point(area.Left - desktop.Left + 50, area.Top - desktop.Top + 70);
            remote.Send(Kind.Window, new ViewInfo(id, "Toolbar test", position.X, position.Y, 320, 220,
                DropPoint: position, Resizable: true));
            using var picture = new Bitmap(320, 220); using var bytes = new MemoryStream();
            picture.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
            remote.Frame(id, bytes.ToArray());
            StreamWindow? Window() => Application.OpenForms.OfType<StreamWindow>().FirstOrDefault(w => w.Id == id);
            await Until(() => Window()?.Visible == true, "Normal streamed window toolbar");
            var view = Window()!;
            await CheckWindowIcon(form, view, remote);
            var buttons = view.Controls.OfType<Panel>().Single().Controls.OfType<Button>().ToArray();
            buttons.Single(b => b.AccessibleName == "Tile on the left").PerformClick();
            Require(view.Bounds == DeskSettings.Place(TileMode.LeftHalf, view.Bounds, area, true), "Normal window tiles on viewer");
            buttons.Single(b => b.AccessibleName == "Send window back").PerformClick();
            await Until(() => returns.Any(r => r.Id == id && r.X == int.MinValue), "Send-back command for original window");
            remote.Send(Kind.EndWindow, id);
            await Until(() => view.IsDisposed, "Streamed window cleanup");
            Console.WriteLine("PASS  Primary pair opens the sole monitor on a forwarded shortcut. Normal window toolbar tiles locally and sends the original home.");
            await CheckWake(form, local, mouse);
            var original = DesktopTopology.Read();
            var changed = original with { Bounds = original.Bounds with { Width = original.Bounds.Width + 1 } };
            form.ObserveTopology(changed);
            form.ObserveTopology(original with { Displays = original.Displays.Reverse().ToArray() });
            Require(form.ScreenLayout != null, "Transient power or reordered inventory disconnected the pair");
            form.ObserveTopology(changed); form.ObserveTopology(changed);
            Require(form.ScreenLayout == null, "A persistent layout change must invalidate the old shared edges");
        }
        finally
        {
            form.Close(); remote.Dispose(); local.Dispose();
            try { await Task.WhenAll(session, reader); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    static async Task CheckWake(Form owner, Peer peer, ConcurrentQueue<MouseSample> reports)
    {
        var layout = new DeskLayout(new Rectangle(50, 60, 400, 300), new Size(500, 300), true);
        using var desk = new WindowDesk(owner, peer, layout, _ => { });
        using var clip = new SharedClipboard(peer, _ => { }, () => { });
        using var files = new EdgeFiles(layout, peer, clip, _ => { });
        Point anchor = default;
        var desktop = new DesktopAvailability();
        using var pointer = new PointerBridge(peer, desk, layout, files, p => anchor = p, desktop);
        pointer.SetSharing(Sharing.Mouse);
        pointer.Physical(new(400, 180), Win.Move, 0);
        Require(pointer.Physical(new(449, 180), Win.Move, 0), "Enter peer before sleep");
        pointer.Physical(anchor + new Size(160, 0), Win.Move, 0);
        await Until(() => reports.Any(m => m.X == 160), "Set initial remote cursor");
        Point parked = anchor;
        foreach (bool saver in new[] { false, true })
        {
            reports.Clear();
            if (saver) desktop.ObserveInputDesktop(true, true); else ReportDisplayPower(desktop, 0);
            Require(!pointer.Physical(new(51, 61), Win.Move, 0), "Windows must see the wake gesture");
            if (saver) Require(!pointer.Physical(new(52, 62), Win.LeftDown, 0), "Screensaver must retain local button input");
            if (saver) desktop.ObserveInputDesktop(false, true); else ReportDisplayPower(desktop, 1);
            Require(pointer.Physical(new(52, 62), Win.Move, 0) && anchor == parked, "Re-anchor the displaced pointer on wake");
            await Until(() => reports.Any(m => m.Message == Win.Move && m.X == 160), "Retain remote cursor position after wake");
            pointer.Physical(anchor + new Size(11, 0), Win.Move, 0);
            pointer.Physical(anchor, Win.LeftDown, 0); pointer.Physical(anchor, Win.LeftUp, 0);
            await Until(() => reports.Any(m => m.Message == Win.LeftUp && m.X == 171), "Remote movement and click after wake");
            pointer.Physical(anchor - new Size(11, 0), Win.Move, 0);
            await Until(() => reports.LastOrDefault()?.X == 160, "Return to baseline position");
        }
        pointer.Physical(anchor - new Size(205, 0), Win.Move, 0);
        Require(!pointer.Physical(anchor, Win.Move, 0), "Still able to cross back normally");
        Console.WriteLine("PASS  Display-off and screensaver transitions preserve remote pointer ownership, coordinates, clicks and return crossing.");
    }
    static unsafe void ReportDisplayPower(DesktopAvailability desktop, int value)
    {
        Span<byte> payload = stackalloc byte[24];
        new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5").TryWriteBytes(payload);
        BitConverter.TryWriteBytes(payload[16..], sizeof(int));
        BitConverter.TryWriteBytes(payload[20..], value);
        fixed (byte* bytes = payload) desktop.Message(0x218, 0x8013, (nint)bytes);
    }
    static async Task CheckWindowIcon(Form source, StreamWindow view, Peer remote)
    {
        using var sample = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(sample)) g.Clear(Color.Crimson);
        using var fixtureIcon = StreamAppIcon.FromBitmap(sample);
        source.Icon = fixtureIcon;
        var hwnd = source.Handle;
        var pixels = await Task.Run(() => StreamAppIcon.Read(hwnd));
        Require(pixels?.Length == StreamAppIcon.Bytes, "Read original source HWND icon");
        // Compare the source colour and the DeskPortal mark on the shell-facing HICON.
        using var expected = StreamAppIcon.Compose(pixels);
        using var bitmap = expected.ToBitmap();
        bool Red(Color c) => c.R > 180 && c.G < 105 && c.B < 105;
        var center = bitmap.GetPixel(bitmap.Width / 3, bitmap.Height / 3);
        Require(Red(center), $"Read the source window's chosen icon (captured BGRA {string.Join(",", pixels!.Skip(4 * (16 * StreamAppIcon.Side + 16)).Take(4))}, composed {center})");
        var fallback = view.Icon;
        TaskbarChecks.VerifyIdentity(view);
        remote.Send(Kind.WindowIcon, new WindowIcon(view.Id, pixels!));
        await Until(() => view.Icon != fallback, "Apply icon received on the paired connection");
        Require(Win.SendMessageTimeout(view.Handle, 0x7f, 1, 0, 2, 200, out var native) != 0 && native != 0, "Window exposes a taskbar HICON");
        using var shellIcon = Icon.FromHandle(native);
        using var actual = shellIcon.ToBitmap();
        Require(Enumerable.Range(0, actual.Width / 2).Any(x => Red(actual.GetPixel(x, actual.Height / 2))), "Windows sees the source icon");
        Require(Enumerable.Range(actual.Width / 2, actual.Width - actual.Width / 2).Any(x =>
            Enumerable.Range(actual.Height / 2, actual.Height - actual.Height / 2).Any(y => {
                var c = actual.GetPixel(x, y); return c.G > 175 && c.R < 140 && c.B > 130;
            })), "Windows sees the DeskPortal mark on the remote app icon");
        Console.WriteLine("PASS  A source HWND's icon crossed the paired connection and Windows returned the badged taskbar icon.");
        source.Icon = null;
    }
    static async Task CheckCapture(MonitorSession session, MonitorSession source, ConcurrentQueue<MonitorInput.Input>? reports)
    {
        var screen = Screen.PrimaryScreen!;
        var choice = session.Choices.First(m => m.Monitor.Primary);
        bool hasCursor = Win.GetCursorPos(out var originalCursor);
        Require(reports != null || (hasCursor && Win.GetForegroundWindow() != 0),
            "The desktop interaction check requires an unlocked interactive Windows desktop.");
        StallLog.Timing = true;
        using var fixture = new Form { Text = "DeskPortal projection fixture", FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = screen.Bounds, BackColor = Color.Teal, TopMost = true, KeyPreview = true };
        int clicks = 0, keys = 0;
        fixture.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) clicks++; };
        fixture.KeyDown += (_, e) => { if (e.KeyCode == Keys.F8) keys++; };
        fixture.Show(); fixture.Refresh();
        try
        {
            session.Open(choice.Monitor, screen.WorkingArea);
            var window = Application.OpenForms.OfType<StreamWindow>().Single(w => w.IsMonitor);
            await Until(() => window.TakePaintedFrames() > 0, "Receive and present a captured monitor frame");
            var buttons = window.Controls.OfType<Panel>().Single().Controls.OfType<Button>().ToArray();
            buttons.Single(b => b.AccessibleName == "Tile on the left").PerformClick();
            Require(window.Bounds == DeskSettings.Place(TileMode.LeftHalf, window.Bounds, screen.WorkingArea, true),
                $"Tile on the viewing monitor's left, got {window.Bounds} in {screen.WorkingArea}");
            buttons.Single(b => b.AccessibleName == "Tile on the right").PerformClick();
            Require(window.Bounds == DeskSettings.Place(TileMode.RightHalf, window.Bounds, screen.WorkingArea, true), "Tile on the viewing monitor's right");
            // Only click our own test surface. Keep the target away from the projection on the right.
            var picture = MonitorGeometry.Fit(window.ClientSize, screen.Bounds.Size, window.Controls.OfType<Panel>().Single().Height);
            var client = new Point(picture.Left + picture.Width / 8, picture.Top + picture.Height / 2);
            var target = MonitorGeometry.ToDesktop(screen.Bounds, (int)(65535L * (client.X - picture.X) / (picture.Width - 1)),
                (int)(65535L * (client.Y - picture.Y) / (picture.Height - 1)));
            Require(Win.WindowFromPoint(target) == fixture.Handle, "Projection input test target must be the fixture");
            Win.PostMessage(window.Handle, Win.LeftDown, 1, Win.Packed(client));
            Win.PostMessage(window.Handle, Win.LeftUp, 0, Win.Packed(client));
            await Until(() => reports == null ? clicks == 1 :
                reports.Any(r => r.Type == 0 && r.Data.Mouse.Flags == 0xC003) && reports.Any(r => r.Type == 0 && r.Data.Mouse.Flags == 0xC005),
                "Forwarded click on monitor fixture");
            Require(reports != null || Win.GetForegroundWindow() == fixture.Handle, "Projection typing test target must be the fixture");
            Win.PostMessage(window.Handle, 0x100, (int)Keys.F8, 0x420001);
            Win.PostMessage(window.Handle, 0x101, (int)Keys.F8, unchecked((int)0xc0420001));
            await Until(() => reports == null ? keys == 1 : reports.Any(r => r.Type == 1 && r.Data.Key.Key == (int)Keys.F8 && r.Data.Key.Flags == 2),
                "Forwarded key on monitor fixture");
            buttons.Single(b => b.AccessibleName == "Close projection").PerformClick();
            await Until(() => source.Sending == 0 && window.IsDisposed, "Projection toolbar should end capture");
            Console.WriteLine("PASS  Native whole-monitor capture, TLS video presentation, viewer-side tiling and toolbar close. " +
                (reports == null ? "The fixture received a Windows click and F8." : "Click and F8 reached the source Windows input adapter."));
        }
        finally { session.StopAll(); source.StopAll(); fixture.Close(); if (reports == null && hasCursor) Cursor.Position = originalCursor; }
    }
}
