namespace DeskLink;

// Submit and Dispose run on the window's UI thread. Only one decode and the
// newest waiting frame are retained, even if a large window outruns the CPU.
sealed class FrameDecoder(int offset, Action changed, Action<Exception> failed) : IDisposable
{
    byte[]? pending;
    bool busy, disposed;
    public Bitmap? Image { get; private set; }

    public async void Submit(byte[] bytes)
    {
        if (disposed) return;
        pending = bytes;
        if (busy) return;
        busy = true;
        try
        {
            while (pending is { } packet)
            {
                pending = null;
                var next = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(packet, offset, packet.Length - offset, false);
                    using var decoded = System.Drawing.Image.FromStream(stream);
                    return new Bitmap(decoded);
                });
                if (disposed) { next.Dispose(); return; }
                Image?.Dispose();
                Image = next;
                changed();
            }
        }
        catch (Exception e) { if (!disposed) failed(e); }
        finally { busy = false; }
    }

    public void Dispose()
    {
        disposed = true;
        pending = null;
        Image?.Dispose();
        Image = null;
    }
}
