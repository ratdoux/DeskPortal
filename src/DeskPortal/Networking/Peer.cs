using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace DeskLink;

enum Kind : byte
{
    Hello = 1, Mouse, Key, Window, Frame, EndWindow, ReturnWindow, WindowInput,
    Clip, FileStart, FileChunk, FileEnd, FileCancel, ReleaseInput, WindowPlaced, HomePreview, ClearHomePreview,
    RecoverWindows, FileDropQuery, FileDropReply, ViewCommand, SharingChanged, SharingAck, WindowCaption, Ping, Pong,
    ResizeWindow, WindowResized, FrameConsumed, TransferSharingChanged, TimedPing, TimedPong, TransferSharingAck,
    // Sent only to a peer whose hello advertises it. An earlier build rejects an unknown kind.
    ViewVisibility, ScreenLayout, MonitorRequest, MonitorClose, MonitorFrame, MonitorInput, MonitorRelease, MonitorPicker,
    MonitorPolicy, MonitorVisibility
}
record Packet(Kind Kind, byte[] Data)
{
    // Local timestamp after the complete payload arrived. Never serialized.
    public long ReceivedAt { get; init; }
    // How long the payload took to arrive once its header had. Never serialized.
    public long ArrivalTicks { get; init; }
    public T Read<T>() => JsonSerializer.Deserialize<T>(Data) ?? throw new InvalidDataException();
    public static Packet Make<T>(Kind kind, T value) => new(kind, JsonSerializer.SerializeToUtf8Bytes(value));
    public static Packet Binary(Kind kind, Guid id, ReadOnlySpan<byte> bytes)
    {
        var data = new byte[16 + bytes.Length];
        id.TryWriteBytes(data);
        bytes.CopyTo(data.AsSpan(16));
        return new(kind, data);
    }
    public Guid Id => Data.Length >= 16 ? new Guid(Data.AsSpan(0, 16)) : throw new InvalidDataException();
}
record DeskHello(int Version, string Name, int Width, int Height, bool PeerRight, Sharing Features, int VideoDecoders = 0,
    bool CaptionUpdates = false, bool LatencyProbe = false, bool ViewVisibility = false,
    DisplayInfo[]? Displays = null, Guid LayoutNode = default, bool Organizer = false, ScreenPlan? SavedLayout = null, bool MonitorProjection = false);
record ScreenLayoutChange(long Revision, ScreenPlan Plan);
record ViewVisibility(Guid Id, bool Visible);
record SharingUpdate(long Revision, Sharing Features);
record MouseSample(int X, int Y, int Message, int Data,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Point? OutsideDesk = null);
record KeySample(int Key, int Scan, bool Up, bool Extended);
record CaptionRegion(int Left, int Top, int Right, int Bottom);
record WindowCaption(Guid Id, CaptionRegion[]? Regions, bool Exact);
record ViewInfo(Guid Id, string Title, int X, int Y, int Width, int Height, Guid? Owner = null, CaptionRegion[]? Caption = null,
    Point? DropPoint = null, Point? OwnerOffset = null, bool ExactCaption = false, bool? Resizable = null);
record WindowSize(Guid Id, int Width, int Height);
record ReturnInfo(Guid Id, int X, int Y);
record ViewCommand(Guid Id, bool Maximize);
record WindowEvent(Guid Id, int Message, int X, int Y, int Data, int Modifiers,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] long Click = 0);

