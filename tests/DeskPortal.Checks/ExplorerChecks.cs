using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DeskLink;

static partial class WindowChecks
{
    static nint ExplorerAt(string folder)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        try
        {
            dynamic items = shell.Windows();
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    dynamic? item = items.Item(i);
                    if (item == null) continue;
                    try
                    {
                        if (Uri.TryCreate((string)item.LocationURL, UriKind.Absolute, out var path) && path.IsFile &&
                            string.Equals(path.LocalPath, folder, StringComparison.OrdinalIgnoreCase)) return (nint)(long)item.HWND;
                    }
                    catch (COMException) { }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(shell); }
        return 0;
    }
    static async Task CheckExplorer(Peer peer, bool receiver, Control ui)
    {
        var desktop = SystemInformation.VirtualScreen;
        var screens = DisplayInfo.Read(desktop);
        var inventory = new TaskCompletionSource<DisplayInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<Endpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Send(Kind.Hello,screens);
        var reader = Task.Run(() => peer.Run(async packet =>
        {
            if (!inventory.Task.IsCompleted && packet.Kind == Kind.Hello) { inventory.SetResult(packet.Read<DisplayInfo[]>()); return; }
            var endpoint = await ready.Task.WaitAsync(peer.Token);
            await endpoint.Dispatch(packet);
        }));
        var other = await inventory.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var layout = new DeskLayout(desktop,other.Select(s => s.Bounds).Aggregate(Rectangle.Union).Size,!receiver,screens,other);
        var receiving = receiver ? screens : other;
        var sending = receiver ? other : screens;
        var targetScreen = receiving.FirstOrDefault(s => !s.Primary) ?? receiving[0];
        var source = sending.FirstOrDefault(s => s.Primary) ?? sending[0];
        int x = 0;
        var arrangement = receiving.Where(s => s != targetScreen).Select(s => (Screen:s,Receiver:true))
            .Concat(sending.Where(s => s != source).Append(source).Select(s => (Screen:s,Receiver:false))).Append((Screen:targetScreen,Receiver:true));
        var left = new List<TilePosition>(); var right = new List<TilePosition>();
        foreach (var tile in arrangement)
        {
            (tile.Receiver ? right : left).Add(new(tile.Screen.Id,x,0));
            x += tile.Screen.Bounds.Width;
        }
        var plan = new ScreenPlan(left.ToArray(),right.ToArray());
        layout.Apply(receiver ? plan.Reverse() : plan);
        var local = layout.Tiles.First(t => t.Here && t.Display.Id == (receiver ? targetScreen.Id : source.Id)).Native;
        if (local.Width < 1280 || local.Height < 680) throw new InvalidOperationException("Explorer check needs a 1280 × 680 display.");
        peer.CaptionUpdates = true;
        using var desk = new WindowDesk(ui, peer, layout, Console.WriteLine);
        using var clipboard = new SharedClipboard(peer, Console.WriteLine, () => { });
        using var files = new EdgeFiles(layout, peer, clipboard, Console.WriteLine);
        using var pointer = new PointerBridge(peer, desk, layout, files, _ => { });
        pointer.SetSharing(Sharing.Windows);
        var endpoint = new Endpoint(desk, layout, ui);
        ready.SetResult(endpoint);
        // Windows keeps a local title-bar press until the window reaches the shared edge, where
        // DeskLink takes the drag over. A projected window is DeskLink's own from the press.
        void DragAcross(Point caption, bool projected = false)
        {
            bool captured = pointer.Physical(caption, Win.LeftDown, 0);
            if (captured != projected || (!projected && !desk.CaptionPressed))
                throw new Exception(projected ? "The projected title bar did not start a drag." : "A local title-bar press must stay with Windows.");
            var seam = new Point(layout.Right ? local.Right - 1 : local.Left, caption.Y);
            if (!pointer.Physical(seam, Win.Move, 0)) throw new Exception("The title-bar drag stopped at the shared edge.");
            var anchor = new Point(local.Left + local.Width / 2, local.Top + local.Height / 2);
            pointer.Physical(anchor + new Size(layout.Right ? 910 : -450, 0), Win.Move, 0);
            pointer.Physical(anchor, Win.LeftUp, 0);
        }
        string? folder = null;
        nint hwnd = 0;
        try
        {
            if (!receiver)
            {
                folder = Directory.CreateTempSubdirectory("DeskLink-Explorer-").FullName;
                using var launch = Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{folder}\"") { UseShellExecute = true });
                await Until(() => (hwnd = ExplorerAt(folder)) != 0, "Opening the temporary Explorer folder");
                if (Win.IsZoomed(hwnd)) Win.ShowWindow(hwnd, 9);
                Win.SetWindowPos(hwnd, -1, local.Left + 20, local.Top + 45, 1090, 510, 0x10);
                await Task.Delay(650);
                var header = Win.ReadExplorerHeader(hwnd) ?? throw new Exception("Expected modern Explorer's native caption child.");
                int row = header.Header.Top + header.Header.Height / 2;
                var strip = Enumerable.Range(header.Header.Left, header.Header.Width).Where(x => x % 8 == 0)
                    .Select(x => new Point(x, row)).ToArray();
                Point NativeHit(int code)
                {
                    var hits = strip.Where(p => Win.WindowFromPoint(p) == header.Handle && Win.Hit(header.Handle, p) == code).ToArray();
                    if (hits.Length == 0) throw new Exception($"Missing Explorer native hit {code}.");
                    return hits[hits.Length / 2];
                }
                var caption = NativeHit(2); var maximize = NativeHit(9);
                var tab = new Point(header.Frame.Left + 75, row);
                var root = AutomationElement.FromHandle(hwnd);
                var tabView = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TabView"));
                static Point Button(AutomationElement parent, string id)
                {
                    var control = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    var bounds = control.Current.BoundingRectangle;
                    return new((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
                }
                var add = Button(tabView, "AddButton");
                foreach (var point in new[] { tab, maximize })
                {
                    if (pointer.Physical(point, Win.LeftDown, 0) || desk.CaptionPressed) throw new Exception("A tab or caption button started a window drag.");
                    pointer.Physical(point, Win.LeftUp, 0);
                }
                if (Win.Hit(hwnd, caption) == 2) throw new Exception("Fixture did not reproduce the outer-window hit-test mismatch.");
                string Wire(Point p) => $"{(p.X - header.Frame.X) * 65535L / (header.Frame.Width - 1)},{(p.Y - header.Frame.Y) * 65535L / (header.Frame.Height - 1)}";
                DragAcross(caption);
                await Until(() => Invisible(hwnd), "Receiving Explorer’s first live frame");
                peer.Send(Kind.Hello, $"points,{Wire(caption)},{Wire(tab)},{Wire(maximize)},{Wire(add)}");
                await Until(() => endpoint.Stages.Any(s => s.StartsWith("max-size,")), "Maximizing projected Explorer");
                var target = endpoint.Stages.Single(s => s.StartsWith("max-size,")).Split(',');
                var large = new Size(int.Parse(target[1]), int.Parse(target[2]));
                await Until(() => Win.FrameBounds(hwnd).Size == large, "Explorer laying out at the receiving monitor's size");
                var expanded = Win.ReadExplorerHeader(hwnd)!;
                int buttonY = expanded.Header.Top + expanded.Header.Height / 2;
                var restoreHits = Enumerable.Range(expanded.Header.Left, expanded.Header.Width).Where(x => x % 6 == 0)
                    .Select(x => new Point(x, buttonY)).Where(p => Win.Hit(expanded.Handle, p) == 9).ToArray();
                var restore = restoreHits[restoreHits.Length / 2];
                peer.Send(Kind.Hello, $"restore,{(restore.X - expanded.Frame.Left) * 65535L / (large.Width - 1)},{(restore.Y - expanded.Frame.Top) * 65535L / (large.Height - 1)}");
                var tabs = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                await Until(() => tabView.FindAll(TreeScope.Descendants, tabs).Count == 2, "Opening a new Explorer tab from the projection");
                var secondTab = tabView.FindAll(TreeScope.Descendants, tabs)[1];
                var now = Win.FrameBounds(hwnd);
                var close = Button(secondTab, "CloseButton");
                close.Offset(header.Frame.Left - now.Left, header.Frame.Top - now.Top);
                peer.Send(Kind.Hello, $"close,{Wire(close)}");
                await Until(() => tabView.FindAll(TreeScope.Descendants, tabs).Count == 1, "Closing the new Explorer tab from the projection");
                peer.Send(Kind.Hello, "one-tab");
                await Until(() => !Invisible(hwnd) && endpoint.Stages.Contains("returned"), "Restoring the original Explorer window");
                Console.WriteLine("PASS  File Explorer crossed the right edge and the original folder returned.");
                peer.Send(Kind.Hello, "done");
                await Until(() => endpoint.Stages.Contains("ack"), "Completing Explorer check");
            }
            else
            {
                await Until(() => endpoint.Stages.Any(s => s.StartsWith("points,")), "Receiving Explorer's title bar");
                var view = Application.OpenForms.OfType<StreamWindow>().Single();
                if (desk.LatestImage(view.Id) == null) throw new Exception("Missing live Explorer view.");
                if (!string.Equals(Screen.FromControl(view).DeviceName,targetScreen.Id,StringComparison.Ordinal))
                    throw new Exception("Explorer arrived on the wrong physical display.");
                Console.WriteLine($"PASS  Explorer landed on {targetScreen.Id}, primary={targetScreen.Primary}, with the source screen beside it.");
                int[] Coords(string stage) => endpoint.Stages.Single(s => s.StartsWith(stage + ",")).Split(',').Skip(1).Select(int.Parse).ToArray();
                var coords = Coords("points");
                Point Position(int i) => view.PointToScreen(new((int)(coords[i] * (long)(view.Width - 1) / 65535), (int)(coords[i+1] * (long)(view.Height - 1) / 65535)));
                if (Win.Hit(view.Handle, Position(0)) != 2 || Win.Hit(view.Handle, Position(2)) == 2 || Win.Hit(view.Handle, Position(4)) == 2)
                    throw new Exception("Projected Explorer caption, tab or button hit test differed from the source.");
                void Click(int index)
                {
                    var point = Win.Packed(view.PointToClient(Position(index)));
                    Win.PostMessage(view.Handle, Win.LeftDown, 1, point);
                    Win.PostMessage(view.Handle, Win.LeftUp, 0, point);
                }
                Click(4);
                await Until(() => view.WindowState == FormWindowState.Maximized, "Maximizing the received Explorer view");
                var full = Screen.FromControl(view).WorkingArea.Size;
                peer.Send(Kind.Hello, $"max-size,{full.Width},{full.Height}");
                bool AtSize(Size size) => desk.LatestImage(view.Id) is { } image &&
                    Math.Abs(image.Width - size.Width) <= 1 && Math.Abs(image.Height - size.Height) <= 1;
                await Until(() => endpoint.Stages.Any(s => s.StartsWith("restore,")) && AtSize(full), "Receiving Explorer at full resolution");
                var firstPoints = coords;
                coords = Coords("restore");
                Click(0);
                coords = firstPoints;
                await Until(() => view.WindowState == FormWindowState.Normal, "Restoring the Explorer view");
                await Until(() => AtSize(view.ClientSize), "Receiving the restored Explorer layout");
                Click(6);
                await Until(() => endpoint.Stages.Any(s => s.StartsWith("close,")), "Opening a second Explorer tab");
                var original = coords;
                coords = Coords("close");
                await Until(() => Win.Hit(view.Handle, Position(0)) != 2, "Updating the projected tab strip");
                Click(0);
                coords = original;
                await Until(() => endpoint.Stages.Contains("one-tab") && Win.Hit(view.Handle, Position(0)) == 2, "Restoring the drag area after closing the tab");
                Win.SetWindowPos(view.Handle, -1, 0, 0, 0, 0, 0x13);
                DragAcross(Position(0), projected: true);
                await Until(() => view.IsDisposed, "Returning Explorer from the receiving PC");
                Console.WriteLine("PASS  Received live Explorer, used its maximize and new/close tab controls, and dragged it home.");
                peer.Send(Kind.Hello, "returned");
                await Until(() => endpoint.Stages.Contains("done"), "Completing Explorer check");
                peer.Send(Kind.Hello, "ack");
                await Until(() => reader.IsCompleted, "Peer finished");
            }
        }
        finally
        {
            desk.RestoreAll();
            if (hwnd != 0 && Win.IsWindow(hwnd)) Win.PostMessage(hwnd, 0x10, 0, 0);
            if (folder != null && Directory.Exists(folder)) Directory.Delete(folder);
            peer.Dispose();
            try { await reader; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
