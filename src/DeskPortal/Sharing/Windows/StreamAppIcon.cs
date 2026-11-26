using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskLink;

// Only a fixed-size raster crosses the link. File paths, shell identities and
// executable resources from the source computer stay on that computer.
static class StreamAppIcon
{
    internal const int Side = 48, Bytes = Side * Side * 4;
    [DllImport("DeskCapture.dll")] static extern nint CopySourceWindowIcon(nint hwnd);
    [DllImport("DeskCapture.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern void SetStreamTaskbarIdentity(nint hwnd, string? id);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);

    internal static byte[]? Read(nint hwnd)
    {
        var handle = CopySourceWindowIcon(hwnd);
        if (handle == 0) return null;
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var source = icon.ToBitmap();
            using var bitmap = new Bitmap(Side, Side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, new Rectangle(0, 0, Side, Side));
            }
            var data = bitmap.LockBits(new(0, 0, Side, Side), ImageLockMode.ReadOnly, bitmap.PixelFormat);
            try { var pixels = new byte[Bytes]; Marshal.Copy(data.Scan0, pixels, 0, Bytes); return pixels; }
            finally { bitmap.UnlockBits(data); }
        }
        finally { DestroyIcon(handle); }
    }
    internal static Icon Compose(byte[]? pixels)
    {
        using var bitmap = new Bitmap(Side, Side, PixelFormat.Format32bppArgb);
        if (pixels is { Length: Bytes })
        {
            var data = bitmap.LockBits(new(0, 0, Side, Side), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try { Marshal.Copy(pixels, 0, data.Scan0, Bytes); }
            finally { bitmap.UnlockBits(data); }
        }
        using (var g = Graphics.FromImage(bitmap))
        {
            if (pixels == null) g.DrawIcon(SystemIcons.Application, new Rectangle(0, 0, Side, Side));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(Brushes.White, 23, 25, 25, 23);
            g.TranslateTransform(23, 25);
            g.ScaleTransform(25 / 43f, 23 / 38f);
            DeskMark.Draw(g, Color.Black, Color.FromArgb(79, 220, 189));
        }
        return FromBitmap(bitmap);
    }
    internal static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
