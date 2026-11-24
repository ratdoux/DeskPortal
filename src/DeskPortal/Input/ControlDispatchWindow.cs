using System.Runtime.InteropServices;

namespace DeskLink;

// Windows caption tracking filters posted messages, including WinForms BeginInvoke.
// While Windows tracks a remote caption press, sent notifications reach that loop.
sealed class ControlDispatchWindow : NativeWindow, IDisposable
{
    const int DispatchMessage = 0x8001;
    readonly Queue<Action> work = new();
    readonly object gate = new();
    readonly int ownerThread = Environment.CurrentManagedThreadId;
    public bool OnOwnerThread => Environment.CurrentManagedThreadId == ownerThread;
    public volatile bool TrackingOwnPress;
    bool dispatching;
    int pendingNotifications;
    ControlMessage? pendingPointer;
    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW")]
    static extern bool SendNotifyMessage(nint hwnd, int message, nint wParam, nint lParam);

    public ControlDispatchWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

    public bool OwnsWindowAt(Point point)
    {
        var window = Win.WindowFromPoint(point);
        return window != 0 && Win.GetWindowThreadProcessId(window, out _) == Win.GetWindowThreadProcessId(Handle, out _);
    }

    // Called by the connection reader. The message carries no data or managed pointers.
    public void Post(Action action)
    {
        lock (gate)
        {
            // A sent message can run inside another Windows input callback. Reserve
            // it for the press whose release must get through native caption tracking.
            bool signaled = Handle != 0 && (TrackingOwnPress && !OnOwnerThread
                ? SendNotifyMessage(Handle, DispatchMessage, 0, 0) : Win.PostMessage(Handle, DispatchMessage, 0, 0));
            if (!signaled)
                throw new InvalidOperationException("The input window is unavailable.");
            work.Enqueue(action);
        }
    }
    public bool PostPointer(ControlMessage report, Action<ControlMessage> apply)
    {
        lock (gate)
        {
            if (pendingPointer is { } prior)
            {
                if (!report.CanReplace(prior)) return false;
                pendingPointer = report;
                return true;
            }
            pendingPointer = report;
            Post(() =>
            {
                ControlMessage latest;
                lock (gate) { latest = pendingPointer!.Value; pendingPointer = null; }
                apply(latest);
            });
            return true;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg != DispatchMessage) { base.WndProc(ref m); return; }
        // Moving another process's window or injecting a pointer can pump Windows
        // messages before the operation returns. Consume nested wakes here, then
        // finish their work in FIFO order after the active drag callback unwinds.
        pendingNotifications++;
        if (dispatching) return;
        dispatching = true;
        try
        {
            while (pendingNotifications > 0)
            {
                pendingNotifications--;
                Action? action;
                lock (gate) work.TryDequeue(out action);
                action?.Invoke();
            }
        }
        finally { dispatching = false; }
    }
    public void Dispose() => DestroyHandle();
}
