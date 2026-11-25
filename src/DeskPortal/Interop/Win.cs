using System.Runtime.InteropServices;
using System.Text;

namespace DeskLink;

static class Win
{
    const string User = "user32.dll";
    public const int Move = 0x200, LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205,
        MiddleDown = 0x207, MiddleUp = 0x208, Wheel = 0x20A, HorizontalWheel = 0x20E,
        // The back and forward buttons. Their data word is 1 or 2.
        XDown = 0x20B, XUp = 0x20C;
    public const nuint Tag = 0x444C4E4B;
    public const nuint ProjectionTag = 0x4450524A;
    [DllImport(User)] public static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport(User)] public static extern nuint GetMessageExtraInfo();
    public delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] public struct MouseData { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L, T, R, B; public Rectangle Box => Rectangle.FromLTRB(L, T, R, B); }
    [StructLayout(LayoutKind.Sequential)] public struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport(User)] public static extern nint SetWindowsHookEx(int type, Hook callback, nint module, uint thread);
    [DllImport(User)] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport(User)] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? module);
    [DllImport(User)] public static extern nint WindowFromPoint(Point point);
    [DllImport(User, CharSet = CharSet.Unicode)] static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport(User)] static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32")] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32")] static extern uint GetRegionData(nint region, uint size, [Out] int[]? data);
    [DllImport("gdi32")] static extern bool DeleteObject(nint obj);
    [DllImport(User)] public static extern nint GetAncestor(nint window, uint flags);
    public delegate bool EnumWindow(nint window, nint data);
    [DllImport(User)] public static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport(User)] public static extern bool IsWindowVisible(nint window);
    [DllImport(User)] static extern bool IsWindowEnabled(nint window);
    public static nint InputChild(nint parent, Point screen, Point local)
    {
        var child = ChildWindowFromPointEx(parent, local, 7);
        if (child != 0 && child != parent) return child;
        // A zero-alpha source can be transparent to USER's hit testing. Descend
        // through its own visible child rectangles, including MFC splitter panes.
        for (var w = GetWindow(parent, 5); w != 0; w = GetWindow(w, 2))
        {
            if (IsWindowVisible(w) && IsWindowEnabled(w) && Bounds(w).Contains(screen)) return w;
        }
        return parent;
    }
    [DllImport(User)] public static extern nint GetWindow(nint window, uint command);
    [DllImport(User)] public static extern bool IsChild(nint parent, nint child);
    [StructLayout(LayoutKind.Sequential)]
    public struct GuiInfo
    { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport(User)] public static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport(User)] public static extern uint GetDpiForWindow(nint window);
    [DllImport(User)] static extern int GetDlgCtrlID(nint window);
    public static bool IsOrcaTitle(nint root, nint child, Point point)
    {
        if (child == root || GetDlgCtrlID(child) != 3200 || GetAncestor(child, 1) != root ||
            Class(child) != "wxWindowNR" || Class(root) != "wxWindowNR") return false;
        var bar = Bounds(child);
        var frame = FrameBounds(root);
        // The slicer's wxAUI title item and stretch spacers occupy the middle of its top toolbar.
        return bar.Top <= frame.Top + 8 && bar.Height <= GetDpiForWindow(root) &&
            bar.Width >= GetDpiForWindow(root) * 9 && bar.Width >= frame.Width - 32 &&
            point.X >= bar.Left + bar.Width / 2 && point.X <= bar.Left + bar.Width * 2 / 3;
    }
    public static bool InSystemMove(nint window)
    {
        var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) &&
            (info.Flags & 2) != 0 && info.MoveSize == window;
    }
    public static void Raise(nint window) => SetWindowPos(window, 0, 0, 0, 0, 0, 0x03);
    [DllImport(User)] public static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport(User)] public static extern bool IsWindow(nint window);
    [DllImport(User)] public static extern bool IsZoomed(nint window);
    [DllImport(User)] public static extern bool ShowWindow(nint window, int show);
    [DllImport(User)] public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport(User)] public static extern bool SetForegroundWindow(nint window);
    [DllImport(User)] public static extern nint GetForegroundWindow();
    [DllImport(User)] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport(User, CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, StringBuilder title, int length);
    [DllImport(User)] public static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport(User)] public static extern nint ChildWindowFromPointEx(nint parent, Point point, uint flags);
    [DllImport(User)] public static extern nint GetCapture();
    [DllImport(User)] public static extern bool SetCursorPos(int x, int y);
    [DllImport(User)] public static extern nint SetCursor(nint cursor);
    [DllImport(User, CharSet = CharSet.Unicode)] static extern int GetClassName(nint w, StringBuilder name, int capacity);
    [DllImport(User)] public static extern bool GetCursorPos(out Point p);
    [StructLayout(LayoutKind.Sequential)] public struct LastInput { public int Size; public uint Time; }
    [DllImport(User)] public static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport(User)] public static extern short GetAsyncKeyState(int key);
    [DllImport(User)] public static extern bool PostMessage(nint w, uint msg, nint wp, nint lp);
    [DllImport(User)] public static extern nint SendMessageTimeout(nint w, uint msg, nint wp, nint lp, uint flags, uint timeout, out nint result);
    [DllImport(User, EntryPoint = "SendMessageTimeoutW")] static extern nint GetSizingLimits(nint w, uint msg, nint wp, ref MinMaxInfo limits, uint flags, uint timeout, out nint result);
    [DllImport(User)] public static extern void mouse_event(uint flags, uint x, uint y, int wheel, nuint extra);
    [DllImport(User)] public static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport(User)] public static extern bool AddClipboardFormatListener(nint w);
    [DllImport(User)] public static extern bool RemoveClipboardFormatListener(nint w);
    [DllImport(User)] public static extern uint GetClipboardSequenceNumber();
    [DllImport(User)] public static extern bool RegisterHotKey(nint w, int id, uint modifiers, uint key);
    [DllImport(User)] public static extern bool UnregisterHotKey(nint w, int id);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint w, int attribute, out Rect rect, int size);
    [DllImport(User, EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint w, int index);
    [DllImport(User, EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint w, int index, nint value);
    [DllImport(User)] public static extern bool SetLayeredWindowAttributes(nint w, uint color, byte alpha, uint flags);
    [DllImport(User)] public static extern bool GetLayeredWindowAttributes(nint w, out uint color, out byte alpha, out uint flags);
    public static Rectangle Bounds(nint w) => GetWindowRect(w, out var r) ? r.Box : Rectangle.Empty;
    public static Rectangle FrameBounds(nint w) => DwmGetWindowAttribute(w, 9, out var r, 16) == 0 ? r.Box : Bounds(w);
    public static bool CanResize(nint w) => ((long)GetWindowLongPtr(w, -16) & 0x40000) != 0; // WS_THICKFRAME
    public static void ResizeForDisplay(nint hwnd, Size desired)
    {
        uint flags = 0x16; // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
        var desktopLimit = SystemInformation.MaxWindowTrackSize;
        if (desired.Width > desktopLimit.Width || desired.Height > desktopLimit.Height)
        {
            // DefWindowProc normally caps the HWND to the source virtual desktop.
            // Ask the app for its tracking limits using the larger destination first.
            var monitor = Screen.FromHandle(hwnd);
            var work = monitor.WorkingArea;
            var limits = new MinMaxInfo {
                MaxSize = new(work.Width, work.Height), MaxPosition = new(work.Left - monitor.Bounds.Left, work.Top - monitor.Bounds.Top),
                MinTrackSize = (Point)SystemInformation.MinWindowTrackSize,
                MaxTrackSize = new(Math.Max(desired.Width, desktopLimit.Width), Math.Max(desired.Height, desktopLimit.Height))
            };
            if (GetSizingLimits(hwnd, 0x24, 0, ref limits, 2, 200, out _) != 0 &&
                limits.MinTrackSize.X <= limits.MaxTrackSize.X && limits.MinTrackSize.Y <= limits.MaxTrackSize.Y &&
                limits.MaxTrackSize.X > 0 && limits.MaxTrackSize.Y > 0)
            {
                desired.Width = Math.Clamp(desired.Width, limits.MinTrackSize.X, limits.MaxTrackSize.X);
                desired.Height = Math.Clamp(desired.Height, limits.MinTrackSize.Y, limits.MaxTrackSize.Y);
                if (desired.Width > desktopLimit.Width || desired.Height > desktopLimit.Height)
                    flags |= 0x400; // SWP_NOSENDCHANGING avoids a second pass with the smaller desktop's limits.
            }
        }
        SetWindowPos(hwnd, 0, 0, 0, desired.Width, desired.Height, flags);
    }
    public static string Title(nint w) { var s = new StringBuilder(512); GetWindowText(w, s, s.Capacity); return s.ToString(); }
    public static string Class(nint w) { var s = new StringBuilder(128); GetClassName(w, s, s.Capacity); return s.ToString(); }
    const string ExplorerTitleBar = "TITLE_BAR_SCAFFOLDING_WINDOW_CLASS";
    public static bool IsExplorer(nint root) => Class(root) is "CabinetWClass" or "ExploreWClass";
    public static bool IsDragCaption(nint root, nint under, Point p)
    {
        if (under != root && Class(under) == ExplorerTitleBar && IsExplorer(root))
            return Hit(under, p) == 2;
        return Hit(root, p) == 2 && (under == root || !WinUiInput.IsInputSite(under));
    }
    // Explorer owns the tab strip and the native caption in separate children.
    // Keep geometry in captured-frame coordinates, including on mixed-DPI PCs.
    public sealed record ExplorerHeader(nint Handle, Rectangle Frame, Rectangle Header, Rectangle Buttons)
    {
        public CaptionRegion[] DragRegions()
        {
            var box = Rectangle.Intersect(Frame, Header);
            if (box.Width <= 0 || box.Height <= 0) return [];
            var result = new List<CaptionRegion>();
            // Walk in from an outside coordinate to a known caption pixel.
            // The native caption in each band is contiguous.
            static int Edge(int outside, int inside, Func<int, bool> isCaption)
            {
                while (Math.Abs(outside - inside) > 1)
                {
                    int mid = outside + (inside - outside) / 2;
                    if (isCaption(mid)) inside = mid; else outside = mid;
                }
                return inside;
            }
            void Add(Rectangle area)
            {
                if (area.Width <= 0 || area.Height <= 0) return;
                int x = area.Left + area.Width / 2, y = area.Top + area.Height / 2;
                bool Across(int column) => Hit(Handle, new(column, y)) == 2;
                if (!Across(x))
                {
                    // DWM can temporarily omit button bounds. Try the ends
                    // of the native child on both LTR and RTL desktops.
                    x = area.Left + Math.Min(2, area.Width / 2);
                    if (!Across(x)) { x = area.Right - 1 - Math.Min(2, (area.Width - 1) / 2); if (!Across(x)) return; }
                }
                bool Down(int row) => Hit(Handle, new(x, row)) == 2;
                int left = Edge(area.Left - 1, x, Across), right = Edge(area.Right, x, Across) + 1;
                int top = Edge(area.Top - 1, y, Down), bottom = Edge(area.Bottom, y, Down) + 1;
                int X(int n) => (int)(65536L * (n - Frame.Left) / Frame.Width);
                int Y(int n) => (int)(65536L * (n - Frame.Top) / Frame.Height);
                result.Add(new(X(left), Y(top), X(right), Y(bottom)));
            }
            var buttons = Rectangle.Intersect(box, Buttons);
            if (buttons.Width <= 0 || buttons.Height <= 0) Add(box);
            else
            {
                Add(Rectangle.FromLTRB(box.Left, box.Top, buttons.Left, box.Bottom));
                Add(Rectangle.FromLTRB(buttons.Right, box.Top, box.Right, box.Bottom));
                Add(Rectangle.FromLTRB(buttons.Left, box.Top, buttons.Right, buttons.Top));
                Add(Rectangle.FromLTRB(buttons.Left, buttons.Bottom, buttons.Right, box.Bottom));
            }
            return result.ToArray();
        }
    }
    public static ExplorerHeader? ReadExplorerHeader(nint root)
    {
        nint header = FindWindowEx(root, 0, ExplorerTitleBar, null);
        if (header == 0 || !IsWindowVisible(header)) return null;
        var box = Bounds(header);
        var frame = FrameBounds(root);
        if (box.Width <= 0 || box.Height <= 0 || frame.Width <= 0 || frame.Height <= 0) return null;
        return new(header, frame, box, CaptionButtons(root));
    }
    public static Rectangle CaptionButtons(nint root)
    {
        if (DwmGetWindowAttribute(root, 5, out var rect, 16) == 0 && rect.R > rect.L && rect.B > rect.T)
        {
            var buttons = rect.Box;
            buttons.Offset(Bounds(root).Location);
            return buttons;
        }
        return Rectangle.Empty;
    }
    // WinUI puts title-bar buttons and search fields in cutouts of this child
    // HWND. The top-level window alone reports HTCAPTION even over a cutout.
    public static CaptionRegion[]? CaptionRegions(nint root)
    {
        var header = FindWindowEx(root, 0, "InputNonClientPointerSource", null);
        if (header == 0) return null;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (region == 0 || GetWindowRgn(header, region) == 0) return null;
            uint bytes = GetRegionData(region, 0, null);
            if (bytes is < 32 or > (32 + 64 * 16)) return null;
            int[] data = new int[(bytes + 3) / 4];
            if (GetRegionData(region, bytes, data) != bytes || data[0] != 32 || data[2] < 0 || data[2] > (data.Length - 8) / 4) return null;
            var frame = FrameBounds(root);
            var box = Bounds(header);
            if (frame.Width <= 0 || frame.Height <= 0) return null;
            int X(int x) => (int)Math.Clamp(65536L * (box.Left + x - frame.Left) / frame.Width, 0, 65536);
            int Y(int y) => (int)Math.Clamp(65536L * (box.Top + y - frame.Top) / frame.Height, 0, 65536);
            var rects = new CaptionRegion[data[2]];
            for (int i = 0, j = 8; i < rects.Length; i++, j += 4)
                rects[i] = new(X(data[j]), Y(data[j+1]), X(data[j+2]), Y(data[j+3]));
            return rects;
        }
        finally { if (region != 0) DeleteObject(region); }
    }
    public static bool Held(Keys key) => GetAsyncKeyState((int)key) < 0;
    public static nint Packed(Point p) => unchecked((nint)((p.X & 65535) | (p.Y << 16)));
    public static void Position(nint w, Point p) => SetWindowPos(w, 0, p.X, p.Y, 0, 0, 0x0015);
    public static int Hit(nint w, Point p) =>
        SendMessageTimeout(w, 0x84, 0, Packed(p), 2, 35, out var hit) != 0 ? (int)hit : 0;
    public static uint MouseFlag(int message) => message switch
    {
        LeftDown => 2,
        LeftUp => 4,
        RightDown => 8,
        RightUp => 16,
        MiddleDown => 32,
        MiddleUp => 64,
        XDown => 0x80,
        XUp => 0x100,
        Wheel => 0x800,
        HorizontalWheel => 0x1000,
        _ => 0
    };
}
