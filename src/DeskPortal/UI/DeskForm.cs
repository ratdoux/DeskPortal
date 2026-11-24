using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace DeskLink;

sealed class DeskForm : Form
{
    readonly DeskList computers = new() { AccessibleName = "Nearby computers" };
    readonly Guid deviceId = Guid.NewGuid();
    readonly CancellationTokenSource formLifetime = new();
    readonly BlockButton networkAccess = new() { Text = "Network access" };
    readonly Label machine = DeskStyle.Caption($"This PC  ·  {Environment.MachineName}\nIPv4  ·  Checking…");
    readonly Panel dashboard = new() { Dock = DockStyle.Fill, AutoScroll = true };
    IPAddress? networkPeer;
    int addressRevision;
    QuickStartForm? guide;
    CancellationTokenSource? discoveryLifetime;
    Task discoveryTask = Task.CompletedTask;
    bool configuringNetwork;
    NearbyComputers? nearby;
    NearbyComputer? outgoing;
    TaskCompletionSource<TcpClient>? connectBack;
    (IPAddress Address, Channel<TcpClient> Queue)? attaching;
    bool comparing;
    readonly ComboBox side = new() { AccessibleName = "Other screen position", DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat, Width = 120 };
    readonly BlockButton connect = new() { Text = "Connect", Enabled = false },
        disconnect = new() { Text = "Disconnect", Enabled = false }, restore = new() { Text = "Bring windows home", Enabled = false },
        files = new() { Text = "Send files…", Enabled = false }, use = new() { Text = "Use selected item", Enabled = false };
    readonly Label state = new() { Text = "Disconnected", AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    readonly Label detail = new() { Text = "Open DeskPortal on the other PC to find it here.", AutoSize = true, MaximumSize = new Size(565, 0) };
    readonly Label sharingState = new() { AutoSize = true, Visible = false };
    readonly Label streamState = new() { AutoSize = true, Visible = false, MaximumSize = new Size(620, 0) };
    DeskSettings settings;
    Sharing remoteFeatures, acknowledged, active;
    const Sharing TransferFeatures = Sharing.Files | Sharing.Clipboard;
    long preferenceRevision;
    readonly DeskList history = new() { AccessibleName = "Shared clipboard history" };
    readonly Label noComputers = new EmptyListLabel(false) { Text = "No computers found yet.", Tag = Tone.Quiet };
    readonly Label noHistory = new EmptyListLabel(true) { Text = "Clipboard history appears after pairing.", Tag = Tone.Quiet };
    readonly Label historyShortcut = DeskStyle.Caption("");
    readonly Label shortcutWarning = DeskStyle.Caption("");
    readonly ScreenArrangement arrangement = new();
    readonly BlockButton editLayout = new() { Text = "Edit screen layout", Visible = false };
    string? pairedName;
    DeskHello? localHello;
    long layoutRevision;
    bool layoutLeader, lastLayoutFromLeader;
    readonly SessionHotkeys hotkeys = new();
    readonly BlockButton preferences = new() { Text = "Settings" }, stats = new() { Text = "Performance" };
    CancellationTokenSource? pending;
    // The transfer connection's reader thread also reads these three. See WriteChunk.
    volatile Peer? peer;
    volatile SharedClipboard? clipboard;
    volatile bool closing;
    DeskLayout? layout;
    WindowDesk? windows;
    PointerBridge? pointer;
    InputThreadPriority? inputThreadPriority;
    EdgeFiles? edgeFiles;
    bool hosting, listeningClipboard;
    uint sequence;
    readonly System.Windows.Forms.Timer observe = new() { Interval = 90 };
    readonly System.Windows.Forms.Timer performanceTimer = new() { Interval = 1000 };
    SessionPerformance? performance;
    PerformanceForm? performanceForm;
    int clipboardRetries;
    readonly bool listenToDesktop;
    public DeskForm() : this(true) { }
    internal DeskForm(bool listenToDesktop)
    {
        SuspendLayout();
        this.listenToDesktop = listenToDesktop;
        try { settings = DeskSettings.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            settings = new() { Features = Sharing.None };
            Shown += (_, _) => MessageBox.Show(this, $"Sharing is off because DeskPortal could not read your settings. Open Settings to choose what to share.\n\n{ex.Message}", "DeskPortal settings");
        }
        Text = "DeskPortal";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(822, 540);
        StartPosition = FormStartPosition.CenterScreen;
        side.Items.AddRange(["On the right", "On the left"]); side.SelectedIndex = 0;
        BuildDashboard();
        editLayout.Click += (_,_) => EditLayout();
        DeskStyle.Apply(this, settings.Appearance);
        UpdateShortcutHint();
        preferences.Click += (_, _) =>
        {
            using var dialog = new SettingsDialog(settings, PeerFeatures, bindings => hotkeys.CheckAvailable(Handle, bindings));
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var selected = dialog.Selection;
            try { selected.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, ex.Message, "Could not save settings"); return; }
            UseSettings(selected);
        };
        stats.Click += (_, _) =>
        {
            if (performanceForm != null) { performanceForm.Activate(); return; }
            performanceForm = new PerformanceForm(settings.Appearance);
            performanceForm.FormClosed += (_, _) => performanceForm = null;
            performanceForm.Render(performance);
            performanceForm.Show(this);
        };
        performanceTimer.Tick += (_, _) =>
        {
            if (performance == null) return;
            var sample = performance.Sample();
            var sending = sample.Windows.Where(w => w.Sending).ToArray();
            streamState.Visible = sending.Length > 0;
            if (sending.Length > 0)
                streamState.Text = $"Sending · {string.Join(" / ", sending.Select(w => w.Codec).Distinct())} · {sending.Sum(w => w.Mbps):F1} Mbps actual (total)";
            performanceForm?.Render(performance);
        };
        void ChooseComputer()
        {
            if (pending == null && computers.SelectedItem is NearbyComputer pc) _ = Connect(pc, null);
        }
        connect.Click += (_, _) => ChooseComputer();
        computers.SelectedIndexChanged += (_, _) =>
        {
            connect.Enabled = pending == null && peer == null && computers.SelectedItem != null;
            if (layout == null) arrangement.SetPeer((computers.SelectedItem as NearbyComputer)?.Name);
        };
        computers.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && computers.IndexFromPoint(e.Location) >= 0) ChooseComputer(); };
        computers.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ChooseComputer(); } };
        Shown += async (_, _) =>
        {
            await RefreshLocalAddress();
            if (listenToDesktop && !closing) await ConfigureNetwork(false);
        };
        networkAccess.Click += async (_, _) => await ConfigureNetwork(true);
        disconnect.Click += (_, _) => Detach("Disconnected");
        restore.Click += (_, _) => pointer?.Recover();
        files.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Multiselect = true, Title = "Send files to the paired PC" };
            if (dialog.ShowDialog(this) == DialogResult.OK) clipboard?.SendFiles(dialog.FileNames);
        };
        void UseSelected() { if (history.SelectedItem is HistoryItem item) clipboard?.Select(item); }
        use.Click += (_, _) => UseSelected();
        history.SelectedIndexChanged += (_, _) => use.Enabled = active.HasFlag(Sharing.Clipboard) && history.SelectedItem != null;
        history.DoubleClick += (_, _) => UseSelected();
        history.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; UseSelected(); } };
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = active.HasFlag(Sharing.Files) && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) clipboard?.SendFiles(paths); };
        observe.Tick += (_, _) =>
        {
            observe.Stop();
            try { clipboard?.Observe(); }
            catch (ExternalException) { if (++clipboardRetries <= 4) observe.Start(); }
            catch (Exception ex) { Status(ex.Message); }
        };
        FormClosing += (_, _) => { closing = true; formLifetime.Cancel(); Detach("Closed"); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        FormClosed += (_, _) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged; observe.Dispose(); performanceTimer.Dispose(); };
        ResumeLayout(true);
    }
    void BuildDashboard()
    {
        var root = DeskStyle.Column(Tone.Canvas);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var banner = new DeskBanner { AutoSize = true, Dock = DockStyle.Fill, Tag = Tone.Primary,
            ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(20, 12, 17, 12) };
        banner.ColumnStyles.Add(new(SizeType.AutoSize)); banner.ColumnStyles.Add(new(SizeType.Percent, 100));
        banner.ColumnStyles.Add(new(SizeType.AutoSize));
        var mark = new DeskMark { Anchor = AnchorStyles.Left };
        banner.Controls.Add(mark, 0, 0);
        var brand = DeskStyle.Heading("DeskPortal", 34); brand.Margin = Padding.Empty;
        banner.Controls.Add(brand, 1, 0);
        machine.Tag = Tone.Inverse; machine.Padding = new Padding(8, 5, 8, 5); machine.Anchor = AnchorStyles.Right;
        machine.Font = new Font("Consolas", 10, FontStyle.Bold);
        banner.Controls.Add(machine, 2, 0); DeskStyle.Row(root, banner);

        var body = DeskStyle.Column(Tone.Canvas, 16); DeskStyle.Row(root, body, true);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var session = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        session.ColumnStyles.Add(new(SizeType.Percent, 100)); session.ColumnStyles.Add(new(SizeType.AutoSize));
        state.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        state.Anchor = AnchorStyles.Left; state.Margin = Padding.Empty;
        session.Controls.Add(state, 0, 0);
        foreach (var b in new[] { preferences, stats, disconnect, files, restore, use, connect, editLayout, networkAccess }) DeskStyle.Button(b, b == connect);
        disconnect.Visible = false;
        var help = new BlockButton { Text = "Quick start" }; DeskStyle.Button(help);
        help.Click += (_, _) =>
        {
            if (guide != null) { guide.Activate(); return; }
            guide = new QuickStartForm(settings);
            guide.FormClosed += (_, _) => guide = null;
            guide.Show(this);
        };
        var tools = DeskStyle.LineOf(help, stats, preferences); tools.WrapContents = false;
        session.Controls.Add(tools, 1, 0);
        DeskStyle.Row(body, session);
        detail.Tag = Tone.Quiet; detail.Dock = DockStyle.Top; detail.MaximumSize = Size.Empty;
        detail.Margin = new Padding(0, 2, 0, 10); DeskStyle.Row(body, detail);

        var cards = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cards.ColumnStyles.Add(new(SizeType.Percent, 50));
        cards.RowStyles.Add(new(SizeType.Percent, 100)); DeskStyle.Row(body, cards, true);
        var devices = DeskStyle.Column(Tone.Frame, 14); devices.Margin = new Padding(0, 0, 6, 0);
        var clips = DeskStyle.Column(Tone.Frame, 14); clips.Margin = new Padding(6, 0, 0, 0);
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        clips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cards.Controls.Add(devices, 0, 0); cards.Controls.Add(clips, 1, 0);
        DeskStyle.Row(devices, DeskStyle.Heading("Nearby computers")); DeskStyle.Row(devices, ListArea(computers, noComputers), true);
        DeskStyle.Row(devices, DeskStyle.Caption("Screen layout"));
        DeskStyle.Row(devices, arrangement); side.SelectedIndexChanged += (_, _) => arrangement.SetSide(side.SelectedIndex == 0);
        DeskStyle.Row(devices, DeskStyle.LineOf(side, connect, editLayout));
        var clipHeading = DeskStyle.Heading("Clipboard history");
        clipHeading.Tag = Tone.Complement; clipHeading.Padding = new Padding(6, 2, 6, 2);
        DeskStyle.Row(clips, clipHeading); DeskStyle.Row(clips, ListArea(history, noHistory), true);
        DeskStyle.Row(clips, historyShortcut); DeskStyle.Row(clips, DeskStyle.LineOf(use));

        var toolbar = DeskStyle.LineOf(files, restore, disconnect, networkAccess); toolbar.Padding = new Padding(0, 10, 0, 0);
        DeskStyle.Row(body, toolbar);
        sharingState.Tag = streamState.Tag = Tone.Quiet;
        DeskStyle.Row(body, sharingState); DeskStyle.Row(body, streamState);
        shortcutWarning.Visible = false; shortcutWarning.MaximumSize = new Size(600, 0); DeskStyle.Row(body, shortcutWarning);
        dashboard.Controls.Add(root); Controls.Add(dashboard);
    }
    protected override void OnLoad(EventArgs e)
    {
        FitDashboard(); base.OnLoad(e);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated && !closing) BeginInvoke(FitDashboard);
    }
    void FitDashboard()
    {
        if (closing || WindowState != FormWindowState.Normal) return;
        var work = Screen.FromControl(this).WorkingArea;
        var canvas = LogicalToDeviceUnits(new Size(650, 475));
        dashboard.AutoScrollMinSize = canvas;
        var minimum = SizeFromClientSize(canvas);
        MinimumSize = new Size(Math.Min(minimum.Width, work.Width), Math.Min(minimum.Height, work.Height));
        var size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        Bounds = new Rectangle(Math.Clamp(Left, work.Left, work.Right - size.Width),
            Math.Clamp(Top, work.Top, work.Bottom - size.Height), size.Width, size.Height);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NetworkChange.NetworkAddressChanged += NetworkChanged;
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        base.OnHandleDestroyed(e);
    }
    void NetworkChanged(object? sender, EventArgs args)
    {
        if (closing || !IsHandleCreated) return;
        try { BeginInvoke(() => _ = RefreshLocalAddress()); } catch (InvalidOperationException) { }
    }
    async Task<IPAddress[]> RefreshLocalAddress()
    {
        int revision = ++addressRevision;
        var target = networkPeer;
        var addresses = await Task.Run(() => LocalIPv4.Read(target));
        if (!closing && !IsDisposed && revision == addressRevision)
            machine.Text = $"This PC  ·  {Environment.MachineName}\nIPv4  ·  {addresses.FirstOrDefault()?.ToString() ?? "Unavailable"}";
        return addresses;
    }
    static Panel ListArea(DeskList list, Label empty)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6), Tag = Tone.Surface };
        empty.Dock = DockStyle.Fill; empty.AutoSize = false; empty.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(list); panel.Controls.Add(empty); empty.BringToFront();
        list.Visible = false;
        return panel;
    }
    bool ActionEnabled(DeskAction action) => pointer != null && action switch
    {
        DeskAction.History => active.HasFlag(Sharing.Clipboard),
        DeskAction.Files => active.HasFlag(Sharing.Files),
        DeskAction.Recover or DeskAction.Performance => true,
        _ => false
    };
    void RunAction(DeskAction action)
    {
        if (!ActionEnabled(action) || closing) return;
        if (action == DeskAction.Recover) { pointer!.Recover(); return; }
        if (!Enabled) return; // A local modal dialog already owns keyboard focus.
        pointer!.BringPointerBack();
        if (action == DeskAction.History) { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); history.Focus(); }
        else if (action == DeskAction.Files) files.PerformClick();
        else if (action == DeskAction.Performance) stats.PerformClick();
    }
    void UpdateShortcutHint()
    {
        var chord = settings.Shortcuts.History;
        historyShortcut.Text = chord == Keys.None ? "" : $"{ActionShortcuts.Format(chord)} opens history";
        historyShortcut.Visible = chord != Keys.None;
    }
    void DisplayChanged(object? sender, EventArgs args)
    {
        if (IsHandleCreated && !closing) BeginInvoke(() => { if (peer != null) Detach("Display arrangement changed. Pair again to update the shared edge."); });
    }
    void Status(string message)
    {
        if (closing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Status(message)); } catch (InvalidOperationException) { }
            return;
        }
        detail.Text = message;
    }
    void Buttons(bool busy)
    {
        networkAccess.Enabled = listenToDesktop && !busy && !configuringNetwork;
        computers.Enabled = side.Enabled = !busy;
        editLayout.Visible = layout?.Editable == true;
        side.Visible = connect.Visible = layout?.Editable != true;
        connect.Enabled = !busy && computers.SelectedItem != null;
        if (nearby != null) nearby.Available = !busy;
        disconnect.Visible = disconnect.Enabled = busy;
        disconnect.Text = busy && peer == null ? "Cancel" : "Disconnect";
        files.Enabled = active.HasFlag(Sharing.Files);
        restore.Enabled = ActionEnabled(DeskAction.Recover);
        history.Enabled = active.HasFlag(Sharing.Clipboard);
        use.Enabled = history.Enabled && history.SelectedItem != null;
        noComputers.Text = layout == null ? "No computers found yet." : $"{pairedName}\n{peer?.RemoteAddress}";
        noHistory.Visible = history.Items.Count == 0;
        history.Visible = history.Items.Count > 0;
        noHistory.Text = peer == null ? "Clipboard history appears after pairing." :
            history.Enabled ? "Copied items will appear here." : "Clipboard sharing is off.";
    }
    internal void UseSettings(DeskSettings selected)
    {
        var previous = settings.Features;
        bool themeChanged = settings.Appearance != selected.Appearance;
        settings = selected;
        if (themeChanged)
        {
            DeskStyle.Apply(this, settings.Appearance);
            if (guide != null) DeskStyle.Apply(guide, settings.Appearance);
            if (performanceForm != null) DeskStyle.Apply(performanceForm, settings.Appearance);
        }
        UpdateShortcutHint();
        if (previous != selected.Features && peer != null)
        {
            // Withdrawing permission takes effect locally now. Wait for the peer's
            // ordered acknowledgement before accepting data for a re-enabled feature.
            acknowledged &= selected.Features;
            peer.Send(Kind.SharingChanged, new SharingUpdate(++preferenceRevision, selected.Features));
        }
        ApplySettings();
    }
    void ApplySettings()
    {
        if (pointer == null || windows == null || clipboard == null) return;
        var previous = active;
        active = settings.Features & acknowledged & remoteFeatures & Sharing.All;
        if (listenToDesktop) SetInputThreadPriority((active & (Sharing.Mouse | Sharing.Windows)) != 0);
        pointer.SetSharing(active);
        pointer.Crossing = settings.Crossing;
        windows.SetCaptureEnabled(active.HasFlag(Sharing.Windows));
        if (previous.HasFlag(Sharing.Windows) && !active.HasFlag(Sharing.Windows)) windows.StopSharing();
        if (previous.HasFlag(Sharing.Files) && !active.HasFlag(Sharing.Files)) edgeFiles?.CancelSharing();
        windows.Tiling = settings.Tiling;
        windows.ConfigureStream(settings.Stream);
        clipboard.SetSharing(active);
        if (listenToDesktop && previous.HasFlag(Sharing.Clipboard) != active.HasFlag(Sharing.Clipboard))
        {
            observe.Stop();
            sequence = Win.GetClipboardSequenceNumber();
            if (active.HasFlag(Sharing.Clipboard))
            {
                listeningClipboard = Win.AddClipboardFormatListener(Handle);
            }
            else
            {
                if (listeningClipboard) Win.RemoveClipboardFormatListener(Handle);
                listeningClipboard = false;
            }
        }
        if (listenToDesktop)
        {
            var warning = hotkeys.Update(Handle, settings.Shortcuts, ActionEnabled);
            shortcutWarning.Text = warning ?? ""; shortcutWarning.Visible = warning != null;
        }
        sharingState.Text = active == Sharing.None ? "All sharing is off" : $"Sharing · {active:G}";
        sharingState.Visible = true;
        Buttons(true);
    }
    void RefreshComputers()
    {
        if (closing) return;
        var selection = (computers.SelectedItem as NearbyComputer)?.Id;
        computers.BeginUpdate();
        computers.Items.Clear();
        if (nearby != null)
            foreach (var pc in nearby.Computers)
            {
                int index = computers.Items.Add(pc);
                if (pc.Id == selection) computers.SelectedIndex = index;
            }
        computers.EndUpdate();
        noComputers.Visible = computers.Items.Count == 0;
        computers.Visible = computers.Items.Count > 0;
    }

    async Task ConfigureNetwork(bool edit)
    {
        if (configuringNetwork || pending != null || peer != null || closing) return;
        configuringNetwork = true;
        networkAccess.Enabled = false;
        try
        {
            string program = PeerFirewall.ApplicationPath();
            var rules = await Task.Run(PeerFirewall.Read);
            if (closing) return;
            var saved = PeerFirewall.LoadPeer();
            var address = saved ?? rules.ExistingPeer(program) ?? await Task.Run(PeerFirewall.PreviousPeer);
            if (closing) return;
            if (edit || address == null)
            {
                networkPeer = address;
                var local = await RefreshLocalAddress();
                if (closing) return;
                using var prompt = new PeerAddressDialog(address, local, settings);
                DeskStyle.Apply(prompt, settings.Appearance);
                if (prompt.ShowDialog(this) != DialogResult.OK)
                {
                    if (discoveryLifetime == null) Status("Choose Network access to set up the other computer.");
                    return;
                }
                address = prompt.SelectedPeer!;
            }
            if (discoveryLifetime != null)
            {
                discoveryLifetime.Cancel();
                await discoveryTask;
                discoveryLifetime.Dispose();
                discoveryLifetime = null;
            }
            if (closing) return;
            networkPeer = address;
            _ = RefreshLocalAddress();
            if (!address.Equals(saved)) PeerFirewall.SavePeer(address);
            Status("Checking Windows Firewall access for " + address + "…");
            await PeerFirewall.EnsureAsync(program, address, rules);
            if (closing) return;
            discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(formLifetime.Token);
            discoveryTask = RunNearby(address, discoveryLifetime.Token);
        }
        catch (Exception ex) { Status($"{ex.Message} Choose Network access to retry."); }
        finally
        {
            configuringNetwork = false;
            if (!closing) Buttons(pending != null || peer != null);
        }
    }

    async Task RunNearby(IPAddress address, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                using var listener = new TcpListener(IPAddress.Any, Invitation.Port);
                listener.Start(4);
                using var discovery = new NearbyComputers(deviceId, Environment.MachineName, address);
                nearby = discovery;
                discovery.Available = pending == null;
                discovery.Changed += RefreshComputers;
                discovery.ConnectBackRequested += pc => { if (pending == null) _ = Connect(pc, null, true); };
                if (pending == null) Status("Select a nearby computer to connect. Open DeskPortal on both PCs.");
                var advertise = discovery.Run(run.Token);
                var accept = AcceptRequests(listener, address, run.Token);
                await Task.WhenAny(advertise, accept);
                run.Cancel();
                await Task.WhenAll(advertise, accept);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { if (!closing && pending == null) Status($"Nearby discovery will retry. {ex.Message}"); }
            finally { nearby = null; RefreshComputers(); }
            try { await Task.Delay(4000, stop); } catch (OperationCanceledException) { return; }
        }
    }

    async Task AcceptRequests(TcpListener listener, IPAddress address, CancellationToken stop)
    {
        while (true)
        {
            var socket = await listener.AcceptTcpClientAsync(stop);
            if (!address.Equals(((IPEndPoint)socket.Client.RemoteEndPoint!).Address)) { socket.Dispose(); continue; }
            if (attaching is { } links && links.Address.Equals(((IPEndPoint)socket.Client.RemoteEndPoint!).Address))
            {
                if (!links.Queue.Writer.TryWrite(socket)) socket.Dispose();
                continue;
            }
            if (pending != null)
            {
                bool sameAddress = outgoing != null && ((IPEndPoint)socket.Client.RemoteEndPoint!).Address.Equals(outgoing.Address);
                if (sameAddress && connectBack != null && connectBack.TrySetResult(socket)) continue;
                // If both people select each other, keep the lower ID's outbound connection.
                if (peer == null && !comparing && sameAddress && connectBack == null && deviceId.CompareTo(outgoing!.Id) > 0)
                    Detach("Connecting…");
                else { socket.Dispose(); continue; }
            }
            _ = Connect(null, socket);
        }
    }

    async Task Connect(NearbyComputer? target, TcpClient? incoming, bool returningRequest = false)
    {
        if (pending != null || closing || configuringNetwork) { incoming?.Dispose(); return; }
        var lifetime = new CancellationTokenSource();
        pending = lifetime;
        outgoing = target;
        hosting = target != null && !returningRequest;
        var token = lifetime.Token;
        var socket = incoming ?? new TcpClient(AddressFamily.InterNetwork);
        try
        {
            Buttons(true);
            state.Text = target == null ? "Incoming connection…" : $"Connecting to {target.Name}…";
            bool dialer = incoming == null;
            if (target != null)
            {
                using var dial = CancellationTokenSource.CreateLinkedTokenSource(token);
                dial.CancelAfter(TimeSpan.FromSeconds(returningRequest ? 6 : 2));
                try { await socket.ConnectAsync(target.Address, Invitation.Port, dial.Token); }
                catch (Exception ex) when (!token.IsCancellationRequested && !returningRequest && nearby != null &&
                    ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    var discovery = nearby;
                    var back = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
                    connectBack = back;
                    var request = (target.Id, Guid.NewGuid());
                    discovery.ConnectBack = request;
                    try { socket = await back.Task.WaitAsync(TimeSpan.FromSeconds(10), token); }
                    catch (TimeoutException) { throw new TimeoutException("Neither computer could reach the other. Check the peer address in Network access on both PCs."); }
                    finally
                    {
                        if (connectBack == back) connectBack = null;
                        if (discovery.ConnectBack == request) discovery.ConnectBack = null;
                    }
                    dialer = false;
                }
            }
            async Task<bool> Compare(string name, string code, CancellationToken wait)
            {
                comparing = true;
                state.Text = $"Pairing with {name}";
                Status("Compare the confirmation code on both computers.");
                using var prompt = new PairingDialog(name, code);
                bool accepted = await prompt.Ask(this, wait);
                if (accepted && pending == lifetime) { pairedName = name; Status($"Waiting for {name} to confirm…"); }
                return accepted;
            }
            var links = Channel.CreateBounded<TcpClient>(2);
            if (!dialer) attaching = (((IPEndPoint)socket.Client.RemoteEndPoint!).Address, links);
            Peer session;
            try
            {
                session = await ConfirmedPairing.Pair(socket, dialer, deviceId, Environment.MachineName, target,
                    Compare, token, dialer ? null : links.Reader.ReadAsync);
            }
            finally
            {
                if (attaching?.Queue == links) attaching = null;
                links.Writer.TryComplete();
                while (links.Reader.TryRead(out var unused)) unused.Dispose();
            }
            if (token.IsCancellationRequested) { session.Dispose(); return; }
            await RunSession(session, token);
            if (!token.IsCancellationRequested) Detach("The other PC disconnected.");
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) Detach(ex is EndOfStreamException ? "The other PC disconnected." : ex.Message); }
        finally { socket.Dispose(); lifetime.Dispose(); }
    }
    internal async Task RunSession(Peer session, CancellationToken token)
    {
        peer = session;
        acknowledged = settings.Features; preferenceRevision = 0;
        Buttons(true);
        var bounds = SystemInformation.VirtualScreen;
        localHello = new DeskHello(2, Environment.MachineName, bounds.Width, bounds.Height, side.SelectedIndex == 0, settings.Features,
            VideoCodec.VideoDecoderMask(), CaptionUpdates: true, LatencyProbe: true, ViewVisibility: true, Displays: DisplayInfo.Read(bounds),
            LayoutNode: deviceId, Organizer: hosting, SavedLayout: pairedName == null ? null : ScreenPlan.Load(pairedName));
        session.Send(Kind.Hello, localHello);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controls = new ControlDispatchWindow();
        // A peer can disconnect while Windows is still tracking a held caption button.
        // Unstick that native loop so the normal session cleanup can run.
        using var release = session.Token.Register(() =>
        {
            void Release() { if (peer == session) pointer?.ReleaseReceived(); }
            if (controls.OnOwnerThread) Release();
            else controls.Post(Release);
        });
        await Task.Run(() => session.Run(async packet =>
        {
            if (session.Split && Peer.IsData(packet.Kind)) await ready.Task.WaitAsync(session.Token).ConfigureAwait(false);
            if (packet.Kind == Kind.FileChunk) { WriteChunk(session, packet); return; }
            await OnUi(session, packet, controls).ConfigureAwait(false);
            if (packet.Kind == Kind.Hello) ready.TrySetResult();
        }), token);
    }
    // File data goes to disk on the connection's reader thread. The UI thread also runs the
    // input hooks, so a slow disk there would stall the pointer on the whole desktop.
    void WriteChunk(Peer session, Packet packet)
    {
        if (closing || session != peer) return;
        var files = clipboard ?? throw new InvalidDataException("Expected display information.");
        files.FilePacket(packet);
    }
    internal Sharing ActiveSharing => active;
    internal Sharing? PeerFeatures => layout == null ? null : remoteFeatures;
    internal DeskLayout? ScreenLayout => layout;
    internal string SessionStatus => detail.Text;
    internal void InstallInputHooks() => (pointer ?? throw new InvalidOperationException("Input is not paired.")).InstallHooks();
    Task OnUi(Peer session, Packet packet, ControlDispatchWindow controls)
    {
        if (closing || !IsHandleCreated) return Task.CompletedTask;
        ControlMessage? pointerMessage = packet.Kind switch
        {
            Kind.Mouse => new ControlMessage(packet.Kind, packet.Read<MouseSample>()),
            Kind.WindowInput => new ControlMessage(packet.Kind, packet.Read<WindowEvent>()),
            _ => null
        };
        if (pointerMessage is { IsMotion: true } report &&
            controls.PostPointer(report, async input =>
            {
                if (peer != session || session.Token.IsCancellationRequested) return;
                try { await ApplyPointer(input); }
                catch (Exception ex) { if (peer == session) Detach(ex.Message); }
            }))
            return Task.CompletedTask;
        // A copied image is megabytes of JSON. Parse it here, on the reader thread.
        Clip? clip = packet.Kind == Kind.Clip ? packet.Read<Clip>() : null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async void Apply()
            {
                try
                {
                    if (session == peer && !session.Token.IsCancellationRequested)
                    {
                        if (pointerMessage is { } input)
                        {
                            if (input.Value is MouseSample { Message: Win.LeftDown } press)
                                controls.TrackingOwnPress = active.HasFlag(Sharing.Mouse) && press.OutsideDesk == null &&
                                    layout != null && controls.OwnsWindowAt(layout.FromWire(new(press.X, press.Y)));
                            else if (input.Value is MouseSample { Message: Win.LeftUp }) controls.TrackingOwnPress = false;
                            await ApplyPointer(input, packet.ReceivedAt).ConfigureAwait(false);
                        }
                        else if (packet.Kind == Kind.Frame)
                        {
                            if (active.HasFlag(Sharing.Windows) && windows != null) await windows.Frame(packet);
                        }
                        else
                        {
                            if (packet.Kind is Kind.ReleaseInput or Kind.RecoverWindows) controls.TrackingOwnPress = false;
                            Dispatch(session, packet, clip);
                        }
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
            }
            if (session.Split && Peer.IsData(packet.Kind)) BeginInvoke((Action)Apply);
            else controls.Post(Apply);
        }
        catch (InvalidOperationException) { done.TrySetCanceled(); }
        return done.Task.WaitAsync(session.Token);
    }
    Task ApplyPointer(ControlMessage input, long receivedAt = 0)
    {
        if (pointer == null || windows == null) throw new InvalidDataException("Expected display information.");
        if (input.Value is WindowEvent windowEvent) return active.HasFlag(Sharing.Windows) ? windows.Input(windowEvent, receivedAt) : Task.CompletedTask;
        return pointer.Receive((MouseSample)input.Value);
    }
    void Dispatch(Peer session, Packet p, Clip? clip = null)
    {
        if (p.Kind == Kind.Hello && layout == null)
        {
            var hello = p.Read<DeskHello>();
            if (hello.Version != 2) throw new InvalidDataException("Update DeskPortal on both PCs to use sharing settings.");
            if (hello.Width is < 1 or > 65535 || hello.Height is < 1 or > 65535) throw new InvalidDataException("Invalid display size.");
            remoteFeatures = hello.Features;
            session.VideoDecoders = hello.VideoDecoders;
            session.CaptionUpdates = hello.CaptionUpdates;
            session.LatencyProbe = hello.LatencyProbe;
            session.ViewVisibility = hello.ViewVisibility;
            if (!NearbyComputers.ValidName(hello.Name) || (pairedName != null && pairedName != hello.Name))
                throw new InvalidDataException("Unexpected computer name.");
            pairedName = hello.Name;
            layout = OpenLayout(hello);
            side.SelectedIndex = layout.Right ? 0 : 1;
            arrangement.SetPeer(hello.Name);
            arrangement.SetLayout(layout);
            windows = new WindowDesk(this, session, layout, Status);
            performance = new(session, windows, hello.Name);
            performance.Start();
            performanceForm?.Render(performance);
            performanceTimer.Start();
            clipboard = new SharedClipboard(session, Status, RefreshHistory);
            edgeFiles = new EdgeFiles(layout, session, clipboard, Status);
            clipboard.TakeDropDestination = edgeFiles.TakeDestination;
            pointer = new PointerBridge(session, windows, layout, edgeFiles);
            pointer.RemoteShortcut = chord =>
            {
                if (hotkeys.Find(chord) is not { } action || (!Enabled && action != DeskAction.Recover)) return false;
                BeginInvoke(() => { if (peer == session) RunAction(action); });
                return true;
            };
            if (listenToDesktop) InstallInputHooks();
            ApplySettings();
            state.Text = $"Connected to {hello.Name}";
            Status("Drag a window across the shared edge to use it on the other computer.");
            Buttons(true); return;
        }
        if (clipboard == null || windows == null || pointer == null || layout == null) throw new InvalidDataException("Expected display information.");
        if (!active.HasFlag(Sharing.Windows) && p.Kind is Kind.Window or Kind.Frame or Kind.WindowPlaced or Kind.EndWindow or
            Kind.ViewCommand or Kind.HomePreview or Kind.ClearHomePreview or Kind.ReturnWindow or Kind.WindowCaption or Kind.ResizeWindow or Kind.WindowResized or
            Kind.ViewVisibility) return;
        if (!active.HasFlag(Sharing.Files) && p.Kind is Kind.FileDropQuery or Kind.FileDropReply) return;
        switch (p.Kind)
        {
            case Kind.ScreenLayout:
                var change = p.Read<ScreenLayoutChange>();
                if (!layout.Editable || change.Revision < 1 || change.Revision >= long.MaxValue)
                    throw new InvalidDataException("Invalid screen layout update.");
                if (change.Revision > layoutRevision || (change.Revision == layoutRevision && !lastLayoutFromLeader && !layoutLeader))
                    ChangeLayout(change.Plan.Reverse(),change.Revision,!layoutLeader);
                break;
            case Kind.TimedPing: break; // Measure the same UI dispatch used by ordered input.
            case Kind.SharingChanged: case Kind.TransferSharingChanged when session.Split:
                var update = p.Read<SharingUpdate>();
                var changedMask = PolicyMask(session, p.Kind);
                remoteFeatures = (remoteFeatures & ~changedMask) | (update.Features & changedMask);
                ApplySettings();
                session.Send(p.Kind == Kind.SharingChanged ? Kind.SharingAck : Kind.TransferSharingAck, update.Revision);
                break;
            case Kind.SharingAck: case Kind.TransferSharingAck when session.Split:
                if (p.Read<long>() == preferenceRevision)
                {
                    var ackMask = PolicyMask(session, p.Kind);
                    acknowledged = (acknowledged & ~ackMask) | (settings.Features & ackMask);
                    ApplySettings();
                }
                break;
            case Kind.Key: pointer.Receive(p.Read<KeySample>()); break;
            case Kind.ReleaseInput: pointer.ReleaseReceived(); break;
            case Kind.RecoverWindows: pointer.Reclaim(); edgeFiles?.Reset(); windows.RestoreAll(); break;
            case Kind.FileDropQuery: edgeFiles?.Query(p.Read<DropQuery>()); break;
            case Kind.FileDropReply: edgeFiles?.Reply(p.Read<DropReply>()); break;
            case Kind.Window: windows.Place(p.Read<ViewInfo>()); break;
            case Kind.WindowCaption: windows.UpdateCaption(p.Read<WindowCaption>()); break;
            case Kind.ResizeWindow: windows.ResizeSource(p.Read<WindowSize>()); break;
            case Kind.WindowResized: windows.Resized(p.Read<WindowSize>()); break;
            case Kind.WindowPlaced: windows.Ready(p.Read<Guid>()); break;
            case Kind.EndWindow: windows.End(p.Read<Guid>()); break;
            case Kind.ViewCommand: windows.ChangeView(p.Read<ViewCommand>()); break;
            case Kind.ViewVisibility: windows.SetViewVisible(p.Read<ViewVisibility>()); break;
            case Kind.HomePreview: windows.HomePreview(p.Read<ViewInfo>()); break;
            case Kind.ClearHomePreview: windows.ClearPreview(p.Read<Guid>()); break;
            case Kind.ReturnWindow:
                var r = p.Read<ReturnInfo>();
                windows.Restore(r.Id, r.X == int.MinValue ? null : layout.FromWire(new(r.X, r.Y)));
                break;
            case Kind.Clip: clipboard.Receive(clip ?? p.Read<Clip>()); break;
            case Kind.FileStart: case Kind.FileChunk: case Kind.FileEnd: case Kind.FileCancel: clipboard.FilePacket(p); break;
            default: throw new InvalidDataException("Unexpected peer message.");
        }
    }
    static Sharing PolicyMask(Peer session, Kind kind)
    {
        if (!session.Split) return Sharing.All;
        return kind is Kind.TransferSharingChanged or Kind.TransferSharingAck ? TransferFeatures : Sharing.Mouse | Sharing.Windows;
    }
    DeskLayout OpenLayout(DeskHello remote)
    {
        var local = localHello!;
        var bounds = SystemInformation.VirtualScreen;
        var size = new Size(remote.Width,remote.Height);
        if (remote.Displays == null) return new(bounds,size,hosting ? local.PeerRight : !remote.PeerRight);
        if (remote.LayoutNode == Guid.Empty || remote.LayoutNode == deviceId ||
            !DisplayInfo.Valid(remote.Displays,size) || !DisplayInfo.Valid(local.Displays,bounds.Size))
            throw new InvalidDataException("Invalid display inventory.");
        layoutLeader = local.Organizer != remote.Organizer ? local.Organizer : deviceId.CompareTo(remote.LayoutNode) < 0;
        bool right = layoutLeader ? local.PeerRight : !remote.PeerRight;
        var map = new DeskLayout(bounds,size,right,local.Displays!,remote.Displays);
        var first = layoutLeader ? local.SavedLayout : remote.SavedLayout?.Reverse();
        var second = layoutLeader ? remote.SavedLayout?.Reverse() : local.SavedLayout;
        if (map.Validate(first,out _) == null) map.Apply(first!);
        else if (map.Validate(second,out _) == null) map.Apply(second!);
        else if (!layoutLeader) map.Apply(DeskLayout.Default(remote.Displays,local.Displays!,remote.PeerRight).Reverse());
        return map;
    }
    void EditLayout()
    {
        if (layout is not { Editable: true } map || pairedName == null) return;
        long openedAt = layoutRevision;
        using var editor = new LayoutEditor(map,pairedName,settings.Appearance);
        if (editor.ShowDialog(this) != DialogResult.OK || map != layout || peer == null) return;
        if (openedAt != layoutRevision) { Status("The other PC changed the layout. Reopen the editor to use its latest positions."); return; }
        ShareLayout(editor.Selection);
    }
    internal void ShareLayout(ScreenPlan plan)
    {
        if (peer == null || layout?.Editable != true) return;
        ChangeLayout(plan,checked(layoutRevision+1),layoutLeader);
        peer.Send(Kind.ScreenLayout,new ScreenLayoutChange(layoutRevision,plan));
    }
    void ChangeLayout(ScreenPlan plan,long revision,bool fromLeader)
    {
        if (layout!.Validate(plan,out _) is { } error) throw new InvalidDataException(error);
        pointer?.Reclaim(); windows?.CancelDrag(); edgeFiles?.CancelSharing();
        layout.Apply(plan); layoutRevision = revision; lastLayoutFromLeader = fromLeader;
        arrangement.SetLayout(layout);
        Status("Screen layout updated.");
        if (listenToDesktop)
            try { plan.Save(pairedName!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Status($"Screen layout applied for this session. Could not save it. {e.Message}"); }
    }
    void RefreshHistory()
    {
        history.BeginUpdate(); history.Items.Clear();
        if (clipboard != null) history.Items.AddRange(clipboard.History.Cast<object>().ToArray());
        if (history.Items.Count > 0) history.SelectedIndex = 0;
        history.EndUpdate();
        noHistory.Visible = history.Items.Count == 0;
        history.Visible = history.Items.Count > 0;
    }
    void Detach(string reason)
    {
        SetInputThreadPriority(false);
        pending?.Cancel(); pending = null; outgoing = null; comparing = false; connectBack = null;
        attaching = null;
        if (nearby != null) nearby.ConnectBack = null;
        observe.Stop();
        performanceTimer.Stop(); performance?.Dispose(); performance = null;
        if (!closing) performanceForm?.Render(null);
        if (listeningClipboard) Win.RemoveClipboardFormatListener(Handle);
        listeningClipboard = false;
        if (IsHandleCreated) hotkeys.Clear(Handle);
        shortcutWarning.Visible = false;
        pointer?.Dispose(); pointer = null;
        edgeFiles?.Dispose(); edgeFiles = null;
        windows?.Dispose(); windows = null;
        clipboard?.Dispose(); clipboard = null;
        peer?.Dispose(); peer = null; layout = null;
        pairedName = null; localHello = null; layoutRevision = 0; lastLayoutFromLeader = false;
        arrangement.SetLayout(null);
        arrangement.SetPeer((computers.SelectedItem as NearbyComputer)?.Name);
        remoteFeatures = active = Sharing.None;
        sharingState.Visible = false;
        streamState.Visible = false;
        if (closing) return;
        history.Items.Clear();
        state.Text = "Disconnected"; Status(reason); Buttons(false);
    }
    void SetInputThreadPriority(bool enabled)
    {
        if (enabled) inputThreadPriority ??= InputThreadPriority.Raise();
        else
        {
            inputThreadPriority?.Dispose();
            inputThreadPriority = null;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x31D && listeningClipboard)
        {
            uint next = Win.GetClipboardSequenceNumber();
            if (next != sequence) { sequence = next; clipboardRetries = 0; observe.Stop(); observe.Start(); }
        }
        if (m.Msg == 0x312 && hotkeys.Matches((DeskAction)(int)m.WParam, m.LParam)) RunAction((DeskAction)(int)m.WParam);
        base.WndProc(ref m);
    }
}
