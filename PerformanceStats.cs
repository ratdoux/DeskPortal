using System.Diagnostics;

namespace DeskLink;

readonly record struct TrafficBytes(long Video, long Files, long Clipboard, long Control)
{
    public long Total => Video + Files + Clipboard + Control;
    public static TrafficBytes operator -(TrafficBytes a, TrafficBytes b) =>
        new(a.Video - b.Video, a.Files - b.Files, a.Clipboard - b.Clipboard, a.Control - b.Control);
}

sealed class TrafficCounter
{
    long video, files, clipboard, control;
    public void Record(Kind kind, int bytes)
    {
        switch (kind)
        {
            case Kind.Frame: Interlocked.Add(ref video, bytes); break;
            case Kind.FileStart: case Kind.FileChunk: case Kind.FileEnd: case Kind.FileCancel:
            case Kind.FileDropQuery: case Kind.FileDropReply: Interlocked.Add(ref files, bytes); break;
            case Kind.Clip: Interlocked.Add(ref clipboard, bytes); break;
            default: Interlocked.Add(ref control, bytes); break;
        }
    }
    public TrafficBytes Read() => new(Interlocked.Read(ref video), Interlocked.Read(ref files),
        Interlocked.Read(ref clipboard), Interlocked.Read(ref control));
}

sealed record WindowPerformance(Guid Id, string Title, bool Sending, string Codec, Size Pixels,
    double Fps, double Mbps, double? CodecMs, double? QueueMs, double? PaintedFps, double? SetupMs, string? LastSlowStage, double? ClickMs);

// One capture worker or ordered decoder writes these counters. The UI takes
// a consistent interval once per second, independent of the stats window.
sealed class FrameMeter
{
    readonly object gate = new();
    long since = Stopwatch.GetTimestamp(), bytes, frames, codecTicks, queueTicks;
    string codec = "Starting…";
    Size pixels;
    double? lastSetupMs;
    double? lastClickMs;
    public void Click(string stage, double ms, string? detail = null)
    {
        lock (gate) lastClickMs = ms;
        ObserveStage(stage, (long)(ms * Stopwatch.Frequency / 1000d), detail);
    }
    string? lastSlowStage;
    // Names the window and direction in the slow-stage log. A meter without one is not logged.
    public string? Label { get; set; }
    public void ObserveStage(string stage, long ticks, string? detail = null)
    {
        if (ticks < Stopwatch.Frequency / 10) return;
        double ms = ticks * 1000d / Stopwatch.Frequency;
        lock (gate) lastSlowStage = $"{DateTime.Now:HH:mm:ss}  {stage}  {ms:F0} ms";
        if (Label is { } label) StallLog.Write($"{label}  {stage}  {ms:F0} ms{(detail == null ? "" : "  " + detail)}");
    }
    public void Record(int length, string mode, Size size, long processing, long waiting = 0, long setup = 0)
    {
        lock (gate)
        {
            bytes += length; frames++; codecTicks += processing; queueTicks += waiting;
            if (setup > 0) lastSetupMs = setup * 1000d / Stopwatch.Frequency;
            else if (mode != codec) lastSetupMs = null;
            codec = mode; pixels = size;
        }
    }
    public WindowPerformance Sample(Guid id, string title, bool sending, long now, long painted = 0)
    {
        lock (gate)
        {
            double seconds = Math.Max(1, now - since) / (double)Stopwatch.Frequency;
            double? AverageMs(long ticks) => frames == 0 ? null : ticks * 1000d / Stopwatch.Frequency / frames;
            var row = new WindowPerformance(id, title, sending, codec, pixels, frames / seconds,
                PerformanceSample.Mbps(bytes, seconds), AverageMs(codecTicks), sending ? AverageMs(queueTicks) : null,
                sending ? null : painted / seconds, lastSetupMs, lastSlowStage, lastClickMs);
            bytes = frames = codecTicks = queueTicks = 0; since = now;
            return row;
        }
    }
}

readonly record struct BandwidthPoint(long Timestamp, double Send, double Receive);
sealed record PerformanceSample(double Seconds, TrafficBytes Sent, TrafficBytes Received, TrafficBytes SentDelta,
    TrafficBytes ReceivedDelta, double? RoundTripMs, double? NetworkRttMs, double? DeskLinkDelayMs,
    double CpuPercent, long WorkingSet, WindowPerformance[] Windows, string? Transport = null)
{
    public static double Mbps(long bytes, double seconds) => bytes * 8d / 1_000_000 / seconds;
    public double SendRate => Mbps(SentDelta.Total, Seconds);
    public double ReceiveRate => Mbps(ReceivedDelta.Total, Seconds);
}

sealed class SessionPerformance(Peer peer, WindowDesk windows, string computer) : IDisposable
{
    readonly Process process = Process.GetCurrentProcess();
    readonly Queue<BandwidthPoint> history = new();
    long previous = Stopwatch.GetTimestamp();
    TrafficBytes prevSent = peer.Sent.Read(), prevReceived = peer.Received.Read();
    TimeSpan? cpu;
    public string Computer => computer;
    public bool IndependentConnections => peer.Split;
    public IReadOnlyCollection<BandwidthPoint> History => history;
    public PerformanceSample? Latest { get; private set; }
    public void Start()
    {
        cpu = process.TotalProcessorTime;
        peer.ProbeLatency();
    }
    public PerformanceSample Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Max(1, now - previous) / (double)Stopwatch.Frequency;
        var sent = peer.Sent.Read(); var received = peer.Received.Read();
        // Both figures are read live. Refreshing the Process object would snapshot every
        // process on the PC, once a second, on the thread that runs the input hooks.
        var currentCpu = process.TotalProcessorTime;
        double percent = 100 * (currentCpu - (cpu ?? currentCpu)).TotalSeconds / seconds / Environment.ProcessorCount;
        Latest = new(seconds, sent, received, sent - prevSent, received - prevReceived, peer.RoundTripMs,
            peer.NetworkRttMs, peer.DeskLinkDelayMs, Math.Clamp(percent, 0, 100), Environment.WorkingSet, windows.SamplePerformance(now), peer.TcpSummary());
        previous = now; prevSent = sent; prevReceived = received; cpu = currentCpu;
        history.Enqueue(new(now, Latest.SendRate, Latest.ReceiveRate));
        while (history.TryPeek(out var point) && (now - point.Timestamp > 60 * Stopwatch.Frequency || history.Count > 65)) history.Dequeue();
        peer.ProbeLatency();
        return Latest;
    }
    public void Dispose() => process.Dispose();
}
