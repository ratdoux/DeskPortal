using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeskLink;

sealed record NearbyComputer(Guid Id, string Name, IPAddress Address)
{
    public override string ToString() => $"{Name}  ({Address})";
}

sealed class NearbyComputers : IDisposable
{
    public const int Port = 47832;
    const string Protocol = "DeskLink/2";
    readonly UdpClient socket = new(AddressFamily.InterNetwork);
    readonly Dictionary<Guid, (NearbyComputer Computer, long Seen)> peers = [];
    readonly Guid id;
    readonly string name;
    readonly IPAddress peerAddress;
    public bool Available { get; set; } = true;
    public (Guid Target, Guid Request)? ConnectBack { get; set; }
    public event Action? Changed;
    public event Action<NearbyComputer>? ConnectBackRequested;
    readonly Dictionary<Guid, long> handled = [];
    public IEnumerable<NearbyComputer> Computers => peers.Values.Select(p => p.Computer)
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id);

    public NearbyComputers(Guid id, string name, IPAddress peerAddress)
    {
        this.id = id;
        this.name = name;
        this.peerAddress = peerAddress;
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch { socket.Dispose(); throw; }
    }

    public static bool ValidName(string name) => name.Length is > 0 and <= 63 &&
        name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    public async Task Run(CancellationToken stop)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var receive = Receive(run.Token);
        var advertise = Advertise(run.Token);
        await Task.WhenAny(receive, advertise);
        run.Cancel();
        await Task.WhenAll(receive, advertise);
    }

    async Task Receive(CancellationToken stop)
    {
        while (true)
        {
            UdpReceiveResult packet;
            try { packet = await socket.ReceiveAsync(stop); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            { continue; } // Windows reports ICMP port unreachable while the other app is closed.
            if (!peerAddress.Equals(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 320) continue;
            var fields = Encoding.UTF8.GetString(packet.Buffer).Split('|');
            if (fields.Length is not (4 or 6) || fields[0] != Protocol || !Guid.TryParseExact(fields[1], "N", out var other) ||
                other == id || other == Guid.Empty || !ValidName(fields[2]) || fields[3] is not ("0" or "1")) continue;
            var computer = new NearbyComputer(other, fields[2], packet.RemoteEndPoint.Address);
            if (Available && fields.Length == 6 && fields[3] == "0" && Guid.TryParseExact(fields[4], "N", out var target) &&
                target == id && Guid.TryParseExact(fields[5], "N", out var request) && handled.Count < 256 &&
                handled.TryAdd(request, Environment.TickCount64))
                ConnectBackRequested?.Invoke(computer);
            if (fields[3] == "0")
            {
                if (peers.Remove(other)) Changed?.Invoke();
                continue;
            }
            if (peers.Count >= 256 && !peers.ContainsKey(other)) continue;
            bool changed = !peers.TryGetValue(other, out var prior) || prior.Computer != computer;
            peers[other] = (computer, Environment.TickCount64);
            if (changed) Changed?.Invoke();
        }
    }

    async Task Advertise(CancellationToken stop)
    {
        while (true)
        {
            var back = ConnectBack is { } request ? $"|{request.Target:N}|{request.Request:N}" : "";
            byte[] data = Encoding.UTF8.GetBytes($"{Protocol}|{id:N}|{name}|{(Available ? 1 : 0)}{back}");
            try { await socket.SendAsync(data, new IPEndPoint(peerAddress, Port), stop); }
            catch (SocketException) { /* The peer's network may be temporarily unavailable. */ }
            long now = Environment.TickCount64;
            foreach (var key in handled.Where(p => now - p.Value > 30000).Select(p => p.Key).ToArray()) handled.Remove(key);
            var stale = peers.Where(p => now - p.Value.Seen >= 7500).Select(p => p.Key).ToArray();
            foreach (var key in stale) peers.Remove(key);
            if (stale.Length > 0) Changed?.Invoke();
            await Task.Delay(1500, stop);
        }
    }

    public void Dispose() => socket.Dispose();
}
