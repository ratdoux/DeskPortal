using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DeskLink;

// Membership belongs to the UI thread. Each confirmed pair has separate input and video links.
sealed class ScreenPeers(Form owner, Guid device, Func<DeskSettings> settings, MonitorInput input,
    Action<string> status, Action changed, ConfirmedPairing.Confirm confirm) : IDisposable
{
    public const int Port = 47833;
    static readonly string path = Path.Combine(AppStorage.Root, "screen-peers.txt");
    sealed class Connection : IDisposable
    {
        public readonly CancellationTokenSource Stop = new();
        public Channel<TcpClient>? Links;
        public Peer? Peer;
        public MonitorSession? Screen;
        public Sharing Remote;
        public void Dispose()
        {
            Stop.Cancel(); Screen?.Dispose(); Screen = null; Peer?.Dispose();
            Links?.Writer.TryComplete();
            while (Links?.Reader.TryRead(out var socket) == true) socket.Dispose();
        }
    }
    readonly Dictionary<IPAddress, Connection> connections = [];
    IPAddress[] allowed = [];
    TcpListener? listener;
    bool closed, configuring;
    public bool Configuring => configuring;
    public bool Ready(IPAddress address) => allowed.Contains(address) && listener != null;
    public IEnumerable<IPAddress> Addresses => allowed;
    public IEnumerable<MonitorSession> Sessions => connections.Values.Select(c => c.Screen).OfType<MonitorSession>();
    public bool Busy(IPAddress address) => connections.ContainsKey(address);
    public string State(IPAddress address) => !connections.TryGetValue(address, out var c) ? "Ready to pair" :
        c.Screen?.Name ?? "Pairing…";

    public async Task Initialize()
    {
        if (!File.Exists(path)) return;
        var entries = File.ReadAllLines(path).Select(PeerFirewall.ParsePeer).ToArray();
        if (entries.Any(e => e == null)) throw new InvalidDataException("Invalid saved screen peer address.");
        await Configure(entries.OfType<IPAddress>().Distinct().ToArray(), false);
    }
    public async Task Configure(IPAddress[] addresses, bool save = true)
    {
        if (closed || configuring) return;
        configuring = true; changed();
        try
        {
            await PeerFirewall.EnsureScreens(PeerFirewall.ApplicationPath(), addresses);
            if (closed) return;
            foreach (var ip in connections.Keys.Except(addresses).ToArray()) Disconnect(ip);
            allowed = addresses.ToArray();
            if (save)
            {
                Directory.CreateDirectory(AppStorage.Root);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllLines(temp, allowed.Select(ip => ip.ToString())); File.Move(temp, path, true); }
                finally { File.Delete(temp); }
            }
            ListenOn(new(IPAddress.Any, Port), allowed);
        }
        finally { configuring = false; changed(); }
    }
    internal int ListenOn(IPEndPoint bind, IPAddress[] addresses)
    {
        allowed = addresses;
        if (allowed.Length == 0) { listener?.Stop(); listener = null; return 0; }
        if (listener == null)
        {
            var server = new TcpListener(bind);
            server.Start(); listener = server; _ = Listen(server);
        }
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    async Task Listen(TcpListener server)
    {
        try
        {
            while (server == listener)
            {
                var socket = await server.AcceptTcpClientAsync();
                var address = ((IPEndPoint)socket.Client.RemoteEndPoint!).Address;
                if (closed || !allowed.Contains(address)) { socket.Dispose(); continue; }
                if (connections.TryGetValue(address, out var c))
                {
                    if (c.Links?.Writer.TryWrite(socket) != true) socket.Dispose();
                }
                else _ = Connect(address, socket);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (server == listener && !closed) { listener = null; server.Stop(); status($"Screen pairing listener stopped. {ex.Message}"); changed(); }
        }
    }
    public async Task Connect(IPAddress address, TcpClient? incoming = null)
    {
        if (closed || !Ready(address) || Busy(address)) { incoming?.Dispose(); return; }
        var c = new Connection();
        connections.Add(address, c);
        var stop = c.Stop.Token;
        using var socket = incoming ?? new TcpClient(AddressFamily.InterNetwork);
        changed();
        try
        {
            if (incoming == null)
            {
                using var dial = CancellationTokenSource.CreateLinkedTokenSource(stop);
                dial.CancelAfter(TimeSpan.FromSeconds(6));
                var local = (IPEndPoint)listener!.LocalEndpoint;
                socket.Client.Bind(new IPEndPoint(local.Address, 0));
                await socket.ConnectAsync(address, local.Port, dial.Token);
            }
            else c.Links = Channel.CreateBounded<TcpClient>(2);
            string? verified = null;
            async Task<bool> Compare(string name, string code, CancellationToken token)
            {
                bool accepted = await confirm(name, code, token);
                if (accepted) verified = name;
                return accepted;
            }
            var links = c.Links;
            using var peer = await ConfirmedPairing.Pair(socket, incoming == null, device, Environment.MachineName, null,
                Compare, stop, links == null ? null : links.Reader.ReadAsync);
            c.Peer = peer;
            links?.Writer.TryComplete(); c.Links = null;
            while (links?.Reader.TryRead(out var unused) == true) unused.Dispose();
            stop.ThrowIfCancellationRequested();
            var bounds = SystemInformation.VirtualScreen;
            peer.Send(Kind.Hello, new DeskHello(2, Environment.MachineName, bounds.Width, bounds.Height, false,
                settings().Features, VideoCodec.VideoDecoderMask(), Displays: DisplayInfo.Read(bounds), MonitorProjection: true));
            using var controls = new ControlDispatchWindow();
            using var cancel = stop.Register(peer.Dispose);
            await Task.Run(() => peer.Run(packet =>
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async void Apply()
                {
                    try
                    {
                        if (stop.IsCancellationRequested || peer.Token.IsCancellationRequested) { done.TrySetCanceled(); return; }
                        if (packet.Kind == Kind.Hello && c.Screen == null)
                        {
                            var hello = packet.Read<DeskHello>();
                            if (!hello.MonitorProjection || hello.Name != verified || hello.Version != 2 ||
                                !DisplayInfo.Valid(hello.Displays, new(hello.Width, hello.Height)))
                                throw new InvalidDataException("The paired PC did not provide a valid monitor inventory.");
                            peer.VideoDecoders = hello.VideoDecoders; c.Remote = hello.Features;
                            c.Screen = new(peer, hello.Name, hello.Displays!, owner, input, status, changed);
                            ApplySettings(c); changed();
                        }
                        else if (c.Screen == null) throw new InvalidDataException("Expected monitor inventory.");
                        else if (packet.Kind == Kind.MonitorPolicy)
                        {
                            c.Remote = packet.Read<Sharing>(); ApplySettings(c); changed();
                        }
                        else await c.Screen.Receive(packet, controls);
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                }
                if (packet.Kind == Kind.MonitorFrame) owner.BeginInvoke((Action)Apply);
                else controls.Post(Apply);
                return done.Task.WaitAsync(peer.Token);
            }), stop);
        }
        catch (Exception ex) { if (!closed && !stop.IsCancellationRequested) status($"Screen peer {address} disconnected. {ex.Message}"); }
        finally
        {
            if (connections.GetValueOrDefault(address) == c) connections.Remove(address);
            c.Dispose(); c.Stop.Dispose(); if (!closed) changed();
        }
    }
    void ApplySettings(Connection c) => c.Screen?.Configure(settings().Features & c.Remote, settings().Stream);
    public void SettingsChanged()
    {
        foreach (var c in connections.Values)
        {
            if (c.Peer == null) continue;
            ApplySettings(c); c.Peer.Send(Kind.MonitorPolicy, settings().Features);
        }
        changed();
    }
    public void Disconnect(IPAddress address)
    {
        if (connections.Remove(address, out var c)) c.Dispose();
        changed();
    }
    public void DisconnectAll()
    {
        foreach (var ip in connections.Keys.ToArray()) Disconnect(ip);
    }
    public void Dispose() { closed = true; listener?.Stop(); listener = null; DisconnectAll(); }
}
