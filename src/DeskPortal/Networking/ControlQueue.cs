using System.Diagnostics;
using System.Text.Json;

namespace DeskLink;

// Coordinates on the wire are absolute. Consecutive reports for the same pointer
// can replace each other, but a key, button, wheel or focus change is an ordering boundary.
readonly record struct ControlMessage(Kind Kind, object Value)
{
    bool IsGeometry => Kind is Kind.Window or Kind.HomePreview && Value is ViewInfo { DropPoint: null };
    public bool IsMotion => Value is MouseSample { Message: Win.Move } or WindowEvent { Message: Win.Move } || IsGeometry;
    public bool IsDesktopWheel => Value is MouseSample { Message: Win.Wheel or Win.HorizontalWheel };
    public bool TryAddWheel(ControlMessage next, out ControlMessage combined)
    {
        combined = default;
        if (!IsDesktopWheel || Value is not MouseSample a || next.Value is not MouseSample b ||
            a.Message != b.Message || a.X != b.X || a.Y != b.Y || a.OutsideDesk != b.OutsideDesk ||
            Math.Sign(a.Data) != Math.Sign(b.Data)) return false;
        long total = (long)a.Data + b.Data;
        if (total is < short.MinValue or > short.MaxValue) return false;
        combined = new(Kind.Mouse, a with { Data = (int)total });
        return true;
    }
    public bool TravelsWith(ControlMessage other) =>
        (IsGeometry && other.Value is MouseSample { Message: Win.Move }) ||
        (Value is MouseSample { Message: Win.Move } && other.IsGeometry);
    public bool CanReplace(ControlMessage prior) => (Value, prior.Value) switch
    {
        (MouseSample { Message: Win.Move }, MouseSample { Message: Win.Move }) => true,
        (WindowEvent { Message: Win.Move } a, WindowEvent { Message: Win.Move } b) =>
            a.Id == b.Id && a.Modifiers == b.Modifiers,
        (ViewInfo a, ViewInfo b) => IsGeometry && Kind == prior.Kind && a.Id == b.Id,
        _ => false
    };
    public Packet Encode() => new(Kind, JsonSerializer.SerializeToUtf8Bytes(Value, Value.GetType()));
}

sealed class ControlQueue : IDisposable
{
    const int Capacity = 1024;
    const int PointerReportsPerSecond = 240, WindowReportsPerSecond = 120;
    readonly LinkedList<ControlMessage> ordered = [];
    readonly Action wake;
    readonly System.Threading.Timer timer;
    readonly object gate = new();
    // While dragging, absolute cursor and window positions arrive interleaved.
    // Keep the latest of each in arrival order until an input boundary.
    readonly List<ControlMessage> latest = new(2);
    // A standalone desktop move can travel more often than updates that also lay out a window.
    long MotionInterval => Stopwatch.Frequency /
        (latest.Count == 1 && latest[0].Value is MouseSample { Message: Win.Move }
            ? PointerReportsPerSecond : WindowReportsPerSecond);
    long lastMove;
    bool timerArmed, disposed;

    public ControlQueue(Action wake)
    {
        this.wake = wake;
        timer = new System.Threading.Timer(_ =>
        {
            lock (gate) timerArmed = false;
            wake();
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Enqueue(ControlMessage message)
    {
        lock (gate)
        {
            if (disposed) return false;
            // The first detent wakes the writer immediately. Combine only unsent
            // reports at its tail so keys, buttons, moves and reversals stay ordered.
            if (latest.Count == 0 && ordered.Last is { } tail && tail.Value.TryAddWheel(message, out var wheel))
            {
                tail.Value = wheel;
                return true;
            }
            int replace = -1;
            for (int i = 0; i < latest.Count; i++)
                if (message.CanReplace(latest[i])) { replace = i; break; }
            if (replace >= 0)
            {
                latest.RemoveAt(replace);
                latest.Add(message);
                // Hardware reports arrive more often than the default Windows timer
                // tick. Wake at the frame deadline even if the tail timer is still asleep.
                if (Stopwatch.GetTimestamp() - lastMove < MotionInterval) return true;
            }
            else
            {
                if (ordered.Count + latest.Count >= Capacity) return false;
                if (!(latest.Count == 1 && message.TravelsWith(latest[0])))
                {
                    foreach (var before in latest) ordered.AddLast(before);
                    latest.Clear();
                }
                if (message.IsMotion) latest.Add(message);
                else ordered.AddLast(message);
            }
        }
        wake();
        return true;
    }

    public bool TryTake(out ControlMessage message)
    {
        lock (gate)
        {
            message = default;
            if (ordered.First is { } first)
            {
                message = first.Value;
                ordered.RemoveFirst();
                if (message.IsMotion) lastMove = Stopwatch.GetTimestamp();
                return true;
            }
            if (disposed || latest.Count == 0) return false;
            long now = Stopwatch.GetTimestamp();
            long remaining = MotionInterval - (now - lastMove);
            if (remaining > 0)
            {
                if (!timerArmed)
                {
                    timerArmed = true;
                    int ms = (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency);
                    timer.Change(ms, Timeout.Infinite);
                }
                return false;
            }
            lastMove = now;
            message = latest[0];
            if (latest.Count == 2) ordered.AddLast(latest[1]);
            latest.Clear();
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; timer.Dispose(); }
    }
}
