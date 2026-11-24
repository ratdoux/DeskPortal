using System.Diagnostics;
using System.Net;

namespace DeskLink;

// Benchmarks the same TLS pointer messages used by the app. Neither endpoint installs
// hooks, moves the system cursor, reads the clipboard, or accepts window or file commands.
static class PointerProbe
{
    const int Samples = 1000;
    record Receipt(int Sequence, int Message);

    public static async Task<int> Run(string[] args)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (args is ["--pointer-check"])
            {
                using var invitation = new Invitation(IPAddress.Loopback, 0);
                var accept = invitation.Accept(deadline.Token);
                using var client = await Invitation.Join("127.0.0.1", invitation.Code, deadline.Token, invitation.ListeningPort);
                using var server = await accept;
                var receiver = Receive(client);
                try { await Measure(server, deadline.Token); }
                finally { server.Dispose(); client.Dispose(); await Stop(receiver); }
            }
            else if (args is ["--pointer-listen", var address])
            {
                using var invitation = new Invitation(IPAddress.Parse(address), Invitation.Port);
                Console.WriteLine(invitation.Code);
                Console.Out.Flush();
                using var server = await invitation.Accept(deadline.Token);
                await Measure(server, deadline.Token);
            }
            else if (args is ["--pointer-join", var host])
            {
                var code = await Console.In.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException();
                using var client = await Invitation.Join(host, code, deadline.Token);
                await Stop(Receive(client).WaitAsync(deadline.Token));
            }
            else throw new ArgumentException("Use --pointer-check, --pointer-listen <local-ip>, or --pointer-join <host-ip>.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    static Task Receive(Peer peer) => peer.Run(packet =>
    {
        if (packet.Kind != Kind.Mouse) throw new InvalidDataException("Unexpected probe message.");
        var m = packet.Read<MouseSample>();
        if (m.Y != m.X / 2) throw new InvalidDataException("Pointer coordinates changed in transit.");
        peer.Send(Kind.Hello, new Receipt(m.X, m.Message));
        return Task.CompletedTask;
    });

    static async Task Measure(Peer peer, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var times = new long[Samples + 1];
        var latencies = new List<double>();
        int last = -1;
        bool pressed = false;
        var reader = peer.Run(packet =>
        {
            if (packet.Kind != Kind.Hello) throw new InvalidDataException("Unexpected probe response.");
            var ack = packet.Read<Receipt>();
            if (ack.Message == Win.Move && ack.Sequence > last && ack.Sequence <= Samples && !pressed)
            {
                last = ack.Sequence;
                latencies.Add(Stopwatch.GetElapsedTime(times[last]).TotalMilliseconds);
            }
            else if (ack.Sequence == Samples && last == Samples && ack.Message == Win.LeftDown && !pressed) pressed = true;
            else if (ack.Sequence == Samples && last == Samples && ack.Message == Win.LeftUp && pressed) done.SetResult();
            else throw new InvalidDataException("The final position or button order was lost.");
            return Task.CompletedTask;
        });
        try
        {
            // One second of reports at a 1000 Hz mouse's rate. Use a timed producer
            // thread so Windows timer granularity cannot turn the benchmark into 64 Hz.
            await Task.Run(() =>
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i <= Samples; i++)
                {
                    long target = start + i * Stopwatch.Frequency / Samples;
                    while (Stopwatch.GetTimestamp() < target) Thread.SpinWait(64);
                    token.ThrowIfCancellationRequested();
                    times[i] = Stopwatch.GetTimestamp();
                    peer.Send(Kind.Mouse, new MouseSample(i, i / 2, Win.Move, 0));
                }
                peer.Send(Kind.Mouse, new MouseSample(Samples, Samples / 2, Win.LeftDown, 0));
                peer.Send(Kind.Mouse, new MouseSample(Samples, Samples / 2, Win.LeftUp, 0));
            }, token);
            var first = await Task.WhenAny(done.Task, reader).WaitAsync(token);
            await first;
            if (!done.Task.IsCompletedSuccessfully) throw new IOException("The probe closed before the final click.");
            latencies.Sort();
            int percentile = (int)Math.Ceiling(latencies.Count * 0.95) - 1;
            Console.WriteLine($"Delivered ({last}, {last / 2}) and both button transitions in order. " +
                $"{latencies.Count}/{Samples + 1} motion reports transmitted. " +
                $"Acknowledgment RTT median {latencies[latencies.Count / 2]:F2} ms, p95 {latencies[percentile]:F2} ms.");
        }
        finally { peer.Dispose(); await Stop(reader); }
    }

    static async Task Stop(Task run)
    {
        try { await run; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
    }
}
