using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskLink;

sealed class PeerAddressDialog : Form
{
    public IPAddress? SelectedPeer { get; private set; }

    public PeerAddressDialog(IPAddress? current)
    {
        Text = "Network access";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var panel = new TableLayoutPanel { AutoSize = true, Padding = new Padding(18), ColumnCount = 1 };
        panel.Controls.Add(DeskStyle.Heading("Other computer’s IPv4 address"));
        panel.Controls.Add(new Label { Text = $"DeskPortal remembers this address. Windows may ask for administrator approval to allow this peer on TCP {Invitation.Port} and UDP {NearbyComputers.Port}.",
            AutoSize = true, MaximumSize = new Size(400, 0) });
        var input = new TextBox { AccessibleName = "Other computer’s IPv4 address", Width = 260,
            PlaceholderText = "e.g. 192.168.1.42", Text = current?.ToString() ?? "" };
        panel.Controls.Add(input);
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        if (localAddresses.Length > 0)
            panel.Controls.Add(new Label { Text = $"{Environment.MachineName} · {string.Join(", ", localAddresses.Select(a => a.ToString()))}",
                AutoSize = true, MaximumSize = new Size(400, 0), Tag = Tone.Quiet });
        var save = new BlockButton { Text = "Allow peer" };
        var cancel = new BlockButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        DeskStyle.Button(save, true); DeskStyle.Button(cancel);
        void ValidatePeer()
        {
            SelectedPeer = PeerFirewall.ParsePeer(input.Text);
            save.Enabled = SelectedPeer != null && !localAddresses.Contains(SelectedPeer);
        }
        input.TextChanged += (_, _) => ValidatePeer(); ValidatePeer();
        save.Click += (_, _) => DialogResult = DialogResult.OK;
        panel.Controls.Add(DeskStyle.LineOf(save, cancel));
        AcceptButton = save; CancelButton = cancel;
        Controls.Add(panel); ActiveControl = input;
    }
}
