using System.Net;
using System.Security.Authentication;

namespace DeskLink;

static class Checks
{
    internal const uint FixtureColor = 0x8043;
    sealed class TestSurface : Form
    {
        public TestSurface() => ResizeRedraw = true;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width >= 480)
                e.Graphics.FillRectangle(Brushes.Cyan, 20, ClientSize.Height - 40, 28, 20);
        }
        protected override void OnMouseUp(MouseEventArgs e) { BackColor = Color.Blue; Refresh(); base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { BackColor = Color.Yellow; Refresh(); base.OnMouseWheel(e); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FixtureColor)
            {
                if (m.WParam != 0) { BackColor = Color.Lime; Refresh(); }
                m.Result = (nint)(BackColor.ToArgb() & 0xffffff); return;
            }
            if (m.Msg == 0x100 && (int)m.WParam == (int)Keys.F8)
            { BackColor = Color.Lime; Refresh(); return; }
            if (m.Msg == 0x8042)
            { MessageBox.Show(this, "Test dialog", "DeskPortal test dialog"); return; }
            base.WndProc(ref m);
        }
    }
    public static int Fixture()
    {
        using var surface = new TestSurface
        {
            Text = "DeskPortal capture fixture",
            BackColor = Color.Red,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40), new Size(440, 250))
        };
        using var timeout = new System.Windows.Forms.Timer { Interval = 30000, Enabled = true };
        timeout.Tick += (_, _) => surface.Close();
        Application.Run(surface); return 0;
    }
    static void Require(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    sealed class MemoryClipboard : IClipboardAccess
    {
        public DataObject Data = new();
        public IDataObject Read() => Data;
        public void Write(DataObject data) => Data = data;
        public void Copy(string text) { Data = new DataObject(); Data.SetText(text); }
    }
    public static int Run()
    {
        try
        {
            var a = new DeskLayout(new Rectangle(-1920, 0, 3840, 1080), new Size(1600, 900), true);
            var b = new DeskLayout(new Rectangle(0, 0, 1600, 900), a.Local.Size, false);
            var cross = new Point(a.Local.Right + 37, 521);
            Require(a.ToPeer(cross) == new Point(37, 521), "Right-hand edge translation");
            Require(b.ToPeer(new Point(-50, 521)) == new Point(3790, 521), "Left-hand edge translation");
            Require(a.AtSeam(new Point(1919, 899)) && !a.AtSeam(new Point(1919, 900)), "Shared edge overlap");
            TestMonitorLayout();
            TestStorageUpgrade();
            TestFirewall();
            TestMouseQueue();
            TestNestedDragDispatch();
            TestStalledWheel().GetAwaiter().GetResult();
            TestInputPriority();
            TestSettings();
            TestDashboardLayout();
            Require(ProjectionChecks.Run(capture: false) == 0, "Multi-PC monitor projection");
            TestFiles();
            Task.Run(TestConfirmedPairing).GetAwaiter().GetResult();
            Task.Run(TestPair).GetAwaiter().GetResult();
            Task.Run(TestTransport).GetAwaiter().GetResult();
            Console.WriteLine("PASS  Layout, matching comparison codes, two-sided approval, decline on either PC, invitation checks, duplex control and frames, clipboard history, echo suppression, and file transfer.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void TestDashboardLayout()
    {
        using var form = new DeskForm(false) { Opacity = 0, ShowInTaskbar = false };
        form.Show();
        IEnumerable<Control> Children(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));
        var controls = Children(form).ToArray();
        var history = controls.Single(c => c.AccessibleName == "Shared clipboard history");
        var area = history.Parent!;
        var help = controls.OfType<Button>().Single(b => b.Text == "Quick start");
        foreach (var size in new[] { form.Size, form.MinimumSize })
        {
            form.Size = size;
            foreach (var item in new[] { area, help, controls.Single(c => c.Text == "Use selected item") })
            {
                var bounds = item.RectangleToScreen(item.ClientRectangle);
                Require(!bounds.IsEmpty && item.Visible, "Missing dashboard control");
                for (Control? ancestor = item.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    // On a smaller work area the scrollable dashboard may extend past its viewport.
                    if (ancestor is Panel { AutoScroll: true } viewport && (viewport.HorizontalScroll.Visible || viewport.VerticalScroll.Visible)) break;
                    Require(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(bounds), $"Clipped dashboard control at {form.DeviceDpi} DPI: {item.Text}");
                }
            }
        }
        form.Close();
        Console.WriteLine("PASS  Clipboard history and dashboard actions fit at the opening and minimum sizes.");
    }
    static void TestNestedDragDispatch()
    {
        foreach (bool tracking in new[] { false, true })
        {
            using var window = new ControlDispatchWindow { TrackingOwnPress = tracking };
            var order = new List<string>();
            window.Post(() =>
            {
                order.Add("move started");
                Task.Run(() =>
                {
                    window.Post(() => order.Add("next move"));
                    window.Post(() => order.Add("release"));
                }).GetAwaiter().GetResult();
                // Cross-thread window positioning and input injection can pump messages here.
                Application.DoEvents();
                order.Add("move finished");
            });
            Application.DoEvents();
            Require(order.SequenceEqual(new[] { "move started", "move finished", "next move", "release" }),
                $"Drag callbacks re-entered or lost input (caption tracking {tracking}): {string.Join(", ", order)}");
        }
        Console.WriteLine("PASS  Nested Windows message loops preserve drag and release order.");
    }
    static void TestStorageUpgrade()
    {
        var root = Directory.CreateTempSubdirectory("DeskPortal-storage-");
        try
        {
            string portal = Path.Combine(root.FullName, "DeskPortal");
            Require(AppStorage.Resolve(root.FullName) == portal, "Use DeskPortal for a new install");
            foreach (string name in new[] { "DeskLink", "DeskStream", "DeskPortal" })
            {
                var folder = root.CreateSubdirectory(name);
                Require(AppStorage.Resolve(root.FullName) == folder.FullName, $"Reuse the existing {name} data folder");
            }
        }
        finally { root.Delete(true); }
    }

    static void TestFirewall()
    {
        var ip = IPAddress.Parse("192.0.2.7");
        Require(PeerFirewall.ParsePeer(" 192.0.2.7 ")?.Equals(ip) == true, "Allow one IPv4 peer");
        foreach (var invalid in new[] { "", "*", "LocalSubnet", "192.0.2.0/24", "192.0.2.1-192.0.2.9",
            "192.0.2.1,192.0.2.2", "127.0.0.1", "0.0.0.0", "255.255.255.255", "224.0.0.1", "192.0.2",
            "0xc0000207", "192.00.02.7", "::1", "0.1.2.3" })
            Require(PeerFirewall.ParsePeer(invalid) == null, $"Reject peer scope {invalid}");
        string program = @"C:\Apps\DeskPortal.exe";
        var tcp = new PeerFirewall.Rule(program, $"{ip}-{ip}", 6, Invitation.Port.ToString(), 1, 1, true, 6,
            false, "*", "*", "All", null);
        var udp = tcp with { Protocol = 17, Ports = NearbyComputers.Port.ToString(), Scope = $"{ip}/255.255.255.255" };
        var both = new PeerFirewall.Rules(tcp, udp);
        var screenRule = tcp with { Ports = ScreenPeers.Port.ToString(), Scope = "192.0.2.8/32,192.0.2.7/32" };
        Require(screenRule.Matches(program, new[] { ip, IPAddress.Parse("192.0.2.8") }, 6, ScreenPeers.Port) &&
            !screenRule.Matches(program, ip, 6, ScreenPeers.Port), "Screen firewall rule must match the exact allowed set");
        Require(both.ExistingPeer(program)?.Equals(ip) == true && both.Match(program.ToUpperInvariant(), ip), "Reuse existing peer-only Windows rules");
        Require(!both.Match(@"C:\Moved\DeskPortal.exe", ip) && !both.Match(program, IPAddress.Parse("192.0.2.8")),
            "Repair rules on a move or address change");
        Require(new PeerFirewall.Rules(tcp, null).ExistingPeer(program) == null, "A partial setup needs verification");
        foreach (var changed in new[] { udp with { Scope = "*" }, udp with { Scope = "192.0.2.0/24" },
            udp with { Scope = "192.0.2.7-192.0.2.8" }, udp with { Scope = "192.0.2.8" }, udp with { Protocol = 6 },
            udp with { Ports = "*" }, udp with { Profiles = 7 }, udp with { Direction = 2 }, udp with { Action = 0 },
            udp with { Enabled = false }, udp with { EdgeTraversal = true } })
        {
            var stale = new PeerFirewall.Rules(tcp, changed);
            Require(!stale.Match(program, ip) && stale.ExistingPeer(program) == null, "Reject a changed firewall rule");
        }
        // Read the real effective Windows policy. A missing rule is an ordinary first-run state.
        _ = PeerFirewall.Read();
        Console.WriteLine("PASS  Peer-only firewall rule scope, upgrade adoption and repair detection.");
    }
    static void TestMonitorLayout()
    {
        DisplayInfo[] fast = [new("left",new(0,0,1920,1080)), new("main",new(1920,160,2560,1440),true)];
        DisplayInfo[] laptop = [new("panel",new(0,0,3200,2000),true)];
        var a = new DeskLayout(new(-1920,-160,4480,1600),laptop[0].Bounds.Size,true,fast,laptop);
        var b = new DeskLayout(new(0,0,3200,2000),a.Local.Size,false,laptop,fast);
        using var board = new TileBoard(a,"Laptop");
        board.SelectTile(2); board.Place(0,1);
        a.Apply(board.Selection); b.Apply(a.Plan.Reverse());
        var left = a.Tiles[0]; var right = a.Tiles[1]; var middle = a.Tiles[2];
        Require(left.Desk.Right == middle.Desk.Left && middle.Desk.Right == right.Desk.Left,"Insert a laptop between two existing monitors");
        var start = new Point(left.Desk.Right-2,left.Desk.Top+325);
        var intoLaptop = a.Walk(left,start,new(start.X+8,start.Y));
        Require(intoLaptop.Tile == middle && a.ToPeer(intoLaptop.Point) == new Point(6,325),"Route into first laptop pixel range");
        var end = new Point(middle.Desk.Right-2,325);
        var intoFast = a.Walk(middle,end,new(end.X+8,end.Y));
        Require(intoFast.Tile == right && right.ToNative(intoFast.Point) == b.ToPeer(intoFast.Point)+new Size(a.Local.Location),
            "Exit laptop on the second FAST display, including its negative native origin");
        var back = b.Walk(b.Tiles[2],intoFast.Point,new(middle.Desk.Right-3,325));
        Require(back.Tile.Here && back.Tile.ToNative(back.Point) == new Point(3197,325),"Reverse entry into laptop panel");
        Require(a.FileStrip(start,60).Right == left.Native.Right && a.FileStrip(intoFast.Point,60).Left == right.Native.Left,
            "File drop strips use both physical monitor edges");
        var above = new ScreenPlan([new("left",0,0),new("main",1920,0)],[new("panel",500,-2000)]);
        a.Apply(above); b.Apply(above.Reverse());
        var top = a.Walk(a.Tiles[0],new(512,2),new(512,-7));
        Require(!top.Tile.Here && a.ToPeer(top.Point) == new Point(12,1993) && a.FileStrip(new(512,0),0).Top == -160,
            "Vertical offset, file drop strip and destination coordinates");
        var gap = a.Walk(a.Tiles[0],new(300,2),new(300,-40));
        Require(gap.Tile.Here && gap.Point.Y == 0 && !a.AtSeam(new(300,0)),"Unshared edges cannot cross a gap");
        Require(a.Validate(above with { There = [new("panel",0,0)] },out _) != null &&
            a.Validate(above with { There = [new("panel",-3200,-2000)] },out _) != null,"Reject overlaps and corner-only links");
        Console.WriteLine("PASS  Interleaved and vertical monitor layouts, bidirectional coordinate mapping, gaps and per-display file drops.");
    }
    static void TestSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"DeskLink-settings-{Guid.NewGuid():N}.json");
        try
        {
            var defaults = DeskSettings.Load(path);
            Require(defaults.Features == Sharing.All && defaults.Stream == new StreamOptions(FrameCodec.Auto, 60, 90, 10), "First-run sharing defaults");
            var bindings = new ActionShortcuts { History = Keys.Control | Keys.Shift | Keys.H, Recover = Keys.Alt | Keys.Home,
                Performance = Keys.Control | Keys.Alt | Keys.F9 };
            var appearance = new DeskAppearance(DeskTheme.Dark, 29, 223);
            var selection = new DeskSettings { Features = Sharing.Files | Sharing.Windows, Tiling = TileMode.RightHalf,
                Stream = new(FrameCodec.Hevc, 24, 76, 31), Appearance = appearance, Crossing = EdgeActivation.HoldShift, Shortcuts = bindings };
            using var dialog = new SettingsDialog(selection, Sharing.Files);
            Require(dialog.Selection == selection, "Settings dialog round trip");
            selection.Save(path);
            Require(DeskSettings.Load(path) == selection, "Saved preferences restored");
            (selection with { Stream = new(FrameCodec.Jpeg, -5, 132, 120) }).Save(path);
            Require(DeskSettings.Load(path).Stream == new StreamOptions(FrameCodec.Jpeg, 1, 100, 80), "Saved numbers bounded");
            File.WriteAllText(path, """{"Features":"Windows","Stream":{"Codec":"Jpeg","FrameRate":30,"JpegQuality":75}}""");
            var migrated = DeskSettings.Load(path);
            Require(migrated.Features == Sharing.Windows && migrated.Stream == new StreamOptions(FrameCodec.Auto, 30, 75, 10), "Old JPEG settings migrated");
            Require(migrated.Shortcuts == new ActionShortcuts() && migrated.Crossing == EdgeActivation.Automatic &&
                migrated.Appearance == DeskAppearance.Initial, "Old preferences retain default gestures and shortcuts");
            Require(bindings.Error() == null && (bindings with { Files = bindings.History }).Error() != null &&
                (bindings with { Recover = Keys.Alt | Keys.F4 }).Error() != null &&
                (bindings with { History = Keys.V }).Error() != null, "Accept valid bindings and reject conflicting or reserved shortcuts");
            TestPalettes();
            TestHotkeys();
            File.WriteAllText(path, """{"Shortcuts":{"Files":"P, Control, Alt"}}""");
            var oldKeys = DeskSettings.Load(path).Shortcuts;
            Require(oldKeys.Files == (Keys.Control | Keys.Alt | Keys.P) && oldKeys.Project == Keys.None,
                "Keep a pre-existing projection shortcut assignment on upgrade");
            var secondary = new Rectangle(-2500, -1600, 1920, 1080);
            Require(MonitorGeometry.ToDesktop(secondary, 65535, 65535) == new Point(secondary.Right - 1, secondary.Bottom - 1) &&
                MonitorGeometry.ToDesktop(secondary, 0, 0) == secondary.Location &&
                MonitorGeometry.Fit(new(1000, 900), new(1920, 1080), 28) == new Rectangle(0, 183, 1000, 562),
                "Input and letterboxing on secondary monitors with negative coordinates");
            var work = new Rectangle(-1200, 80, 999, 802);
            var original = new Rectangle(-875, 300, 320, 160);
            Require(DeskSettings.Place(TileMode.Free, original, work) == original, "Free drop placement");
            Require(DeskSettings.Place(TileMode.RightHalf, original, work) == new Rectangle(-701, 356, 500, 250), "Right tile uses monitor coordinates and preserves aspect");
        }
        finally { File.Delete(path); }
    }
    static void TestPalettes()
    {
        foreach (var mode in Enum.GetValues<DeskTheme>())
        {
            var original = new DeskAppearance(mode, 43, 310);
            var shuffled = original.Shuffle();
            Require(shuffled.Hue != original.Hue && (mode == DeskTheme.Light ? shuffled.DarkHue == original.DarkHue :
                shuffled.LightHue == original.LightHue), "Shuffle changes only the selected theme");
            for (int hue = 0; hue < 360; hue++)
            {
                var p = DeskPalette.From(new(mode, hue, hue), false);
                Require(Math.Abs(Math.Abs(p.Primary.GetHue() - p.Complement.GetHue()) - 180) < 1, "Hues must be complementary");
                foreach (var fill in new[] { p.Primary, p.Complement })
                {
                    double l1 = DeskPalette.Luminance(fill), l2 = DeskPalette.Luminance(p.OnAccent(fill));
                    Require((Math.Max(l1, l2) + .05) / (Math.Min(l1, l2) + .05) >= 4.5, "Accent text contrast");
                }
            }
        }
    }
    static void TestHotkeys()
    {
        using var owner = new Form();
        using var other = new Form();
        var registrations = new SessionHotkeys();
        var competitor = new SessionHotkeys();
        bool Enabled(DeskAction action) => action == DeskAction.Performance;
        var bindings = new ActionShortcuts { History = Keys.None, Recover = Keys.None };
        try
        {
            foreach (var key in ActionShortcuts.MainKeys.Where(k => k is >= Keys.F1 and <= Keys.F11))
            {
                bindings = bindings with { Performance = Keys.Control | Keys.Shift | Keys.Alt | key };
                if (registrations.Update(owner.Handle, bindings, Enabled) == null) break;
            }
            Keys chord = bindings.Performance;
            Require(registrations.Find(chord) == DeskAction.Performance && competitor.CheckAvailable(other.Handle, bindings) != null,
                "Registered shortcuts dispatch to their owner and report an operating system conflict");
            nint message = ((int)(chord & Keys.KeyCode) << 16) | 7;
            Require(registrations.Matches(DeskAction.Performance, message) && !registrations.Matches(DeskAction.Performance, message - 1),
                "Verify the triggering chord before dispatching");
            registrations.Update(owner.Handle, bindings, _ => false);
            Require(registrations.Find(chord) == null && !registrations.Matches(DeskAction.Performance, message) &&
                competitor.CheckAvailable(other.Handle, bindings) == null, "Disabling an action releases its shortcut and discards queued messages");
        }
        finally { registrations.Clear(owner.Handle); competitor.Clear(other.Handle); }
    }
    static void TestInputPriority()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = Thread.CurrentThread.Priority;
        var target = process.PriorityClass switch
        {
            System.Diagnostics.ProcessPriorityClass.BelowNormal => ThreadPriority.Highest,
            System.Diagnostics.ProcessPriorityClass.Normal => ThreadPriority.AboveNormal,
            _ => before
        };
        using (InputThreadPriority.Raise())
        {
            Require(Thread.CurrentThread.Priority == (before > target ? before : target), "Input thread scheduling");
            using (InputThreadPriority.Raise()) { }
            Require(Thread.CurrentThread.Priority >= target, "A second acquisition preserves active input scheduling");
        }
        Require(Thread.CurrentThread.Priority == before, "Disconnect restores the thread's starting priority");
    }
    static void TestMouseQueue()
    {
        using var queue = new ControlQueue(() => { });
        var window = Guid.NewGuid();
        void Mouse(int x, int message) =>
            Require(queue.Enqueue(new(Kind.Mouse, new MouseSample(x, 0, message, 0))), "Mouse queue capacity");
        void View(int x) =>
            Require(queue.Enqueue(new(Kind.WindowInput, new WindowEvent(window, Win.Move, x, 0, 0, 1))), "View queue capacity");
        for (int i = 0; i <= 8000; i++) Mouse(i, Win.Move);
        Mouse(8000, Win.LeftDown);
        Mouse(8001, Win.Move);
        View(17); View(18);
        queue.Enqueue(new(Kind.Key, new KeySample(65, 0, false, false)));
        Mouse(8002, Win.Move);
        Mouse(8002, Win.LeftUp);
        var actual = new List<object>();
        while (queue.TryTake(out var command)) actual.Add(command.Value);
        Require(actual.SequenceEqual(new object[] {
            new MouseSample(8000,0,Win.Move,0), new MouseSample(8000,0,Win.LeftDown,0),
            new MouseSample(8001,0,Win.Move,0), new WindowEvent(window,Win.Move,18,0,0,1),
            new KeySample(65,0,false,false), new MouseSample(8002,0,Win.Move,0),
            new MouseSample(8002,0,Win.LeftUp,0)
        }), "Absolute positions survive a motion burst and cannot overtake a click or key");
        for (int i = 0; i < 8000; i++)
        {
            Require(queue.Enqueue(new(Kind.Window, new ViewInfo(window, "Test", i, 0, 320, 240))), "Drag geometry backlog");
            Mouse(i, Win.Move);
        }
        Mouse(7999, Win.LeftUp);
        actual.Clear();
        while (queue.TryTake(out var message)) actual.Add(message.Value);
        Require(actual.SequenceEqual(new object[] { new ViewInfo(window,"Test",7999,0,320,240),
            new MouseSample(7999,0,Win.Move,0), new MouseSample(7999,0,Win.LeftUp,0) }),
            "A stalled drag retains its final geometry and button release without dropping the connection");
        void Wheel(int delta, int x = 3, int axis = Win.Wheel) =>
            Require(queue.Enqueue(new(Kind.Mouse, new MouseSample(x, 4, axis, delta))), "Wheel queue capacity");
        for (int i = 0; i < 8; i++) Wheel(-15);
        Wheel(15); Wheel(15, 4); Wheel(15, 4, Win.HorizontalWheel);
        Mouse(4, Win.LeftDown);
        Wheel(short.MaxValue); Wheel(1);
        actual.Clear();
        while (queue.TryTake(out var item)) actual.Add(item.Value);
        Require(actual.SequenceEqual(new object[] {
            new MouseSample(3,4,Win.Wheel,-120), new MouseSample(3,4,Win.Wheel,15),
            new MouseSample(4,4,Win.Wheel,15), new MouseSample(4,4,Win.HorizontalWheel,15),
            new MouseSample(4,0,Win.LeftDown,0), new MouseSample(3,4,Win.Wheel,short.MaxValue),
            new MouseSample(3,4,Win.Wheel,1) }),
            "Wheel bursts preserve distance, direction, position, axis, button order and signed delta limits");
    }
    static async Task TestStalledWheel()
    {
        var injector = new WheelInjector(TimeSpan.FromMilliseconds(150));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var blocked = injector.Run(() => { entered.Set(); resume.Wait(); }, CancellationToken.None);
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "Wheel worker did not start");
            try { await blocked.ConfigureAwait(false); throw new InvalidOperationException("Missing wheel timeout"); }
            catch (IOException ex) when (ex.InnerException is TimeoutException) { }
            try
            {
                await injector.Run(() => throw new InvalidOperationException("Duplicate wheel injection"), CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Accepted input behind a blocked Windows call");
            }
            catch (IOException ex) when (ex.InnerException == null) { }
        }
        finally { resume.Set(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            try { await injector.Run(() => { }, deadline.Token).ConfigureAwait(false); break; }
            catch (IOException) { await Task.Delay(5, deadline.Token).ConfigureAwait(false); }
        }
        Console.WriteLine("PASS  Stalled wheel injection timed out, rejected overlapping delivery and resumed after Windows returned.");
    }
    static void TestFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskLink-test-" + Guid.NewGuid().ToString("N"));
        var clip = new Clip(Guid.NewGuid(), 1, "test", "files", "test files");
        var offer = new FileOffer(clip, [new("empty", 0), new("one.txt", 3), new("two.txt", 2)]);
        using (var batch = new IncomingFiles(root, offer))
        {
            batch.Append([1, 2]); batch.Append([3, 4, 5]);
            var files = batch.Finish();
            Require(files.Length == 3 && File.ReadAllBytes(files[1]).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                File.ReadAllBytes(files[2]).SequenceEqual(new byte[] { 4, 5 }), "Split file payload");
            foreach (var file in files) File.Delete(file);
            Directory.Delete(Path.GetDirectoryName(files[0])!);
        }
        using (var batch = new IncomingFiles(root, offer))
        {
            batch.Append([42]);
            try { batch.Finish(); throw new Exception("Accepted incomplete file"); } catch (InvalidDataException) { }
        }
        foreach (var name in new[] { "../out.txt", "a:b", "NUL.txt", "COM1", "a." })
        {
            try { IncomingFiles.Validate(new(clip, [new(name, 0)])); throw new Exception("Invalid name accepted"); }
            catch (InvalidDataException) { }
        }
        Require(!Directory.EnumerateFileSystemEntries(root).Any(), "Partial transfer cleanup");
        File.WriteAllText(Path.Combine(root, "one.txt"), "original");
        using (var drop = new IncomingFiles(root, offer, root))
        {
            drop.Append([1, 2, 3, 4, 5]);
            var placed = drop.Finish();
            Require(File.ReadAllText(Path.Combine(root, "one.txt")) == "original" && Path.GetFileName(placed[1]) == "one (2).txt",
                "File drop preserves existing content");
            foreach (var path in placed) File.Delete(path);
        }
        File.Delete(Path.Combine(root, "one.txt"));
        // Day-old paste copies and abandoned batches go. Explicit sends and recent copies stay.
        string copies = Path.Combine(root, IncomingFiles.ClipboardFolder);
        string[] swept = [Path.Combine(copies, "old"), Path.Combine(root, "crashed.partial")];
        string[] kept = [Path.Combine(copies, "recent"), Path.Combine(root, "sent")];
        foreach (var folder in swept.Concat(kept))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "file.txt"), "x");
        }
        foreach (var folder in swept.Append(kept[1])) Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-2));
        SharedClipboard.Sweep(root);
        Require(swept.All(f => !Directory.Exists(f)) && kept.All(Directory.Exists), "Sweeping removes only stale paste copies and abandoned batches");
        Directory.Delete(root, true);
    }
    static async Task TestConfirmedPairing()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var serverId = Guid.NewGuid();
        foreach (var outcome in new[] { "accept", "legacy", "decline client", "decline server", "cancel" })
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(address, stop.Token);
            using var incoming = await listener.AcceptTcpClientAsync(stop.Token);
            var codes = new[] { NewSignal<string>(), NewSignal<string>() };
            var choices = new[] { NewSignal<bool>(), NewSignal<bool>() };
            ConfirmedPairing.Confirm Prompt(int side) => (name, code, token) =>
            {
                Require(name == (side == 0 ? "Laptop" : "Desktop"), "Confirmation identifies the other PC");
                codes[side].SetResult(code);
                return choices[side].Task.WaitAsync(token);
            };
            var client = ConfirmedPairing.Pair(socket, true, Guid.NewGuid(), "Desktop", new(serverId, "Laptop", IPAddress.Loopback), Prompt(0), attempt.Token);
            var server = ConfirmedPairing.Pair(incoming, false, serverId, "Laptop", null, Prompt(1), stop.Token,
                outcome == "legacy" ? null : listener.AcceptTcpClientAsync);
            var prompts = Task.WhenAll(codes.Select(c => c.Task));
            await await Task.WhenAny(prompts, client, server).WaitAsync(stop.Token);
            var both = await prompts.WaitAsync(stop.Token);
            Require(both[0] == both[1] && both[0].Length == 6 && both[0].All(char.IsAsciiDigit), "Matching six-digit codes");
            if (outcome is "accept" or "legacy")
            {
                choices[1].SetResult(true);
                await Task.Delay(60, stop.Token);
                Require(!client.IsCompleted && !server.IsCompleted, "Single approval cannot open a peer");
                choices[0].SetResult(true);
                using var left = await client.WaitAsync(stop.Token);
                using var right = await server.WaitAsync(stop.Token);
                Require(left.Split == (outcome == "accept") && left.Split == right.Split, "Confirmed peers agree on transport mode");
            }
            else
            {
                if (outcome == "cancel") attempt.Cancel();
                else choices[outcome == "decline client" ? 0 : 1].SetResult(false);
                foreach (var pairing in new[] { client, server })
                {
                    try { using var peer = await pairing.WaitAsync(stop.Token); throw new Exception("Pairing accepted after rejection"); }
                    catch (Exception e) when (!stop.IsCancellationRequested && e is AuthenticationException or IOException or OperationCanceledException) { }
                }
            }
        }
        static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static async Task TestTransport()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var invite = new Invitation(IPAddress.Loopback, 0);
        var accept = invite.Accept(stop.Token);
        using var a = await Invitation.Join("127.0.0.1", invite.Code, stop.Token, invite.ListeningPort);
        using var b = await accept;
        var first = new TransportChecks(); var second = new TransportChecks();
        a.LatencyProbe = b.LatencyProbe = true;
        var ra = a.Run(p => first.Dispatch(p, _ => Task.CompletedTask));
        var rb = b.Run(p => second.Dispatch(p, _ => Task.CompletedTask));
        try { await Task.WhenAll(first.Check(a, stop.Token), second.Check(b, stop.Token)); }
        finally
        {
            a.Dispose(); b.Dispose();
            try { await Task.WhenAll(ra, rb); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    static async Task TestPair()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var invite = new Invitation(IPAddress.Loopback, 0);
        var accept = invite.Accept(stop.Token);
        var forged = invite.Code.ToCharArray();
        forged[0] = forged[0] == 'A' ? 'B' : 'A';
        try { using var wrong = await Invitation.Join("127.0.0.1", new string(forged), stop.Token, invite.ListeningPort); throw new Exception("Wrong certificate accepted"); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        forged = invite.Code.ToCharArray(); forged[32] = forged[32] == 'A' ? 'B' : 'A';
        try { using var wrong = await Invitation.Join("127.0.0.1", new string(forged), stop.Token, invite.ListeningPort); throw new Exception("Wrong secret accepted"); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        using var client = await Invitation.Join("127.0.0.1", invite.Code, stop.Token, invite.ListeningPort);
        using var server = await accept;
        client.ProbeLatency(); // A peer without the capability must receive no probe.
        var gotClip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        var leftClipboard = new MemoryClipboard(); var rightClipboard = new MemoryClipboard();
        var root = Path.Combine(Path.GetTempPath(), "DeskLink-wire-" + Guid.NewGuid().ToString("N"));
        var sendDir = Path.Combine(root, "from"); var receiveDir = Path.Combine(root, "to");
        Directory.CreateDirectory(sendDir); Directory.CreateDirectory(receiveDir);
        var rightNotes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var leftNotes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var leftSharing = new SharedClipboard(client, note => { leftNotes.Enqueue(note); Console.WriteLine(note); }, () => { }, sendDir, leftClipboard);
        using var rightSharing = new SharedClipboard(server, note => { rightNotes.Enqueue(note); Console.WriteLine(note); }, () => { }, receiveDir, rightClipboard);
        var dropRequest = Guid.NewGuid();
        rightSharing.TakeDropDestination = drop => drop == dropRequest ? receiveDir : throw new InvalidDataException();
        var receivedClip = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var receivedFile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readServer = server.Run(p =>
        {
            if (p.Kind == Kind.Hello && p.Read<string>() == "test") gotClip.TrySetResult();
            if (p.Kind == Kind.Frame && p.Id == id && p.Data.AsSpan(16).SequenceEqual(new byte[] { 2, 3 })) gotFrame.TrySetResult();
            if (p.Kind == Kind.Clip) { rightSharing.Receive(p.Read<Clip>()); receivedClip.Writer.TryWrite(true); }
            if (p.Kind is Kind.FileStart or Kind.FileChunk or Kind.FileEnd)
            { rightSharing.FilePacket(p); if (p.Kind == Kind.FileEnd) receivedFile.TrySetResult(); }
            return Task.CompletedTask;
        });
        var readClient = client.Run(p =>
        {
            if (p.Kind == Kind.Key && p.Read<int>() == 42) gotKey.TrySetResult();
            if (p.Kind == Kind.FileCancel) leftSharing.FilePacket(p);
            return Task.CompletedTask;
        });
        server.Send(Kind.Key, 42); client.Send(Kind.Hello, "test"); client.Frame(id, [2, 3]);
        await Task.WhenAll(gotClip.Task, gotFrame.Task, gotKey.Task).WaitAsync(stop.Token);
        Require(server.Received.Read().Control == 5 + Packet.Make(Kind.Hello, "test").Data.Length, "No probe sent to an older peer");
        client.LatencyProbe = server.LatencyProbe = true;
        foreach (var text in new[] { "first", "second" })
        {
            leftClipboard.Copy(text); leftSharing.Observe();
            await receivedClip.Reader.ReadAsync(stop.Token);
            Require(rightClipboard.Data.GetText() == text, "Text paste on receiver");
        }
        rightSharing.Observe();
        Require(rightSharing.History.Count == 2 && rightSharing.History[1].Clip.Text == "first", "History and echo suppression");
        leftClipboard.Copy("private");
        leftClipboard.Data.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new byte[] { 1 });
        leftSharing.Observe();
        Require(leftSharing.History.Count == 2, "Private clipboard content excluded");
        byte[] payload = new byte[192000]; new Random(42).NextBytes(payload);
        var source = Path.Combine(sendDir, "network.bin"); var target = Path.Combine(receiveDir, "network.bin");
        await File.WriteAllBytesAsync(source, payload);
        leftSharing.SendFiles([source], dropRequest);
        await receivedFile.Task.WaitAsync(stop.Token);
        Require(payload.SequenceEqual(await File.ReadAllBytesAsync(target)), "File bytes after a multi-chunk network drop");
        Require(rightClipboard.Data.GetText() == "second", "File drop preserved active clipboard");
        leftSharing.SetSharing(Sharing.Files); rightSharing.SetSharing(Sharing.Files);
        leftClipboard.Copy("local only"); leftSharing.Observe();
        rightSharing.Receive(new Clip(Guid.NewGuid(), 999, "test", "text", "blocked", "blocked"));
        Require(leftSharing.History.Count == 0 && rightSharing.History.Count == 0 && rightClipboard.Data.GetText() == "second", "Clipboard off in both directions");
        File.Delete(target);
        receivedFile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        leftSharing.SendFiles([source], dropRequest);
        await receivedFile.Task.WaitAsync(stop.Token);
        Require(payload.SequenceEqual(await File.ReadAllBytesAsync(target)) && rightSharing.History.Count == 0, "Explicit file transfer with clipboard off");
        // A destination that cannot be written fails that batch. The pairing carries on.
        var blocked = Path.Combine(receiveDir, "blocked");
        File.WriteAllText(blocked, "");
        var refusedDrop = Guid.NewGuid();
        rightSharing.TakeDropDestination = drop => drop == refusedDrop ? blocked : drop == dropRequest ? receiveDir : throw new InvalidDataException();
        leftSharing.SendFiles([source], refusedDrop);
        while (!rightNotes.Any(n => n.StartsWith("Could not save network.bin"))) await Task.Delay(10, stop.Token);
        File.Delete(blocked);
        Require(!Directory.EnumerateDirectories(receiveDir).Any() && !readServer.IsCompleted && !readClient.IsCompleted,
            "An unwritable drop folder refuses the batch without ending the session");
        var partialId = Guid.NewGuid();
        var clip = new Clip(partialId, 4, "test", "files", "in flight");
        var start = Packet.Make(Kind.FileStart, new FileOffer(clip, [new FileItem("part.bin", 2)]));
        rightSharing.FilePacket(start);
        rightSharing.FilePacket(Packet.Binary(Kind.FileChunk, partialId, [1]));
        rightSharing.SetSharing(Sharing.Clipboard);
        rightSharing.FilePacket(start);
        rightSharing.SetSharing(Sharing.All);
        rightSharing.FilePacket(Packet.Make(Kind.FileEnd, partialId));
        Require(!Directory.EnumerateDirectories(receiveDir).Any(), "Withdrawing file sharing clears partial transfers");
        leftSharing.SetSharing(Sharing.Clipboard);
        leftClipboard.Copy("enabled again"); leftSharing.Observe();
        await receivedClip.Reader.ReadAsync(stop.Token);
        Require(rightClipboard.Data.GetText() == "enabled again", "Clipboard resumed without file sharing");
        // The sharing side owns and disposes the copied bitmap once it has been compressed.
        var picture = new Bitmap(64, 40);
        using (var canvas = Graphics.FromImage(picture)) canvas.Clear(Color.Teal);
        leftClipboard.Data = new DataObject();
        leftClipboard.Data.SetImage(picture);
        leftSharing.Observe();
        await receivedClip.Reader.ReadAsync(stop.Token);
        while (!rightClipboard.Data.GetDataPresent(DataFormats.Bitmap)) await Task.Delay(10, stop.Token);
        Require(rightSharing.History[0].Clip.Label == "Image 64 × 40" && leftSharing.History[0].Clip.Image != null,
            "A copied image crosses after compressing and decoding on worker threads");
        // A copy above the automatic limit waits in history until it is chosen there.
        leftSharing.SetSharing(Sharing.All);
        leftSharing.AutoSendLimit = payload.Length - 1;
        var selection = new System.Collections.Specialized.StringCollection { source };
        leftClipboard.Data = new DataObject();
        leftClipboard.Data.SetFileDropList(selection);
        receivedFile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        leftSharing.Observe();
        await Task.Delay(150, stop.Token);
        Require(!receivedFile.Task.IsCompleted && leftSharing.History[0].Paths?[0] == source && leftNotes.Any(n => n.Contains("too large")),
            "A large copied selection stays on this PC");
        leftSharing.Select(leftSharing.History[0]);
        await receivedFile.Task.WaitAsync(stop.Token);
        string pasted = ((string[])rightClipboard.Data.GetData(DataFormats.FileDrop)!)[0];
        string pasteFolder = Path.Combine(receiveDir, IncomingFiles.ClipboardFolder);
        Require(Path.GetDirectoryName(Path.GetDirectoryName(pasted)) == pasteFolder && payload.SequenceEqual(await File.ReadAllBytesAsync(pasted)),
            "Choosing it in history sends it to the paste folder");
        Directory.Delete(pasteFolder, true);
        client.ProbeLatency();
        while (client.RoundTripMs == null || client.Sent.Read() != server.Received.Read() || server.Sent.Read() != client.Received.Read())
            await Task.Delay(10, stop.Token);
        var totals = client.Sent.Read();
        Require(totals.Video == 23 && totals.Files > payload.Length * 2 && totals.Clipboard > 0 && totals.Control > 0, "Wire bytes by traffic type");
        var meter = new FrameMeter();
        meter.Record(125000, "AV1", new(320, 180), System.Diagnostics.Stopwatch.Frequency / 100, System.Diagnostics.Stopwatch.Frequency / 1000);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var row = meter.Sample(id, "fixture", true, now);
        meter.ObserveStage("Source input", System.Diagnostics.Stopwatch.Frequency / 5);
        meter.ObserveStage("Encode", System.Diagnostics.Stopwatch.Frequency / 100);
        var idle = meter.Sample(id, "fixture", true, now + System.Diagnostics.Stopwatch.Frequency);
        Require(row.LastSlowStage == null && idle.LastSlowStage?.Contains("Source input") == true &&
            meter.Sample(id, "fixture", true, now + 2 * System.Diagnostics.Stopwatch.Frequency).LastSlowStage == idle.LastSlowStage,
            "A slow stage remains visible across idle intervals and fast operations");
        Require(Math.Abs(row.Mbps / row.Fps - 1) < .00001 && row.CodecMs == 10 && row.QueueMs == 1 && row.PaintedFps == null &&
            idle.Mbps == 0 && idle.Fps == 0 && idle.CodecMs == null, "Window rates, timings and idle intervals");
        Console.WriteLine("PASS  Framed traffic totals match in both directions, latency probes are negotiated, and frame metrics reset between samples.");
        Console.WriteLine("PASS  Settings persistence, tiling, independent clipboard/file switches, cancellation and resumption.");
        File.Delete(source); File.Delete(target); Directory.Delete(sendDir); Directory.Delete(receiveDir); Directory.Delete(root);
        client.Dispose(); server.Dispose();
        try { await Task.WhenAll(readServer, readClient); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
