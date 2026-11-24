using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskLink;

sealed class CaptureDeviceLease : SafeHandleZeroOrMinusOneIsInvalid
{
    public CaptureDeviceLease() : base(true) { }
    [DllImport("DeskCapture.dll")] internal static extern CaptureDeviceLease PrepareCaptureDevice();
    [DllImport("DeskCapture.dll")] static extern void ReleaseCaptureDevice(nint lease);
    protected override bool ReleaseHandle() { ReleaseCaptureDevice(handle); return true; }
}

sealed class CaptureStream : IDisposable
{
    // Keep the Windows capture service ready while window sharing is enabled.
    // This HWND contains only DeskPortal's own transparent 16 px surface. Its
    // frames are never read, encoded or sent, and it cannot take focus or input.
    internal sealed class CapturePrimer : Form
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x080000A0; return p; }
        }
        public CapturePrimer()
        {
            ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Bounds = new Rectangle(Screen.PrimaryScreen?.Bounds.Location ?? Point.Empty, new Size(16, 16));
            Opacity = 0; Enabled = false;
            Show();
            nint window = Handle;
            var thread = new Thread(() =>
            {
                // Open and close on the same COM apartment. The speculative path
                // declines silently if Windows hasn't granted borderless capture.
                try
                {
                    if (OpenCapture(window, true, out var capture) < 0) return;
                    try { closed.Task.GetAwaiter().GetResult(); }
                    finally { CloseCapture(capture); }
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
            }) { IsBackground = true, Name = "Window capture readiness" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) closed.TrySetResult();
            base.Dispose(disposing);
        }
    }
    sealed class FramePacer : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, bool resume);
        readonly EventWaitHandle timer = new(false, EventResetMode.AutoReset);
        readonly WaitHandle[] handles;
        readonly bool precise;
        public FramePacer(CancellationToken stop)
        {
            var handle = CreateWaitableTimerEx(0, null, 2, 0x100002); // High resolution, synchronize and modify.
            precise = !handle.IsInvalid;
            if (precise) { timer.SafeWaitHandle.Dispose(); timer.SafeWaitHandle = handle; }
            else handle.Dispose();
            handles = [stop.WaitHandle, timer];
        }
        public void Wait(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return;
            long due = -delay.Ticks;
            if (precise && SetWaitableTimer(timer.SafeWaitHandle, ref due, 0, 0, 0, false))
                WaitHandle.WaitAny(handles);
            else handles[0].WaitOne((int)Math.Ceiling(delay.TotalMilliseconds));
        }
        public void Dispose() => timer.Dispose();
    }
    [DllImport("DeskCapture.dll", EntryPoint = "OpenWindowCapture")] static extern int OpenCapture(nint hwnd, bool preparing, out nint capture);
    [DllImport("DeskCapture.dll", EntryPoint = "ReadWindowTexture")] static extern int CaptureTexture(nint capture, int waitMs, out nint texture, out int width, out int height);
    [DllImport("DeskCapture.dll")] static extern int ReadCapturePixels(nint capture, out nint data, out int stride);
    [DllImport("DeskCapture.dll")] static extern int ReadOpeningImage(nint capture, int level, nint pixels, int width, int height, int stride);
    [DllImport("DeskCapture.dll")] static extern void CloseCapture(nint capture);
    [DllImport("DeskCapture.dll")] static extern bool BorderlessCapture(nint capture);
    [DllImport("DeskCapture.dll")] static extern long WindowFrameTime(nint capture);
    [DllImport("DeskCapture.dll")] static extern nint CaptureEncoderDevice(nint capture, out int width, out int height);
    [DllImport("DeskCapture.dll")] static extern nint BorrowEncoderDevice();
    [DllImport("DeskCapture.dll")] static extern void CaptureSetupTimes(nint capture, out int pool, out int border, out int start);
    static readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(e => e.FormatID == ImageFormat.Jpeg.Guid);
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource movement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object openingGate = new();
    readonly Func<byte[], CancellationToken, long> sendOpening;
    readonly CancellationToken lifetime;
    bool liveStarted, openingSent;
    public Task Completion => finished.Task;
    long sharedAt;
    sealed record ClickReceipt(long Id, long Received, long Dispatched);
    ClickReceipt? pendingClick;
    public void NoteClick(long id, long received)
    {
        if (id > 0 && received != 0) Volatile.Write(ref pendingClick, new(id, received, Stopwatch.GetTimestamp()));
    }
    public void Share()
    {
        if (Interlocked.CompareExchange(ref sharedAt, Stopwatch.GetTimestamp(), 0) == 0) handoff.TrySetResult();
        movement.TrySetResult();
    }
    public void OfferOpeningPicture(Task<DragOpeningPicture?> pending, Size windowSize)
    {
        // Even a completed snapshot must not enqueue network traffic on the pointer hook.
        _ = Task.Run(async () =>
        {
            try
            {
                var picture = await pending.WaitAsync(TimeSpan.FromMilliseconds(400), lifetime);
                if (picture == null) return;
                lock (openingGate)
                {
                    if (liveStarted || lifetime.IsCancellationRequested || !picture.CanShare(windowSize)) return;
                    long wait = sendOpening(picture.Jpeg, lifetime);
                    openingSent = true;
                    Meter.Record(picture.Jpeg.Length, "Drag snapshot", picture.Size, 0, wait);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or
                ObjectDisposedException or System.Threading.Channels.ChannelClosedException) { }
        });
    }
    public bool Borderless { get; private set; }
    public volatile StreamOptions Options;
    public volatile bool WantPreview;
    public void PrepareForMovement() => movement.TrySetResult();
    // Set while the receiving PC has this window minimized. Capture keeps the newest picture ready.
    public volatile bool Paused;
    public volatile string Description = "";
    public FrameMeter Meter { get; } = new();

    public CaptureStream(nint hwnd, Func<byte[], CancellationToken, long> send, Action<byte[]> preview, Action<string> onFailure,
        bool nativeDialog, StreamOptions options, int decoders, Func<CancellationToken, long>? waitForSpace = null, string? label = null,
        bool prepare = false)
    {
        Options = options;
        sendOpening = send;
        lifetime = stop.Token;
        Meter.Label = label;
        if (!prepare) Share();
        var worker = new Thread(() =>
        {
            nint capture = 0;
            using var video = new VideoWriter(decoders);
            var gpuVideo = new VideoWriter(decoders);
            using var pacer = new FramePacer(stop.Token);
            (Size Size, StreamOptions Options)? unsupported = null;
            StreamOptions? last = null;
            long lastPreview = 0;
            long creditWait = 0;
            long captureStarted = Stopwatch.GetTimestamp();
            long captureOpened = captureStarted;
            bool sawTexture = false;
            void ObserveTexture(nint texture)
            {
                if (sawTexture || texture == 0) return;
                sawTexture = true;
                StallLog.Stage("pool ready to first texture", captureOpened);
                Meter.ObserveStage("Waiting for first WGC texture", Stopwatch.GetTimestamp() - captureOpened);
            }
            bool previewSent = false, missed = false;
            byte[]? previousDialog = null;
            Bitmap ReadBitmap(nint pixels, int w, int h, int stride)
            {
                if (stride == 0) Marshal.ThrowExceptionForHR(ReadCapturePixels(capture, out pixels, out stride));
                return new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, pixels);
            }
            void SendPreview(nint source, int w, int h, int stride)
            {
                long start = Stopwatch.GetTimestamp();
                using var bitmap = ReadBitmap(source, w, h, stride);
                const int maxSide = 960;
                if (Math.Max(w, h) <= maxSide) preview(Still(bitmap, FrameCodec.Jpeg, 80));
                else
                {
                    double scale = maxSide / (double)Math.Max(w, h);
                    using var thumb = new Bitmap(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)),
                        PixelFormat.Format32bppRgb);
                    using (var graphics = Graphics.FromImage(thumb))
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        graphics.DrawImage(bitmap, new Rectangle(Point.Empty, thumb.Size), 0, 0, w, h, GraphicsUnit.Pixel);
                    }
                    preview(Still(thumb, FrameCodec.Jpeg, 80));
                }
                Meter.ObserveStage("Local preview", Stopwatch.GetTimestamp() - start);
            }
            void Publish(nint source, int w, int h, int stride, StreamOptions settings)
            {
                var click = Volatile.Read(ref pendingClick);
                long sampledAt = Stopwatch.GetTimestamp();
                // WGC can deliver a queued image captured before input was posted.
                // Its SystemRelativeTime is QPC in 100 ns units.
                if (click != null && stride == 0 && WindowFrameTime(capture) < Stopwatch.GetElapsedTime(0, click.Dispatched).Ticks)
                    click = null;
                if (w < 1 || h < 1 || (long)w * h > 32_000_000) throw new IOException("Window dimensions exceed the capture limit.");
                bool still = settings.Codec is FrameCodec.Png or FrameCodec.Jpeg;
                if (last == null)
                {
                    bool needOpening;
                    lock (openingGate) { liveStarted = true; needOpening = !openingSent; }
                    Meter.ObserveStage("Handoff to first capture frame", Stopwatch.GetTimestamp() - Volatile.Read(ref sharedAt));
                    if (needOpening && !still && stride == 0)
                    {
                        long begin = Stopwatch.GetTimestamp();
                        var opening = OpeningImage(capture, w, h, out var size);
                        if (opening != null)
                        {
                            long elapsed = Stopwatch.GetTimestamp() - begin;
                            long wait = send(opening, stop.Token);
                            Meter.Record(opening.Length, "Opening image", size, elapsed, wait);
                        }
                    }
                }
                byte[]? frame = null;
                long setup = 0;
                long start = Stopwatch.GetTimestamp();
                if (still)
                {
                    gpuVideo.Dispose(); video.Dispose();
                    using var bitmap = ReadBitmap(source, w, h, stride);
                    frame = Still(bitmap, settings.Codec, settings.JpegQuality);
                    Description = settings.Codec == FrameCodec.Png ? "PNG" : "JPEG";
                }
                else
                {
                    var configuration = (new Size(w, h), settings);
                    if (stride == 0 && unsupported != configuration)
                    {
                        try
                        {
                            frame = gpuVideo.Encode(settings, source, w, h, 0);
                            setup = gpuVideo.SetupTicks;
                            Description = gpuVideo.Description;
                            video.Dispose();
                        }
                        catch (IOException)
                        {
                            unsupported = configuration;
                            gpuVideo.Dispose();
                            setup = Stopwatch.GetTimestamp() - start;
                        }
                    }
                    if (frame == null)
                    {
                        gpuVideo.Dispose();
                        nint pixels = source;
                        int pitch = stride;
                        if (pitch == 0) Marshal.ThrowExceptionForHR(ReadCapturePixels(capture, out pixels, out pitch));
                        frame = video.Encode(settings, pixels, w, h, pitch);
                        setup += video.SetupTicks;
                        Description = video.Description;
                    }
                }
                long encodeTime = Stopwatch.GetTimestamp() - start;
                Meter.ObserveStage("Encoder setup", setup);
                Meter.ObserveStage("Encode", encodeTime - setup);
                if (click != null && ReferenceEquals(Interlocked.CompareExchange(ref pendingClick, null, click), click) &&
                    !still && (decoders & VideoCodec.ClickFeedback) != 0)
                {
                    double sourceMs = Stopwatch.GetElapsedTime(click.Received, sampledAt).TotalMilliseconds;
                    frame = VideoCodec.WithClick(frame, click.Id, sourceMs);
                    Meter.Click("Input arrival to fresh capture", sourceMs);
                    StallLog.Stage("click dispatch to frame queued", click.Dispatched);
                }
                // The receiving PC can display this frame while the local drag preview is built.
                long queueWait = send(frame, stop.Token);
                if (last == null)
                {
                    StallLog.Stage("handoff to first live packet queued", Volatile.Read(ref sharedAt));
                    StallLog.Stage("first frame encode including setup", start);
                }
                Meter.ObserveStage("Send queue", queueWait);
                if (WantPreview && Environment.TickCount64 - lastPreview >= 50)
                {
                    if (still) preview(frame);
                    else SendPreview(source, w, h, stride);
                    lastPreview = Environment.TickCount64;
                    previewSent = true;
                }
                Meter.Record(frame.Length, Description, new(w, h), encodeTime - setup, queueWait + creditWait, setup);
                creditWait = 0;
                last = settings;
            }
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                bool useBufferedFrame = false;
                if (!nativeDialog)
                {
                    void PrepareEncoder()
                    {
                        var device = CaptureEncoderDevice(capture, out int w, out int h);
                        if (w > 0 && h > 0 && (long)w * h <= 32_000_000)
                            gpuVideo.PrepareDevice(Options, device, w, h, stop.Token);
                    }
                    bool localOnly = !handoff.Task.IsCompleted;
                    // The sender already owns this device while paired. Open the encoder
                    // in parallel with WGC once the selected window actually starts moving.
                    nint paired = BorrowEncoderDevice();
                    if (paired != 0)
                    {
                        try
                        {
                            var frame = Win.FrameBounds(hwnd);
                            if (frame.Width > 0 && frame.Height > 0 && (long)frame.Width * frame.Height <= 32_000_000)
                                gpuVideo.PrepareDevice(Options, paired, frame.Width, frame.Height, lifetime, movement.Task);
                        }
                        finally { Marshal.Release(paired); }
                    }
                    int rc = OpenCapture(hwnd, localOnly, out capture);
                    StallLog.Stage("open capture", captureStarted);
                    if (localOnly)
                    {
                        bool preparedEncoder = false;
                        captureOpened = Stopwatch.GetTimestamp();
                        // WGC has a two-frame pool. Drain it during the approach so a long drag
                        // opens on the latest content. Only the capture worker touches the texture.
                        while (rc >= 0 && !handoff.Task.IsCompleted)
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            if (movement.Task.IsCompleted && !preparedEncoder)
                            {
                                PrepareEncoder();
                                preparedEncoder = true;
                            }
                            rc = CaptureTexture(capture, 0, out var texture, out _, out _);
                            if (rc >= 0)
                            {
                                ObserveTexture(texture);
                                handoff.Task.Wait(16, stop.Token);
                            }
                        }
                        handoff.Task.Wait(stop.Token);
                        stop.Token.ThrowIfCancellationRequested();
                        useBufferedFrame = rc >= 0;
                        if (rc < 0)
                        {
                            gpuVideo.Dispose();
                            if (capture != 0) { CloseCapture(capture); capture = 0; }
                            captureStarted = Stopwatch.GetTimestamp();
                            sawTexture = false;
                            rc = OpenCapture(hwnd, false, out capture);
                        }
                    }
                    Marshal.ThrowExceptionForHR(rc);
                    PrepareEncoder();
                    if (!useBufferedFrame) captureOpened = Stopwatch.GetTimestamp();
                    CaptureSetupTimes(capture, out int pool, out int border, out int start);
                    Meter.ObserveStage("Open window capture", captureOpened - captureStarted,
                        $"Window and pool {pool} ms, border permission {border} ms, start {start} ms");
                    Borderless = BorderlessCapture(capture);
                }
                while (!stop.IsCancellationRequested)
                {
                    long tick = Stopwatch.GetTimestamp();
                    // Take the newest WGC frame after the receiver has room for it.
                    long wait = waitForSpace?.Invoke(stop.Token) ?? 0;
                    Meter.ObserveStage("Receiver credit", wait);
                    creditWait += wait;
                    if (!WantPreview) previewSent = false;
                    if (!Win.IsWindow(hwnd)) throw new IOException("The original window was closed.");
                    var settings = Options;
                    // A change that arrives while paused is sent when the window is shown again.
                    bool paused = Paused;
                    if (nativeDialog)
                    {
                        settings = settings with { FrameRate = Math.Min(10, settings.FrameRate) };
                        var size = Win.Bounds(hwnd).Size;
                        if (size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height <= 32_000_000)
                        {
                            using var image = PrintDialog(hwnd, size);
                            var bits = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                            try
                            {
                                bool changed = Changed(bits, ref previousDialog);
                                if (paused) missed |= changed;
                                else if (changed || missed || last != settings)
                                {
                                    Publish(bits.Scan0, size.Width, size.Height, bits.Stride, settings);
                                    missed = false;
                                }
                            }
                            finally { image.UnlockBits(bits); }
                        }
                    }
                    else
                    {
                        int rc = CaptureTexture(capture, useBufferedFrame ? 0 : 100, out var texture, out int w, out int h);
                        useBufferedFrame = false;
                        Marshal.ThrowExceptionForHR(rc);
                        if (stop.IsCancellationRequested) break;
                        ObserveTexture(texture);
                        if (paused) missed |= rc == 0;
                        else if (texture != 0 && (rc == 0 || missed || last != settings))
                        {
                            Publish(texture, w, h, 0, settings);
                            missed = false;
                        }
                        else if (texture != 0 && WantPreview && !previewSent)
                        {
                            SendPreview(texture, w, h, 0);
                            previewSent = true; lastPreview = Environment.TickCount64;
                        }
                        if (last == null && !paused && Stopwatch.GetElapsedTime(Volatile.Read(ref sharedAt)).TotalSeconds > 6)
                            throw new IOException("Windows did not deliver a frame for this window.");
                    }
                    pacer.Wait(TimeSpan.FromSeconds(1d / settings.FrameRate) - Stopwatch.GetElapsedTime(tick));
                }
            }
            catch (Exception ex) { if (!stop.IsCancellationRequested) onFailure(ex.Message); }
            finally
            {
                lock (openingGate) liveStarted = true;
                try
                {
                    if (capture != 0) CloseCapture(capture);
                    gpuVideo.Dispose();
                }
                finally
                {
                    lock (stop) { finished.TrySetResult(); stop.Dispose(); }
                }
            }
        }) { IsBackground = true, Name = "Window capture" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }
    static byte[]? OpeningImage(nint capture, int width, int height, out Size size)
    {
        int level = 0;
        while ((Math.Max(width, height) >> level) > 960) level++;
        size = new(Math.Max(1, width >> level), Math.Max(1, height >> level));
        using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        var bits = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, image.PixelFormat);
        try
        {
            if (ReadOpeningImage(capture, level, bits.Scan0, size.Width, size.Height, bits.Stride) < 0) return null;
        }
        finally { image.UnlockBits(bits); }
        return Still(image, FrameCodec.Jpeg, 82);
    }
    internal static byte[] Still(Bitmap bitmap, FrameCodec codec, int quality)
    {
        using var stream = new MemoryStream();
        if (codec == FrameCodec.Png) bitmap.Save(stream, ImageFormat.Png);
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(stream, jpeg, parameters);
        }
        if (stream.Length > 12 * 1024 * 1024) throw new IOException("Encoded window exceeds 12 MiB per frame. Reduce the window size or select a video codec.");
        return stream.ToArray();
    }
    static unsafe bool Changed(BitmapData bits, ref byte[]? previous)
    {
        var pixels = new ReadOnlySpan<byte>((void*)bits.Scan0, checked(bits.Stride * bits.Height));
        if (previous != null && pixels.SequenceEqual(previous)) return false;
        if (previous?.Length != pixels.Length) previous = new byte[pixels.Length];
        pixels.CopyTo(previous);
        return true;
    }
    static Bitmap PrintDialog(nint hwnd, Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                if (Win.SendMessageTimeout(hwnd, 0x0317, hdc, 0x1e, 2, 250, out _) == 0)
                    throw new IOException("The dialog did not respond to capture.");
            }
            finally { graphics.ReleaseHdc(hdc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    public void Dispose()
    {
        lock (stop) { if (!finished.Task.IsCompleted) stop.Cancel(); }
    }
}
