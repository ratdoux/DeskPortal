namespace DeskLink;

sealed record MonitorRequest(Guid Id, string Display);
sealed record MonitorChoice(MonitorSession Session, DisplayInfo Monitor, int Number)
{
    public override string ToString() => $"{Session.Name} ({Session.Address})   ·   Display {Number}   ·   {Monitor.Bounds.Width} × {Monitor.Bounds.Height}{(Monitor.Primary ? "   ·   Primary" : "")}";
}

// One instance belongs to one confirmed TLS session. IDs and permissions never cross peers.
sealed class MonitorSession(Peer peer, string name, DisplayInfo[] displays, Control owner, MonitorInput input,
    Action<string> status, Action changed) : IDisposable
{
    sealed record View(string Display, StreamWindow Window, ReceivedVideo Video);
    sealed class Source(string display, CaptureStream capture, Rectangle bounds)
    {
        public string Display = display;
        public CaptureStream Capture = capture;
        public Rectangle Bounds = bounds;
        public MonitorInput.Controller? Controller;
        public CancellationTokenRegistration Disconnect;
    }
    readonly Dictionary<Guid, View> views = [];
    readonly Dictionary<Guid, Source> sources = [];
    Sharing permissions;
    StreamOptions options = new();
    bool closed;
    public string Name => name;
    public System.Net.IPAddress? Address => peer.RemoteAddress;
    public bool Available => !closed && permissions.HasFlag(Sharing.Windows);
    public int Sending => sources.Count;
    public IEnumerable<MonitorChoice> Choices => Available ? displays.Select((d, i) => new MonitorChoice(this, d, i + 1)) : [];
    public static bool Handles(Kind kind) => kind is Kind.MonitorRequest or Kind.MonitorClose or Kind.MonitorFrame or
        Kind.MonitorInput or Kind.MonitorRelease or Kind.MonitorVisibility;

    public void Configure(Sharing features, StreamOptions stream)
    {
        permissions = features; options = stream;
        if (!Available) StopAll();
        foreach (var source in sources.Values)
        {
            source.Capture.Options = stream;
            if (!features.HasFlag(Sharing.Mouse)) Revoke(source);
        }
    }
    public void Open(DisplayInfo display, Rectangle work)
    {
        if (!Available || !displays.Contains(display)) return;
        if (views.Values.FirstOrDefault(v => v.Display == display.Id) is { } existing)
        {
            if (existing.Window.WindowState == FormWindowState.Minimized) existing.Window.WindowState = FormWindowState.Normal;
            existing.Window.Activate(); return;
        }
        if (views.Count >= 8) { status("Close a projection before opening another."); return; }
        Guid id = Guid.NewGuid();
        var info = new ViewInfo(id, $"{name} · Display {Array.IndexOf(displays, display) + 1}", 0, 0, display.Bounds.Width, display.Bounds.Height, Resizable: true);
        var view = new StreamWindow(peer, info, _ => Close(id, true), status, monitor: true);
        var video = new ReceivedVideo();
        view.Uncovered = w => { if (!video.Presented) w.Display(video); };
        views.Add(id, new(display.Id, view, video));
        var area = Rectangle.Inflate(work, -work.Width / 16, -work.Height / 16);
        view.PlaceBounds(area, true, true, false);
        view.Show(); view.ReleaseDragPriority(); view.Activate();
        peer.Send(Kind.MonitorRequest, new MonitorRequest(id, display.Id));
    }
    void Start(MonitorRequest request)
    {
        if (request.Id == Guid.Empty || sources.ContainsKey(request.Id) || views.ContainsKey(request.Id))
            throw new InvalidDataException("Duplicate projection ID.");
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == request.Display);
        if (!Available || screen == null || sources.Count >= 8 || sources.Values.Any(s => s.Display == request.Display))
        { peer.Send(Kind.MonitorClose, request.Id); return; }
        var bounds = screen.Bounds;
        var handle = Win.MonitorFromPoint(bounds.Location, 0);
        if (handle == 0) { peer.Send(Kind.MonitorClose, request.Id); return; }
        var capture = new CaptureStream(handle, (bytes, token) => peer.Frame(request.Id, bytes, token, monitor: true),
            _ => { }, reason =>
            {
                try { owner.BeginInvoke(() => { if (!closed) { Close(request.Id, true); status($"Screen capture stopped. {reason}"); } }); }
                catch (InvalidOperationException) { }
            }, false, options, peer.VideoDecoders, token => peer.WaitForFrameSpace(request.Id, token), screen.DeviceName, monitor: true);
        sources.Add(request.Id, new(screen.DeviceName, capture, bounds));
        changed();
    }
    public async Task Receive(Packet packet, ControlDispatchWindow controls)
    {
        if (closed) return;
        switch (packet.Kind)
        {
            case Kind.MonitorRequest: Start(packet.Read<MonitorRequest>()); break;
            case Kind.MonitorClose: Close(packet.Read<Guid>(), false); break;
            case Kind.MonitorVisibility:
                var visibility = packet.Read<ViewVisibility>();
                if (sources.TryGetValue(visibility.Id, out var source)) source.Capture.Paused = !visibility.Visible;
                break;
            case Kind.MonitorRelease:
                controls.TrackingOwnPress = false;
                if (sources.TryGetValue(packet.Read<Guid>(), out var released) && released.Controller is { } held) input.Post(held, null);
                break;
            case Kind.MonitorInput:
                var e = packet.Read<WindowEvent>();
                if (Available && permissions.HasFlag(Sharing.Mouse) && sources.TryGetValue(e.Id, out var target))
                {
                    if (e.Message == Win.LeftDown)
                        controls.TrackingOwnPress = controls.OwnsWindowAt(MonitorGeometry.ToDesktop(target.Bounds, e.X, e.Y));
                    else if (e.Message == Win.LeftUp) controls.TrackingOwnPress = false;
                    if (target.Controller == null)
                    {
                        var controller = new MonitorInput.Controller(target.Bounds, SystemInformation.VirtualScreen, error =>
                        {
                            try { owner.BeginInvoke(() => { if (!closed) { Close(e.Id, true); status(error); } }); }
                            catch (InvalidOperationException) { }
                        });
                        target.Controller = controller;
                        target.Disconnect = peer.Token.Register(() => { controller.Revoked = true; input.Post(controller, null); });
                    }
                    input.Post(target.Controller, e);
                }
                break;
            case Kind.MonitorFrame:
                if (!Available || !views.TryGetValue(packet.Id, out var view)) break;
                try
                {
                    var surface = view.Window.PreparePresentation(packet.Data);
                    await view.Video.Decode(packet.Data, surface, view.Window.NeedsFirstPicture);
                    if (views.ContainsKey(packet.Id)) view.Window.Display(view.Video, surface != 0);
                }
                catch (Exception ex) when (!closed) { Close(packet.Id, true); status($"Could not display {name}. {ex.Message}"); }
                break;
            default: throw new InvalidDataException("Unexpected projection message.");
        }
    }
    void Revoke(Source source)
    {
        if (source.Controller is not { } controller) return;
        source.Disconnect.Dispose();
        controller.Revoked = true; input.Post(controller, null); source.Controller = null;
    }
    void Close(Guid id, bool notify)
    {
        if (sources.Remove(id, out var source))
        { Revoke(source); source.Capture.Dispose(); changed(); }
        else if (views.Remove(id, out var view)) { view.Window.End(); view.Video.Dispose(); }
        else return;
        if (notify) peer.Send(Kind.MonitorClose, id);
    }
    public void StopAll()
    {
        foreach (var id in sources.Keys.Concat(views.Keys).ToArray()) Close(id, !closed);
    }
    public void StopSending()
    {
        foreach (var id in sources.Keys.ToArray()) Close(id, true);
    }
    public void Dispose() { closed = true; StopAll(); }
}
