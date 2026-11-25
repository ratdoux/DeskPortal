# Embedded in the application. The caller supplies the executable path, peer and ports.
$ErrorActionPreference = 'Stop'
try {
    $env:PSModulePath = "$PSHOME\Modules"
    Import-Module (Join-Path $env:PSModulePath 'NetSecurity\NetSecurity.psd1')
    $entries = @(@('TCP', $tcpPort), @('UDP', $udpPort))
    if ($screenOnly) { $entries = ,@('TCP', $tcpPort) }
    foreach ($entry in $entries) {
        $protocol, $port = $entry
        # The persistent IDs let an upgrade retarget the existing rules in place.
        $name = "DeskStream-Peer-$protocol-$port"
        $display = "DeskPortal from paired PC ($protocol $port)"
        if ($screenOnly) {
            $name = "DeskPortal-Screen-Peers-TCP-$port"
            $display = "DeskPortal screen peers (TCP $port)"
            if ($peerIp.Count -eq 0) {
                Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                continue
            }
        }
        $parameters = @{
            PolicyStore = 'PersistentStore'
            Name = $name
            Direction = 'Inbound'
            Action = 'Allow'
            Enabled = 'True'
            Profile = @('Private', 'Public')
            Program = $program
            Protocol = $protocol
            LocalPort = $port
            RemotePort = 'Any'
            LocalAddress = 'Any'
            RemoteAddress = $peerIp
            Service = 'Any'
            InterfaceType = 'Any'
            EdgeTraversalPolicy = 'Block'
        }
        if (Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule @parameters -NewDisplayName $display
        } else {
            New-NetFirewallRule @parameters -DisplayName $display | Out-Null
        }
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
