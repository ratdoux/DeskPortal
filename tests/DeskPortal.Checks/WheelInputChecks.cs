using System.Diagnostics;

namespace DeskLink;

static partial class WindowChecks
{
    sealed class WheelTarget : Form
    {
        public readonly List<(int Axis, int Delta)> Strokes = [];
        public int Reports;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is Win.Wheel or Win.HorizontalWheel)
            {
                int delta = (short)(m.WParam.ToInt64() >> 16);
                Reports++;
                if (Strokes.Count > 0 && Strokes[^1].Axis == m.Msg && Math.Sign(Strokes[^1].Delta) == Math.Sign(delta))
                    Strokes[^1] = (m.Msg, Strokes[^1].Delta + delta);
                else Strokes.Add((m.Msg, delta));
                return;
            }
            base.WndProc(ref m);
        }
    }

    static async Task CheckWheelDelivery(Form owner, Peer sender, Rectangle desktop)
    {
        using var target = new WheelTarget { Text = "DeskPortal wheel test", Width = 340, Height = 200,
            StartPosition = FormStartPosition.CenterParent, TopMost = true };
        target.Show(owner);
        target.Activate();
        var center = target.PointToScreen(new Point(150, 85));
        var hwnd = target.Handle;
        var expected = new List<(int Axis, int Delta)>();
        const int count = 240;
        var heartbeat = Stopwatch.StartNew();
        var gaps = new List<double>();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) => { gaps.Add(heartbeat.Elapsed.TotalMilliseconds); heartbeat.Restart(); };
        timer.Start();
        await Task.Run(() =>
        {
            foreach (int axis in new[] { Win.Wheel, Win.HorizontalWheel })
            foreach (int delta in new[] { -15, 15, -120, 120 })
            {
                expected.Add((axis, count * delta));
                for (int i = 0; i < count; i++)
                {
                    // Alternate between sustained scrolling and bursts, with occasional movement.
                    var p = center + new Size(i / 24 % 2, 0);
                    if (i % 24 == 0 && Win.GetAncestor(Win.WindowFromPoint(p), 2) != hwnd)
                        throw new InvalidOperationException("Wheel test target lost the pointer.");
                    sender.Send(Kind.Mouse, new MouseSample(p.X - desktop.Left, p.Y - desktop.Top, axis, delta));
                    if (i % 2 == 0) Thread.Sleep(2);
                }
            }
        });
        try { await Until(() => target.Strokes.SequenceEqual(expected), "Receiving complete ordered wheel strokes"); }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Received {target.Reports} wheel messages in {heartbeat.Elapsed.TotalMilliseconds:F0} ms since the last UI tick. " +
                $"Expected {string.Join(", ", expected)}, received {string.Join(", ", target.Strokes)}. " +
                $"Target foreground: {Win.GetForegroundWindow() == hwnd}. Peer disconnected: {sender.Token.IsCancellationRequested}.", ex);
        }
        timer.Stop();
        gaps.Add(heartbeat.Elapsed.TotalMilliseconds);
        if (gaps.Count < 5 || gaps.Max() > 1000)
            throw new TimeoutException($"Receiver UI stalled during scrolling. Maximum heartbeat gap {gaps.Max():F0} ms.");
        Console.WriteLine($"PASS  {count * expected.Count} wheel reports sent, {target.Reports} Windows wheel messages. " +
            $"Every signed delta arrived in order on both axes. Maximum UI heartbeat gap {gaps.Max():F0} ms.");
    }
}
