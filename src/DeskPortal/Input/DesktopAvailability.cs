using System.Runtime.InteropServices;

namespace DeskLink;

// Display power and input desktop availability are separate. A dark display can
// still deliver hardware reports. A screensaver or locked desktop owns its input.
sealed class DesktopAvailability
{
    static readonly Guid sessionDisplay = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    [DllImport("user32.dll")] static extern nint RegisterPowerSettingNotification(nint hwnd, in Guid setting, uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
    nint power, window;
    bool sessionNotifications, locked, saver, otherDesktop;
    internal const int PollMessage = 0x8247;
    long lastWakeProbe;
    public bool DisplayOff { get; private set; }
    public bool Blocked => locked || saver || otherDesktop;
    public bool CanWakeSaver => saver && !locked && !otherDesktop;
    public event Action? Changed;
    public void Register(nint hwnd)
    {
        window = hwnd;
        power = RegisterPowerSettingNotification(hwnd, in sessionDisplay, 0);
        sessionNotifications = WTSRegisterSessionNotification(hwnd, 0);
        Poll();
    }
    public void Unregister()
    {
        if (power != 0) UnregisterPowerSettingNotification(power);
        if (sessionNotifications) WTSUnRegisterSessionNotification(window);
        power = window = 0; sessionNotifications = false;
    }
    public void Poll()
    {
        bool running = SystemParametersInfo(0x72, 0, out int value, 0) ? value != 0 : saver;
        ObserveInputDesktop(running, Win.GetCursorPos(out _));
    }
    internal void ObserveInputDesktop(bool saverRunning, bool accessible)
    {
        bool before = Blocked;
        saver = saverRunning;
        otherDesktop = !accessible;
        if (before != Blocked) Changed?.Invoke();
    }
    // Let the wake gesture through first. Check the desktop again outside the
    // low-level hook so that remote input can resume before the next timer tick.
    public void WakeGesture()
    {
        long now = Environment.TickCount64;
        if (window == 0 || now - lastWakeProbe < 50) return;
        lastWakeProbe = now;
        Win.PostMessage(window, PollMessage, 0, 0);
    }
    public void Message(int msg, nint code, nint data)
    {
        if (msg == 0x218 && code == 0x8013 && data != 0 && Marshal.PtrToStructure<Guid>(data) == sessionDisplay &&
            Marshal.ReadInt32(data, 16) == sizeof(int))
        {
            bool off = Marshal.ReadInt32(data, 20) == 0;
            if (DisplayOff != off) { DisplayOff = off; Changed?.Invoke(); }
        }
        else if (msg == 0x2B1 && (code == 7 || code == 8)) // WTS_SESSION_LOCK / UNLOCK
        {
            locked = code == 7;
            if (!locked) Poll();
            Changed?.Invoke();
        }
    }
}
