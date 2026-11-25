# Installing DeskPortal

Install the same DeskPortal release on both Windows x64 PCs. Connect them to a network where they can reach each other by IPv4. A wired desktop and a Wi-Fi laptop can pair on the same LAN. The release installer and portable ZIP both include the .NET desktop runtime and the FFmpeg libraries.

## Windows installer

1. Open **Releases** on the DeskPortal GitHub repository and select the version you want. Download the asset ending in `-win-x64-Setup.exe` on each PC. [GitHub Releases](https://github.com/ratdoux/DeskPortal/releases)
2. Run the installer under your Windows account. The default location is `%LOCALAPPDATA%\Programs\DeskPortal`. You can launch **DeskPortal** from Start after installation.
3. Complete the network and pairing steps below on both PCs.

**Portable option.** Download the matching `-win-x64.zip` asset, extract the entire archive to a permanent folder, and run `DeskPortal.exe` there. Keep the DLLs beside the executable. GitHub’s automatically generated *Source code* archives are for building the project.

To verify a download, run `Get-FileHash -Algorithm SHA256 .\<downloaded-file>` in PowerShell and compare the result with the entry for that filename in the release’s `SHA256SUMS.txt`.

## Pair the two PCs

1. Open DeskPortal on both PCs. Each dashboard shows its own IPv4 address beside **This PC**. In **Network access** on each PC, enter the address from the other PC’s dashboard. If you have several network adapters, the setup dialog lists their available IPv4 addresses.
2. Approve the Windows administrator prompt for the firewall rules. They allow the selected peer to reach this copy of DeskPortal on UDP 47832 for discovery and TCP 47831 for pairing and sharing.
3. On either PC, select the other machine under **Nearby computers**. Check the six digits on both screens. If they match, click **Codes match** on each PC.
4. Open **Edit screen layout**. Place each monitor where it sits on your desk, with an edge touching its neighbor. Click **Apply layout** to synchronize the arrangement.
5. Move your mouse through a shared edge. Try dragging a window across by its title bar, then click or type in it on the other PC. **Ctrl+Alt+Home** brings the windows and input home.

Open **Quick start** on the dashboard or in the network setup dialog for a short walkthrough of pairing, arranging monitors, sharing and returning control.

## Pair additional screen peers

For additional projection partners, open **Screen peers…** on both PCs, allow each other’s IPv4 address and approve Windows Firewall setup. Choose **Pair** on one PC and confirm the same code on both. These direct connections use a separate peer-only TCP 47833 rule. Press **Ctrl+Alt+P** to choose a remote monitor.

## If the other PC does not appear

- Check the IPv4 address on each PC. After switching Wi-Fi networks or receiving a new address, disconnect DeskPortal and update **Network access** on the other machine.
- If a firewall prompt was declined or a portable copy was moved, use **Network access** to check the rules for the current executable.
- If pairing works but a shared edge feels wrong, compare the labeled display tiles in **Edit screen layout** with your physical monitors. A Windows display change takes effect in DeskPortal when you pair again.

## Update or uninstall

Disconnect and close DeskPortal on both PCs to return any shared windows. For an installed copy, run the new installer in the same location. For a portable copy, extract the new ZIP into an empty folder and start the new executable. Check **Network access** if the executable path changed, then pair again.

Remove an installed copy from **Settings → Apps → Installed apps**. For a portable copy, close it and remove its extracted folder. User settings, layouts and received files are stored separately in `%LOCALAPPDATA%\DeskPortal`. Upgraded installations can use `%LOCALAPPDATA%\DeskStream` or `%LOCALAPPDATA%\DeskLink`. Windows Firewall lists the two inbound rules under **DeskPortal from paired PC**.
