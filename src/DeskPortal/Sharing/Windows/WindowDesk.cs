namespace DeskLink;

sealed class WindowDesk : IDisposable
{
    sealed class Origin(nint hwnd, Rectangle home, bool maximized)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly nint Hwnd = hwnd;
        public string Title = "";
        public bool Explorer, Resizable;
        public Win.ExplorerHeader? Header;
        public CaptionRegion[]? Caption;
        public readonly Rectangle Home = home;
        public readonly bool Maximized = maximized;
        public CaptureStream? Stream;
        public SourceState? State;
        public byte[]? Latest;
        public DragPreview? Preview;
        public Rectangle? PreviewAt;
        public bool PreviewQueued;
        public bool Ready, Dropped;
        public Size? ResizeMargins;
        public nint MouseTarget;
        public WinUiInput? WinUi;
        public int CaptionButton;
        public Guid? Owner;
        public Point PeerPosition;
    }
    sealed class Drag(nint hwnd, Point start, Point nativeStart, Rectangle home, StreamWindow? view, bool maximized)
    {
        public readonly nint Hwnd = hwnd;
        public readonly Point Start = start;
        public Point Offset = new(nativeStart.X - home.X, nativeStart.Y - home.Y);
        public Rectangle Home = home;
        public Size Size = home.Size;
        public readonly bool Maximized = maximized;
        public readonly StreamWindow? View = view;
        public Origin? Source;
        public Task<DragOpeningPicture?>? OpeningPicture;
        public bool Moved;
        public bool ClientTitle;
        public bool ShowingHomePreview;
        // Windows saw this press begin and has not seen it end. See Approach.
        public bool ReleasePending;
        SourceState? originalOrder;
        public void RaiseForDrag(RecoveryGuard guard)
        {
            if (((long)Win.GetWindowLongPtr(Hwnd, -20) & 8) != 0) return;
            try
            {
                var state = SourceState.Read(Hwnd, Home.Location, Maximized);
                guard.Track(state, zOrderOnly: true);
                originalOrder = state;
                Win.SetWindowPos(Hwnd, -1, 0, 0, 0, 0, 0x13);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { Win.Raise(Hwnd); }
        }
        public void ReleaseZOrder(RecoveryGuard guard)
        {
            if (originalOrder is not { } state) return;
            originalOrder = null;
            if (state.SameWindow()) Win.SetWindowPos(Hwnd, -2, 0, 0, 0, 0, 0x13);
            guard.Forget(state);
        }
    }
    readonly Control ui;
    readonly Peer peer;
    readonly DeskLayout layout;
    readonly Action<string> status;
    readonly Dictionary<Guid, Origin> sources = [];
    readonly Dictionary<Guid, StreamWindow> views = [];
    readonly Dictionary<Guid, ReceivedVideo> received = [];
    readonly Dictionary<int, nint> heldKeys = [];
    readonly RecoveryGuard guard = new();
    Task<CaptureDeviceLease>? captureDevice;
    CaptureStream.CapturePrimer? capturePrimer;
    public void SetCaptureEnabled(bool enabled)
    {
        if (enabled)
        {
            captureDevice ??= Task.Run(CaptureDeviceLease.PrepareCaptureDevice);
            capturePrimer ??= new CaptureStream.CapturePrimer();
        }
        else if (captureDevice is { } pending)
        {
            capturePrimer?.Dispose(); capturePrimer = null;
            captureDevice = null;
            _ = pending.ContinueWith(t => t.Result.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
    }
    readonly System.Windows.Forms.Timer repaint = new() { Interval = 32 };
    Drag? drag;
    Drag? clientPress;
    Origin? preparing;
    Task discardedCapture = Task.CompletedTask;
    // A title-bar press on a local window that Windows is still moving itself.
    Drag? captionPress;
    public bool CaptionPressed => captionPress != null;
    nint lastClick;
    Point lastPoint;
    long lastTime;
    long nextDialogCheck;
    public bool Dragging => drag != null;
    StreamOptions streamOptions = new();
    public TileMode Tiling { get; set; }
    public WindowPerformance[] SamplePerformance(long now)
    {
        var rows = new List<WindowPerformance>();
        foreach (var s in sources.Values)
            if (s.Stream != null) rows.Add(s.Stream.Meter.Sample(s.Id, s.Title, true, now));
        foreach (var view in views.Values)
            if (received.TryGetValue(view.Id, out var stream))
                rows.Add(stream.Meter.Sample(view.Id, view.Text, false, now, view.TakePaintedFrames()));
        return rows.ToArray();
    }
    public void ConfigureStream(StreamOptions options)
    {
        streamOptions = options;
        foreach (var s in sources.Values)
            if (s.Stream != null) s.Stream.Options = options;
    }
    public void StopSharing()
    {
        clientPress = captionPress = null;
        drag?.ReleaseZOrder(guard);
        drag = null;
        RestoreAll();
        foreach (var id in views.Keys.ToArray()) End(id);
        foreach (var id in received.Keys.ToArray()) End(id);
    }
    public void CancelDrag()
    {
        DiscardPreparation();
        clientPress = captionPress = null;
        var pending = drag;
        drag = null;
        pending?.ReleaseZOrder(guard);
        if (pending?.Source is { } source) Restore(source.Id);
        if (pending?.View is { } view) { view.FinishDrag(); peer.Send(Kind.ClearHomePreview, view.Id); }
    }
    public WindowDesk(Control ui, Peer peer, DeskLayout layout, Action<string> status)
    {
        this.ui = ui; this.peer = peer; this.layout = layout; this.status = status;
        // Record a frame the network or the peer held up, with what Windows knows about the connections then.
        peer.SlowFrame = (id, stage, ticks) =>
        {
            string connections = peer.TcpSummary();
            try
            {
                if (ui.IsHandleCreated) ui.BeginInvoke(() =>
                {
                    if (sources.TryGetValue(id, out var s)) s.Stream?.Meter.ObserveStage(stage, ticks, connections);
                });
            }
            catch (InvalidOperationException) { }
        };
        repaint.Tick += (_, _) =>
        {
            var foreground = Win.GetForegroundWindow();
            foreach (var view in views.Values) view.CheckForeground(foreground);
            foreach (var id in received.Where(p => !views.ContainsKey(p.Key) && Environment.TickCount64 - p.Value.Updated > 3000).Select(p => p.Key).ToArray())
                End(id);
            foreach (var s in sources.Values)
                if (s.Preview is { Composited: false } && s.PreviewAt is { } box)
                    PaintPreview(s, box);
            if (sources.Count > 0 && Environment.TickCount64 >= nextDialogCheck)
            {
                nextDialogCheck = Environment.TickCount64 + 200;
                RefreshExplorerCaptions();
                FindDialogs();
            }
        };
        repaint.Start();
    }

    public bool Route(Point p, int message, bool outside)
    {
        if (!outside && message is Win.LeftDown or Win.RightDown or Win.MiddleDown)
        {
            var hit = Win.GetAncestor(Win.WindowFromPoint(layout.LocalPoint(p)), 2);
            foreach (var window in views.Values)
                if (window.IsHandleCreated && window.Handle != hit) window.ReleaseDragPriority();
        }
        if (message == Win.LeftDown && !outside)
        {
            DiscardPreparation();
            clientPress = captionPress = null;
            var native = layout.LocalPoint(p);
            var under = Win.WindowFromPoint(native);
            nint hwnd = Win.GetAncestor(under, 2);
            if (hwnd == 0) return false;
            var view = Control.FromHandle(hwnd) as StreamWindow;
            if (view != null && (view.IsMonitor || view.ToolbarAt(native))) return false;
            if (Control.FromHandle(hwnd) != null && view == null) return false;
            var box = Win.Bounds(hwnd);
            if (box.IsEmpty) return false;
            if (!Win.IsDragCaption(hwnd, under, native))
            {
                // Custom title bars start their own system move loop after the client click.
                // Let the app decide which toolbar pixels are drag handles.
                if (view == null && native.Y < box.Top + (int)Win.GetDpiForWindow(hwnd))
                    clientPress = new(hwnd, p, native, box, null, Win.IsZoomed(hwnd)) { ClientTitle = Win.IsOrcaTitle(hwnd, under, native) };
                return false;
            }
            if (view == null)
            {
                // Windows keeps the press and moves the window itself, with Snap and the
                // app's own handling, until the window reaches the shared edge. See Approach.
                captionPress = new(hwnd, p, native, box, null, Win.IsZoomed(hwnd));
                captionPress.OpeningPicture = DragOpeningPicture.Start(hwnd);
                // A press close to the shared edge can cross it in one mouse report.
                var near = new Rectangle(layout.LocalTile(native).ToDesk(box.Location), box.Size);
                int margin = (int)Win.GetDpiForWindow(hwnd) * 2;
                if (layout.PeerVisible(Rectangle.Inflate(near, margin, margin))) Prepare(captionPress, moved: false);
                return false;
            }
            drag = new(hwnd, p, native, box, view, Win.IsZoomed(hwnd));
            Win.SetForegroundWindow(hwnd);
            Win.Raise(hwnd);
            return true;
        }
        if (captionPress != null)
        {
            if (message == Win.Move || (outside && message == Win.LeftUp)) Approach(p, outside);
            if (message == Win.LeftUp) captionPress = null;
        }
        if (clientPress is { } press)
        {
            if (message == Win.LeftUp) clientPress = null;
            else if (message == Win.Move &&
                Math.Abs(p.X - press.Start.X) + Math.Abs(p.Y - press.Start.Y) >= SystemInformation.DragSize.Width &&
                (press.ClientTitle || Win.InSystemMove(press.Hwnd)))
            {
                clientPress = null;
                if (Win.SendMessageTimeout(press.Hwnd, 0x001F, 0, 0, 2, 50, out _) == 0) return false;
                press.OpeningPicture = DragOpeningPicture.Start(press.Hwnd);
                drag = press;
            }
        }
        if (drag is not { } d)
        {
            if (message == Win.LeftUp) DiscardPreparation();
            return false;
        }
        if (d.ReleasePending && !Win.InSystemMove(d.Hwnd))
        {
            // Balance the press Windows saw, now that its own move has ended. The real release ends this drag.
            d.ReleasePending = false;
            if (Win.Held(Keys.LButton)) Win.mouse_event(Win.MouseFlag(Win.LeftUp), 0, 0, 0, Win.Tag);
        }
        if (message == Win.Move || message == Win.LeftUp)
        {
            if (Math.Abs(p.X - d.Start.X) + Math.Abs(p.Y - d.Start.Y) >= SystemInformation.DragSize.Width || d.Moved)
                Move(d, p);
            if (message == Win.LeftUp)
            {
                DiscardPreparation();
                drag = null;
                if (d.Moved) Drop(d, p, outside);
                else
                {
                    if (d.View?.AllowsZoom != false && lastClick == d.Hwnd && Environment.TickCount64 - lastTime <= SystemInformation.DoubleClickTime &&
                        Math.Abs(p.X - lastPoint.X) <= SystemInformation.DoubleClickSize.Width && Math.Abs(p.Y - lastPoint.Y) <= SystemInformation.DoubleClickSize.Height)
                        Win.ShowWindow(d.Hwnd, Win.IsZoomed(d.Hwnd) ? 9 : 3);
                    lastTime = Environment.TickCount64; lastClick = d.Hwnd; lastPoint = p;
                }
            }
        }
        return true;
    }
    // Take over a window Windows is dragging once it reaches the shared edge. From there the
    // window can continue onto the other PC. Short of the edge the drag stays native.
    public void Approach(Point p, bool crossed = false)
    {
        if (captionPress is not { } press) return;
        if (Math.Abs(p.X - press.Start.X) + Math.Abs(p.Y - press.Start.Y) < SystemInformation.DragSize.Width) return;
        if (!Win.IsWindow(press.Hwnd)) { captionPress = null; DiscardPreparation(); return; }
        bool moving = Win.InSystemMove(press.Hwnd);
        // Windows may have restored a maximized or snapped window under the pointer, so ask where it is.
        var projected = new Rectangle(p.X - press.Offset.X, p.Y - press.Offset.Y, press.Size.Width, press.Size.Height);
        var box = moving ? Win.Bounds(press.Hwnd) : projected;
        if (moving) box.Location = layout.HomeTile(p).ToDesk(box.Location);
        // Ignore the invisible resize border, which already overhangs the edge on a maximized window.
        int reach = Math.Max(24, (int)Win.GetDpiForWindow(press.Hwnd) / 4);
        // The input report can precede Windows' move, or jump over the edge in one sample.
        if (!crossed && !layout.AtSeam(p) && !layout.ReachesPeer(box, reach) &&
            (press.Maximized || projected.Size != box.Size || !layout.ReachesPeer(projected, reach)))
        {
            Prepare(press);
            return;
        }
        if (moving)
        {
            // Cancelling returns the window to where this press found it, which is the home it is restored to later.
            if (Win.SendMessageTimeout(press.Hwnd, 0x001F, 0, 0, 2, 50, out _) == 0) return;
            press.ReleasePending = true;
        }
        captionPress = null;
        drag = press;
    }
    void Move(Drag d, Point p)
    {
        if (!d.Moved && d.Maximized)
        {
            Win.ShowWindow(d.Hwnd, 9);
            d.Size = Win.Bounds(d.Hwnd).Size;
            if (d.View == null && d.Size != d.Home.Size) d.OpeningPicture = DragOpeningPicture.Start(d.Hwnd);
            d.Offset = new Point((int)((long)d.Offset.X * d.Size.Width / Math.Max(1, d.Home.Width)), Math.Min(d.Offset.Y, 24));
        }
        var box = new Rectangle(p.X - d.Offset.X, p.Y - d.Offset.Y, d.Size.Width, d.Size.Height);
        bool overlapsPeer = layout.PeerVisible(box);
        if (!d.Moved && d.View == null && !overlapsPeer) d.RaiseForDrag(guard);
        d.Moved = true;
        var native = new Rectangle(layout.LocalPoint(box.Location,p),box.Size);
        if (d.View != null)
        {
            d.View.MoveWithPointer(native.Location);
            if (overlapsPeer)
            {
                var q = layout.ToPeer(box.Location,p);
                peer.Send(Kind.HomePreview, new ViewInfo(d.View.Id, d.View.Text, q.X, q.Y, box.Width, box.Height));
                d.ShowingHomePreview = true;
            }
            else if (d.ShowingHomePreview)
            {
                peer.Send(Kind.ClearHomePreview, d.View.Id);
                d.ShowingHomePreview = false;
            }
            return;
        }
        if (overlapsPeer && d.Source == null)
        {
            if (sources.Count >= 8) { status("Return a shared window before sharing another."); return; }
            d.ReleaseZOrder(guard);
            d.Source = Start(d.Hwnd, d.Home, d.Maximized);
            if (d.OpeningPicture is { } picture)
            {
                d.Source.Stream!.OfferOpeningPicture(picture, d.Size);
                d.OpeningPicture = null;
            }
        }
        if (d.Source is { } source)
        {
            var pos = layout.ToPeer(box.Location,p);
            source.PeerPosition = pos;
            peer.Send(Kind.Window, new ViewInfo(source.Id, source.Title, pos.X, pos.Y, box.Width, box.Height, Caption: source.Caption, ExactCaption: source.Header != null, Resizable: source.Resizable));
            Preview(source, layout.LocalVisible(box) ? native : null);
            return;
        }
        Prepare(d);
        Win.Position(d.Hwnd, native.Location);
    }
    void Prepare(Drag d, bool moved = true)
    {
        if (preparing == null && discardedCapture.IsCompleted && sources.Count < 8)
            preparing = CreateCapture(d.Hwnd, d.Home, d.Maximized, dialog: false, prepare: true);
        if (moved) preparing?.Stream?.PrepareForMovement();
    }
    void DiscardPreparation()
    {
        if (preparing?.Stream is not { } stream) return;
        preparing = null;
        stream.Dispose();
        discardedCapture = stream.Completion;
    }
    Origin Start(nint hwnd, Rectangle home, bool maximized, bool dialog = false)
    {
        Origin s;
        if (!dialog && preparing is { Stream.Completion.IsCompleted: false } pending && pending.Hwnd == hwnd)
        {
            s = pending;
            preparing = null;
        }
        else
        {
            if (!dialog) DiscardPreparation();
            s = CreateCapture(hwnd, home, maximized, dialog, false);
        }
        s.Title = Win.Title(hwnd);
        s.Explorer = Win.IsExplorer(hwnd);
        s.Resizable = Win.CanResize(hwnd);
        if (s.Explorer) s.Header = Win.ReadExplorerHeader(hwnd);
        s.Caption = s.Header?.DragRegions() ?? Win.CaptionRegions(hwnd);
        sources.Add(s.Id, s);
        s.Stream!.Options = streamOptions;
        s.Stream.Meter.Label = "send  " + s.Title;
        s.Stream.Share();
        return s;
    }
    Origin CreateCapture(nint hwnd, Rectangle home, bool maximized, bool dialog, bool prepare)
    {
        var s = new Origin(hwnd, home, maximized);
        s.Stream = new CaptureStream(hwnd, (frame, stop) => peer.Frame(s.Id, frame, stop),
            preview => Volatile.Write(ref s.Latest, preview), error =>
        {
            try
            {
                if (ui.IsHandleCreated) ui.BeginInvoke(() =>
                {
                    if (!sources.ContainsKey(s.Id)) return;
                    bool closed = !Win.IsWindow(s.Hwnd);
                    Restore(s.Id);
                    if (!closed) status(error);
                });
            }
            catch (InvalidOperationException) { }
        }, dialog, streamOptions, peer.VideoDecoders, token => peer.WaitForFrameSpace(s.Id, token), prepare: prepare);
        return s;
    }
    void RefreshExplorerCaptions()
    {
        foreach (var s in sources.Values)
        {
            if (!s.Explorer || !s.Ready || !s.Dropped || !peer.CaptionUpdates) continue;
            var header = Win.ReadExplorerHeader(s.Hwnd);
            if (header == s.Header) continue;
            s.Header = header;
            s.Caption = header?.DragRegions() ?? Win.CaptionRegions(s.Hwnd);
            peer.Send(Kind.WindowCaption, new WindowCaption(s.Id, s.Caption, header != null));
        }
    }
    void FindDialogs()
    {
        foreach (var s in sources.Values.Where(s => s.Owner != null && !Win.IsWindowVisible(s.Hwnd)).ToArray()) Restore(s.Id);
        if (sources.Count >= 8 || !sources.Values.Any(s => s.Dropped && s.Ready)) return;
        Win.EnumWindows((hwnd, _) =>
        {
            if (sources.Count >= 8 || !Win.IsWindowVisible(hwnd) || Win.Class(hwnd) != "#32770" || sources.Values.Any(s => s.Hwnd == hwnd)) return true;
            var owner = Win.GetWindow(hwnd, 4); // GW_OWNER
            var parent = sources.Values.FirstOrDefault(s => s.Hwnd == owner && s.Ready && s.Dropped);
            if (parent == null) return true;
            var bounds = Win.Bounds(hwnd);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;
            var parentBounds = Win.Bounds(owner);
            var s = Start(hwnd, bounds, false, true);
            s.Owner = parent.Id; s.Dropped = true;
            var pos = new Point(Math.Clamp(parent.PeerPosition.X + bounds.X - parentBounds.X, 0, Math.Max(0, layout.Other.Width - bounds.Width)),
                Math.Clamp(parent.PeerPosition.Y + bounds.Y - parentBounds.Y, 0, Math.Max(0, layout.Other.Height - bounds.Height)));
            s.PeerPosition = pos;
            peer.Send(Kind.Window, new ViewInfo(s.Id, Win.Title(hwnd), pos.X, pos.Y, bounds.Width, bounds.Height, parent.Id,
                OwnerOffset: new Point(bounds.X - parentBounds.X, bounds.Y - parentBounds.Y), Resizable: s.Resizable));
            return true;
        }, 0);
    }
    void Drop(Drag d, Point p, bool outside)
    {
        d.ReleaseZOrder(guard);
        if (d.View is { } view)
        {
            view.FinishDrag();
            var pos = layout.ToPeer(new Point(p.X - d.Offset.X, p.Y - d.Offset.Y),p);
            // Keep the owner's final position for newly opened dialogs, even if
            // the drag stayed entirely on the receiving PC.
            if (!outside && !d.ShowingHomePreview)
                peer.Send(Kind.HomePreview, new ViewInfo(view.Id, view.Text, pos.X, pos.Y, d.Size.Width, d.Size.Height));
            peer.Send(Kind.ClearHomePreview, view.Id);
            if (outside)
            {
                peer.Send(Kind.ReturnWindow, new ReturnInfo(view.Id, pos.X, pos.Y));
            }
            return;
        }
        if (d.Source is not { } s) return;
        if (!outside) { Restore(s.Id, layout.LocalPoint(new Point(p.X - d.Offset.X, p.Y - d.Offset.Y),p), false); return; }
        s.Dropped = true;
        var dropPoint = layout.ToPeer(p);
        peer.Send(Kind.Window, new ViewInfo(s.Id, s.Title, s.PeerPosition.X, s.PeerPosition.Y, d.Size.Width,
            d.Size.Height, Caption: s.Caption, DropPoint: dropPoint, ExactCaption: s.Header != null, Resizable: s.Resizable));
        Preview(s, null);
    }
    void Preview(Origin s, Rectangle? bounds)
    {
        s.PreviewAt = bounds is { } box && layout.NativeVisible(box) ? box : null;
        if (s.PreviewAt == null)
        {
            if (s.Stream != null) s.Stream.WantPreview = false;
            if (s.Preview is { Visible: true } visible) visible.Hide();
            if (s.State == null) ParkSource(s);
            return;
        }
        if (s.PreviewQueued) return;
        s.PreviewQueued = true;
        ui.BeginInvoke(() =>
        {
            s.PreviewQueued = false;
            if (s.PreviewAt is { } latest && sources.ContainsKey(s.Id)) PaintPreview(s, latest);
        });
    }
    void PaintPreview(Origin s, Rectangle bounds)
    {
        var preview = s.Preview ??= new DragPreview(s.Hwnd, status);
        preview.Present(bounds, Volatile.Read(ref s.Latest));
        if (s.Stream != null) s.Stream.WantPreview = !preview.Composited;
        if (s.State == null)
        {
            if (preview.Visible) ParkSource(s);
            else Win.Position(s.Hwnd, bounds.Location);
        }
    }
    public void HomePreview(ViewInfo v)
    {
        if (sources.TryGetValue(v.Id, out var s) && s.Dropped)
        {
            var pos = layout.FromWire(new(v.X, v.Y));
            s.PeerPosition = layout.ToPeer(layout.ToWorld(pos));
            Preview(s, new Rectangle(pos, new Size(v.Width, v.Height)));
        }
    }
    public void ClearPreview(Guid id) { if (sources.TryGetValue(id, out var s)) Preview(s, null); }
    // Keep WGC's source on its monitor. Conceal it before parking it, as soon as
    // the local drag image can replace it or the drag has left the monitor.
    bool ParkSource(Origin s)
    {
        if (s.State != null) return true;
        try
        {
            var state = SourceState.Read(s.Hwnd, s.Home.Location, s.Maximized);
            guard.Track(state); s.State = state;
            state.Conceal();
            Win.Position(s.Hwnd, s.Home.Location);
            return true;
        }
        catch (Exception ex) { Restore(s.Id); status(ex.Message); return false; }
    }
    public void Ready(Guid id)
    {
        if (!sources.TryGetValue(id, out var s) || s.Ready) return;
        if ((s.PreviewAt == null || s.Preview?.Visible == true) && !ParkSource(s)) return;
        s.Ready = true;
        if (s.Owner == null) status($"Sharing {s.Title} · {s.Stream?.Description}");
    }
    public void Place(ViewInfo v)
    {
        if (v.Width is < 1 or > 16384 || v.Height is < 1 or > 16384 || v.Title.Length > 1024) throw new InvalidDataException();
        if (!views.TryGetValue(v.Id, out var view))
        {
            if (views.Count >= 8) throw new InvalidDataException("Too many shared windows.");
            view = new StreamWindow(peer, v, w => peer.Send(Kind.ReturnWindow, new ReturnInfo(w.Id, int.MinValue, 0)), status)
            {
                // Frames that arrived while the window was minimized were decoded and not shown.
                Uncovered = w => { if (received.TryGetValue(w.Id, out var latest) && !latest.Presented) w.Display(latest); }
            };
            if (v.Owner is { } owner && views.TryGetValue(owner, out var parent)) view.Owner = parent;
            views.Add(v.Id, view);
            if (received.TryGetValue(v.Id, out var early)) early.Meter.Label = "receive  " + v.Title;
        }
        var box = new Rectangle(layout.FromWire(new(v.X, v.Y)), new(v.Width, v.Height));
        view.SourceSize = box.Size;
        if (v.Owner == null && v.DropPoint is { } drop)
        {
            foreach (var other in views.Values)
                if (other != view) other.ReleaseDragPriority();
            box = DeskSettings.Place(Tiling, box, Screen.FromPoint(layout.FromWire(drop)).WorkingArea, v.Resizable == true);
        }
        if (view.Owner is StreamWindow ownerView && v.OwnerOffset is { } offset)
        {
            double scale = (double)ownerView.ClientSize.Width / ownerView.SourceSize.Width;
            var work = Screen.FromControl(ownerView).WorkingArea;
            var size = new Size(Math.Max(1, (int)Math.Round(box.Width * scale)), Math.Max(1, (int)Math.Round(box.Height * scale)));
            box = new Rectangle(Math.Clamp(ownerView.Left + (int)Math.Round(offset.X * scale), work.Left, Math.Max(work.Left, work.Right - size.Width)),
                Math.Clamp(ownerView.Top + (int)Math.Round(offset.Y * scale), work.Top, Math.Max(work.Top, work.Bottom - size.Height)), size.Width, size.Height);
        }
        view.PlaceBounds(box, layout.NativeVisible(box), v.DropPoint != null || v.Owner != null,
            v.DropPoint != null && Tiling != TileMode.Free, received.GetValueOrDefault(v.Id));
    }
    internal Bitmap? LatestImage(Guid id) => received.GetValueOrDefault(id)?.Image;
    internal StreamWindow? FindView(Guid id) => views.GetValueOrDefault(id);
    public async Task Frame(Packet p)
    {
        if (!received.TryGetValue(p.Id, out var stream))
        {
            if (received.Count >= 16) throw new InvalidDataException("Too many incoming window streams.");
            // EndWindow can overtake the last queued video packet.
            if (p.Data.Length > 19 && p.Data[16] == VideoCodec.Marker && p.Data[18] == 0) return;
            received.Add(p.Id, stream = new ReceivedVideo());
            stream.Meter.Label = "receive  " + (views.TryGetValue(p.Id, out var shown) ? shown.Text : p.Id.ToString());
        }
        try
        {
            // A frame held up mid-transfer shows here. One delayed before its first byte shows as the sender's round trip.
            stream.Meter.ObserveStage("Frame arrival", p.ArrivalTicks);
            if (p.ReceivedAt != 0) stream.Meter.ObserveStage("Frame dispatch", System.Diagnostics.Stopwatch.GetTimestamp() - p.ReceivedAt);
            nint target = views.TryGetValue(p.Id, out var current) ? current.PreparePresentation(p.Data) : 0;
            await stream.Decode(p.Data, target, current == null || current.NeedsFirstPicture);
            if (received.GetValueOrDefault(p.Id) != stream) return;
            if (views.TryGetValue(p.Id, out var view))
            {
                view.Display(stream, target != 0);
                var feedback = VideoCodec.ReadClick(p.Data);
                if (feedback.Click != 0 && view.Visible && !stream.Held)
                    view.ObserveClick(feedback.Click, feedback.SourceMs, stream.Meter);
            }
        }
        catch (Exception ex)
        {
            if (received.GetValueOrDefault(p.Id) != stream) return;
            End(p.Id);
            peer.Send(Kind.ReturnWindow, new ReturnInfo(p.Id, int.MinValue, 0));
            status($"Could not display the shared window. {ex.Message}");
        }
    }
    public void End(Guid id)
    {
        if (views.Remove(id, out var v)) { if (drag?.View == v) drag = null; v.End(); }
        if (received.Remove(id, out var stream)) stream.Dispose();
    }
    public void ChangeView(ViewCommand command)
    {
        if (!views.TryGetValue(command.Id, out var view)) return;
        if (command.Maximize && !view.AllowsZoom) return;
        view.WindowState = command.Maximize ?
            (view.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized) :
            FormWindowState.Minimized;
    }
    public void ResizeSource(WindowSize request)
    {
        if (!sources.TryGetValue(request.Id, out var s) || !s.Ready || !s.Dropped || !s.Resizable || !Win.IsWindow(s.Hwnd)) return;
        var outer = Win.Bounds(s.Hwnd);
        if (outer.IsEmpty) return;
        // WGC captures the visible DWM frame. SetWindowPos sizes the outer HWND,
        // including its invisible resize margins on the source monitor.
        // The concealed source stays on its monitor. During rapid resizes DWM
        // may still report the previous frame while GetWindowRect has advanced.
        var margins = s.ResizeMargins ??= s.Owner == null ? outer.Size - Win.FrameBounds(s.Hwnd).Size : Size.Empty;
        if (Win.CanResize(s.Hwnd) && request.Width is >= 1 and <= 16384 && request.Height is >= 1 and <= 16384 &&
            (long)request.Width * request.Height <= 32_000_000)
            Win.ResizeForDisplay(s.Hwnd, new(request.Width + margins.Width, request.Height + margins.Height));
        var actual = Win.Bounds(s.Hwnd).Size - margins;
        if (actual.Width <= 0 || actual.Height <= 0) return;
        peer.Send(Kind.WindowResized, new WindowSize(s.Id, actual.Width, actual.Height));
        if (peer.CaptionUpdates && !s.Explorer && s.Caption != null)
        {
            s.Caption = Win.CaptionRegions(s.Hwnd);
            peer.Send(Kind.WindowCaption, new WindowCaption(s.Id, s.Caption, false));
        }
    }
    // The receiving PC minimized or restored its view of this window.
    public void SetViewVisible(ViewVisibility view)
    {
        if (sources.TryGetValue(view.Id, out var s) && s.Stream != null) s.Stream.Paused = !view.Visible;
    }
    public void Resized(WindowSize size)
    {
        if (views.TryGetValue(size.Id, out var view)) view.AcceptSize(new(size.Width, size.Height));
    }
    public void UpdateCaption(WindowCaption update)
    {
        if (views.TryGetValue(update.Id, out var view)) view.SetCaption(update.Regions, update.Exact);
    }
    public void Restore(Guid id, Point? position = null, bool maximize = true)
    {
        if (!sources.TryGetValue(id, out var s)) return;
        s.WinUi?.Stop();
        foreach (var key in heldKeys.Where(k => k.Value == s.Hwnd).Select(k => k.Key).ToArray())
        {
            Win.keybd_event((byte)key, 0, 2, Win.Tag);
            heldKeys.Remove(key);
        }
        foreach (var child in sources.Values.Where(c => c.Owner == id).ToArray()) Restore(child.Id);
        if (Win.IsWindow(s.Hwnd))
        {
            var point = position ?? s.Home.Location;
            var area = Screen.FromPoint(point).WorkingArea;
            point.X = Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - 100));
            point.Y = Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - 40));
            if (s.State is { } state)
            {
                (state with { Maximized = maximize && state.Maximized }).Restore(point);
            }
            else
            {
                Win.Position(s.Hwnd, point);
                if (maximize && s.Maximized) Win.ShowWindow(s.Hwnd, 3);
            }
        }
        if (drag?.Source == s) drag = null;
        Stop(id);
    }
    void Stop(Guid id)
    {
        if (sources.Remove(id, out var s))
        {
            if (s.State != null) guard.Forget(s.State);
            s.Stream?.Dispose(); s.Preview?.Dispose();
        }
        peer.Send(Kind.EndWindow, id);
    }
    public async Task Input(WindowEvent e, long receivedAt = 0)
    {
        if (!sources.TryGetValue(e.Id, out var s) || !s.Dropped || !Win.IsWindow(s.Hwnd)) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (receivedAt != 0) s.Stream?.Meter.ObserveStage("Input dispatch", start - receivedAt);
        if (e.Message == Win.LeftUp && receivedAt != 0) StallLog.Stage("click arrival to input handler", receivedAt);
        try { await ApplyInput(e, s); }
        finally
        {
            if (e.Message == Win.LeftUp) { s.Stream?.NoteClick(e.Click, receivedAt); StallLog.Stage("click routing", start); }
            s.Stream?.Meter.ObserveStage("Source input", System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }
    async Task ApplyInput(WindowEvent e, Origin s)
    {
        if (e.Message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            if (e.Data is < 1 or > 254) return;
            bool up = e.Message is 0x101 or 0x105;
            if (Win.GetForegroundWindow() != s.Hwnd && !(up && heldKeys.ContainsKey(e.Data)))
            {
                var info = new Win.GuiInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Win.GuiInfo>() };
                var thread = Win.GetWindowThreadProcessId(s.Hwnd, out _);
                nint focus = Win.GetGUIThreadInfo(thread, ref info) && Win.IsChild(s.Hwnd, info.Focus) ? info.Focus : s.Hwnd;
                Win.PostMessage(focus, (uint)e.Message, e.Data, e.Modifiers);
                return;
            }
            if (up) heldKeys.Remove(e.Data); else heldKeys[e.Data] = s.Hwnd;
            Win.keybd_event((byte)e.Data, (byte)(e.Modifiers >> 16), (up ? 2u : 0) | ((uint)(e.Modifiers >> 24) & 1), Win.Tag);
            return;
        }
        if (e.Message != Win.Move && Win.MouseFlag(e.Message) == 0 && e.Message is not (0x203 or 0x206)) return;
        bool extraButton = e.Message is Win.XDown or Win.XUp;
        if (extraButton && e.Data is not (1 or 2)) return;
        var frame = s.Owner != null ? Win.Bounds(s.Hwnd) : Win.FrameBounds(s.Hwnd);
        var screen = new Point(frame.Left + (int)((long)Math.Clamp(e.X, 0, 65535) * (frame.Width - 1) / 65535),
            frame.Top + (int)((long)Math.Clamp(e.Y, 0, 65535) * (frame.Height - 1) / 65535));
        int CaptionHit()
        {
            if (s.Header is { } header && Win.IsWindow(header.Handle) && Win.Bounds(header.Handle).Contains(screen))
                return Win.Hit(header.Handle, screen);
            return Win.Hit(s.Hwnd, screen);
        }
        if (e.Message == Win.LeftDown)
        {
            int hit = CaptionHit();
            if (hit is 8 or 9 or 20) { s.CaptionButton = hit; return; }
        }
        if (e.Message == Win.LeftUp && s.CaptionButton != 0)
        {
            int button = s.CaptionButton; s.CaptionButton = 0;
            if (CaptionHit() == button)
            {
                if (button == 20) Win.PostMessage(s.Hwnd, 0x112, 0xF060, 0);
                else peer.Send(Kind.ViewCommand, new ViewCommand(s.Id, button == 9));
            }
            return;
        }
        nint target = s.MouseTarget != 0 && Win.IsWindow(s.MouseTarget) ? s.MouseTarget : s.Hwnd;
        var local = screen;
        Win.ScreenToClient(target, ref local);
        while (s.MouseTarget == 0)
        {
            var child = Win.InputChild(target, screen, local);
            if (child == 0 || child == target) break;
            target = child; local = screen; Win.ScreenToClient(target, ref local);
        }
        if (e.Message is Win.LeftDown or 0x203 or Win.RightDown or Win.MiddleDown)
        {
            Win.SetForegroundWindow(s.Hwnd); s.MouseTarget = target;
            // Explorer's XAML tab strip can be transparent to child-HWND
            // lookup while the source is concealed. Its UIA controls remain actionable.
            var strip = s.Header is { } header ? Win.Bounds(header.Handle) : Rectangle.Empty;
            bool explorerTabStrip = screen.Y >= strip.Top && screen.Y < strip.Bottom && !strip.Contains(screen);
            if ((e.Message == Win.LeftDown || e.Message == 0x203) &&
                (explorerTabStrip || WinUiInput.IsInputSite(target) || WinUiInput.IsInputSite(s.Hwnd)))
            {
                s.WinUi ??= new WinUiInput(s.Hwnd);
                s.WinUi.Press = screen;
                s.WinUi.ExplorerTabPress = explorerTabStrip;
                s.WinUi.DoubleClick = e.Message == 0x203;
                return;
            }
        }
        if (e.Message is Win.LeftUp or Win.RightUp or Win.MiddleUp) s.MouseTarget = 0;
        bool wheel = e.Message is Win.Wheel or Win.HorizontalWheel;
        if (wheel && (s.WinUi != null || WinUiInput.IsInputSite(target) || WinUiInput.IsInputSite(s.Hwnd)))
        {
            s.WinUi ??= new WinUiInput(s.Hwnd);
            if (await WinUiInput.TryAction(() => s.WinUi.Scroll(screen, e.Message == Win.HorizontalWheel, e.Data))) return;
            if (!sources.ContainsKey(e.Id) || !Win.IsWindow(target)) return;
        }
        if (e.Message == Win.LeftUp && s.WinUi?.Press is { } start)
        {
            var input = s.WinUi;
            input.Press = null;
            if (await WinUiInput.TryAction(() => input.Click(start, screen, input.DoubleClick))) return;
            if (!sources.ContainsKey(e.Id) || !Win.IsWindow(target)) return;
            var down = start;
            Win.ScreenToClient(target, ref down);
            Win.PostMessage(target, Win.LeftDown, (e.Modifiers & 127) | 1, Win.Packed(down));
        }
        Win.PostMessage(target, (uint)e.Message, (nint)((e.Modifiers & 127) | (wheel || extraButton ? e.Data << 16 : 0)), Win.Packed(wheel ? screen : local));
    }
    public void RestoreAll()
    {
        DiscardPreparation();
        foreach (var id in sources.Keys.ToArray()) Restore(id);
    }
    public void Dispose()
    {
        SetCaptureEnabled(false);
        peer.SlowFrame = null;
        drag?.ReleaseZOrder(guard);
        repaint.Dispose();
        foreach (int key in heldKeys.Keys) Win.keybd_event((byte)key, 0, 2, Win.Tag);
        heldKeys.Clear();
        RestoreAll();
        foreach (var id in views.Keys.ToArray()) End(id);
        guard.Dispose();
        foreach (var stream in received.Values) stream.Dispose();
        received.Clear();
        drag = clientPress = captionPress = null;
    }
}
