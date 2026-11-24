using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskLink;

static class PeerFirewall
{
    internal sealed record Rule(string Program, string Scope, int Protocol, string Ports,
        int Direction, int Action, bool Enabled, int Profiles, bool EdgeTraversal, string LocalScope,
        string RemotePorts, string Interfaces, string? Service)
    {
        public bool Matches(string program, IPAddress peer, int protocol, int port) =>
            string.Equals(Program, program, StringComparison.OrdinalIgnoreCase) && SingleHost(Scope)?.Equals(peer) == true &&
            Protocol == protocol && Ports == port.ToString() && Direction == 1 && Action == 1 && Enabled &&
            Profiles == 6 && !EdgeTraversal && LocalScope == "*" && RemotePorts == "*" &&
            string.Equals(Interfaces, "All", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(Service);
    }

    internal sealed record Rules(Rule? Tcp, Rule? Udp)
    {
        public bool Match(string program, IPAddress peer) => Tcp?.Matches(program, peer, 6, Invitation.Port) == true &&
            Udp?.Matches(program, peer, 17, NearbyComputers.Port) == true;

        public IPAddress? ExistingPeer(string program) => Tcp != null && SingleHost(Tcp.Scope) is { } ip &&
            Match(program, ip) ? ip : null;
    }

    public static IPAddress? ParsePeer(string? value)
    {
        string? input = value?.Trim();
        // IPAddress.TryParse alone also accepts hexadecimal, octal and shortened addresses.
        if (!IPAddress.TryParse(input, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            ip.ToString() != input) return null;
        int first = ip.GetAddressBytes()[0];
        return first is > 0 and < 224 and not 127 ? ip : null;
    }

    internal static IPAddress? SingleHost(string scope)
    {
        var host = scope.Split('-', '/')[0];
        return scope == host || scope == $"{host}-{host}" || scope == $"{host}/32" || scope == $"{host}/255.255.255.255"
            ? ParsePeer(host) : null;
    }

    static readonly string peerFile = Path.Combine(AppStorage.Root, "firewall-peer.txt");
    public static IPAddress? LoadPeer()
    {
        try { return ParsePeer(File.ReadAllText(peerFile)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void SavePeer(IPAddress peer)
    {
        Directory.CreateDirectory(AppStorage.Root);
        string temp = Path.Combine(AppStorage.Root, Path.GetRandomFileName());
        try { File.WriteAllText(temp, peer.ToString()); File.Move(temp, peerFile, true); }
        finally { File.Delete(temp); }
    }

    public static string ApplicationPath()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the running application.");
        if (Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start DeskPortal.exe so Windows can limit the rules to this application.");
        return path;
    }

    public static Rules Read() => ReadRules("DeskPortal");

    public static IPAddress? PreviousPeer()
    {
        var legacy = ReadRules("DeskStream");
        string? path = legacy.Tcp?.Program;
        return path != null && Path.GetFileName(path).Equals("DeskStream.exe", StringComparison.OrdinalIgnoreCase)
            ? legacy.ExistingPeer(path) : null;
    }

    static Rules ReadRules(string productName)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
        try
        {
            dynamic rules = policy.Rules;
            try
            {
                Rule? ReadRule(string name)
                {
                    dynamic rule;
                    try { rule = rules.Item(name); }
                    catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
                    try
                    {
                        return new Rule(rule.ApplicationName ?? "", rule.RemoteAddresses ?? "", rule.Protocol,
                            rule.LocalPorts ?? "", rule.Direction, rule.Action, rule.Enabled, rule.Profiles,
                            rule.EdgeTraversal, rule.LocalAddresses ?? "", rule.RemotePorts ?? "",
                            rule.InterfaceTypes ?? "", rule.ServiceName);
                    }
                    finally { Marshal.ReleaseComObject(rule); }
                }
                return new Rules(ReadRule($"{productName} from paired PC (TCP {Invitation.Port})"),
                    ReadRule($"{productName} from paired PC (UDP {NearbyComputers.Port})"));
            }
            finally { Marshal.ReleaseComObject(rules); }
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    public static async Task EnsureAsync(string program, IPAddress peer, Rules current)
    {
        if (current.Match(program, peer)) return;
        using var source = typeof(PeerFirewall).Assembly.GetManifestResourceStream("DeskPortal.PeerFirewall.ps1")
            ?? throw new InvalidOperationException("Firewall setup is missing from this build.");
        using var reader = new StreamReader(source);
        // Pass paths as data. The only elevated code is embedded in this build.
        string programData = Convert.ToBase64String(Encoding.UTF8.GetBytes(program));
        string script = $"$program = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{programData}'))\n" +
            $"$peerIp = '{peer}'\n$tcpPort = {Invitation.Port}\n$udpPort = {NearbyComputers.Port}\n" + reader.ReadToEnd();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = system,
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
        };
        try
        {
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start firewall setup.");
            await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) throw new InvalidOperationException("Windows could not install the peer rules. Check your administrator account and firewall policy.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException("Administrator approval was canceled."); }
        if (!(await Task.Run(Read)).Match(program, peer))
            throw new InvalidOperationException("The installed rules could not be verified in Windows Firewall.");
    }
}
