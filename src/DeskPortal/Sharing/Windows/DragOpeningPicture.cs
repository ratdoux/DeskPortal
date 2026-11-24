using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskLink;

// An optional still of the selected HWND, independent of WGC and its encoder.
// PrintWindow can block inside the owning application. Keep at most one request
// outstanding, and never wait for it on the input, capture or network threads.
sealed record DragOpeningPicture(byte[] Jpeg, Size Size, Size WindowSize, long TakenAt, nint Window, uint ThreadId, uint ProcessId)
{
    static int active;
    [DllImport("user32.dll")] static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    static bool Available(nint window, uint thread, uint pid) => thread != 0 && Win.IsWindowVisible(window) &&
        Win.GetWindowThreadProcessId(window, out uint current) == thread && current == pid &&
        GetWindowDisplayAffinity(window, out uint affinity) && affinity == 0;
    public bool CanShare(Size size) => WindowSize == size && Stopwatch.GetElapsedTime(TakenAt).TotalSeconds <= 1 &&
        Available(Window, ThreadId, ProcessId);

    public static Task<DragOpeningPicture?> Start(nint window)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return Task.FromResult<DragOpeningPicture?>(null);
        uint thread = Win.GetWindowThreadProcessId(window, out uint pid);
        return Task.Run(() =>
        {
            try
            {
                if (!Available(window, thread, pid)) return null;
                var bounds = Win.Bounds(window);
                var frame = Rectangle.Intersect(bounds, Win.FrameBounds(window));
                if (frame.Width < 2 || frame.Height < 2 || (long)bounds.Width * bounds.Height > 16_000_000) return null;
                long begin = Stopwatch.GetTimestamp();
                using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(full))
                {
                    var dc = graphics.GetHdc();
                    try { if (!PrintWindow(window, dc, 2 /* PW_RENDERFULLCONTENT */)) return null; }
                    finally { graphics.ReleaseHdc(dc); }
                }
                if (!Available(window, thread, pid) || Win.Bounds(window).Size != bounds.Size) return null;
                frame.Offset(-bounds.Left, -bounds.Top);
                double scale = Math.Min(1, 960d / Math.Max(frame.Width, frame.Height));
                var size = new Size(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale)));
                using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(image))
                    g.DrawImage(full, new Rectangle(Point.Empty, size), frame, GraphicsUnit.Pixel);
                // Some GPU-only windows acknowledge WM_PRINT but supply a black client.
                for (int y = 1; y <= 3; y++)
                    for (int x = 1; x <= 5; x++)
                    {
                        var c = image.GetPixel(x * size.Width / 6, y * size.Height / 4);
                        if (Math.Max(c.R, Math.Max(c.G, c.B)) > 8)
                            return new DragOpeningPicture(CaptureStream.Still(image, FrameCodec.Jpeg, 80), size, bounds.Size, begin, window, thread, pid);
                    }
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or OutOfMemoryException)
            { return null; }
            finally { Volatile.Write(ref active, 0); }
        });
    }
}
