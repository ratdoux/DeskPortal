using System.Net;

namespace DeskLink;

sealed class MonitorPicker : Form
{
    readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Computers and monitors" };
    readonly BlockButton project = new() { Text = "Project screen" };
    readonly Action<MonitorChoice> open;
    public MonitorPicker(Rectangle work, DeskAppearance appearance, Action<MonitorChoice> open)
    {
        this.open = open;
        Text = "Choose a screen"; StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        ClientSize = new(470, 250);
        var page = DeskStyle.Column(Tone.Canvas, 14);
        page.ColumnStyles.Add(new(SizeType.Percent, 100));
        DeskStyle.Row(page, DeskStyle.Heading("Project a screen"));
        DeskStyle.Row(page, list, true);
        var cancel = new BlockButton { Text = "Cancel" }; DeskStyle.Button(cancel); DeskStyle.Button(project, true);
        DeskStyle.Row(page, DeskStyle.LineOf(project, cancel)); Controls.Add(page);
        AcceptButton = project; CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        list.DoubleClick += (_, _) => Choose(); project.Click += (_, _) => Choose();
        list.SelectedIndexChanged += (_, _) => project.Enabled = list.SelectedItem != null;
        DeskStyle.Apply(this, appearance);
        Location = new(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        Shown += (_, _) => { Center(); list.Focus(); };
        void Center() => Location = new(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
    }
    void Choose()
    {
        if (list.SelectedItem is not MonitorChoice choice) return;
        Close(); open(choice);
    }
    public void UpdateChoices(MonitorChoice[] choices)
    {
        var selected = list.SelectedItem;
        list.Items.Clear(); list.Items.AddRange(choices);
        list.SelectedIndex = choices.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(choices, selected));
        project.Enabled = list.SelectedItem != null;
    }
}

sealed class ScreenPeerDialog : Form
{
    readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Allowed screen peers" };
    readonly TextBox address = new() { Width = 180, PlaceholderText = "Peer IPv4 address", AccessibleName = "Peer IPv4 address" };
    readonly BlockButton add = new() { Text = "Allow IP" }, pair = new() { Text = "Pair" }, remove = new() { Text = "Forget" };
    readonly ScreenPeers peers;
    sealed record Row(IPAddress Address, string State) { public override string ToString() => $"{Address}   ·   {State}"; }
    public ScreenPeerDialog(ScreenPeers peers, DeskAppearance appearance)
    {
        this.peers = peers;
        Text = "Screen peers"; StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10); ClientSize = new(455, 300); MinimumSize = new(390, 300);
        var page = DeskStyle.Column(Tone.Canvas, 14); page.ColumnStyles.Add(new(SizeType.Percent, 100));
        DeskStyle.Row(page, DeskStyle.Heading("Additional screen peers"));
        DeskStyle.Row(page, new Label { AutoSize = true, Dock = DockStyle.Top, Text = "Allow the other PC’s IPv4 address here on both PCs. Choose Pair on one PC and compare the codes." });
        DeskStyle.Row(page, DeskStyle.LineOf(address, add)); DeskStyle.Row(page, list, true);
        foreach (var b in new[] { add, pair, remove }) DeskStyle.Button(b, b == pair);
        DeskStyle.Row(page, DeskStyle.LineOf(pair, remove)); Controls.Add(page);
        DeskStyle.Apply(this, appearance);
        address.TextChanged += (_, _) => Buttons(); list.SelectedIndexChanged += (_, _) => Buttons();
        add.Click += async (_, _) =>
        {
            if (PeerFirewall.ParsePeer(address.Text) is not { } ip) return;
            if (LocalIPv4.Read(null).Contains(ip)) { MessageBox.Show(this, "Enter the address shown on the other computer.", "Screen peers"); return; }
            await Change(peers.Addresses.Append(ip).Distinct().ToArray());
        };
        remove.Click += async (_, _) => { if (list.SelectedItem is Row r) await Change(peers.Addresses.Where(ip => !ip.Equals(r.Address)).ToArray()); };
        pair.Click += (_, _) =>
        {
            if (list.SelectedItem is not Row r) return;
            if (peers.Busy(r.Address)) peers.Disconnect(r.Address); else _ = peers.Connect(r.Address);
        };
        RefreshPeers();
    }
    async Task Change(IPAddress[] addresses)
    {
        try { await peers.Configure(addresses); if (!IsDisposed) address.Clear(); }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Could not update screen peers"); }
    }
    void Buttons()
    {
        var ip = PeerFirewall.ParsePeer(address.Text);
        add.Enabled = !peers.Configuring && ip != null && !peers.Addresses.Contains(ip) && peers.Addresses.Count() < 16;
        var selected = (list.SelectedItem as Row)?.Address;
        remove.Enabled = selected != null && !peers.Configuring;
        pair.Enabled = selected != null && peers.Ready(selected) && !peers.Configuring;
        pair.Text = selected != null && peers.Busy(selected) ? "Disconnect" : "Pair";
    }
    public void RefreshPeers()
    {
        var ip = (list.SelectedItem as Row)?.Address;
        var rows = peers.Addresses.Select(a => new Row(a, peers.State(a))).ToArray();
        list.Items.Clear(); list.Items.AddRange(rows);
        list.SelectedIndex = rows.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(rows, r => r.Address.Equals(ip)));
        Buttons();
    }
}