sealed class Peer : IDisposable
{
    readonly SessionLink primary;
    readonly SessionLink? video, transfer;
    readonly CancellationTokenSource stop = new();
    readonly ControlQueue control;
    readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    readonly Channel<Packet> frames = Channel.CreateBounded<Packet>(1);
    readonly WindowFrameFlow flow = new();
    readonly Channel<TransferItem> transfers = Channel.CreateBounded<TransferItem>(32);
    readonly SemaphoreSlim fileSlots = new(4);
    readonly TaskCompletionSource<Exception> videoFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    sealed record TransferItem(Packet? Packet, ControlMessage? Command, CancellationToken Cancel);
    sealed record TimedReply(long Stamp, double PeerDelayMs);
    sealed record PendingReply(long Stamp, long Arrival);
    public int VideoDecoders { get; set; } = VideoCodec.VideoDecoderMask();
    public bool CaptionUpdates { get; set; }
    public bool LatencyProbe { get; set; }
    public bool ViewVisibility { get; set; }
    public bool Split => video != null;
    public TrafficCounter Sent { get; } = new();
    public TrafficCounter Received { get; } = new();
    readonly object probeGate = new();
    long outstandingProbe, lastReply, writeStart;
    double roundTripMs, appDelayMs;
    bool Fresh => lastReply != 0 && Stopwatch.GetElapsedTime(lastReply).TotalSeconds < 5;
    internal long LastProbeReply { get { lock (probeGate) return lastReply; } }
    public double? RoundTripMs { get { lock (probeGate) return Fresh ? roundTripMs : null; } }
    public double? DeskLinkDelayMs { get { lock (probeGate) return Split && Fresh ? appDelayMs : null; } }
    public double? NetworkRttMs => TcpInfo(primary)?.RttMs;
    // The retransmission and timeout counters are totals since the connection opened.
    static (double RttMs, uint Retransmitted, uint Timeouts)? TcpInfo(SessionLink? link)
    {
        // mstcpip.h: SIO_TCP_INFO with version 0 returns TCP_INFO_v0, with RttUs at byte 20,
        // BytesRetrans at 68 and TimeoutEpisodes at 80.
        byte[] info = new byte[128];
        try
        {
            if (link?.Socket.Client is not { } socket) return null;
            int length = socket.IOControl(unchecked((int)0xD8000027), new byte[4], info);
            if (length < 84) return length >= 24 ? (BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20)) / 1000d, 0u, 0u) : null;
            return (BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20)) / 1000d,
                BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(68)), BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(80)));
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException) { return null; }
    }
    // What Windows reports for the connections a stalled frame depends on.
    public string TcpSummary()
    {
        static string One(string name, SessionLink? link) => TcpInfo(link) is { } tcp
            ? $"{name} TCP: RTT {tcp.RttMs:F1} ms, {tcp.Retransmitted:N0} bytes retransmitted, {tcp.Timeouts} timeouts"
            : $"{name} TCP: unavailable";
        return video == null ? One("Shared", primary) : $"{One("Video", video)}     {One("Input", primary)}";
    }
    // A frame's write to the video connection, or its round trip to the peer and back, took
    // long enough to notice. Called on a connection thread with the window, the stage and its duration.
    public Action<Guid, string, long>? SlowFrame { get; set; }
    public void ProbeLatency()
    {
        if (!LatencyProbe || Token.IsCancellationRequested) return;
        long now = Stopwatch.GetTimestamp();
        lock (probeGate)
        {
            if (outstandingProbe != 0 && Stopwatch.GetElapsedTime(outstandingProbe, now).TotalSeconds < 5) return;
            outstandingProbe = now;
            writeStart = 0;
        }
        Send(Split ? Kind.TimedPing : Kind.Ping, now);
    }
    void AcceptReply(long stamp, double remoteDelay = 0)
    {
        lock (probeGate)
        {
            if (outstandingProbe == 0 || stamp != outstandingProbe) return;
            lastReply = Stopwatch.GetTimestamp();
            roundTripMs = Stopwatch.GetElapsedTime(stamp, lastReply).TotalMilliseconds;
            if (Split) appDelayMs = Stopwatch.GetElapsedTime(stamp, writeStart).TotalMilliseconds + remoteDelay;
            outstandingProbe = 0;
        }
    }
    public CancellationToken Token => stop.Token;
    public IPAddress? RemoteAddress { get; }
    public Peer(TcpClient client, SslStream stream, SessionLink? video = null, SessionLink? transfer = null)
    {
        RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        primary = new(client, stream);
        this.video = video;
        this.transfer = transfer;
        control = new ControlQueue(() => wake.Writer.TryWrite(true));
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 4);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
    }
    static bool IsTransfer(Kind k) => k is Kind.Clip or Kind.FileStart or Kind.FileChunk or Kind.FileEnd or Kind.FileCancel
        or Kind.FileDropQuery or Kind.FileDropReply or Kind.TransferSharingChanged or Kind.TransferSharingAck;
    internal static bool IsData(Kind k) => k is Kind.Frame or Kind.MonitorFrame || IsTransfer(k);
    public void Send<T>(Kind kind, T data) where T : notnull
    {
        if (stop.IsCancellationRequested) return;
        var message = new ControlMessage(kind, data);
        if (Split && (IsTransfer(kind) || kind == Kind.SharingChanged))
        {
            // Each channel applies and acknowledges its own permissions in wire order.
            var command = kind == Kind.SharingChanged ? new ControlMessage(Kind.TransferSharingChanged, data) : message;
            if (!transfers.Writer.TryWrite(new(null, command, default))) { Dispose(); return; }
            if (kind != Kind.SharingChanged) return;
        }
        if (!control.Enqueue(message)) Dispose();
    }
    public long WaitForFrameSpace(Guid id, CancellationToken token) => Split ? flow.Reserve(id, token, take: false) : 0;
    public long Frame(Guid id, byte[] frame, CancellationToken capture = default, bool monitor = false)
    {
        capture.ThrowIfCancellationRequested();
        long wait = Split ? flow.Reserve(id, capture) : 0;
        try
        {
            var packet = Packet.Binary(monitor ? Kind.MonitorFrame : Kind.Frame, id, frame);
            if (!frames.Writer.TryWrite(packet))
            {
                long start = Stopwatch.GetTimestamp();
                frames.Writer.WriteAsync(packet, capture).AsTask().GetAwaiter().GetResult();
                wait += Stopwatch.GetTimestamp() - start;
            }
            if (!Split) wake.Writer.TryWrite(true);
            return wait;
        }
        catch { if (Split) flow.Release(id, unsent: true); throw; }
    }
    public async ValueTask Bulk(Packet packet, CancellationToken transfer = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(transfer, Token);
        await fileSlots.WaitAsync(cancel.Token).ConfigureAwait(false);
        try { await transfers.Writer.WriteAsync(new(packet, null, transfer), cancel.Token).ConfigureAwait(false); }
        catch { fileSlots.Release(); cancel.Token.ThrowIfCancellationRequested(); throw; }
        if (!Split) wake.Writer.TryWrite(true);
    }
    public async Task Run(Func<Packet, Task> receive)
    {
        var wire = new PacketWire(primary.Stream, Sent, Received);
        var workers = new List<Task> {
            Task.Run(() => ControlWriter(wire)),
            Split ? Task.Factory.StartNew(() => InputReader(wire, receive), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default)
                  : Task.Run(() => ControlReader(wire, receive))
        };
        if (video != null && transfer != null)
        {
            var pictures = new PacketWire(video.Stream, Sent, Received);
            var files = new PacketWire(transfer.Stream, Sent, Received);
            workers.Add(Task.Run(async () =>
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(Token))
                {
                    long start = Stopwatch.GetTimestamp();
                    await pictures.Write(frame, Token);
                    long spent = Stopwatch.GetTimestamp() - start;
                    if (spent >= Stopwatch.Frequency / 10) SlowFrame?.Invoke(frame.Id, "Video write", spent);
                }
            }));
            workers.Add(Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await pictures.Read(Token);
                    if (frame.Kind is not (Kind.Frame or Kind.MonitorFrame)) throw new InvalidDataException("Unexpected video message.");
                    flow.Dispatch(frame, receive, id => Send(Kind.FrameConsumed, id), videoFailure, Token);
                }
            }));
            workers.Add(Task.Run(async () =>
            {
                await foreach (var item in transfers.Reader.ReadAllAsync(Token)) await WriteTransfer(files, item);
            }));
            workers.Add(Task.Run(() => TransferReader(files, receive)));
        }
        try
        {
            var first = await Task.WhenAny(workers.Append(videoFailure.Task)).ConfigureAwait(false);
            if (first == videoFailure.Task) throw await videoFailure.Task.ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        finally
        {
            Dispose();
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
        }
    }
    async Task ControlReader(PacketWire wire, Func<Packet, Task> receive)
    {
        while (true) await HandleControl(await wire.Read(Token), receive);
    }
    void InputReader(PacketWire wire, Func<Packet, Task> receive)
    {
        // Closing the session socket interrupts a pending synchronous read.
        using var priority = InputThreadPriority.Raise();
        while (!Token.IsCancellationRequested)
            HandleControl(wire.ReadBlocking(), receive).GetAwaiter().GetResult();
    }
    async Task HandleControl(Packet packet, Func<Packet, Task> receive)
    {
        if (Split && IsData(packet.Kind))
            throw new InvalidDataException("Unexpected input-channel message.");
        switch (packet.Kind)
        {
            case Kind.TimedPing when Split && LatencyProbe:
                long arrival = Stopwatch.GetTimestamp();
                await receive(packet).WaitAsync(Token);
                Send(Kind.TimedPong, new PendingReply(packet.Read<long>(), arrival));
                break;
            case Kind.TimedPong when Split && LatencyProbe:
                var reply = packet.Read<TimedReply>();
                AcceptReply(reply.Stamp, reply.PeerDelayMs);
                break;
            case Kind.Ping when !Split && LatencyProbe: Send(Kind.Pong, packet.Read<long>()); break;
            case Kind.Pong when !Split && LatencyProbe: AcceptReply(packet.Read<long>()); break;
            case Kind.FrameConsumed when Split:
                var window = packet.Read<Guid>();
                long outstanding = flow.Release(window);
                // A quarter of a second is several times a healthy round trip, even for a 4K frame.
                if (outstanding >= Stopwatch.Frequency / 4) SlowFrame?.Invoke(window, "Frame round trip", outstanding);
                break;
            default:
                await receive(packet).WaitAsync(Token);
                break;
        }
    }
    async Task TransferReader(PacketWire wire, Func<Packet, Task> receive)
    {
        while (true)
        {
            var packet = await wire.Read(Token);
            if (!IsTransfer(packet.Kind)) throw new InvalidDataException("Unexpected transfer message.");
            await receive(packet).WaitAsync(Token);
        }
    }
    async ValueTask WriteTransfer(PacketWire wire, TransferItem item)
    {
        try
        {
            if (!item.Cancel.IsCancellationRequested)
                await wire.Write(item.Packet ?? item.Command!.Value.Encode(), Token);
        }
        finally { if (item.Packet != null) fileSlots.Release(); }
    }
    async Task ControlWriter(PacketWire wire)
    {
        async Task DrainControls()
        {
            for (int i = 0; i < 64; i++)
            {
                if (!control.TryTake(out var command)) return;
                if (command.Kind == Kind.TimedPing)
                {
                    lock (probeGate)
                        if ((long)command.Value == outstandingProbe) writeStart = Stopwatch.GetTimestamp();
                }
                else if (command.Value is PendingReply reply)
                    command = new(Kind.TimedPong, new TimedReply(reply.Stamp, Stopwatch.GetElapsedTime(reply.Arrival).TotalMilliseconds));
                await wire.Write(command.Encode(), Token);
            }
            wake.Writer.TryWrite(true);
        }
        while (await wake.Reader.WaitToReadAsync(Token))
        {
            wake.Reader.TryRead(out _);
            await DrainControls();
            if (Split) continue;
            if (frames.Reader.TryRead(out var frame)) { await wire.Write(frame, Token); await DrainControls(); }
            if (transfers.Reader.TryRead(out var chunk)) await WriteTransfer(wire, chunk);
            if (transfers.Reader.TryPeek(out _) || frames.Reader.TryPeek(out _)) wake.Writer.TryWrite(true);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
        flow.Close();
        frames.Writer.TryComplete();
        transfers.Writer.TryComplete();
        control.Dispose();
        primary.Dispose();
        video?.Dispose();
        transfer?.Dispose();
    }
    int closed;
}
