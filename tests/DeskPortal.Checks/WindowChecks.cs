using System.Diagnostics;
using System.Net;

namespace DeskLink;

// Two real TLS endpoints and two independently owned HWNDs. The test partitions one monitor
// into two desks and calls the same drag handler the physical input hooks use.
static partial class WindowChecks
{
    sealed class Endpoint(WindowDesk desk, DeskLayout layout, Control ui)
    {
        public StreamWindow? View(Guid id) => desk.FindView(id);
        public readonly Dictionary<Guid, Color> Frames = [];
        public Action<Guid, Color>? AfterFrame;
        public readonly Dictionary<Guid, FrameCodec> Codecs = [];
        public readonly Dictionary<Guid, (int Hash, int Changes)> Motion = [];
        public readonly HashSet<string> Stages = [];
        public Task Dispatch(Packet packet)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.BeginInvoke(async () =>
            {
                try
                {
                    switch (packet.Kind)
                    {
                        case Kind.Hello: Stages.Add(packet.Read<string>()); break;
                        case Kind.Window:
                            var info = packet.Read<ViewInfo>();
                            desk.Place(info);
                            break;
                        case Kind.Frame:
                            await desk.Frame(packet);
                            if (desk.LatestImage(packet.Id) is { } image)
                            {
                                Frames[packet.Id] = image.GetPixel(image.Width / 2, image.Height / 2);
                                AfterFrame?.Invoke(packet.Id, Frames[packet.Id]);
                                Codecs[packet.Id] = packet.Data[16] == VideoCodec.Marker ? (FrameCodec)packet.Data[17] :
                                    packet.Data[16] == 0x89 ? FrameCodec.Png : FrameCodec.Jpeg;
                                var hash = new HashCode();
                                for (int x = 1; x <= 8; x++)
                                    for (int y = 2; y <= 7; y++) hash.Add(image.GetPixel(image.Width * x / 10, image.Height * y / 9).ToArgb());
                                int value = hash.ToHashCode();
                                var previous = Motion.GetValueOrDefault(packet.Id);
                                Motion[packet.Id] = (value, previous.Changes + (value == previous.Hash ? 0 : 1));
                            }
                            break;
                        case Kind.WindowPlaced: desk.Ready(packet.Read<Guid>()); break;
                        case Kind.WindowCaption: desk.UpdateCaption(packet.Read<WindowCaption>()); break;
                        case Kind.ResizeWindow: desk.ResizeSource(packet.Read<WindowSize>()); break;
                        case Kind.WindowResized: desk.Resized(packet.Read<WindowSize>()); break;
                        case Kind.ViewCommand: desk.ChangeView(packet.Read<ViewCommand>()); break;
                        case Kind.WindowInput: await desk.Input(packet.Read<WindowEvent>()); break;
                        case Kind.EndWindow: desk.End(packet.Read<Guid>()); break;
                        case Kind.HomePreview: desk.HomePreview(packet.Read<ViewInfo>()); break;
                        case Kind.ClearHomePreview: desk.ClearPreview(packet.Read<Guid>()); break;
                        case Kind.ReturnWindow:
                            var r = packet.Read<ReturnInfo>();
                            desk.Restore(r.Id, layout.FromWire(new(r.X, r.Y))); break;
                    }
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
            });
            return done.Task;
        }
    }
    static async Task Until(Func<bool> check, string stage, int seconds = 6)
    {
        for (int i = 0; i < seconds * 20; i++) { if (check()) return; await Task.Delay(50); }
        throw new TimeoutException(stage);
    }
    static bool Invisible(nint hwnd) => Win.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _) && alpha == 0;
    static bool MatchesLayout(Bitmap? frame, Size size, bool wide)
    {
        if (frame == null || Math.Abs(frame.Width - size.Width) > 1 || Math.Abs(frame.Height - size.Height) > 1) return false;
        // The fixture paints this widget only when its own client layout becomes wide.
        for (int x = 12; x < 76; x += 4)
            for (int y = frame.Height - 66; y < frame.Height - 12; y += 4)
            {
                var c = frame.GetPixel(x, y);
                if (c.R < 50 && c.G > 200 && c.B > 200) return wide;
            }
        return !wide;
    }
    static async Task CheckResize(WindowDesk desk, StreamWindow view, bool dragCorner = false)
    {
        var original = view.Bounds;
        int Hit(int x, int y) => Win.Hit(view.Handle, view.PointToScreen(new(x, y)));
        if (Hit(1, 1) != 13 || Hit(view.Width - 2, view.Height / 2) != 11 || Hit(view.Width - 2, view.Height - 2) != 17)
            throw new Exception("The projected frame lacks native resize grips.");
        var resized = new Size(593, 357);
        if (dragCorner)
        {
            var center = Screen.FromControl(view).WorkingArea.Location + new Size(240, 160);
            view.Bounds = new Rectangle(center, new Size(510, 285));
            await Until(() => MatchesLayout(desk.LatestImage(view.Id), view.ClientSize, true), "Preparing the native corner drag");
            Win.SetWindowPos(view.Handle, -1, 0, 0, 0, 0, 0x13);
            var corner = view.PointToScreen(new(view.Width - 2, view.Height - 2));
            var delta = resized - view.Size;
            nint handle = view.Handle;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ResizeBegin += (_, _) => started.TrySetResult();
            view.ResizeEnd += (_, _) => finished.TrySetResult();
            await Task.Run(async () =>
            {
                Win.GetCursorPos(out var before);
                bool pressed = false;
                try
                {
                    Win.SetCursorPos(corner.X, corner.Y);
                    await Task.Delay(80);
                    if (Win.WindowFromPoint(corner) != handle || Win.Held(Keys.LButton))
                        throw new Exception("The test window's resize corner is unavailable.");
                    Win.mouse_event(2, 0, 0, 0, Win.Tag); pressed = true;
                    // Windows can wait for the pointer to move before it enters the native sizing loop.
                    Win.SetCursorPos(corner.X + Math.Sign(delta.Width)*8, corner.Y + Math.Sign(delta.Height)*8);
                    try { await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException)
                    {
                        Win.GetCursorPos(out var actual);
                        throw new TimeoutException($"Windows did not start resizing. Corner {corner}, pointer {actual}, window {Win.Bounds(handle)}, hit {Win.Hit(handle, corner)}, foreground {Win.GetForegroundWindow():X}, target {handle:X}, held {Win.Held(Keys.LButton)}.");
                    }
                    for (int step = 1; step <= 5; step++)
                    {
                        Win.SetCursorPos(corner.X + delta.Width * step / 5, corner.Y + delta.Height * step / 5);
                        await Task.Delay(45);
                    }
                }
                finally
                {
                    try
                    {
                        if (pressed) Win.mouse_event(4, 0, 0, 0, Win.Tag);
                        if (started.Task.IsCompleted) await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    finally { Win.SetCursorPos(before.X, before.Y); }
                }
            });
            if (view.Size != resized) throw new Exception($"Native corner drag produced {view.Size}, expected {resized}.");
        }
        else view.Size = resized;
        await Until(() => MatchesLayout(desk.LatestImage(view.Id), resized, true), "Reflowing the source app to the dragged size");
        var work = Screen.FromControl(view).WorkingArea;
        desk.ChangeView(new(view.Id, true));
        try
        {
            await Until(() => view.ClientSize == work.Size && MatchesLayout(desk.LatestImage(view.Id), work.Size, true),
                $"Reflowing the maximized app at {work.Width}×{work.Height}");
        }
        catch (TimeoutException ex)
        {
            throw new Exception($"{ex.Message}. View {view.WindowState} {view.Bounds}, client {view.ClientSize}, accepted {view.SourceSize}, decoded {desk.LatestImage(view.Id)?.Size}.", ex);
        }
        desk.ChangeView(new(view.Id, true));
        await Until(() => view.Bounds.Size == resized && MatchesLayout(desk.LatestImage(view.Id), resized, true), "Restoring the pre-maximize layout");
        view.Bounds = original;
        try { await Until(() => MatchesLayout(desk.LatestImage(view.Id), original.Size, original.Width > 500), "Restoring the original app layout"); }
        catch (TimeoutException ex) { throw new Exception($"Expected {original.Size}, client {view.ClientSize}, accepted {view.SourceSize}, decoded {desk.LatestImage(view.Id)?.Size}.", ex); }
        Console.WriteLine($"PASS  Native resize grips, responsive app layout at {resized.Width}×{resized.Height} and {work.Width}×{work.Height}, and maximize/restore.");
    }
    static Point Caption(nint hwnd, int offset = 60)
    {
        var box = Win.Bounds(hwnd);
        for (int y = 6; y < 48; y += 2)
        {
            var p = new Point(box.X + offset, box.Y + y);
            if (Win.Hit(hwnd, p) == 2 && Win.GetAncestor(Win.WindowFromPoint(p), 2) == hwnd) return p;
        }
        var sample = new Point(box.X + offset, box.Y + 18);
        throw new InvalidOperationException($"Test title bar unavailable at {box}. Fixture '{Win.Title(hwnd)}', hit {Win.Hit(hwnd, sample)}, requested {hwnd:X}, actual {Win.GetAncestor(Win.WindowFromPoint(sample), 2):X}.");
    }
    public static int Run(bool video) => Run(ui => Exercise(ui, video));
    static int Run(Func<Form, Task> exercise)
    {
        using var ui = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(2, 2) };
        int result = 1;
        ui.Shown += async (_, _) =>
        {
            ui.Hide();
            try { await exercise(ui); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { ui.Close(); }
        };
        Application.Run(ui);
        return result;
    }
    public static int RunLan(bool listen, string address, int port, bool settingsOnly = false, bool explorer = false) => Run(async ui =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var invite = listen ? new Invitation(IPAddress.Parse(address), port) : null;
        if (invite != null) { Console.WriteLine(invite.Code); Console.Out.Flush(); }
        using var peer = invite != null ? await invite.Accept(stop.Token) :
            await Invitation.Join(address, await Console.In.ReadLineAsync(stop.Token) ?? throw new EndOfStreamException(), stop.Token, port);
        if (settingsOnly) { await CheckPreferences(peer, listen, stop.Token); return; }
        if (explorer) { await CheckExplorer(peer, listen, ui); return; }
        var work = Screen.PrimaryScreen!.WorkingArea;
        var local = new Rectangle(work.Location, new Size(640, 420));
        if (!work.Contains(local)) throw new InvalidOperationException("The drag check needs a 640 by 420 work area.");
        var layout = new DeskLayout(local, local.Size, listen);
        using var desk = new WindowDesk(ui, peer, layout, Console.WriteLine);
        var endpoint = new Endpoint(desk, layout, ui);
        peer.LatencyProbe = true;
        var isolation = new TransportChecks();
        var reader = Task.Run(() => peer.Run(p => isolation.Dispatch(p, endpoint.Dispatch)));
        using var performance = new SessionPerformance(peer, desk, listen ? "joining PC" : "listening PC");
        async Task<string> CheckStats(bool sending)
        {
            long previous = peer.LastProbeReply;
            peer.ProbeLatency();
            await Until(() => peer.LastProbeReply > previous, "Measuring input timing during streaming");
            var sample = performance.Sample();
            var row = sample.Windows.Single(w => w.Sending == sending);
            if (row.Fps <= 0 || row.Mbps <= 0 || row.CodecMs is not > 0 || row.Pixels.IsEmpty ||
                (sending ? row.QueueMs == null || row.SetupMs is not > 0 || sample.SendRate <= 0 : row.PaintedFps is not > 0 || sample.ReceiveRate <= 0))
                throw new Exception($"Incomplete live stream metrics: {row}");
            using var panel = new PerformanceForm();
            panel.Render(performance);
            using var bitmap = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height);
            panel.DrawToBitmap(bitmap, panel.ClientRectangle);
            panel.Render(null);
            Console.WriteLine($"PASS  {(sending ? "Send" : "Receive")} {row.Codec}, {row.Fps:F1} fps, {row.Mbps:F3} Mbps, {row.CodecMs:F2} ms/frame, TCP {sample.NetworkRttMs:F2} ms, DeskPortal {sample.DeskLinkDelayMs:F2} ms, input round trip {sample.RoundTripMs:F2} ms.");
            if (sending) Console.WriteLine($"Encoder setup {row.SetupMs:F2} ms for {row.Pixels.Width}x{row.Pixels.Height}.");
            return row.Codec.Split(' ')[0];
        }
        try
        {
            await isolation.Check(peer, stop.Token);
            isolation.End();
            performance.Start();
            // The joining PC offers the first window. Both use real HWNDs, WGC and
            // the application's input routing without hooking the user's devices.
            foreach (bool sender in new[] { !listen, listen })
            {
                string phase = sender == listen ? "second" : "first";
                using var clipboard = new SharedClipboard(peer, Console.WriteLine, () => { });
                using var files = new EdgeFiles(layout, peer, clipboard, Console.WriteLine);
                using var pointer = new PointerBridge(peer, desk, layout, files, _ => { });
                pointer.SetSharing(Sharing.Windows);
                if (pointer.Physical(new Point(layout.Right ? local.Right - 1 : local.Left, local.Top + 50), Win.Move, 0))
                    throw new Exception("Mouse sharing crossed the edge while disabled.");
                desk.ConfigureStream(new(FrameCodec.Auto, 30));
                FrameCodec[] switches = [FrameCodec.Hevc, FrameCodec.H264, FrameCodec.Png, FrameCodec.Av1];
                void Cross(Point from)
                {
                    var seam = new Point(layout.Right ? local.Right - 1 : local.Left, from.Y);
                    if (!pointer.Physical(seam, Win.Move, 0)) throw new Exception("Pointer did not cross the test edge.");
                    var anchor = new Point(local.Left + local.Width / 2, local.Top + local.Height / 2);
                    pointer.Physical(anchor + new Size(layout.Right ? 185 : -225, 12), Win.Move, 0);
                    pointer.Physical(anchor, Win.LeftUp, 0);
                }
                if (sender)
                {
                    using var fixture = Process.Start(Program.StartSelf("--capture-fixture"))!;
                    nint hwnd = 0;
                    try
                    {
                        await Until(() => { fixture.Refresh(); hwnd = fixture.MainWindowHandle; return hwnd != 0; }, "Opening source window");
                        Win.SetWindowPos(hwnd, -1, local.Left + 30, local.Top + 60, 320, 180, 0x10);
                        var initialSize = Win.Bounds(hwnd).Size;
                        var from = PressCaption(pointer, desk, hwnd, new Size(48, 12));
                        var edge = new Point(layout.Right ? local.Right - 20 : local.Left + 20, from.Y);
                        pointer.Physical(edge, Win.Move, 0);
                        await Until(() => Application.OpenForms.OfType<DragPreview>().Any(w => w.Visible), "Showing the source drag preview");
                        var preview = Application.OpenForms.OfType<DragPreview>().Single(w => w.Visible);
                        var clock = Stopwatch.StartNew();
                        var updates = new List<double> { 0 };
                        int lastY = preview.Top;
                        for (int i = 1; i <= 48; i++)
                        {
                            pointer.Physical(new Point(edge.X, edge.Y + i), Win.Move, 0);
                            await Task.Delay(12);
                            if (preview.Top == lastY) continue;
                            updates.Add(clock.Elapsed.TotalMilliseconds);
                            lastY = preview.Top;
                        }
                        var gaps = updates.Zip(updates.Skip(1), (a, b) => b - a).Order().ToArray();
                        if (gaps.Length == 0) throw new Exception("The source drag preview remained stationary.");
                        Console.WriteLine($"Preview moved on {gaps.Length}/48 sampled updates. p95 gap {gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]:F1} ms.");
                        Cross(new Point(edge.X, edge.Y + 48));
                        await Until(() => Invisible(hwnd), "Waiting for the receiving PC to decode the window");
                        peer.Send(Kind.Hello, phase + "-auto-" + await CheckStats(true));
                        peer.Send(Kind.Hello, phase + "-dropped");
                        foreach (var codec in switches)
                        {
                            await Until(() => endpoint.Stages.Contains(phase + "-select-" + codec), $"Receiver requesting {codec}");
                            desk.ConfigureStream(new(codec, 30, Mbps: 8));
                        }
                        await Until(() => !Invisible(hwnd) && endpoint.Stages.Contains(phase + "-returned"), "Returning original window", 20);
                        if (Win.Bounds(hwnd).Size != initialSize) throw new Exception("The returned window lost its original size.");
                        Console.WriteLine("PASS  Local title-bar drag crossed the edge and the original window returned.");
                    }
                    finally { if (hwnd != 0) Win.PostMessage(hwnd, 0x10, 0, 0); }
                }
                else
                {
                    await Until(() => endpoint.Stages.Contains(phase + "-dropped") &&
                        endpoint.Frames.Values.Any(c => c.R > 220 && c.G < 40), "Receiving the live test window", 18);
                    var view = Application.OpenForms.OfType<StreamWindow>().Single();
                    await Until(() => endpoint.Stages.Contains(phase + "-auto-" + VideoCodec.Name(endpoint.Codecs[view.Id])), "Receiving the codec selected by Auto");
                    foreach (var codec in switches)
                    {
                        peer.Send(Kind.Hello, phase + "-select-" + codec);
                        await Until(() => endpoint.Codecs[view.Id] == codec, $"Receiving {codec} in the existing window");
                    }
                    Console.WriteLine("PASS  Received the Auto selection and switched the same window through HEVC, H.264, PNG and AV1.");
                    await CheckResize(desk, view);
                    Win.SetWindowPos(view.Handle, -1, 0, 0, 0, 0, 0x13);
                    var center = new Point(view.ClientSize.Width / 2, view.ClientSize.Height / 2);
                    Win.PostMessage(view.Handle, Win.LeftDown, 1, Win.Packed(center));
                    Win.PostMessage(view.Handle, Win.LeftUp, 0, Win.Packed(center));
                    await Until(() => endpoint.Frames[view.Id].B > 220 && endpoint.Frames[view.Id].R < 40, "Clicking the received window");
                    Win.PostMessage(view.Handle, Win.Wheel, 120 << 16, Win.Packed(view.PointToScreen(center)));
                    await Until(() => endpoint.Frames[view.Id].R > 220 && endpoint.Frames[view.Id].G > 220, "Scrolling the received window");
                    Win.PostMessage(view.Handle, 0x100, (int)Keys.F8, 0x420001);
                    Win.PostMessage(view.Handle, 0x101, (int)Keys.F8, unchecked((int)0xc0420001));
                    await Until(() => endpoint.Frames[view.Id].G > 220 && endpoint.Frames[view.Id].R < 40, "Keyboard input in the received window");
                    Console.WriteLine("PASS  The original app responded to a click, scroll and keystroke from the receiving PC.");
                    await CheckStats(false);
                    var from = CheckLocalMovement(pointer, view.Handle, new Size(48, 24));
                    pointer.Physical(from, Win.LeftUp, 0);
                    from = CheckLocalMovement(pointer, view.Handle, new Size(-24, 0));
                    Cross(from);
                    await Until(() => view.IsDisposed, "Removing the returned projection");
                    Console.WriteLine("PASS  Received live pixels, repositioned the streamed window locally and dragged it home.");
                    peer.Send(Kind.Hello, phase + "-returned");
                }
            }
            long sentVideo = peer.Sent.Read().Video;
            peer.Send(Kind.Hello, $"video-bytes/{sentVideo}");
            await Until(() => endpoint.Stages.Contains($"video-bytes/{peer.Received.Read().Video}"), "Matching sender and receiver window byte counts");
            Console.WriteLine($"PASS  Both ends counted {sentVideo:N0} bytes sent and {peer.Received.Read().Video:N0} bytes received.");
            if (listen)
            {
                peer.Send(Kind.Hello, "finished");
                await Until(() => endpoint.Stages.Contains("finished"), "Receiving the final confirmation");
            }
            else
            {
                await Until(() => endpoint.Stages.Contains("finished"), "Waiting for the listener to finish");
                peer.Send(Kind.Hello, "finished");
                await Until(() => reader.IsCompleted, "Waiting for the listener to close");
            }
        }
        finally
        {
            peer.Dispose();
            try { await reader; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    });
    static async Task CheckPreferences(Peer peer, bool listen, CancellationToken token)
    {
        using var form = new DeskForm(listenToDesktop: false);
        _ = form.Handle;
        void Choose(Sharing features) => form.UseSettings(new DeskSettings { Features = features });
        var common = Sharing.Windows | Sharing.Files;
        Choose(listen ? common : Sharing.Windows);
        var session = form.RunSession(peer, token);
        try
        {
            await Until(() => form.ScreenLayout?.Editable == true,"Receiving the physical display inventory");
            var map = form.ScreenLayout!;
            Console.WriteLine($"Displays: local {string.Join(", ",map.Here.Select(s => $"{s.Id} {s.Bounds}"))}; peer {string.Join(", ",map.There.Select(s => $"{s.Id} {s.Bounds}"))}");
            // Apply a real interleaved row through the paired control channel, then edit it from the other PC.
            if (listen)
            {
                int x = 301;
                var local = new List<TilePosition>(); var other = new List<TilePosition>();
                for (int i=0;i<Math.Max(map.Here.Length,map.There.Length);i++)
                {
                    if (i < map.Here.Length) { var s = map.Here[i]; local.Add(new(s.Id,x,77)); x += s.Bounds.Width; }
                    if (i < map.There.Length) { var s = map.There[i]; other.Add(new(s.Id,x,77)); x += s.Bounds.Width; }
                }
                form.ShareLayout(new(local.ToArray(),other.ToArray()));
                if (map.Here.Length >= 2)
                {
                    using var windows = new WindowDesk(form,peer,map,Console.WriteLine);
                    using var clipboard = new SharedClipboard(peer,Console.WriteLine,() => { }, Path.GetTempPath());
                    using var files = new EdgeFiles(map,peer,clipboard,Console.WriteLine);
                    var warped = Point.Empty;
                    using var pointer = new PointerBridge(peer,windows,map,files,p => warped = p);
                    pointer.SetSharing(Sharing.Mouse);
                    for (int direction = 1;direction >= -1;direction -= 2)
                    {
                        var source = map.Tiles[direction == 1 ? 0 : 1];
                        var destination = map.Tiles[direction == 1 ? 1 : 0];
                        var edge = new Point(direction == 1 ? source.Native.Right-1 : source.Native.Left,source.Native.Top+38);
                        pointer.Physical(edge-new Size(direction*6,0),Win.Move,0);
                        if (!pointer.Physical(edge,Win.Move,0)) throw new Exception("Display crossing did not capture the pointer.");
                        var anchor = warped;
                        int inside = map.There[0].Bounds.Width-1;
                        for (int moved=0; moved < inside; moved+=64)
                            pointer.Physical(anchor+new Size(direction*Math.Min(64,inside-moved),0),Win.Move,0);
                        pointer.Physical(anchor+new Size(direction*5,0),Win.Move,0);
                        if (warped != new Point(direction == 1 ? destination.Native.Left+4 : destination.Native.Right-5,destination.Native.Top+38))
                            throw new Exception($"Pointer returned to the wrong physical display at {warped}.");
                    }
                    Console.WriteLine("PASS  Pointer reports crossed both FAST monitors through the peer screen and returned in reverse order.");
                }
                await Until(() => map.Tiles.All(t => t.Desk.Y == -177),"Receiving the other PC's layout edit");
            }
            else
            {
                await Until(() => map.Tiles.All(t => t.Desk.Y == 77),"Receiving the interleaved display row");
                var from = map.Tiles.First(t => t.Desk.X == 301);
                var edge = new Point(from.Desk.Right-1,from.Desk.Top+1);
                var cross = map.Walk(from,edge,new(edge.X+3,edge.Y));
                if (from.Here || !cross.Tile.Here) throw new Exception("The interleaved crossing selected the wrong PC.");
                TilePosition[] Shift(TilePosition[] positions) => positions.Select(p => p with { Y = -177 }).ToArray();
                form.ShareLayout(new(Shift(map.Plan.Here),Shift(map.Plan.There)));
            }
            Console.WriteLine("PASS  Individual monitor positions propagated in both directions.");
            if (listen)
            {
                await Until(() => form.ActiveSharing == common, "Both peers enabling files");
                Choose(Sharing.None);
                await Until(() => form.PeerFeatures == Sharing.Files, "Peer saw all features withdrawn");
                Choose(Sharing.All);
                await Until(() => form.ActiveSharing == (Sharing.Mouse | Sharing.Clipboard), "Mouse and clipboard alone enabled on both PCs");
                Choose(Sharing.Windows);
                await Until(() => form.PeerFeatures == Sharing.None, "Peer acknowledged final withdrawal");
            }
            else
            {
                await Until(() => form.ActiveSharing == Sharing.Windows, "Initial negotiated features");
                Choose(common);
                await Until(() => form.PeerFeatures == Sharing.None && form.ActiveSharing == Sharing.None, "Other PC withdrew sharing");
                Choose(Sharing.Files);
                await Until(() => form.ActiveSharing == Sharing.Files, "Files resumed on this connection");
                Choose(Sharing.Mouse | Sharing.Clipboard);
                await Until(() => form.PeerFeatures == Sharing.Windows && form.ActiveSharing == Sharing.None, "Peer turned off mouse and clipboard");
                Choose(Sharing.None);
                await Until(() => session.IsCompleted, "Completing settings verification");
            }
            Console.WriteLine("PASS  Both PCs enforced and renegotiated all four sharing switches on one connection.");
        }
        finally
        {
            form.Close();
            peer.Dispose();
            try { await session; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    static async Task CheckDragPicture()
    {
        var area = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(160, 160), new Size(220, 160));
        Form Fixture(Color color) => new() { Bounds = area, BackColor = color, StartPosition = FormStartPosition.Manual,
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
        using var original = Fixture(Color.Lime);
        using var cover = Fixture(Color.Red);
        original.Show(); original.Update();
        cover.TopMost = true; cover.Show(); cover.Update();
        var center = original.PointToScreen(new Point(original.ClientSize.Width / 2, original.ClientSize.Height / 2));
        if (Win.WindowFromPoint(center) != cover.Handle) throw new IOException("The test cover did not overlap the selected window.");
        var picture = await DragOpeningPicture.Start(original.Handle).WaitAsync(TimeSpan.FromSeconds(2));
        if (picture == null || !picture.CanShare(original.Size)) throw new IOException("The selected window supplied no drag snapshot.");
        using var bytes = new MemoryStream(picture.Jpeg);
        using var bitmap = new Bitmap(bytes);
        var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        if (pixel.G < 215 || pixel.R > 45 || pixel.B > 45) throw new IOException("The opening picture included the covering window.");
        if (!SetWindowDisplayAffinity(original.Handle, 0x11) || picture.CanShare(original.Size) ||
            await DragOpeningPicture.Start(original.Handle) != null)
            throw new IOException("The preview did not respect the window's capture exclusion.");
        Console.WriteLine("PASS  The opening picture contains only the selected window, and enabling capture exclusion discards a prepared picture.");
    }
    static async Task Exercise(Form ui, bool video)
    {
        await CheckDragPicture();
        using var first = Process.Start(Program.StartSelf("--capture-fixture"))!;
        using var second = Process.Start(Program.StartSelf("--capture-fixture"))!;
        nint hwndA = 0, hwndB = 0;
        try
        {
            await Until(() =>
            {
                first.Refresh(); second.Refresh();
                hwndA = first.MainWindowHandle; hwndB = second.MainWindowHandle;
                return hwndA != 0 && hwndB != 0;
            }, "Waiting for the two independent test windows");
            var work = Screen.PrimaryScreen!.WorkingArea;
            var left = new Rectangle(work.X, work.Y, work.Width / 2, work.Height);
            var right = new Rectangle(left.Right, work.Y, work.Width - left.Width, work.Height);
            var a = new DeskLayout(left, right.Size, true);
            var b = new DeskLayout(right, left.Size, false);
            int width = Math.Min(360, Math.Min(left.Width, right.Width) - 30);
            int height = Math.Min(200, work.Height / 3);
            Win.SetWindowPos(hwndA, -1, left.Left + 12, work.Top + 10, width, height, 0x10);
            Win.SetWindowPos(hwndB, -1, right.Left + 10, work.Bottom - height - 10, width, height, 0x10);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var invite = new Invitation(IPAddress.Loopback, 0);
            var wait = invite.Accept(stop.Token);
            using var client = await Invitation.Join("127.0.0.1", invite.Code, stop.Token, invite.ListeningPort);
            using var server = await wait;
            using var deskA = new WindowDesk(ui, server, a, Console.WriteLine);
            using var deskB = new WindowDesk(ui, client, b, Console.WriteLine);
            var endpointA = new Endpoint(deskA, a, ui);
            var endpointB = new Endpoint(deskB, b, ui);
            var taskA = Task.Run(() => server.Run(endpointA.Dispatch));
            var taskB = Task.Run(() => client.Run(endpointB.Dispatch));
            await Task.Delay(150);
            using var clipA = new SharedClipboard(server, Console.WriteLine, () => { });
            using var filesA = new EdgeFiles(a, server, clipA, Console.WriteLine);
            using var pointerA = new PointerBridge(server, deskA, a, filesA, _ => { });
            deskB.Tiling = TileMode.RightHalf;
            var from = PressCaption(pointerA, deskA, hwndA, new Size(48, 12));
            pointerA.Physical(new Point(left.Right - 60, from.Y), Win.Move, 0);
            await Until(() => Invisible(hwndA) && endpointB.Frames.Values.Any(c => c.R > 220), "First half-drag and red frame");
            Console.WriteLine("PASS  Half-drag captures another process and decodes its red client area over TLS.");
            pointerA.Physical(new Point(left.Right - 1, from.Y), Win.Move, 0);
            var anchor = new Point(left.Left + left.Width / 2, left.Top + left.Height / 2);
            pointerA.Physical(anchor + new Size(80, 20), Win.Move, 0);
            pointerA.Physical(anchor, Win.LeftUp, 0);
            var id = endpointB.Frames.Keys.Single();
            var view = Application.OpenForms.OfType<StreamWindow>().Single(w => w.Id == id);
            var expectedTile = DeskSettings.Place(TileMode.RightHalf, new Rectangle(Point.Empty, view.SourceSize), work, true);
            await Until(() => view.Bounds == expectedTile && Win.FrameBounds(hwndA).Size == expectedTile.Size &&
                MatchesLayout(deskB.LatestImage(id), expectedTile.Size, true), "Tiling and reflowing the original window on release");
            await CheckResize(deskB, view, dragCorner: true);
            deskA.ConfigureStream(new(FrameCodec.Hevc, 24, Mbps: 8));
            var click = Win.Packed(new(view.Width / 2, view.Height / 2));
            Win.PostMessage(view.Handle, Win.LeftDown, 1, click);
            Win.PostMessage(view.Handle, Win.LeftUp, 0, click);
            await Until(() => endpointB.Frames[id].B > 220 && endpointB.Codecs[id] == FrameCodec.Hevc, "Clicking a tiled window after switching its stream to HEVC");
            Console.WriteLine("PASS  A right-half tile preserved input through a live switch to HEVC.");
            Win.PostMessage(view.Handle, 0x100, (int)Keys.F8, 0x420001);
            Win.PostMessage(view.Handle, 0x101, (int)Keys.F8, unchecked((int)0xC0420001));
            await Until(() => endpointB.Frames[id].G > 220, "Typing into the remote window reaches the original");
            Console.WriteLine("PASS  Keyboard input reaches the original window and its new green frame arrives.");
            Win.PostMessage(hwndA, 0x8042, 0, 0);
            await Until(() => Application.OpenForms.OfType<StreamWindow>().Any(w => w.Owner == view && w.Text == "DeskPortal test dialog"), "Forwarding the owned dialog");
            var dialog = Application.OpenForms.OfType<StreamWindow>().Single(w => w.Owner == view);
            await Until(() => endpointB.Codecs.GetValueOrDefault(dialog.Id) == FrameCodec.Hevc, "Decoding the HEVC dialog");
            // On two PCs the source dialog holds the foreground of its own desktop, and a key typed
            // into its projection is injected there. On this one desktop the projection has just
            // taken the only foreground, and Windows ignores Enter posted to a background dialog.
            // Hand the foreground back to model the source PC.
            nint sourceDialog = 0;
            Win.EnumWindows((w, _) => { if (Win.Class(w) == "#32770" && Win.GetWindow(w, 4) == hwndA) sourceDialog = w; return true; }, 0);
            if (sourceDialog == 0) throw new Exception("The source dialog was not found.");
            Win.SetForegroundWindow(sourceDialog);
            await Until(() => Win.GetForegroundWindow() == sourceDialog, "Returning the foreground to the source dialog");
            Win.PostMessage(dialog.Handle, 0x100, (int)Keys.Return, 0x1c0001);
            Win.PostMessage(dialog.Handle, 0x101, (int)Keys.Return, unchecked((int)0xC01c0001));
            try { await Until(() => dialog.IsDisposed, "Dismissing the dialog from its projection"); }
            catch (TimeoutException ex)
            {
                // Say which half failed: the key reaching the source dialog, or the projection being withdrawn.
                nint front = Win.GetForegroundWindow();
                throw new Exception($"{ex.Message}. Source dialog {(!Win.IsWindow(sourceDialog) ? "closed" : "still open")}, " +
                    $"foreground '{Win.Title(front)}' ({Win.Class(front)}), projection visible {dialog.Visible}.", ex);
            }
            Console.WriteLine("PASS  A native owned dialog appears on the receiving desk and can be dismissed there.");
            deskA.ConfigureStream(new(FrameCodec.H264, 60, Mbps: 4));
            Win.PostMessage(view.Handle, Win.Wheel, 120 << 16, Win.Packed(view.PointToScreen(new(view.Width / 2, view.Height / 2))));
            await Until(() => endpointB.Codecs[id] == FrameCodec.H264 && endpointB.Frames[id].R > 220, "Switching the active stream to H.264");
            deskB.Tiling = TileMode.Free;
            Win.SetWindowPos(hwndB, -1, 0, 0, 0, 0, 0x13);
            from = Caption(hwndB);
            if (deskB.Route(from, Win.LeftDown, false) || !deskB.CaptionPressed) throw new Exception("Right title bar press was not left to Windows.");
            var drop = new Point(left.Left + 100, from.Y);
            deskB.Route(drop, Win.Move, true); deskB.Route(drop, Win.LeftUp, true);
            await Until(() => Invisible(hwndA) && Invisible(hwndB) && endpointA.Frames.Values.Any(c => c.R > 220), "Two simultaneous streams");
            Console.WriteLine("PASS  Both desks simultaneously host the other process’s live window.");
            Win.SetWindowPos(view.Handle, -1, 0, 0, 0, 0, 0x13);
            await Task.Delay(80);
            using var clipB = new SharedClipboard(client, Console.WriteLine, () => { });
            using var filesB = new EdgeFiles(b, client, clipB, Console.WriteLine);
            using var pointerB = new PointerBridge(client, deskB, b, filesB, _ => { });
            from = CheckLocalMovement(pointerB, view.Handle, new Size(72, 28));
            Console.WriteLine("PASS  A received window follows relative cursor motion. A native window stays with Windows until it reaches the edge.");
            drop = new Point(left.Left + 125, from.Y);
            deskB.Route(drop, Win.Move, true); deskB.Route(drop, Win.LeftUp, true);
            await Until(() => !Invisible(hwndA) && view.IsDisposed, "Return drag");
            Console.WriteLine("PASS  Dragging the projection home restores the original HWND.");
            deskB.RestoreAll();
            await Until(() => !Invisible(hwndB), "Restoring the second source");
            if (video) await ExerciseVideo(deskA, endpointB, deskB, a);
            server.Dispose(); client.Dispose();
            try { await Task.WhenAll(taskA, taskB); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            var state = SourceState.Read(hwndA, Win.Bounds(hwndA).Location, false);
            using (var guard = new RecoveryGuard()) { guard.Track(state); state.Conceal(); }
            await Until(() => !Invisible(hwndA), "Recovery helper after parent pipe closure");
            Console.WriteLine("PASS  Recovery restores a concealed window when the parent pipe closes.");
        }
        finally
        {
            if (hwndA != 0) Win.PostMessage(hwndA, 0x10, 0, 0);
            if (hwndB != 0) Win.PostMessage(hwndB, 0x10, 0, 0);
        }
    }
    // Windows owns a local title-bar drag until the window reaches the shared edge. These
    // checks call the routing directly, so nothing moves the window before DeskLink takes over.
    internal static Point PressCaption(PointerBridge pointer, WindowDesk desk, nint hwnd, Size distance)
    {
        var cursor = Caption(hwnd);
        var start = Win.Bounds(hwnd).Location;
        if (pointer.Physical(cursor, Win.LeftDown, 0) || !desk.CaptionPressed)
            throw new Exception("A local title-bar press must stay with Windows.");
        if (pointer.Physical(cursor + distance, Win.Move, 0) || desk.Dragging || Win.Bounds(hwnd).Location != start)
            throw new Exception("DeskPortal took a window that had not reached the shared edge.");
        return cursor + distance;
    }
    // Model the WH_MOUSE_LL contract. Windows applies a relative device report
    // to the last accepted position, then asks the hook whether to keep it.
    internal static Point CheckLocalMovement(PointerBridge pointer, nint hwnd, Size distance)
    {
        var cursor = Caption(hwnd);
        var start = Win.Bounds(hwnd).Location;
        if (!pointer.Physical(cursor, Win.LeftDown, 0)) throw new Exception("Title bar press was not captured.");
        for (int i = 1; i <= 12; i++)
        {
            var step = new Size(i * distance.Width / 12 - (i - 1) * distance.Width / 12,
                i * distance.Height / 12 - (i - 1) * distance.Height / 12);
            var proposed = cursor + step;
            if (!pointer.Physical(proposed, Win.Move, 0)) cursor = proposed;
        }
        Application.DoEvents();
        if (Win.Bounds(hwnd).Location != start + distance)
            throw new Exception($"Window did not follow the local cursor. Expected {start + distance}, got {Win.Bounds(hwnd).Location}.");
        return cursor;
    }
    static async Task ExerciseVideo(WindowDesk source, Endpoint receiver, WindowDesk destination, DeskLayout layout)
    {
        var info = new ProcessStartInfo("ffplay.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]{"-hide_banner","-loglevel","error","-an","-autoexit","-f","lavfi","-i",
            "testsrc2=size=360x200:rate=20:duration=25","-window_title","DeskPortal playback check"})
            info.ArgumentList.Add(arg);
        using var player = Process.Start(info)!;
        nint hwnd = 0;
        try
        {
            await Until(() => { player.Refresh(); hwnd = player.MainWindowHandle; return hwnd != 0; }, "Opening the video player");
            Win.SetWindowPos(hwnd, -1, layout.Local.Left + 30, layout.Local.Top + 280, 0, 0, 0x11);
            var from = Caption(hwnd);
            if (source.Route(from, Win.LeftDown, false) || !source.CaptionPressed) throw new Exception("Video title bar press was not left to Windows.");
            var drop = new Point(layout.Remote.Left + 120, from.Y);
            source.Route(drop, Win.Move, true); source.Route(drop, Win.LeftUp, true);
            await Until(() => Invisible(hwnd), "Moving live video to the receiving desk");
            var view = Application.OpenForms.OfType<StreamWindow>().Single(w => w.Text == "DeskPortal playback check");
            int initial = receiver.Motion[view.Id].Changes;
            await Until(() => receiver.Motion[view.Id].Changes >= initial + 5, "Playback kept animating while concealed");
            void Space()
            {
                Win.PostMessage(view.Handle, 0x100, 32, 0x390001);
                Win.PostMessage(view.Handle, 0x101, 32, unchecked((int)0xC0390001));
            }
            Space();
            await Task.Delay(450);
            int paused = receiver.Motion[view.Id].Changes;
            await Task.Delay(500);
            if (receiver.Motion[view.Id].Changes != paused) throw new Exception("Remote pause did not stop video motion.");
            Space();
            await Until(() => receiver.Motion[view.Id].Changes >= paused + 4, "Resuming the player through the streamed window");
            Win.SetWindowPos(view.Handle, -1, 0, 0, 0, 0, 0x13);
            from = Caption(view.Handle);
            destination.Route(from, Win.LeftDown, false);
            drop = new Point(layout.Local.Left + 100, from.Y);
            destination.Route(drop, Win.Move, true); destination.Route(drop, Win.LeftUp, true);
            await Until(() => !Invisible(hwnd) && view.IsDisposed, "Returning the playing video");
            Console.WriteLine("PASS  A real FFplay video continued playing while concealed, paused and resumed from the receiver, and returned home.");
        }
        finally { if (hwnd != 0) Win.PostMessage(hwnd, 0x10, 0, 0); }
    }
}
