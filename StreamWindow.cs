using System.Runtime.InteropServices;

namespace DeskLink;

sealed class StreamWindow : Form
{
    // Only the narrow rim is a window. A cold capture can take several compositor
    // frames to start, and the desktop underneath must stay visible and clickable.
    sealed class OpeningOutline : Form
    {
        public OpeningOutline()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.White; TransparencyKey = Color.Magenta;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x080000A0; return p; }
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            var rim = new Region(ClientRectangle);
            var inside = Rectangle.Inflate(ClientRectangle, -2, -2);
            if (inside.Width > 0 && inside.Height > 0) rim.Exclude(inside);
            Region = rim;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawRectangle(Pens.Black, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        }
    }
    OpeningOutline? outline;
    internal Rectangle? OpeningBounds => outline?.Visible == true ? outline.Bounds : null;
    // DXGI owns this child's pixels. Hit testing, focus and window controls belong to the form.
    sealed class VideoSurface : Control
    {
        public VideoSurface()
        {
            Enabled = false; TabStop = false; Visible = false;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = -1; return; }
            base.WndProc(ref m);
        }
    }
    readonly VideoSurface surface = new();
    readonly Peer peer;
    readonly Action<StreamWindow> returnHome;
    Bitmap? image;
    CaptionRegion[]? caption;
    bool exactCaption;
    readonly Dictionary<int, int> pressedKeys = [];
    bool ending, ready, pendingPaint, scaleOpeningPicture;
    long clickTime;
    public void ObserveClick(long id, double sourceMs, FrameMeter meter)
    {
        if (id != clickTime || clickTime == 0) return;
        double total = System.Diagnostics.Stopwatch.GetElapsedTime(clickTime).TotalMilliseconds;
        clickTime = 0;
        meter.Click("Click to fresh video", total, $"Source input and capture {sourceMs:F1} ms");
        if (StallLog.Timing) Console.WriteLine($"Timing {Environment.MachineName} correlated click {total:F1} ms, source arrival to fresh capture {sourceMs:F1} ms");
    }
    long painted;
    readonly bool? resizable;
    readonly System.Windows.Forms.Timer resizeTimer = new() { Interval = 150 };
    bool placed, applyingBounds, nativePixels, placementVisible, hasPicture;
    Point? pendingDragPosition;
    nint foregroundAtDrop;
    Size lastRequest, inFlight;
    public bool AllowsZoom => resizable != false;
    public bool NeedsFirstPicture => placementVisible && !hasPicture;
    public Guid Id { get; }
    public Size SourceSize { get; set; }
    public int CaptionHeight => Math.Max(24, SystemInformation.CaptionHeight);
    public StreamWindow(Peer peer, ViewInfo info, Action<StreamWindow> returnHome, Action<string> status)
    {
        this.peer = peer; this.returnHome = returnHome; Id = info.Id; SourceSize = new(info.Width, info.Height);
        resizable = info.Resizable;
        resizeTimer.Tick += (_, _) => SendSize();
        SetCaption(info.Caption, info.ExactCaption);
        Text = info.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(34, 36, 41);
        Controls.Add(surface);
    }
    protected override bool ShowWithoutActivation => true;
    public void SetCaption(CaptionRegion[]? regions, bool exact) { caption = regions; exactCaption = exact && regions != null; }
    protected override bool IsInputKey(Keys keyData) => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Style |= 0x20000; // WS_MINIMIZEBOX
            if (resizable == true) p.Style |= 0x50000; // WS_THICKFRAME | WS_MAXIMIZEBOX
            return p;
        }
    }
    public void PlaceBounds(Rectangle bounds, bool visible, bool dropped, bool tile, ReceivedVideo? incoming = null)
    {
        applyingBounds = true;
        try
        {
            placementVisible = visible;
            Bounds = bounds;
            // The video channel can beat the placement message on a prepared drag.
            // Use that picture on the first show and skip creating a temporary rim.
            if (incoming is { Presented: false }) Display(incoming);
            if (!visible) { outline?.Hide(); Hide(); }
            else if (hasPicture) ShowPicture();
            else if (Owner == null)
            {
                outline ??= new OpeningOutline();
                outline.Bounds = bounds;
                if (!outline.Visible) outline.Show();
            }
        }
        finally { applyingBounds = false; }
        if (!dropped || placed) return;
        placed = true;
        FinishDrag();
        if (tile) QueueSize();
        else lastRequest = ClientSize;
    }
    void ShowPicture()
    {
        if (!placementVisible || !hasPicture || Visible || ending) return;
        TopMost = true;
        Show();
        if (image != null) Update();
        outline?.Dispose(); outline = null;
        if (placed) FinishDrag();
    }
    public void MoveWithPointer(Point position)
    {
        bool queued = pendingDragPosition.HasValue;
        pendingDragPosition = position;
        if (!queued) BeginInvoke(ApplyDragPosition);
    }
    void ApplyDragPosition()
    {
        if (pendingDragPosition is not { } position) return;
        pendingDragPosition = null;
        if (ending || IsDisposed) return;
        if (!TopMost) TopMost = true;
        Win.Position(Handle, position);
    }
    public void FinishDrag()
    {
        ApplyDragPosition();
        if (!Visible) return;
        TopMost = true;
        Win.SetForegroundWindow(Handle);
        foregroundAtDrop = Win.GetForegroundWindow();
    }
    public void ReleaseDragPriority()
    {
        foregroundAtDrop = 0;
        if (TopMost) TopMost = false;
    }
    public void CheckForeground(nint foreground)
    {
        if (foregroundAtDrop != 0 && foreground != 0 && foreground != foregroundAtDrop)
            ReleaseDragPriority();
    }
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        QueueSize();
        if (surface != null) SizeSurface();
    }
    void QueueSize()
    {
        if (!NeedsSize) return;
        nativePixels = true;
        if (ready && inFlight.IsEmpty) resizeTimer.Start();
        Invalidate();
    }
    void SendSize()
    {
        resizeTimer.Stop();
        if (!NeedsSize || !ready || !inFlight.IsEmpty) return;
        inFlight = lastRequest = ClientSize;
        peer.Send(Kind.ResizeWindow, new WindowSize(Id, inFlight.Width, inFlight.Height));
    }
    bool NeedsSize => placed && resizable == true && !applyingBounds && !ending && WindowState != FormWindowState.Minimized &&
        ClientSize.Width > 0 && ClientSize.Height > 0 && ClientSize != lastRequest;
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SendSize(); }
    public void AcceptSize(Size actual)
    {
        if (inFlight.IsEmpty || actual.Width is < 1 or > 16384 || actual.Height is < 1 or > 16384) return;
        var requested = inFlight;
        inFlight = Size.Empty;
        SourceSize = actual;
        if (WindowState != FormWindowState.Minimized && ClientSize == requested && actual != requested)
        {
            // Respect an application's native size limits. An older reply must
            // never undo a newer drag or a restore from maximized.
            applyingBounds = true;
            try
            {
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                Size = actual; lastRequest = ClientSize;
            }
            finally { applyingBounds = false; }
        }
        QueueSize();
    }
    void SizeSurface() => surface.Size = nativePixels && !SourceSize.IsEmpty ? SourceSize : ClientSize;
    bool displayUnavailable;
    public nint PreparePresentation(byte[] packet)
    {
        if (packet.Length < 20 || packet[16] != VideoCodec.Marker) return 0;
        if (packet[18] == 1) displayUnavailable = false;
        if (displayUnavailable || !Visible || !IsHandleCreated || WindowState == FormWindowState.Minimized) return 0;
        SizeSurface();
        return surface.Handle;
    }
    // Called when this window can show a picture again after being minimized.
    public Action<StreamWindow>? Uncovered;
    bool minimized;
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool now = WindowState == FormWindowState.Minimized;
        if (now == minimized) return;
        minimized = now;
        // A minimized projection needs no frames. The source resumes with its current picture.
        if (peer.ViewVisibility && !ending) peer.Send(Kind.ViewVisibility, new ViewVisibility(Id, !now));
        if (!now) Uncovered?.Invoke(this);
    }
    public void Display(ReceivedVideo stream, bool offered = false)
    {
        if (!stream.Presented)
        {
            if (offered) displayUnavailable = true;
            // A frame decoded while this window had nowhere to show it stays on the GPU.
            // Read it back only once it can be seen.
            if (stream.Held && (!placementVisible || WindowState == FormWindowState.Minimized)) { MarkReady(); return; }
            if (stream.Image is { } frame) Display(frame);
            return;
        }
        image = null; pendingPaint = false;
        SizeSurface(); surface.Visible = true;
        hasPicture = true; ShowPicture();
        painted++;
        MarkReady();
    }
    public void Display(Bitmap frame)
    {
        surface.Visible = false;
        if (!ReferenceEquals(image, frame))
        {
            scaleOpeningPicture = !hasPicture && (frame.Width < SourceSize.Width || frame.Height < SourceSize.Height);
            pendingPaint = true;
        }
        image = frame; hasPicture = true; ShowPicture(); Invalidate();
        MarkReady();
    }
    void MarkReady() { if (!ready) { ready = true; peer.Send(Kind.WindowPlaced, Id); QueueSize(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (image == null) return;
        if (!nativePixels || scaleOpeningPicture) e.Graphics.DrawImage(image, ClientRectangle);
        else if (image.Size != SourceSize && image.Size == VideoCodec.EncodedSize(SourceSize))
            e.Graphics.DrawImage(image, new Rectangle(Point.Empty, SourceSize));
        else e.Graphics.DrawImageUnscaled(image, Point.Empty);
        if (pendingPaint) { painted++; pendingPaint = false; }
    }
    public long TakePaintedFrames() { long count = painted; painted = 0; return count; }
    protected override void WndProc(ref Message m)
    {
        if (resizable == true && m.Msg == 0x83) { m.Result = 0; return; } // Keep the captured chrome as the entire client.
        if (resizable == true && m.Msg == 0x24) // WM_GETMINMAXINFO
        {
            base.WndProc(ref m);
            var monitor = Screen.FromHandle(m.HWnd);
            var work = monitor.WorkingArea;
            var limits = Marshal.PtrToStructure<Win.MinMaxInfo>(m.LParam);
            limits.MaxPosition = new(work.Left - monitor.Bounds.Left, work.Top - monitor.Bounds.Top);
            limits.MaxSize = new(work.Width, work.Height);
            Marshal.StructureToPtr(limits, m.LParam, false);
            return;
        }
        if (m.Msg == 0x84)
        {
            int packed = unchecked((int)m.LParam);
            var p = PointToClient(new Point((short)packed, (short)(packed >> 16)));
            if (placed && resizable == true && WindowState == FormWindowState.Normal && ClientRectangle.Contains(p))
            {
                int grip = LogicalToDeviceUnits(6);
                bool left = p.X < grip, right = p.X >= ClientSize.Width - grip;
                bool top = p.Y < grip, bottom = p.Y >= ClientSize.Height - grip;
                int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                if (hit != 0) { m.Result = hit; return; }
            }
            // Leave source caption buttons clickable.
            var x = ScaleCoord(p.X, ClientSize.Width);
            var y = ScaleCoord(p.Y, ClientSize.Height);
            bool inCaption = caption == null ? p.Y >= 0 && y * (long)SourceSize.Height / 65535 < CaptionHeight :
                caption.Any(r => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom);
            if (ClientRectangle.Contains(p) && inCaption && (exactCaption || (65535 - x) * (long)SourceSize.Width / 65535 > 3 * SystemInformation.CaptionButtonSize.Width))
            { m.Result = 2; return; }
        }
        if (m.Msg is Win.Move or Win.LeftDown or Win.LeftUp or Win.RightDown or Win.RightUp or
            Win.MiddleDown or Win.MiddleUp or Win.Wheel or Win.HorizontalWheel or 0x203 or 0x206 or Win.XDown or Win.XUp)
        {
            int packed = unchecked((int)m.LParam);
            var p = new Point((short)packed, (short)(packed >> 16));
            if (m.Msg is Win.Wheel or Win.HorizontalWheel) p = PointToClient(p);
            if (m.Msg is Win.LeftDown or Win.RightDown or Win.MiddleDown or 0x203 or 0x206) { Activate(); Focus(); Capture = true; }
            if (m.Msg is Win.LeftUp or Win.RightUp or Win.MiddleUp) Capture = false;
            long click = 0;
            if (m.Msg == Win.LeftUp && (peer.VideoDecoders & VideoCodec.ClickFeedback) != 0)
                click = clickTime = System.Diagnostics.Stopwatch.GetTimestamp();
            peer.Send(Kind.WindowInput, new WindowEvent(Id, m.Msg, ScaleCoord(p.X, Width), ScaleCoord(p.Y, Height),
                (short)((long)m.WParam >> 16), (int)((long)m.WParam & 0x7f), click));
            m.Result = 0; return;
        }
        if (m.Msg is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int key = (int)m.WParam, bits = unchecked((int)m.LParam);
            if (m.Msg is 0x100 or 0x104) pressedKeys[key] = bits; else pressedKeys.Remove(key);
            peer.Send(Kind.WindowInput, new WindowEvent(Id, m.Msg, 0, 0, key, bits));
            m.Result = 0; return;
        }
        base.WndProc(ref m);
    }
    protected override void OnDeactivate(EventArgs e)
    {
        foreach (var (key, bits) in pressedKeys)
            peer.Send(Kind.WindowInput, new WindowEvent(Id, 0x101, 0, 0, key, bits | unchecked((int)0xc0000000)));
        pressedKeys.Clear();
        base.OnDeactivate(e);
    }
    static int ScaleCoord(int x, int size) => Math.Clamp((int)(65535L * x / Math.Max(1, size - 1)), 0, 65535);
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ending) { e.Cancel = true; returnHome(this); }
        base.OnFormClosing(e);
    }
    public void End() { ending = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { ending = true; resizeTimer.Dispose(); outline?.Dispose(); }
        base.Dispose(disposing);
    }
}
