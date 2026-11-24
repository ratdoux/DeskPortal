using System.ComponentModel;
using System.Diagnostics;

namespace DeskLink;

// The socket reader and the Windows input thread must both get time to run while
// the foreground app is rendering. A scheduled launch can inherit BelowNormal.
sealed class InputThreadPriority(Thread thread, ThreadPriority previous, ThreadPriority applied) : IDisposable
{
    public static InputThreadPriority? Raise()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var target = process.PriorityClass switch
            {
                ProcessPriorityClass.BelowNormal => ThreadPriority.Highest,
                ProcessPriorityClass.Normal => ThreadPriority.AboveNormal,
                _ => ThreadPriority.Normal
            };
            var current = Thread.CurrentThread;
            var before = current.Priority;
            if (target == ThreadPriority.Normal || before >= target) return null;
            current.Priority = target;
            return new(current, before, target);
        }
        catch (Exception ex) when (ex is Win32Exception or ThreadStateException or UnauthorizedAccessException)
        { return null; }
    }

    public void Dispose()
    {
        try { if (thread.Priority == applied) thread.Priority = previous; }
        catch (Exception ex) when (ex is Win32Exception or ThreadStateException or UnauthorizedAccessException) { }
    }
}
