using System.Threading.Channels;

namespace DeskLink;

// Each end drives the other independently. Holding these callbacks open reproduces a
// slow decoder and a slow file consumer without relying on a particular codec or disk.
sealed class TransportChecks
{
    static readonly Guid slowWindow = new("98938054-4c73-48df-ad92-d8b654a5b379");
    static readonly Guid fastWindow = new("81f1638f-a9c8-4326-b604-7157142e6623");
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource decoding = Signal(), writingFile = Signal(), resume = Signal(), partnerReady = Signal();
    readonly Channel<int> delivered = Channel.CreateUnbounded<int>();
    readonly List<byte> sequence = [];
    readonly TaskCompletionSource allFrames = Signal(), secondWindow = Signal();
    readonly TaskCompletionSource inputPolicy = Signal();
    volatile bool delayProbe;

    public async Task Dispatch(Packet packet, Func<Packet, Task> normal)
    {
        if (packet.Kind == Kind.Frame && packet.Id == slowWindow)
        {
            byte index = packet.Data[16];
            if (index == 1) { decoding.TrySetResult(); await resume.Task; }
            sequence.Add(index);
            if (index == 3) allFrames.TrySetResult();
        }
        else if (packet.Kind == Kind.Frame && packet.Id == fastWindow) secondWindow.TrySetResult();
        else if (packet.Kind == Kind.FileChunk && packet.Id == slowWindow)
        { writingFile.TrySetResult(); await resume.Task; }
        else if (packet.Kind == Kind.Mouse) delivered.Writer.TryWrite(packet.Read<MouseSample>().Message);
        else if (packet.Kind == Kind.Key) delivered.Writer.TryWrite(packet.Read<KeySample>().Key);
        else if (packet.Kind == Kind.SharingChanged) inputPolicy.TrySetResult();
        else if (packet.Kind == Kind.TimedPing && delayProbe) { await Task.Delay(80); await normal(packet); }
        else if (packet.Kind == Kind.Hello && packet.Read<string>() == "isolation-verified") partnerReady.TrySetResult();
        else await normal(packet);
    }

    public async Task Check(Peer peer, CancellationToken stop)
    {
        if (!peer.Split) throw new Exception("Expected independent input, video and transfer connections.");
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlier = peer.SlowFrame;
        peer.SlowFrame = (id, stage, ticks) =>
        {
            if (id == slowWindow) held.TrySetResult(stage); else earlier?.Invoke(id, stage, ticks);
        };
        try
        {
            peer.Frame(slowWindow, [1], stop);
            peer.Frame(slowWindow, [2], stop);
            await peer.Bulk(Packet.Binary(Kind.FileChunk, slowWindow, [1]), stop);
            await Task.WhenAll(decoding.Task, writingFile.Task).WaitAsync(stop);
            var third = Task.Run(() => peer.Frame(slowWindow, [3], stop), stop);
            peer.Frame(fastWindow, [1], stop);
            peer.Send(Kind.SharingChanged, new SharingUpdate(1, Sharing.All));
            peer.Send(Kind.Mouse, new MouseSample(8, 9, Win.LeftDown, 0));
            peer.Send(Kind.Key, new KeySample(65, 30, false, false));
            await Task.WhenAll(secondWindow.Task, inputPolicy.Task).WaitAsync(stop);
            if (await delivered.Reader.ReadAsync(stop) != Win.LeftDown || await delivered.Reader.ReadAsync(stop) != 65)
                throw new Exception("Click and key arrived out of order.");
            if (third.IsCompleted) throw new Exception("The stalled window exceeded its frame limit.");
            delayProbe = true;
            peer.Send(Kind.Hello, "isolation-verified");
            await partnerReady.Task.WaitAsync(stop);
            peer.LatencyProbe = true;
            peer.ProbeLatency();
            while (peer.DeskLinkDelayMs == null) await Task.Delay(5, stop);
            if (peer.DeskLinkDelayMs < 70 || peer.RoundTripMs + 0.1 < peer.DeskLinkDelayMs || peer.NetworkRttMs == null)
                throw new Exception($"Unexpected timing: TCP {peer.NetworkRttMs}, DeskPortal {peer.DeskLinkDelayMs}, total {peer.RoundTripMs}.");
            Console.WriteLine($"PASS  Click, key and second window arrived with a held decoder and file transfer. Injected 80 ms dispatch delay: TCP {peer.NetworkRttMs:F2} ms, DeskPortal {peer.DeskLinkDelayMs:F2} ms, full probe {peer.RoundTripMs:F2} ms.");
            // Hold the other end's frames past the quarter second that counts as a stalled round trip.
            await Task.Delay(300, stop);
            resume.TrySetResult();
            await third.WaitAsync(stop);
            await allFrames.Task.WaitAsync(stop);
            if (!sequence.SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("Dependent frames arrived out of order.");
            if (await held.Task.WaitAsync(stop) != "Frame round trip" || !peer.TcpSummary().Contains("timeouts"))
                throw new Exception($"A frame held by the peer was not reported with the connection state: {peer.TcpSummary()}");
            Console.WriteLine($"PASS  A frame the peer held for 300 ms was reported as a slow round trip. {peer.TcpSummary()}");
        }
        finally { resume.TrySetResult(); peer.SlowFrame = earlier; }
    }
    public void End() => delayProbe = false;
}
