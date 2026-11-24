using System.Runtime.InteropServices;

namespace DeskLink;

// A native input call already inside Windows cannot be canceled. A new session
// must not start another worker behind one that Windows still holds.
sealed class WheelInjector(TimeSpan timeout, Action<string>? log = null)
{
    readonly SemaphoreSlim idle = new(1, 1);

    public async Task Run(Action inject, CancellationToken token)
    {
        if (!idle.Wait(0)) throw new IOException("Windows is still processing the previous wheel input.");
        var delivery = Task.Run(() =>
        {
            try { token.ThrowIfCancellationRequested(); inject(); }
            finally { idle.Release(); }
        });
        try { await delivery.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException ex)
        {
            log?.Invoke($"Input  Windows wheel injection did not return within {timeout.TotalMilliseconds:F0} ms");
            throw new IOException("Windows stopped responding to shared wheel input. The sharing session was closed.", ex);
        }
    }
}

// Only a paired session installs these hooks. DeskLink tags its own injected events.
sealed class PointerBridge : IDisposable
{
    static readonly WheelInjector wheelInjector = new(TimeSpan.FromSeconds(3), StallLog.Write);
    sealed class CursorCover : Form
    {
        public CursorCover()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; Opacity = 0.02; TopMost = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20) { Win.SetCursor(0); m.Result = 1; return; }
            base.WndProc(ref m);
        }
        public void At(Point point) { Bounds = new Rectangle(point.X - 3, point.Y - 3, 7, 7); Show(); }
    }
    readonly CursorCover cover = new();
    readonly Peer peer;
    readonly WindowDesk windows;
    readonly DeskLayout layout;
    readonly EdgeFiles files;
    readonly Win.Hook mouseHook, keyHook;
    readonly Action<Point> moveCursor;
    nint mouseHandle, keyHandle;
    bool remote, remotePress, localPress, warping, receivedDrag;
    Sharing features = Sharing.All;
    bool MouseEnabled => features.HasFlag(Sharing.Mouse);
    bool FilesEnabled => features.HasFlag(Sharing.Files);
    bool WindowsEnabled => features.HasFlag(Sharing.Windows);
    public void SetSharing(Sharing next)
    {
        if (features == next) return;
        if ((features & ~next & (Sharing.Mouse | Sharing.Windows | Sharing.Files)) != 0)
        {
            if ((remote || remotePress) && (!next.HasFlag(Sharing.Mouse) ||
                (windows.Dragging && !next.HasFlag(Sharing.Windows)) || (files.Recognized && !next.HasFlag(Sharing.Files)))) Reclaim();
            if (MouseEnabled && !next.HasFlag(Sharing.Mouse))
            {
                if (receivedDrag) windows.CancelDrag();
                receivedDrag = false;
                ReleaseReceived();
            }
        }
        features = next;
    }
    public EdgeActivation Crossing { get; set; }
    public Func<Keys, bool>? RemoteShortcut { get; set; }
    readonly HashSet<int> shortcutKeys = [];
    bool CrossingAllowed() => Crossing switch
    {
        EdgeActivation.HoldCtrl => Win.Held(Keys.ControlKey),
        EdgeActivation.HoldShift => Win.Held(Keys.ShiftKey),
        _ => true
    };
    Point logical, anchor;
    Point? previousLocal;
    DisplayTile? remoteTile;
    readonly HashSet<int> sentKeys = [], receivedKeys = [], receivedButtons = [], receivedExtraButtons = [];
    // Keys this desktop saw go down and has not yet seen released.
    readonly Dictionary<int, (uint Scan, bool Extended)> localKeys = [];
    public PointerBridge(Peer peer, WindowDesk windows, DeskLayout layout, EdgeFiles files, Action<Point>? moveCursor = null)
    {
        this.peer = peer; this.windows = windows; this.layout = layout; this.files = files;
        mouseHook = Mouse;
        keyHook = Key;
        this.moveCursor = moveCursor ?? Warp;
    }
    public void InstallHooks()
    {
        Hook();
        if (mouseHandle == 0 || keyHandle == 0) { Dispose(); throw new InvalidOperationException("Windows could not enable shared input."); }
        hooked = true;
        hookWatch.Tick += (_, _) => CheckHooks();
        hookWatch.Start();
    }
    void Hook()
    {
        var module = Win.GetModuleHandle(null);
        mouseHandle = Win.SetWindowsHookEx(14, mouseHook, module, 0);
        keyHandle = Win.SetWindowsHookEx(13, keyHook, module, 0);
        lastHookTick = Environment.TickCount;
    }
    // Windows removes a low-level hook, without notice, when its callback overruns the
    // system timeout. Input that arrives with no callback means that happened.
    readonly System.Windows.Forms.Timer hookWatch = new() { Interval = 2000 };
    bool hooked;
    int lastHookTick;
    void CheckHooks()
    {
        if (!hooked) return;
        var input = new Win.LastInput { Size = Marshal.SizeOf<Win.LastInput>() };
        if (!Win.GetLastInputInfo(ref input)) return;
        int now = Environment.TickCount;
        uint sinceInput = unchecked((uint)now - input.Time), sinceHook = unchecked((uint)(now - lastHookTick));
        if (mouseHandle != 0 && keyHandle != 0 && sinceHook <= sinceInput + 1500) { hookWatch.Interval = 2000; return; }
        if (mouseHandle != 0) Win.UnhookWindowsHookEx(mouseHandle);
        if (keyHandle != 0) Win.UnhookWindowsHookEx(keyHandle);
        Hook();
        // Input to an elevated window never reaches these hooks either. Check less often while that lasts.
        hookWatch.Interval = Math.Min(hookWatch.Interval * 2, 30000);
    }
    nint Mouse(int code, nint w, nint l)
    {
        lastHookTick = Environment.TickCount;
        if (code >= 0 && !warping)
        {
            var m = Marshal.PtrToStructure<Win.MouseData>(l);
            if (RouteMouseReport(m, (int)w)) return 1;
        }
        return Win.CallNextHookEx(0, code, w, l);
    }
    internal bool RouteMouseReport(Win.MouseData report, int message)
    {
        // Smooth-wheel drivers can synthesize fractional wheel reports. Only
        // accept those while this mouse controls the peer. Our own injections
        // and lower-integrity injections must never travel back across the link.
        if (report.Extra == Win.Tag || (report.Flags & 2) != 0) return false;
        if ((report.Flags & 1) != 0 && !(remote && MouseEnabled && message is Win.Wheel or Win.HorizontalWheel)) return false;
        return Physical(report.Point, message, (short)(report.Data >> 16));
    }
    void Warp(Point p)
    {
        warping = true;
        try { InjectMove(p, layout.Local); }
        finally { warping = false; }
    }
    static void InjectMove(Point p, Rectangle desktop)
    {
        uint x = (uint)Math.Clamp(((long)(p.X - desktop.Left) * 65536 + 32768) / desktop.Width, 0, 65535);
        uint y = (uint)Math.Clamp(((long)(p.Y - desktop.Top) * 65536 + 32768) / desktop.Height, 0, 65535);
        Win.mouse_event(0xC001, x, y, 0, Win.Tag);
    }
    internal bool Physical(Point p, int msg, int data)
    {
        bool wasRemote = remote;
        bool returnedHome = false;
        if (remote && msg == Win.Move)
        {
            var next = layout.Walk(remoteTile!, logical, new(logical.X + p.X - anchor.X, logical.Y + p.Y - anchor.Y));
            logical = next.Point;
            remoteTile = next.Tile;
            if (next.Tile.Here)
            {
                remote = false;
                cover.Hide();
                previousLocal = next.Tile.ToNative(logical);
                moveCursor(previousLocal.Value);
                ReleaseSentKeys();
                returnedHome = true;
            }
        }
        else if (!remote)
        {
            var nativeTile = layout.LocalTile(p);
            logical = nativeTile.ToDesk(p);
            bool remapped = false;
            if (msg == Win.Move)
            {
                var origin = previousLocal ?? p;
                var tile = layout.LocalTile(origin);
                var start = tile.ToDesk(origin);
                var target = tile.ToDesk(p);
                // Windows clips hardware motion at outer edges. Supply that last pixel when the report reaches the edge.
                var r = tile.Desk;
                if (target.X == r.Right-1 && p.X >= origin.X && (p.X != origin.X || p.Y == origin.Y)) target.X++;
                else if (target.X == r.Left && p.X <= origin.X && (p.X != origin.X || p.Y == origin.Y)) target.X--;
                if (target.Y == r.Bottom-1 && p.Y >= origin.Y && (p.Y != origin.Y || p.X == origin.X)) target.Y++;
                else if (target.Y == r.Top && p.Y <= origin.Y && (p.Y != origin.Y || p.X == origin.X)) target.Y--;
                var step = layout.Walk(tile,start,target);
                var near = layout.Clamp(r,target);
                if (WindowsEnabled) windows.Approach(step.Tile == tile ? step.Point : near, !step.Tile.Here);
                bool carry = (MouseEnabled && !localPress && CrossingAllowed()) || (WindowsEnabled && windows.Dragging) || (FilesEnabled && files.Recognized);
                if (!step.Tile.Here && carry)
                {
                    remote = true; remoteTile = step.Tile; logical = step.Point;
                    anchor = new(tile.Native.Left + tile.Native.Width / 2, tile.Native.Top + tile.Native.Height / 2);
                    if (localPress && FilesEnabled && files.Recognized) files.Begin(anchor);
                    else cover.At(anchor);
                    moveCursor(anchor);
                    if (MouseEnabled) ReleaseLocalKeys();
                }
                else if (layout.Editable && step.Tile.Here && (MouseEnabled || windows.Dragging || files.Recognized))
                {
                    var physical = step.Tile.ToNative(step.Point);
                    logical = step.Point;
                    if (physical != p) { moveCursor(physical); remapped = true; }
                    previousLocal = physical;
                }
            }
            if (!remapped) previousLocal = remote ? null : p;
            wasRemote |= remapped; // A tagged warp already delivered the corrected native position.
        }
        if (msg == Win.LeftDown) { remotePress = remote; localPress = !remote; }
        bool dragFromPeer = remotePress;
        bool owned = !dragFromPeer && ((FilesEnabled && files.Route(logical, msg, remote)) ||
            (WindowsEnabled && windows.Route(logical, msg, remote)));
        if (FilesEnabled && !remote && !dragFromPeer)
            files.Track(logical, localPress && !windows.Dragging && !windows.CaptionPressed && msg != Win.LeftUp);
        if (MouseEnabled && (remote || dragFromPeer))
        {
            var q = layout.ToPeer(logical);
            // When moving a local title bar, the peer only needs a cursor; the real button belongs to the drag.
            if (!owned || msg == Win.Move)
                peer.Send(Kind.Mouse, new MouseSample(q.X, q.Y, msg, data, layout.Editable && !remote ? logical : null));
        }
        // The window owner needs the crossing coordinate while its native title-bar
        // drag is still active. ReleaseInput is an ordering barrier in the input queue.
        if (returnedHome) peer.Send(Kind.ReleaseInput, 0);
        if (msg == Win.LeftUp)
        {
            localPress = remotePress = false;
            if (remote && !MouseEnabled) Reclaim();
        }
        // Windows computes the next hardware delta from its last accepted cursor
        // position. Swallow the title-bar button, but let local motion advance.
        return remote || wasRemote || (msg != Win.Move && (dragFromPeer || owned));
    }
    nint Key(int code, nint w, nint l)
    {
        lastHookTick = Environment.TickCount;
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Win.KeyboardData>(l);
            bool up = (k.Flags & 0x80) != 0;
            int key = (int)k.Key;
            if ((k.Flags & 16) == 0 && shortcutKeys.Contains(key))
            {
                if (up) shortcutKeys.Remove(key);
                return 1;
            }
            if ((k.Flags & 16) == 0 && remote && MouseEnabled)
            {
                if (!up && !sentKeys.Contains(key) && RemoteShortcut != null && !sentKeys.Contains(0x5B) && !sentKeys.Contains(0x5C))
                {
                    Keys chord = (Keys)key;
                    if (HeldOnPeer(0x11, 0xA2, 0xA3)) chord |= Keys.Control;
                    if (HeldOnPeer(0x12, 0xA4, 0xA5)) chord |= Keys.Alt;
                    if (HeldOnPeer(0x10, 0xA0, 0xA1)) chord |= Keys.Shift;
                    if (RemoteShortcut(chord)) { shortcutKeys.Add(key); return 1; }
                }
                if (up) sentKeys.Remove((int)k.Key); else sentKeys.Add((int)k.Key);
                peer.Send(Kind.Key, new KeySample((int)k.Key, (int)k.Scan, up, (k.Flags & 1) != 0));
                return 1;
            }
            if ((k.Flags & 16) == 0 && k.Key is >= 1 and <= 254)
            {
                if (up) localKeys.Remove((int)k.Key); else localKeys[(int)k.Key] = (k.Scan, (k.Flags & 1) != 0);
            }
        }
        return Win.CallNextHookEx(0, code, w, l);
    }
    bool HeldOnPeer(int generic, int left, int right) => sentKeys.Contains(generic) || sentKeys.Contains(left) || sentKeys.Contains(right);
    public void BringPointerBack() { if (remote) Reclaim(); }
    // The keyboard follows the pointer across the edge. A key held during the crossing
    // has its release sent to the peer, so release it here first or it stays down.
    void ReleaseLocalKeys()
    {
        var held = localKeys.Where(k => Win.GetAsyncKeyState(k.Key) < 0).ToArray();
        localKeys.Clear();
        if (held.Length == 0) return;
        // An unassigned key between press and release keeps a lone Alt or Windows key from opening a menu.
        if (held.Any(k => k.Key is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C))
        {
            Win.keybd_event(0xE8, 0, 0, Win.Tag);
            Win.keybd_event(0xE8, 0, 2, Win.Tag);
        }
        foreach (var (key, (scan, extended)) in held)
            Win.keybd_event((byte)key, (byte)scan, 2u | (extended ? 1u : 0), Win.Tag);
    }
    public Task Receive(MouseSample m)
    {
        if (!MouseEnabled) return Task.CompletedTask;
        bool wheel = m.Message is Win.Wheel or Win.HorizontalWheel;
        var point = layout.FromWire(new(m.X, m.Y));
        bool outside = m.OutsideDesk.HasValue || !layout.Local.Contains(point);
        if (remote) Reclaim(returnCursor: !wheel || outside);
        var desk = m.OutsideDesk ?? layout.ToWorld(point);
        if (outside && FilesEnabled && files.Recognized) files.Begin(Cursor.Position);
        if (!outside)
        {
            if (!wheel) moveCursor(point);
            previousLocal = point;
        }
        bool owned = (FilesEnabled && files.Route(desk, m.Message, outside)) || (WindowsEnabled && windows.Route(desk, m.Message, outside));
        if (m.Message == Win.LeftDown) receivedDrag = windows.Dragging || windows.CaptionPressed;
        if (m.Message == Win.LeftUp) receivedDrag = false;
        if (FilesEnabled && !outside) files.Track(desk, !windows.Dragging && !windows.CaptionPressed && (receivedButtons.Contains(Win.LeftUp) || m.Message == Win.LeftDown) && m.Message != Win.LeftUp);
        if (owned || outside) return Task.CompletedTask;
        uint flag = Win.MouseFlag(m.Message);
        if (flag == 0) return Task.CompletedTask;
        if (wheel)
        {
            // Windows can call the low-level mouse hook while processing injected input.
            // Keep its UI thread pumping, even if the native wheel call stalls.
            var desktop = layout.Local;
            var token = peer.Token;
            return wheelInjector.Run(() =>
            {
                if (Cursor.Position != point) InjectMove(point, desktop);
                token.ThrowIfCancellationRequested();
                Win.mouse_event(flag, 0, 0, m.Data, Win.Tag);
            }, token);
        }
        if (m.Message is Win.XDown or Win.XUp && m.Data is not (1 or 2)) return Task.CompletedTask;
        if (m.Message is Win.LeftDown or Win.RightDown or Win.MiddleDown) receivedButtons.Add(m.Message + 1);
        if (m.Message is Win.LeftUp or Win.RightUp or Win.MiddleUp) receivedButtons.Remove(m.Message);
        if (m.Message == Win.XDown) receivedExtraButtons.Add(m.Data);
        if (m.Message == Win.XUp) receivedExtraButtons.Remove(m.Data);
        Win.mouse_event(flag, 0, 0, m.Data, Win.Tag);
        return Task.CompletedTask;
    }
    public void Receive(KeySample k)
    {
        if (!MouseEnabled || k.Key is < 1 or > 254) return;
        if (k.Up) receivedKeys.Remove(k.Key); else receivedKeys.Add(k.Key);
        Win.keybd_event((byte)k.Key, (byte)k.Scan, (k.Up ? 2u : 0) | (k.Extended ? 1u : 0), Win.Tag);
    }
    public void ReleaseReceived()
    {
        foreach (int key in receivedKeys) Win.keybd_event((byte)key, 0, 2, Win.Tag);
        foreach (int up in receivedButtons) Win.mouse_event(Win.MouseFlag(up), 0, 0, 0, Win.Tag);
        foreach (int button in receivedExtraButtons) Win.mouse_event(Win.MouseFlag(Win.XUp), 0, 0, button, Win.Tag);
        receivedKeys.Clear(); receivedButtons.Clear(); receivedExtraButtons.Clear();
    }
    void ReleaseSentKeys()
    {
        foreach (var key in sentKeys) peer.Send(Kind.Key, new KeySample(key, 0, true, false));
        sentKeys.Clear();
    }
    public void Reclaim(bool returnCursor = true)
    {
        cover.Hide();
        if (remote && returnCursor)
        {
            var home = layout.HomeTile(logical);
            moveCursor(home.ToNative(layout.Clamp(home.Desk,logical)));
        }
        previousLocal = null; remoteTile = null;
        remote = remotePress = localPress = false;
        ReleaseSentKeys();
        peer.Send(Kind.ReleaseInput, 0);
    }
    public void Recover()
    {
        Reclaim(); files.Reset(); windows.RestoreAll(); peer.Send(Kind.RecoverWindows, 0);
    }
    public void Dispose()
    {
        hooked = false;
        hookWatch.Dispose();
        if (mouseHandle != 0) Win.UnhookWindowsHookEx(mouseHandle);
        if (keyHandle != 0) Win.UnhookWindowsHookEx(keyHandle);
        mouseHandle = keyHandle = 0;
        Reclaim();
        ReleaseReceived();
        cover.Dispose();
    }
}
