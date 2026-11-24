using System.Diagnostics;
using System.Text.Json;

namespace DeskLink;

// Captured HWNDs must remain on a display. A zero-alpha layered window lets the desktop
// underneath receive clicks while WGC continues reading the original surface.
sealed record SourceState(long Handle, uint Pid, long ProcessStart, int X, int Y, bool Maximized,
    bool WasLayered, uint Color, byte Alpha, uint Flags, int Width = 0, int Height = 0)
{
    const long Layered = 0x80000;
    public static SourceState Read(nint hwnd, Point home, bool maximized)
    {
        Win.GetWindowThreadProcessId(hwnd, out uint pid);
        using var process = Process.GetProcessById((int)pid);
        bool layered = ((long)Win.GetWindowLongPtr(hwnd, -20) & Layered) != 0;
        uint color = 0, flags = 0; byte alpha = 255;
        if (layered && !Win.GetLayeredWindowAttributes(hwnd, out color, out alpha, out flags))
            throw new InvalidOperationException("This window manages its own transparency and cannot be moved safely.");
        var normal = Win.Bounds(hwnd).Size;
        return new((long)hwnd, pid, process.StartTime.ToUniversalTime().Ticks, home.X, home.Y, maximized, layered, color, alpha, flags,
            normal.Width, normal.Height);
    }
    public void Conceal()
    {
        var hwnd = (nint)Handle;
        var style = Win.GetWindowLongPtr(hwnd, -20);
        if (!WasLayered) Win.SetWindowLongPtr(hwnd, -20, style | (nint)Layered);
        if (!Win.SetLayeredWindowAttributes(hwnd, 0, 0, 2) ||
            !Win.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out _) || alpha != 0)
            throw new InvalidOperationException("Windows denied the handoff for this window.");
    }
    public bool SameWindow()
    {
        var hwnd = (nint)Handle;
        if (!Win.IsWindow(hwnd)) return false;
        Win.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != Pid) return false;
        try { using var process = Process.GetProcessById((int)pid); return process.StartTime.ToUniversalTime().Ticks == ProcessStart; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public void Restore(Point? drop = null)
    {
        if (!SameWindow()) return;
        var hwnd = (nint)Handle;
        var position = drop ?? new Point(X, Y);
        if (Width > 0 && Height > 0 && Win.Bounds(hwnd).Size != new Size(Width, Height))
            Win.SetWindowPos(hwnd, 0, position.X, position.Y, Width, Height, 0x14);
        else Win.Position(hwnd, position);
        if (WasLayered) Win.SetLayeredWindowAttributes(hwnd, Color, Alpha, Flags);
        else Win.SetWindowLongPtr(hwnd, -20, Win.GetWindowLongPtr(hwnd, -20) & ~(nint)Layered);
        Win.SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37); // Redraw after removing the layered style.
        if (Maximized) Win.ShowWindow(hwnd, 3);
    }
}

sealed record RecoveryMessage(bool Add, SourceState State, bool ZOrderOnly = false);
sealed class RecoveryGuard : IDisposable
{
    readonly Process process;
    public RecoveryGuard()
    {
        var info = Program.StartSelf("--restore-guard");
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;
        process = Process.Start(info) ?? throw new IOException("Could not start window recovery.");
        process.StandardInput.AutoFlush = true;
    }
    public void Track(SourceState s, bool zOrderOnly = false) => process.StandardInput.WriteLine(JsonSerializer.Serialize(new RecoveryMessage(true, s, zOrderOnly)));
    public void Forget(SourceState s)
    {
        try { process.StandardInput.WriteLine(JsonSerializer.Serialize(new RecoveryMessage(false, s))); }
        catch (IOException) { }
    }
    public void Dispose() { process.StandardInput.Close(); process.Dispose(); }
    // Anonymous pipe EOF also occurs on an unexpected parent-process exit.
    public static int Run()
    {
        var tracked = new Dictionary<long, RecoveryMessage>();
        try
        {
            while (Console.ReadLine() is { } line)
            {
                var entry = JsonSerializer.Deserialize<RecoveryMessage>(line);
                if (entry == null) continue;
                if (entry.Add) tracked[entry.State.Handle] = entry;
                else tracked.Remove(entry.State.Handle);
            }
        }
        finally
        {
            foreach (var entry in tracked.Values)
            {
                if (!entry.ZOrderOnly) entry.State.Restore();
                else if (entry.State.SameWindow()) Win.SetWindowPos((nint)entry.State.Handle, -2, 0, 0, 0, 0, 0x13);
            }
        }
        return 0;
    }
}
