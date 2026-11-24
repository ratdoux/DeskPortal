using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DeskLink;

static partial class WindowChecks
{
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);

    // Exercise the receiver's own non-client message loop with a real TLS peer.
    // The sender must keep running even if the receiver's UI is stuck tracking a button.
    public static int CheckCaptionInput(bool wheel = false)
    {
        int result = 1;
        var oldCursor = Cursor.Position;
        var foreground = Win.GetForegroundWindow();
        using var form = new DeskForm(false) { TopMost = true };
        form.UseSettings(new DeskSettings { Features = Sharing.Mouse | Sharing.Windows });
        form.Shown += async (_, _) =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var invitation = new Invitation(IPAddress.Loopback, 0);
                var accept = invitation.Accept(deadline.Token);
                using var sender = await Invitation.Join("127.0.0.1", invitation.Code, deadline.Token, invitation.ListeningPort);
                using var receiver = await accept;
                var drain = Task.Run(() => sender.Run(_ => Task.CompletedTask));
                var session = form.RunSession(receiver, deadline.Token);
                var desktop = SystemInformation.VirtualScreen;
                sender.Send(Kind.Hello, new DeskHello(2, "CaptionTest", desktop.Width, desktop.Height, true, Sharing.Mouse | Sharing.Windows));
                await Until(() => form.ScreenLayout != null, "Receiving display information");
                var hwnd = form.Handle;
                form.Activate();
                try
                {
                    if (wheel)
                    {
                        form.InstallInputHooks();
                        await CheckWheelDelivery(form, sender, desktop);
                    }
                    else
                    {
                        Point start = default;
                        await Until(() =>
                        {
                            Win.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x43);
                            try { start = Caption(hwnd); return true; }
                            catch (InvalidOperationException) { return false; }
                        }, "Exposing the test window's title bar");
                        await Task.Run(() => DriveCaption(sender, hwnd, desktop, start));
                    }
                }
                finally
                {
                    sender.Dispose(); receiver.Dispose();
                    try { await Task.WhenAll(drain, session); } catch (Exception) when (receiver.Token.IsCancellationRequested) { }
                }
                result = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally { form.Close(); }
        };
        try { Application.Run(form); }
        finally { Cursor.Position = oldCursor; if (foreground != 0) Win.SetForegroundWindow(foreground); }
        return result;
    }

    static void DriveCaption(Peer sender, nint hwnd, Rectangle desktop, Point start)
    {
        void Send(Point p, int message) => sender.Send(Kind.Mouse, new MouseSample(p.X - desktop.Left, p.Y - desktop.Top, message, 0));
        void Aim(Point p)
        {
            Send(p, Win.Move);
            if (!SpinWait.SpinUntil(() => Near(p), 1500) || Win.GetAncestor(Win.WindowFromPoint(p), 2) != hwnd)
                throw new Exception($"The test title bar is not under the remote pointer. Target {p}, actual {Cursor.Position}, desktop {desktop}.");
        }
        bool pressed = false;
        void Down(Point p)
        {
            pressed = true;
            Send(p, Win.LeftDown);
            if (!SpinWait.SpinUntil(() => Win.Held(Keys.LButton), 900)) throw new Exception("Windows did not receive the remote press.");
            Thread.Sleep(90);
        }
        void Up(Point p)
        {
            Send(p, Win.LeftUp);
            if (!SpinWait.SpinUntil(() => !Win.Held(Keys.LButton), 1200))
                throw new Exception("The remote release did not reach Windows while tracking the title bar.");
            pressed = false;
        }
        try
        {
            var box = Win.Bounds(hwnd);
            Aim(start); Down(start);
            var end = start + new Size(64, 28);
            var elapsed = Stopwatch.StartNew();
            Send(start + new Size(12, 6), Win.Move); Thread.Sleep(30); Send(end, Win.Move);
            bool moved = SpinWait.SpinUntil(() => Win.Bounds(hwnd).Left >= box.Left + 40, 900);
            var dragMs = elapsed.ElapsedMilliseconds;
            Up(end);
            if (!moved) throw new Exception("The remote title-bar drag did not move the window before release.");
            Console.WriteLine($"PASS  Remote title-bar drag moved the receiver window in {dragMs} ms.");

            var buttons = Win.CaptionButtons(hwnd);
            if (buttons.IsEmpty) throw new Exception("Windows did not supply the caption button bounds.");
            var min = new Point(buttons.Left + buttons.Width / 6, buttons.Top + buttons.Height / 2);
            Aim(min); Down(min); Up(min);
            if (!SpinWait.SpinUntil(() => IsIconic(hwnd), 900)) throw new Exception("The remote minimize click did not minimize the receiver.");
            var after = min - new Size(48, 32);
            Send(after, Win.Move);
            if (!SpinWait.SpinUntil(() => Near(after), 900)) throw new Exception("Remote pointer stopped responding after minimize.");
            Console.WriteLine("PASS  Remote minimize released the button and the pointer continued moving.");

            Win.ShowWindow(hwnd, 9);
            buttons = Win.CaptionButtons(hwnd);
            min = new Point(buttons.Left + buttons.Width / 6, buttons.Top + buttons.Height / 2);
            Aim(min); Down(min);
            sender.Dispose();
            if (!SpinWait.SpinUntil(() => !Win.Held(Keys.LButton), 1500))
                throw new Exception("Disconnect left the remote caption button held.");
            pressed = false;
            Console.WriteLine("PASS  Disconnect during a caption press released the mouse.");
        }
        finally
        {
            // Unblock the test window on a failing build, before closing it.
            if (pressed) Win.mouse_event(Win.MouseFlag(Win.LeftUp), 0, 0, 0, Win.Tag);
        }
    }
    static bool Near(Point target)
    {
        var actual = Cursor.Position;
        return Math.Abs(actual.X - target.X) <= 2 && Math.Abs(actual.Y - target.Y) <= 2;
    }
}
