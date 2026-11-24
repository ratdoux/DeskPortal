using System.Runtime.InteropServices;

namespace DeskLink;

sealed class DragPreview : Form
{
    byte[]? last;
    readonly FrameDecoder frames;
    readonly nint source;
    nint thumbnail;
    Size thumbnailSize;
    bool registrationTried;
    public bool Composited => thumbnail != 0;
    [StructLayout(LayoutKind.Sequential)]
    struct ThumbnailProperties
    {
        public uint Flags;
        public Win.Rect Destination, Source;
        public byte Opacity;
        public int Visible, ClientOnly;
    }
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(nint thumbnail);
    public DragPreview(nint source, Action<string> status)
    {
        this.source = source;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        frames = new FrameDecoder(0, () => Invalidate(), e => status($"Could not display the drag preview. {e.Message}"));
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20; return p; }
    }
    public void Present(Rectangle bounds, byte[]? jpeg)
    {
        if (Bounds != bounds) Bounds = bounds;
        if (!registrationTried)
        {
            registrationTried = true;
            if (DwmRegisterThumbnail(Handle, source, out var registered) >= 0) thumbnail = registered;
        }
        if (Composited && thumbnailSize != ClientSize)
        {
            var properties = new ThumbnailProperties { Flags = 1 | 8, Visible = 1,
                Destination = new Win.Rect { R = ClientSize.Width, B = ClientSize.Height } };
            if (DwmUpdateThumbnailProperties(thumbnail, properties) >= 0) thumbnailSize = ClientSize;
            else ReleaseThumbnail();
        }
        if (!Composited && jpeg != null && jpeg != last)
        {
            last = jpeg;
            frames.Submit(jpeg);
        }
        if (!Visible && (Composited || frames.Image != null)) Show();
    }
    void ReleaseThumbnail()
    {
        if (thumbnail != 0) DwmUnregisterThumbnail(thumbnail);
        thumbnail = 0;
        thumbnailSize = Size.Empty;
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseThumbnail();
        registrationTried = false;
        base.OnHandleDestroyed(e);
    }
    protected override void OnPaint(PaintEventArgs e) { if (!Composited && frames.Image is { } image) e.Graphics.DrawImage(image, ClientRectangle); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = -1; return; }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing) { if (disposing) frames.Dispose(); base.Dispose(disposing); }
}
