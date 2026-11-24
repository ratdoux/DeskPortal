# DeskPortal @VERSION@ for Windows x64

Drag a running window from one Windows PC onto another, use it there, and drag it back. Both PCs can share a mouse, keyboard, clipboard, and files over the same LAN.

## Downloads

| Package | How to use it |
| --- | --- |
| [Windows installer](https://github.com/@REPOSITORY@/releases/download/@TAG@/DeskPortal-@VERSION@-win-x64-Setup.exe) | Run it on both PCs, then open DeskPortal from Start. |
| [Portable ZIP](https://github.com/@REPOSITORY@/releases/download/@TAG@/DeskPortal-@VERSION@-win-x64.zip) | Extract the whole archive on each PC and run `DeskPortal.exe`. |

Both packages include the .NET desktop runtime and FFmpeg. To check a download, run `Get-FileHash -Algorithm SHA256 .\<filename>` in PowerShell and compare the result with the [published checksums](https://github.com/@REPOSITORY@/releases/download/@TAG@/SHA256SUMS.txt).

## Pair your PCs

1. In **Network access** on each PC, enter the IPv4 address displayed by the other PC. Approve the Windows prompt for the peer-specific firewall rules.
2. Pick the other PC under **Nearby computers**. If the six digits match, select **Codes match** on both screens.
3. Open **Edit screen layout**, arrange the monitors as they sit on your desk, and apply the layout. Drag a window by its title bar across a shared edge. **Ctrl+Alt+Home** brings your windows and input home.

To update, disconnect and close DeskPortal on both PCs before installing the same version on each. [Installation and troubleshooting](https://github.com/@REPOSITORY@/blob/@TAG@/INSTALL.md) · [Features and settings](https://github.com/@REPOSITORY@/blob/@TAG@/README.md)
