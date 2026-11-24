using System.Diagnostics;
using System.Net;

namespace DeskLink;

static partial class WindowChecks
{
    record DragTarget(int X, int Y, int Edge, int Height, DisplayInfo[]? Displays, ScreenPlan? Layout);

    // The owner runs the production receiver, including its input hook and control dispatcher.
    // The other PC holds the mouse button across the owner's edge and checks the actual video.
    public static int CheckRemoteDrag(bool owner, string address, int port, bool brave = false, bool flick = false, bool wide = false, bool approach = false, bool large = false) => Run(async ui =>
    {
        var cursor = Cursor.Position;
        var foreground = Win.GetForegroundWindow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var invite = owner ? new Invitation(IPAddress.Parse(address), port) : null;
        if (invite != null) { Console.WriteLine(invite.Code); Console.Out.Flush(); }
        using var peer = invite != null ? await invite.Accept(deadline.Token) :
            await Invitation.Join(address, await Console.In.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException(), deadline.Token, port);
        try
        {
            if (owner) await OwnDragFixture(peer, deadline.Token, brave, flick, wide, large);
            else await DriveDragFixture(peer, ui, deadline.Token, flick, wide, approach);
        }
        finally { Cursor.Position = cursor; if (foreground != 0) Win.SetForegroundWindow(foreground); }
    });

