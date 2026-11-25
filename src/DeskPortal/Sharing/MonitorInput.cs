using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace DeskLink;

static class MonitorGeometry
{
    public static Rectangle Fit(Size client, Size source, int header)
    {
        var area = new Rectangle(0, header, client.Width, Math.Max(0, client.Height - header));
        if (source.Width <= 0 || source.Height <= 0 || area.Width <= 0 || area.Height <= 0) return Rectangle.Empty;
        double scale = Math.Min((double)area.Width / source.Width, (double)area.Height / source.Height);
        var size = new Size((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale));
        return new(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    public static Point ToDesktop(Rectangle monitor, int x, int y) => new(
        monitor.Left + (int)((Math.Clamp(x, 0, 65535) * (long)(monitor.Width - 1) + 32767) / 65535),
        monitor.Top + (int)((Math.Clamp(y, 0, 65535) * (long)(monitor.Height - 1) + 32767) / 65535));
}

// Windows has one system pointer. Serialize all monitors' input off the UI thread and
// release the previous controller's held keys/buttons before switching controllers.
sealed class MonitorInput : IDisposable
{
    internal sealed class Controller(Rectangle monitor, Rectangle desktop, Action<string> failure)
    {
        public Rectangle Monitor { get; } = monitor;
        public Rectangle Desktop { get; } = desktop;
        public Action<string> Failure { get; } = failure;
        public volatile bool Revoked;
    }
    readonly Channel<(Controller Controller, WindowEvent? Event)> queue =
        Channel.CreateBounded<(Controller, WindowEvent?)>(256);
    readonly Dictionary<int, (byte Scan, uint Extended)> keys = [];
    static readonly WheelInjector wheel = new(TimeSpan.FromSeconds(3), StallLog.Write);
    readonly HashSet<(int Up, int Data)> buttons = [];
    Controller? current;

    readonly Action<Input> deliver;
    public MonitorInput(Action<Input>? deliver = null)
    {
        this.deliver = deliver ?? Send;
        _ = Task.Run(Pump);
    }
    public void Post(Controller controller, WindowEvent? input)
    {
        if (!queue.Writer.TryWrite((controller, input)))
        {
            controller.Revoked = true;
            if (input != null) throw new IOException("Screen input could not keep up. Reopen the projection.");
        }
    }
    async Task Pump()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (current?.Revoked == true || (current == item.Controller && item.Event == null)) Release();
                if (item.Event is not { } e || item.Controller.Revoked) continue;
                if (current != item.Controller) { Release(); current = item.Controller; }
                try
                {
                    if (e.Message is Win.Wheel or Win.HorizontalWheel)
                        await wheel.Run(() => { if (!item.Controller.Revoked) Inject(item.Controller, e); }, CancellationToken.None).ConfigureAwait(false);
                    else Inject(item.Controller, e);
                }
                catch (IOException ex)
                {
                    item.Controller.Revoked = true; Release(); item.Controller.Failure(ex.Message);
                }
            }
        }
        finally { Release(); }
    }
    // INPUT is 40 bytes on x64. Using the native union makes SendInput's delivery count
    // observable (including a protected or unavailable input desktop).
    [StructLayout(LayoutKind.Sequential)]
    internal struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Key; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { public uint Type; public InputData Data; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, ref Input value, int size);
    static void Send(Input value)
    {
        if (SendInput(1, ref value, Marshal.SizeOf<Input>()) != 1)
            throw new IOException($"Windows declined projected input (error {Marshal.GetLastWin32Error()}).");
    }
    void Inject(Controller controller, WindowEvent e)
    {
        if (e.Message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            if (e.Data is < 1 or > 254) return;
            byte scan = (byte)(e.Modifiers >> 16);
            uint extended = (uint)(e.Modifiers >> 24) & 1;
            bool up = e.Message is 0x101 or 0x105;
            if (up && !keys.Remove(e.Data)) return;
            if (!up) keys[e.Data] = (scan, extended);
            deliver(new() { Type = 1, Data = new() { Key = new() { Key = (ushort)e.Data, Scan = scan,
                Flags = extended | (up ? 2u : 0), Extra = Win.ProjectionTag } } });
            return;
        }
        int message = e.Message switch { 0x203 => Win.LeftDown, 0x206 => Win.RightDown, _ => e.Message };
        uint flag = Win.MouseFlag(message);
        if (flag == 0 && message != Win.Move || e.X is < 0 or > 65535 || e.Y is < 0 or > 65535) return;
        if (message is Win.XDown or Win.XUp && e.Data is not (1 or 2)) return;
        var point = MonitorGeometry.ToDesktop(controller.Monitor, e.X, e.Y);
        if (StallLog.Timing && message != Win.Move) Console.WriteLine($"Monitor input {message:X} at {point}.");
        var desktop = controller.Desktop;
        static int Absolute(int pixel, int span) => (int)Math.Clamp(((long)pixel * 65536 + 32768) / span, 0, 65535);
        int data = message is Win.XDown or Win.XUp or Win.Wheel or Win.HorizontalWheel ? (short)e.Data : 0;
        if (message is Win.LeftDown or Win.RightDown or Win.MiddleDown or Win.XDown) buttons.Add((message + 1, data));
        if (message is Win.LeftUp or Win.RightUp or Win.MiddleUp or Win.XUp && !buttons.Remove((message, data))) return;
        deliver(new() { Data = new() { Mouse = new() { X = Absolute(point.X - desktop.Left, desktop.Width),
            Y = Absolute(point.Y - desktop.Top, desktop.Height), Flags = 0xC001 | flag, Data = unchecked((uint)data), Extra = Win.ProjectionTag } } });
    }
    void Release()
    {
        // A locked desktop can reject releases too. Always clear this controller's ownership.
        void TryRelease(Input report) { try { deliver(report); } catch (IOException) { } }
        foreach (var (key, bits) in keys) TryRelease(new() { Type = 1, Data = new() { Key = new() {
            Key = (ushort)key, Scan = bits.Scan, Flags = 2 | bits.Extended, Extra = Win.ProjectionTag } } });
        foreach (var (up, data) in buttons) TryRelease(new() { Data = new() { Mouse = new() {
            Flags = Win.MouseFlag(up), Data = (uint)data, Extra = Win.ProjectionTag } } });
        keys.Clear(); buttons.Clear(); current = null;
    }
    public void Dispose() => queue.Writer.TryComplete();
}
