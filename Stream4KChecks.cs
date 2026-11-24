using System.Diagnostics;
using System.Net;

namespace DeskLink;

static partial class WindowChecks
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);
    sealed class Animation4K : Form
    {
        readonly Stopwatch animation = Stopwatch.StartNew();
        bool clicked;
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { clicked = !clicked; Invalidate(); }
            base.OnMouseUp(e);
        }
        public Animation4K()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            Text = "DeskPortal 4K streaming check";
            DoubleBuffered = true;
            MaximumSize = new Size(4096, 2304);
            Bounds = new Rectangle(0, 0, 3840, 2160);
        }
        protected override bool ShowWithoutActivation => true;
        protected override void OnPaint(PaintEventArgs e)
        {
            int t = (int)(animation.ElapsedMilliseconds * 0.35) % 3840;
            e.Graphics.Clear(Color.MidnightBlue);
            for (int x = -192 + t % 192; x < Width; x += 192)
                for (int y = 0; y < Height; y += 128)
                    e.Graphics.FillRectangle(((y / 128) % 2 == 0) ? Brushes.RoyalBlue : Brushes.Teal, x, y, 112, 70);
            e.Graphics.FillRectangle(Brushes.Gold, t, 200, 140, 540);
            e.Graphics.FillRectangle(clicked ? Brushes.Lime : Brushes.Red, 4, 4, 32, 32);
        }
    }

    // The same fixture is built against the original and accelerated implementations.
    public static int Run4K(bool listen, string address, int port, FrameCodec codec = FrameCodec.Av1) => Run(async ui =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var invite = listen ? new Invitation(IPAddress.Parse(address), port) : null;
        if (invite != null) { Console.WriteLine(invite.Code); Console.Out.Flush(); }
        using var peer = invite != null ? await invite.Accept(stop.Token) :
            await Invitation.Join(address, await Console.In.ReadLineAsync(stop.Token) ?? throw new EndOfStreamException(), stop.Token, port);
        var id = new Guid("34448eb7-e331-4dcc-b154-fd9e0e23294a");
        using var incoming = new ReceivedVideo();
        using var view = listen ? new StreamWindow(peer, new(id, "DeskPortal 4K receiving check", 0, 0, 3840, 2160), _ => {}, _ => {}) : null;
        if (view != null) view.PlaceBounds(new(0, 0, 3840, 2160), true, true, false);
        var signals = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nint source = 0;
        int darkest = 255, brightest = 0, warmingFrames = 0;
        bool measuring = false, screenVerified = false;
        Color screenSample = Color.Empty;
        async Task WaitFor(Task task) { await await Task.WhenAny(task, failed.Task).WaitAsync(stop.Token); }
        async Task Stage(string name)
        {
            var next = signals.Reader.ReadAsync(stop.Token).AsTask();
            await WaitFor(next);
            if (await next != name) throw new IOException("Unexpected benchmark stage.");
        }
        Task Receive(Packet packet)
        {
            if (packet.Kind == Kind.Hello) signals.Writer.TryWrite(packet.Read<string>());
            if (packet.Kind == Kind.WindowInput && !listen)
            {
                var input = packet.Read<WindowEvent>();
                if (input.Id == id && input.Message is Win.LeftDown or Win.LeftUp)
                    Win.PostMessage(source, (uint)input.Message, input.Modifiers & 127, Win.Packed(new Point(1920, 1080)));
            }
            if (packet.Kind != Kind.Frame) return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.BeginInvoke(async () =>
            {
                try
                {
                    nint target = view!.PreparePresentation(packet.Data);
                    await incoming.Decode(packet.Data, target);
                    if (!first.Task.IsCompleted && packet.Data[16] != VideoCodec.Marker)
                    {
                        view.Display(incoming);
                        done.TrySetResult(); return;
                    }
                    if (incoming.FrameSize != new Size(3840, 2160)) throw new IOException($"Expected 3840x2160, got {incoming.FrameSize}.");
                    if (!first.Task.IsCompleted)
                    {
                        var image = incoming.Image!;
                        Color red = image.GetPixel(16, 16);
                        var corner = image.GetPixel(3700, 2058);
                        if (red.R - red.B < 120 || red.R - red.G < 120 || corner.B < 65)
                        {
                            if (++warmingFrames >= 30)
                                throw new IOException($"Incomplete 4K capture after {warmingFrames} frames. Marker {red}, far corner {corner}, center {image.GetPixel(1920,1080)}.");
                            done.TrySetResult(); return;
                        }
                    }
                    // This part of the 4K surface is outside the test laptop's physical panel.
                    if (!measuring && (brightest - darkest < 45))
                    {
                        int blue = incoming.Image!.GetPixel(3700, 2058).B;
                        darkest = Math.Min(darkest, blue); brightest = Math.Max(brightest, blue);
                    }
                    view.Display(incoming, target != 0);
                    if (!screenVerified && !measuring)
                    {
                        using var pixel = new Bitmap(1, 1);
                        using var g = Graphics.FromImage(pixel);
                        g.CopyFromScreen(view.PointToScreen(new(16, 16)), Point.Empty, pixel.Size);
                        var color = screenSample = pixel.GetPixel(0, 0);
                        screenVerified = color.R > color.B + 120 && color.R > color.G + 120;
                    }
                    first.TrySetResult();
                    done.TrySetResult();
                }
                catch (Exception e) { failed.TrySetException(e); done.TrySetException(e); }
            });
            return done.Task;
        }
        var run = Task.Run(() => peer.Run(Receive));
        void Report(WindowPerformance w) => Console.WriteLine($"4K {w.Codec} | {w.Pixels.Width}x{w.Pixels.Height} | {w.Fps:F2} fps | paint {w.PaintedFps:F2} fps | codec {w.CodecMs:F2} ms | queue {w.QueueMs:F2} ms | {w.Mbps:F3} Mbps");
        try
        {
            if (listen)
            {
                await WaitFor(first.Task);
                await Task.Delay(2500, stop.Token);
                if (!screenVerified) throw new IOException($"Missing on-screen red marker. Pixel {screenSample}, view {view!.Bounds}, client {view.ClientSize}, direct {incoming.Presented}, covering class {Win.Class(Win.GetAncestor(Win.WindowFromPoint(view.PointToScreen(new(16,16))),2))}, own {Win.GetAncestor(Win.WindowFromPoint(view.PointToScreen(new(16,16))),2)==view.Handle}.");
                measuring = true;
                incoming.Meter.Sample(id, "4K", false, Stopwatch.GetTimestamp(), view!.TakePaintedFrames());
                peer.Send(Kind.Hello, "start");
                await Task.Delay(12000, stop.Token);
                if (brightest - darkest < 45) throw new IOException("The decoded 4K animation did not move.");
                Report(incoming.Meter.Sample(id, "4K", false, Stopwatch.GetTimestamp(), view.TakePaintedFrames()));
                peer.Send(Kind.Hello, "end");
                await Stage("reported");
                var delays = new List<double>();
                var marker = view.PointToScreen(new(16, 16));
                nint handle = view.Handle;
                var center = Win.Packed(new Point(view.ClientSize.Width / 2, view.ClientSize.Height / 2));
                // Sample the desktop from a worker at millisecond resolution. This thread delivers the
                // frames being timed, and its default timer would round each wait up to about 15 ms.
                timeBeginPeriod(1);
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        bool green = (i & 1) == 0;
                        var start = Stopwatch.GetTimestamp();
                        Win.PostMessage(handle, Win.LeftDown, 1, center);
                        Win.PostMessage(handle, Win.LeftUp, 0, center);
                        delays.Add(await Task.Run(() =>
                        {
                            using var sample = new Bitmap(1, 1);
                            using var desktop = Graphics.FromImage(sample);
                            while (true)
                            {
                                desktop.CopyFromScreen(marker, Point.Empty, sample.Size);
                                var pixel = sample.GetPixel(0, 0);
                                if ((green ? pixel.G - pixel.R : pixel.R - pixel.G) > 120) return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                if (Stopwatch.GetElapsedTime(start).TotalSeconds > 3) throw new IOException("Click produced no visible color change.");
                                Thread.Sleep(1);
                            }
                        }, stop.Token));
                        await Task.Delay(80, stop.Token);
                    }
                }
                finally { timeEndPeriod(1); }
                // Keep the order. A single slow click is otherwise lost in the summary.
                Console.WriteLine($"4K click to desktop pixel, ms for each click in order: {string.Join(", ", delays.Select(d => d.ToString("F1")))}.");
                delays.Sort();
                Console.WriteLine($"4K click to desktop pixel, 10 clicks: median {(delays[4]+delays[5])/2:F1} ms, maximum {delays[^1]:F1} ms.");
                peer.Send(Kind.Hello, "finished");
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (EndOfStreamException) { }
            }
            else
            {
                using var window = new Animation4K();
                window.Show();
                source = window.Handle;
                Win.SetWindowPos(window.Handle, 0, 0, 0, 3840, 2160, 0x14);
                // Use the same layered backing surface as a handed-off application.
                var state = SourceState.Read(window.Handle, Point.Empty, false);
                state.Conceal();
                using var timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += (_, _) => window.Invalidate(); timer.Start();
                using var capture = new CaptureStream(window.Handle, (frame, token) => peer.Frame(id, frame, token),
                    _ => {}, e => failed.TrySetException(new IOException(e)), false, new(codec, 60, Mbps: 10), peer.VideoDecoders,
                    token => peer.WaitForFrameSpace(id, token));
                await Stage("start");
                capture.Meter.Sample(id, "4K", true, Stopwatch.GetTimestamp());
                await Stage("end");
                Report(capture.Meter.Sample(id, "4K", true, Stopwatch.GetTimestamp()));
                peer.Send(Kind.Hello, "reported");
                await Stage("finished");
            }
        }
        finally { peer.Dispose(); try { await run; } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { } }
    });
}