    static Process StartBraveFixture(string directory, bool large)
    {
        string exe = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData }.Select(folder => Path.Combine(Environment.GetFolderPath(folder),
            @"BraveSoftware\Brave-Browser\Application\brave.exe")).First(File.Exists);
        var html = Path.Combine(directory, "preview.html");
        string chat = large ? """
            <style>body{font:20px Segoe UI;display:grid;grid-template-columns:1fr 3fr;gap:2em}section{background:#fafafa;overflow:hidden;padding:1em}
            p{padding:.4em .6em;border-bottom:1px solid #bcbcbc}aside{position:fixed;inset:42% 0;background:inherit}body{background:inherit}</style>
            <section id="list"></section><section id="pane"></section><aside></aside><script>
            let page=0;
            for(let i=0;i<60;i++)list.appendChild(document.createElement('p')).textContent='Conversation '+i;
            function populate(){pane.replaceChildren();for(let i=0;i<80;i++)pane.appendChild(document.createElement('p')).textContent=
            'Message '+(page*80+i)+'   '+('A local conversation fixture for click timing. ').repeat(1+(i+page)%5);}
            populate();addEventListener('click',()=>{page++;populate()});
            </script>
            """ : "";
        File.WriteAllText(html, "<title>DeskPortal drag fixture</title><style>html{background:lime}</style>" + chat +
            "<script>addEventListener('click',()=>{let s=document.documentElement.style;s.background=s.background==='blue'?'lime':'blue'})</script>");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in new[] { "--no-first-run", "--no-default-browser-check", "--disable-background-mode",
            "--disable-background-networking", "--user-data-dir=" + Path.Combine(directory, "profile"), "--app=" + new Uri(html).AbsoluteUri })
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    static async Task OwnDragFixture(Peer peer, CancellationToken token, bool brave, bool flick, bool wide, bool large)
    {
        using var form = new DeskForm(false);
        _ = form.Handle;
        form.UseSettings(new DeskSettings { Features = Sharing.Mouse | Sharing.Windows });
        var session = form.RunSession(peer, token);
        var directory = brave ? Directory.CreateTempSubdirectory("DeskPortal-brave-") : null;
        using var fixture = directory != null ? StartBraveFixture(directory.FullName, large) : Process.Start(Program.StartSelf("--capture-fixture"))!;
        nint hwnd = 0;
        try
        {
            await Until(() => { fixture.Refresh(); hwnd = fixture.MainWindowHandle; return hwnd != 0 && form.ScreenLayout != null &&
                (!brave || Win.Title(hwnd).Contains("DeskPortal drag fixture")); }, "Preparing the drag source", 15);
            form.InstallInputHooks();
            var local = form.ScreenLayout!.Local;
            var work = Screen.AllScreens.MaxBy(s => s.Bounds.Right)!.WorkingArea;
            Point start = default;
            int attempt = 0;
            await Until(() =>
            {
                int y = work.Top + 100 + attempt++ % 3 * Math.Max(1, (work.Height - 400) / 3);
                int width = wide ? work.Width : brave ? Math.Min(1400, work.Width - 260) : 350;
                if (large) width = work.Width - 180;
                int left = wide ? work.Left : flick ? work.Left + 160 : local.Right - width - 160;
                Win.SetWindowPos(hwnd, -1, left, large ? work.Top + 40 : y, width, large ? work.Height - 80 : brave ? Math.Min(850, work.Height - 120) : 220, 0);
                Win.SetForegroundWindow(hwnd);
                Win.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x13);
                try { start = Caption(hwnd, flick ? width / 2 : 60); return true; }
                catch (InvalidOperationException) { return false; }
            }, "Finding an unobstructed title bar on the capture fixture");
            if (brave)
            {
                using var sample = new Bitmap(1, 1);
                using var screen = Graphics.FromImage(sample);
                await Until(() =>
                {
                    var bounds = Win.FrameBounds(hwnd);
                    var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    if (Win.GetAncestor(Win.WindowFromPoint(center), 2) != hwnd) return false;
                    screen.CopyFromScreen(center, Point.Empty, sample.Size);
                    var c = sample.GetPixel(0, 0);
                    return c.G > 215 && c.R < 45 && c.B < 45;
                }, "Waiting for Brave to paint the original page before the press");
            }
            var map = form.ScreenLayout!;
            peer.Send(Kind.ScreenLayout, new DragTarget(start.X - local.Left, start.Y - local.Top, local.Width, local.Height,
                map.Editable ? map.Here : null, map.Editable ? map.Plan.Reverse() : null));
            if (wide)
            {
                using var sample = new Bitmap(1, 1);
                using var screen = Graphics.FromImage(sample);
                foreach (var (distance, signal) in new[] { (120, "following"), (280, "moving") })
                {
                    int left = work.Left - distance;
                    await Until(() =>
                    {
                        var preview = Application.OpenForms.OfType<DragPreview>().FirstOrDefault(w => w.Visible);
                        if (preview == null || Math.Abs(preview.Left - left) > 3 || !Invisible(hwnd)) return false;
                        var visible = Rectangle.Intersect(preview.Bounds, work);
                        screen.CopyFromScreen(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2, 0, 0, sample.Size);
                        var color = sample.GetPixel(0, 0);
                        return color.G > 215 && color.R < 45 && color.B < 45;
                    }, $"Following the held mouse {distance} pixels across the edge before FAST displays a frame", 8);
                    peer.Send(Kind.Hello, signal);
                }
                Console.WriteLine("PASS  The screen-width source followed two held drag positions with live content before the receiver decoded a frame.");
            }
            await Until(() => Invisible(hwnd), "Concealing the shared source", 16);
            await Until(() => !Win.InSystemMove(hwnd) && !Win.Held(Keys.LButton), "Finishing the native caption drag before interacting", 6);
            // A caption drag can itself deliver a button-up to the fixture. Establish
            // a known color after the drop so the remote click must change a later frame.
            if (!brave && (Win.SendMessageTimeout(hwnd, Checks.FixtureColor, 1, 0, 2, 750, out var painted) == 0 || painted != 0x00ff00))
                throw new IOException("The fixture did not repaint after the caption drag");
            using var pixel = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(pixel);
            await Until(() =>
            {
                var preview = Application.OpenForms.OfType<DragPreview>().FirstOrDefault(w => w.Visible && w.Composited);
                if (preview == null) return false;
                var visible = Rectangle.Intersect(preview.Bounds, work);
                if (visible.Width < 25 || visible.Height < 50) return false;
                graphics.CopyFromScreen(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2, 0, 0, pixel.Size);
                var color = pixel.GetPixel(0, 0);
                return color.B > 215 && color.R < 45 && color.G < 45;
            }, "Showing the blue live return preview on the source desktop", 18);
            peer.Send(Kind.Hello, "preview");
            await Until(() => !Invisible(hwnd) && !Win.Held(Keys.LButton), "Returning the original window and releasing the mouse", 14);
            peer.Token.ThrowIfCancellationRequested();
            peer.Send(Kind.Hello, "restored");
            await Until(() => session.IsCompleted, "Completing the transfer test");
            Console.WriteLine("PASS  The source window returned, the button released and the input dispatcher remained responsive.");
        }
        catch (Exception ex)
        {
            var alive = Win.SendMessageTimeout(hwnd, Checks.FixtureColor, 0, 0, 2, 250, out var color) != 0;
            throw new InvalidOperationException($"{ex.Message} Source status: {form.SessionStatus}. Fixture responding: {alive}, color: {color:X6}, native move: {Win.InSystemMove(hwnd)}, button held: {Win.Held(Keys.LButton)}.", ex);
        }
        finally
        {
            if (hwnd != 0) Win.PostMessage(hwnd, 0x10, 0, 0);
            peer.Dispose();
            if (!fixture.HasExited && !await Task.Run(() => fixture.WaitForExit(2500))) { fixture.Kill(entireProcessTree: brave); await fixture.WaitForExitAsync(); }
            if (directory != null)
                try { directory.Delete(true); } catch (IOException ex) { Console.Error.WriteLine(ex.Message); }
            try { await session; } catch (Exception) when (peer.Token.IsCancellationRequested) { }
        }
    }

    static async Task DriveDragFixture(Peer peer, Form ui, CancellationToken token, bool flick, bool wide, bool approach)
    {
        var local = flick ? Screen.PrimaryScreen!.Bounds : SystemInformation.VirtualScreen;
        DisplayInfo[]? displays = flick ? [new("test-primary", new(Point.Empty, local.Size), true)] : null;
        long crossingTime = 0;
        long outlineTime = 0;
        long videoTime = 0;
        Task<double>? firstPixel = null;
        var pictureWindow = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        int beforeCrossing = approach ? 1 : 0;
        int prematurePackets = 0;
        var dispatch = new TaskCompletionSource<Endpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new TaskCompletionSource<DragTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlacement = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool firstFrame = true;
        var drop = new TaskCompletionSource<ViewInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() => peer.Run(async packet =>
        {
            if (Volatile.Read(ref beforeCrossing) != 0 && packet.Kind is Kind.Window or Kind.Frame)
                Interlocked.Increment(ref prematurePackets);
            if (packet.Kind == Kind.ScreenLayout) { target.TrySetResult(packet.Read<DragTarget>()); return; }
            if (packet.Kind == Kind.Hello)
            {
                if (packet.Data[0] == '"' && packet.Read<string>() == "restored") restored.TrySetResult();
                if (packet.Data[0] == '"' && packet.Read<string>() == "preview") preview.TrySetResult();
                if (packet.Data[0] == '"' && packet.Read<string>() == "following") following.TrySetResult();
                if (packet.Data[0] == '"' && packet.Read<string>() == "moving") moving.TrySetResult();
                return;
            }
            var handler = await dispatch.Task.WaitAsync(peer.Token);
            if (packet.Kind == Kind.Frame && firstFrame && (!flick || wide))
            {
                firstFrame = false;
                var id = await firstPlacement.Task.WaitAsync(peer.Token);
                if (wide) await moving.Task.WaitAsync(peer.Token);
                if (!flick || wide) await Task.Delay(350, peer.Token);
                var checkedVisibility = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.BeginInvoke(() =>
                {
                    var w = handler.View(id);
                    bool matches = w is { Visible: false, OpeningBounds: { } outline } && outline == w.Bounds;
                    if (!matches) Console.WriteLine($"Expected a tracking outline. Visible {w?.Visible}, view {w?.Bounds}, outline {w?.OpeningBounds}.");
                    checkedVisibility.TrySetResult(matches);
                });
                if (!await checkedVisibility.Task) throw new IOException("Expected the moving window outline before the first picture.");
                Console.WriteLine("PASS  The outline tracked the incoming window before its first picture.");
            }
            if (packet.Kind == Kind.Frame && packet.Data[16] == VideoCodec.Marker && (firstPixel != null || StallLog.Timing) && videoTime != 0)
            {
                if (StallLog.Timing) StallLog.Stage("crossing to first live packet received", videoTime);
                else
                {
                    await firstPixel!.WaitAsync(TimeSpan.FromSeconds(2), token);
                    Console.WriteLine("PASS  The opening image painted Brave on FAST before the first video frame was released.");
                }
            }
            await handler.Dispatch(packet);
            if (packet.Kind == Kind.Frame)
            {
                long crossed = Interlocked.Exchange(ref crossingTime, 0);
                if (crossed != 0) Console.WriteLine($"First picture decoded {Stopwatch.GetElapsedTime(crossed).TotalMilliseconds:F0} ms after crossing. Center pixel {handler.Frames.GetValueOrDefault(packet.Id)}.");
                if (packet.Data[16] == VideoCodec.Marker)
                {
                    long began = Interlocked.Exchange(ref videoTime, 0);
                    if (began != 0) Console.WriteLine($"First full video frame {Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms after crossing.");
                    live.TrySetResult();
                }
            }
            if (packet.Kind == Kind.Window)
            {
                var view = packet.Read<ViewInfo>();
                if (flick && !wide && !pictureWindow.Task.IsCompleted)
                    ui.BeginInvoke(() =>
                    {
                        if (handler.View(view.Id) is { } w) pictureWindow.TrySetResult(w.Handle);
                    });
                long began = Interlocked.Exchange(ref outlineTime, 0);
                if (began != 0) Console.WriteLine($"First window placement {Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms after the fast crossing.");
                firstPlacement.TrySetResult(view.Id);
                if (view.DropPoint != null) drop.TrySetResult(view);
            }
        }));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, peer.Token);
        token = lifetime.Token;
        peer.Send(Kind.Hello, new DeskHello(2, "RemoteDragTest", local.Width, local.Height, flick,
            Sharing.Mouse | Sharing.Windows, VideoCodec.VideoDecoderMask(), Displays: displays, LayoutNode: Guid.NewGuid(), Organizer: flick));
        try
        {
            var aim = await target.Task.WaitAsync(token);
            var other = new Size(aim.Edge, aim.Height);
            var layout = flick ? new DeskLayout(local, other, true, displays!, aim.Displays!) : new DeskLayout(local, other, false);
            if (flick) layout.Apply(aim.Layout!);
            using var desk = new WindowDesk(ui, peer, layout, Console.WriteLine);
            if (StallLog.Timing) desk.SetCaptureEnabled(true);
            var endpoint = new Endpoint(desk, layout, ui);
            dispatch.SetResult(endpoint);
            void Mouse(int x, int message) => peer.Send(Kind.Mouse, new MouseSample(x, aim.Y, message, 0));
            if (flick)
            {
                using var clip = new SharedClipboard(peer, Console.WriteLine, () => { });
                using var files = new EdgeFiles(layout, peer, clip, Console.WriteLine);
                Point anchor = default;
                using var pointer = new PointerBridge(peer, desk, layout, files, p => anchor = p);
                pointer.SetSharing(Sharing.Mouse | Sharing.Windows);
                var atEdge = new Point(local.Right - 1, local.Top + aim.Y);
                pointer.Physical(atEdge with { X = atEdge.X - 2 }, Win.Move, 0);
                pointer.Physical(atEdge, Win.Move, 0);
                void Delta(int dx) => pointer.Physical(new(anchor.X + dx, anchor.Y), Win.Move, 0);
                Delta(aim.X);
                await Task.Delay(100, token);
                if (approach)
                {
                    pointer.Physical(anchor, Win.LeftDown, 0);
                    await Task.Delay(80, token);
                    Delta(-30);
                    await Task.Delay(550, token);
                    Delta(30);
                    await Task.Delay(80, token);
                    pointer.Physical(anchor, Win.LeftUp, 0);
                    await Task.Delay(300, token);
                    if (Volatile.Read(ref prematurePackets) != 0) throw new IOException("A drag released on the source sent window data.");
                }
                pointer.Physical(anchor, Win.LeftDown, 0);
                await Task.Delay(80, token);
                if (wide)
                {
                    Delta(-120);
                    await following.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    Delta(-160);
                    await moving.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                }
                else
                {
                    Delta(-24);
                    await Task.Delay(45, token);
                    if (approach)
                    {
                        for (int i = 0; i < 8; i++) { Delta(-12); await Task.Delay(65, token); }
                        if (Volatile.Read(ref prematurePackets) != 0)
                            throw new IOException("A local-only drag sent a window or frame before the edge.");
                        Console.WriteLine("PASS  No window or frame reached the peer during the local approach.");
                    }
                }
                if (!wide)
                {
                    long began = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref crossingTime, began);
                    Interlocked.Exchange(ref outlineTime, began);
                    Interlocked.Exchange(ref videoTime, began);
                    if (!StallLog.Timing)
                        firstPixel = Task.Run(async () => MeasureOpeningPixel(await pictureWindow.Task.WaitAsync(token), local, began, token), token);
                }
                Volatile.Write(ref beforeCrossing, 0);
                Delta(-(aim.X - (wide ? 280 : approach ? 120 : 24) + (wide ? 300 : 780)));
                pointer.Physical(anchor, Win.LeftUp, 0);
            }
            else await Task.Run(async () =>
            {
                Mouse(aim.X, Win.Move); await Task.Delay(100, token);
                Mouse(aim.X, Win.LeftDown); await Task.Delay(80, token);
                bool crossed = false;
                for (int x = aim.X + 3; x < aim.Edge + 210; x += 3)
                {
                    // Returning to the mouse's own PC releases its injected buttons on the peer.
                    if (!crossed && x >= aim.Edge) { peer.Send(Kind.ReleaseInput, 0); crossed = true; }
                    Mouse(x, Win.Move);
                    await Task.Delay(4, token);
                }
                Mouse(aim.Edge + 210, Win.LeftUp);
            }, token);
            var dropped = await drop.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            if (flick && dropped.DropPoint != new Point(local.Width - (wide ? 300 : 780), aim.Y))
                throw new IOException($"Fast release landed at {dropped.DropPoint}.");
            if (dropped.Title != "DeskPortal capture fixture" && !dropped.Title.Contains("DeskPortal drag fixture"))
                throw new InvalidOperationException("Another application covered the fixture during the drag.");
            var id = dropped.Id;
            async Task WaitForColor(Func<Color, bool> matches, string stage)
            {
                try { await Until(() => endpoint.Frames.TryGetValue(id, out var c) && matches(c), stage, 10); }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"{stage}. Last decoded center pixel: {endpoint.Frames.GetValueOrDefault(id)}, codec: {endpoint.Codecs.GetValueOrDefault(id)}, connection closed: {peer.Token.IsCancellationRequested}.");
                }
            }
            await WaitForColor(c => c.G > 220 && c.R < 40, "Displaying the post-drop source frame");
            if (firstPixel != null) Console.WriteLine($"First green desktop pixel on FAST {await firstPixel:F0} ms after crossing.");
            var view = Application.OpenForms.OfType<StreamWindow>().Single(w => w.Id == id);
            if (firstPixel != null && view.Handle != await pictureWindow.Task)
                throw new IOException("The live stream replaced the preview's receiving window.");
            if (view.OpeningBounds.HasValue) throw new IOException("Opening outline remained after the first picture.");
            var center = Win.Packed(new(view.ClientSize.Width / 2, view.ClientSize.Height / 2));
            if (StallLog.Timing)
            {
                for (int i = 0; i < 9; i++)
                {
                    if (i == 1) await live.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
                    if (i > 0) await Task.Delay(i == 1 ? 1200 : 250, token);
                    var changed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool blue = i % 2 == 0;
                    endpoint.AfterFrame = (window, pixel) =>
                    {
                        if (window == id && (blue ? pixel.B > 215 && pixel.G < 45 : pixel.G > 215 && pixel.B < 45))
                            changed.TrySetResult(Stopwatch.GetTimestamp());
                    };
                    long click = Stopwatch.GetTimestamp();
                    Win.PostMessage(view.Handle, Win.LeftDown, 1, center);
                    Win.PostMessage(view.Handle, Win.LeftUp, 0, center);
                    long endAt = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    Console.WriteLine($"Timing FAST {(i == 0 ? "opening" : "steady")} click to changed presented frame {Stopwatch.GetElapsedTime(click, endAt).TotalMilliseconds:F1} ms");
                }
                endpoint.AfterFrame = null;
            }
            else { Win.PostMessage(view.Handle, Win.LeftDown, 1, center); Win.PostMessage(view.Handle, Win.LeftUp, 0, center); }
            await WaitForColor(c => c.B > 220 && c.R < 40, "Clicking the shared window");
            using var sample = new Bitmap(1, 1);
            using var desktop = Graphics.FromImage(sample);
            Color displayed = Color.Empty;
            Point sampled = default;
            int copyError = 0;
            if (!StallLog.Timing) try { await Until(() =>
            {
                var visible = Rectangle.Intersect(view.RectangleToScreen(view.ClientRectangle), local);
                if (visible.Width <= 0 || visible.Height <= 0) return false;
                sampled = new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2);
                try { desktop.CopyFromScreen(sampled, Point.Empty, sample.Size); copyError = 0; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 6) { copyError = ex.NativeErrorCode; return false; }
                displayed = sample.GetPixel(0, 0);
                return displayed.B > 215 && displayed.G < 45 && displayed.R < 45;
            }, "Displaying the clicked frame on the receiving monitor"); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"{ex.Message}. View {view.Bounds}, monitor {local}, sample {sampled} {displayed}, screen read error {copyError}, covered={Win.GetAncestor(Win.WindowFromPoint(sampled), 2) != view.Handle}.", ex);
            }
            var from = Caption(view.Handle);
            if (!desk.Route(from, Win.LeftDown, false)) throw new IOException("Could not grab the projected title bar");
            var times = new List<double>();
            var movementGaps = new List<double>();
            var lastPosition = view.Location;
            long lastMove = Stopwatch.GetTimestamp();
            var end = new Point(flick ? local.Right + 180 : local.Left - 180, from.Y);
            int step = flick ? 12 : -3;
            for (int x = from.X; flick ? x < end.X : x > end.X; x += step)
            {
                long started = Stopwatch.GetTimestamp();
                desk.Route(new Point(x, from.Y), Win.Move, x < local.Left || x >= local.Right);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await Task.Delay(10, token);
                if (view.Location != lastPosition)
                {
                    movementGaps.Add(Stopwatch.GetElapsedTime(lastMove).TotalMilliseconds);
                    lastMove = Stopwatch.GetTimestamp();
                    lastPosition = view.Location;
                }
            }
            await preview.Task.WaitAsync(TimeSpan.FromSeconds(6), token);
            desk.Route(end, Win.LeftUp, true);
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            times.Sort();
            Console.WriteLine($"Return drag: {times.Count} moves, p95 {times[(int)(times.Count * .95)]:F2} ms, max {times[^1]:F2} ms inside the routing callback.");
            if (movementGaps.Count < 2) throw new IOException("The projected window did not follow the slow return drag");
            movementGaps.Sort();
            Console.WriteLine($"Observed {movementGaps.Count} window position changes, p95 gap {movementGaps[(int)(movementGaps.Count * .95)]:F1} ms, max {movementGaps[^1]:F1} ms.");
            Console.WriteLine("PASS  Remote pointer drag crossed the peer edge, displayed live frames, delivered a click and returned the window.");
        }
        finally
        {
            peer.Dispose();
            try { await reader; } catch (Exception) when (peer.Token.IsCancellationRequested) { }
        }
    }
    static double MeasureOpeningPixel(nint window, Rectangle screen, long began, CancellationToken token)
    {
        using var sample = new Bitmap(1, 1);
        using var desktop = Graphics.FromImage(sample);
        while (Stopwatch.GetElapsedTime(began).TotalSeconds < 10)
        {
            token.ThrowIfCancellationRequested();
            var bounds = Rectangle.Intersect(Win.Bounds(window), screen);
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            if (!bounds.IsEmpty && Win.IsWindowVisible(window) && Win.GetAncestor(Win.WindowFromPoint(point), 2) == window)
            {
                try { desktop.CopyFromScreen(point, Point.Empty, sample.Size); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 6)
                { Thread.Sleep(4); continue; }
                var c = sample.GetPixel(0, 0);
                if (c.G > 215 && c.R < 45 && c.B < 45)
                    return Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            }
            Thread.Sleep(4);
        }
        throw new TimeoutException("Observing Brave's first green pixel on the receiving desktop.");
    }
}
