# DeskPortal

Drag a running window from your Windows laptop onto your desktop PC, use it there, and drag it back. The app keeps running on the computer that opened it. DeskPortal streams that window over your LAN and sends clicks, scrolling, typing and resize requests back to it.

Both computers can share windows at once. One mouse and keyboard can move across the desk, along with clipboard text, images and files. Each monitor gets its own tile, so a laptop can sit between two monitors connected to the same desktop.

## Installation

1. On **both Windows x64 PCs**, run the release asset ending in `-win-x64-Setup.exe`, or extract the portable `-win-x64.zip` and start `DeskPortal.exe`. Both packages include the .NET desktop runtime. [Download a release](https://github.com/ratdoux/DeskPortal/releases) · [Installation and updates](INSTALL.md)
2. Enter the other PC’s IPv4 address on each machine. Approve the firewall prompt, select the peer under **Nearby computers**, and confirm the matching six-digit code on both PCs.
3. Open **Edit screen layout**, arrange the displays, then drag a window’s title bar across a shared edge. **Ctrl+Alt+Home** returns shared windows and reclaims input.

[Using the shared desk](#use-the-shared-desk) · [Streaming settings](#settings) · [Performance](#performance) · [Building from source](#build-and-verify) · [Publishing a release](#publishing-a-release)

## Screen layout

The map uses physical pixels and supports displays above, below, beside, or between one another. Offset displays share just the length where their edges touch. The editor checks that every screen is reachable and that tiles do not overlap. The map controls the shared pointer, title-bar drags and file drops while the PCs are paired. Native Windows display settings and application sizes are independent of the map.

Each PC remembers the layout for its peer in the app data folder. After a monitor is connected or removed, pair again to load the current inventory. An incompatible saved layout falls back to a connected arrangement. DeskPortal and DeskStream support the layout editor. Pairing with DeskLink uses its original left/right layout.

New installations use `%LOCALAPPDATA%\DeskPortal`. Upgrades continue using the existing DeskStream or DeskLink data folder for settings, layouts, received files and the firewall peer address. On the first DeskPortal launch, approve the Windows prompt to update the existing peer-only rules for the renamed executable.

## Use the shared desk

- **Mouse and keyboard.** Push the pointer through a shared screen edge. The keyboard follows it, and so do the back and forward mouse buttons. A key held during the crossing is released on the PC you leave. Crossing follows the shared portions of the monitor edges in the screen layout, including top and bottom edges.
- **Windows.** Grab a window by its title bar and drag across the shared edge. Windows moves the window itself, with Snap, until it reaches that edge. DeskPortal carries it from there. In File Explorer, grab the empty strip beside the tabs. Release on the other PC, then click, scroll or type in the streamed window. The Explorer tab controls and caption buttons remain usable there. DeskPortal also preserves interactive title-bar controls in WinUI apps such as PowerToys Settings. Drag the title bar back to return the window, or use **Bring windows home**. Basic Win32 owned dialogs are streamed separately.
- **Clipboard.** Copy text or an image on either PC and paste normally on the other. **Ctrl+Alt+V** opens the session's recent items. Double-click one to make it the active clipboard on both PCs. History holds up to 20 entries within a 32 MiB memory budget. The app honors the Windows clipboard history and synchronization exclusion markers.
- **Files.** Copy files in Explorer, then paste on the other PC after the received notification appears in DeskPortal. A selection over 1 GiB stays where it is and appears in the clipboard history. Double-click it there to send it. You can also drag files across the edge and release on the other desktop or inside an open filesystem folder in Explorer. The destination is the open folder. Open a subfolder first to drop inside it. Explorer windows with ambiguous tab paths decline the drop. **Send files** and dropping files onto the DeskPortal window also work. Transfers support up to 128 individual files and 8 GiB per batch. Existing destination filenames get numbered copies. A batch the other PC cannot save, such as a drop into a protected folder, fails by itself and DeskPortal says why.
- **Recovery.** **Ctrl+Alt+Home** reclaims input and brings shared windows home on both PCs. Disconnecting or closing the app also restores its sources. A small local helper watches the app's lifetime and restores transparency if it exits unexpectedly.

Files received ready to paste are scratch copies in `%LOCALAPPDATA%\DeskPortal\Received\Clipboard`. DeskPortal removes those older than a day the next time the PCs pair. Files sent with clipboard sharing off stay in `%LOCALAPPDATA%\DeskPortal\Received`, and DeskPortal shows the folder. Files dropped into Explorer go into the chosen folder. Clipboard history and pairing information last for the session.

## Settings

Open **Settings** on either PC, even before pairing. Save applies your choices immediately and keeps them in `%LOCALAPPDATA%\DeskPortal\settings.json` on that PC. Both PCs must run a build with sharing settings.

- **Appearance.** Choose Light or Dark. Each theme has its own randomly generated pair of complementary colors. **Shuffle colors** previews a new pair for the selected theme. **Save** keeps both palettes and applies the active one to the dashboard, settings, pairing codes and performance view. Windows contrast themes use the system colors.
- **Actions.** Choose whether moving the pointer across the edge is automatic or requires holding Ctrl or Shift. Returning the pointer and dragging windows or files across still use the normal gesture. Assign session shortcuts for clipboard history, bringing windows and input home, sending files, and opening performance. History defaults to **Ctrl+Alt+V** and recovery to **Ctrl+Alt+Home**. The other two start unassigned. Choose **Unassigned** to clear a binding or **Reset shortcuts** to restore the defaults.

- **Sharing** has four independent switches. Both PCs must enable a feature to use it. Mouse and keyboard controls the desktop edge crossing. With it off, you can still drag a shared window across and use the receiving PC’s own mouse and keyboard inside it. Turning off interactive windows returns shared windows home.
- **Files** controls explicit sends and Explorer drops. Copying file selections through the clipboard requires both Files and Clipboard. With only Files enabled, explicit sends are saved in the receiving folder and DeskPortal shows their location. Turning off Files cancels pending transfers and removes partial copies. Turning off Clipboard clears session history and stops monitoring the system clipboard.
- **Window size.** On the receiving PC, drag an edge or corner of a resizable window, double-click its title bar, or use its maximize button. DeskPortal resizes the original app and streams the new layout. Returning the window home or disconnecting restores its original size. Both PCs need a build with native resizing.
- **Placement on drop** controls incoming windows on this PC. Free placement leaves the window where you drop it. Fit left half, Fit right half, and Fit work area resize a supported app to fill the selected area, leaving the taskbar available. Fixed-size windows and older peers keep proportional fitting.
- **Windows streamed from this PC** defaults to Auto, 10 Mbps per window and a 60 fps cap. Auto benchmarks AV1, HEVC and H.264 encoders on the sending PC. It chooses the compatible encoder with the lowest warmed 95th-percentile encode time for a representative window size, the selected frame rate and the bitrate. A new profile runs in the background while streaming starts with an available encoder. Results are reused for up to seven days and invalidated by changes to the benchmark, codec library or graphics driver. You can also choose any of the three directly. The **1–80 Mbps** slider sets the encoder target for each outgoing window. Save applies changes to open streams. Actual bitrate varies with motion and content, and the sending PC shows the active codec, encoder and aggregate outgoing video payload rate. Native owned dialogs refresh up to 10 times per second.
- **PNG** preserves pixels losslessly. **JPEG** retains its quality control. These image modes also work with earlier builds that support sharing settings. Old JPEG defaults migrate to Auto on upgrade.

## Performance

Open **Performance…** on either PC while paired. The view updates once a second and keeps the last minute of bandwidth history, even while the panel is closed. A new pairing starts fresh counters.

- **Network** shows send and receive rates in decimal Mbps and session totals for window video, files, clipboard, and input/control. Counts include DeskPortal message framing and exclude pairing, discovery, TLS, and network headers. Network RTT uses the smoothed TCP round-trip estimate from Windows on the input connection. DeskPortal delay measures the local send queue plus the time at the peer from receipt through UI dispatch to sending the reply. Input round trip measures the whole exchange. These metrics use local monotonic clocks and exclude capture, decoding and display time. Older peers show the legacy round trip and leave DeskPortal delay blank. The CPU and working-set memory figures belong to the local DeskPortal process. The last line reports what Windows knows about the video and input connections: round-trip time, bytes retransmitted and timeouts since pairing.
- **Click ms** follows a left-button release to the first video frame captured after the source dispatched it. The sender measures from input arrival to capture. The receiver measures from release to presentation submission. This uses a per-click identifier and local timers on each PC. An app can paint a loading indicator before the conversation finishes opening.
- **Shared windows** shows the actual codec, encoded dimensions, frame rate, and encoded payload rate for each stream. On the source PC, **Codec ms** measures per-frame encoding including color conversion and packet copying. **Setup ms** shows the latest encoder initialization separately. **Queue ms** measures time waiting for room in the outgoing video queue. On the receiving PC, **Codec ms** measures decoder processing through GPU presentation submission or bitmap preparation. **Present FPS** counts new frames submitted to DXGI or drawn by the bitmap renderer. The **Last slow stage** column keeps the time, operation and duration of the latest measured step that took at least 100 ms while that window was shared. Every such step is also appended to `%LOCALAPPDATA%\DeskPortal\slow-stages.log`, which survives returning the window. The sending PC adds a **Frame round trip** entry when a frame takes 250 ms or more to reach the other PC, be shown and be acknowledged, along with the connection figures at that moment. A static window can use zero video bandwidth between changes.

## How it works

Each peer captures the original window through Windows Graphics Capture and a D3D11 frame pool. DeskPortal requests permission to omit the capture border on the source PC. Windows keeps the border when permission is unavailable or another app requires it for the same window. Each video stream uses a persistent FFmpeg encoder in low-latency mode. Capture samples the newest available pixels, while encoded reference frames travel in order. For compatible NVENC and AMF encoders, the capture surface stays on the GPU. A D3D11 video processor converts BGRA to NV12 on the encoder adapter. Native dialogs, still-image codecs and local drag previews can read pixels on the CPU. Unsupported texture encoders use the CPU-pixel input path. The receiver uses D3D11 hardware decoding and GPU color conversion when supported, then presents the GPU surface directly through a DXGI swap chain in the receiving window. The stats label this path **D3D11VA, GPU display**. A bitmap renderer handles unsupported display configurations. A software decoder handles unsupported hardware or stream profiles. Mouse, keyboard and window controls use the paired TLS connection. Video uses a second connection, and files and clipboard use a third. Pairing binds both data connections to the same certificate and session using single-use tickets. Each window decodes independently, in order, with at most two unacknowledged frames. A slow decoder pauses capture for that window. Pairing with an earlier build uses its original single connection. A resize or encoder setting change starts a fresh independently decodable sequence, and no keyframes are inserted in between. A window whose size the encoder cannot take, such as one with an odd width, is padded and cropped again on the receiving PC. A minimized streamed window stops its source from encoding until it is restored. The comparison code incorporates the TLS certificate, both peer identities and fresh random values from both computers.

Once the receiver decodes a frame, the source keeps the original window on its display and makes it fully transparent. This leaves its desktop area available for other windows while capture continues. A temporary preview draws the visible portion during the drag. Native owned Win32 dialogs use their window-printing interface. The app installs no display driver.

The prototype targets ordinary desktop apps at the same privilege level. Compatibility varies with custom title bars, popup menus, protected video, exclusive fullscreen rendering, raw input, and applications that manage their own transparency. File drops currently target Explorer and the desktop. The prototype streams window video only. Its practical latency and bandwidth depend on the window size and network.

## Adding a third computer

The current release allows and discovers one configured peer at a time. Multi-peer discovery would need an explicit list of allowed addresses and corresponding firewall rules. Each encrypted `Peer` has its own input, video and transfer connections. The single-session assumptions are in `DeskForm`, the global input hooks in `PointerBridge`, and the two groups of display tiles in `DeskLayout`.

1. Introduce persistent device identities verified on first pairing and a collection of independently authenticated sessions. A single input coordinator should own the Windows hooks and track which device currently owns the pointer, keyboard and pressed buttons. On disconnect it should release those inputs and return control locally.
2. Key each tile by device and display ID. Have one designated PC serialize and distribute layout revisions. Test a three-PC chain, simultaneous moves, monitor hot-plug and a peer leaving mid-drag before adding shared content.
3. Keep window video and files on direct links between the source and destination. Moving a projected window on to a third PC requires an explicit destination, a verified direct session, a new stream from the original app, and acknowledgement before releasing the old view. File sends need an explicit destination as well.
4. Give clipboard events an origin, unique ID and deduplication across sessions. Negotiate each feature per peer and keep performance counters and recovery scoped to that peer. Pairing with one computer should never grant automatic access to another.

For three fully connected PCs this design needs three pairwise sessions, up to nine TLS connections across the desk. The first deliverable would be discovery, pairing and pointer routing across three devices. Clipboard, files and multi-hop window handoff can then be verified separately.

## Build and verify

Building requires the .NET 8 SDK, CMake, Visual Studio 2022 with the Desktop development with C++ workload, and a Windows 10 or 11 SDK with C++/WinRT. Run from the project directory on Windows x64.

CMake verifies the SHA-256 of the pinned FFmpeg 8.1 x64 LGPL SDK in `vendor/` on first extraction. Keep that exact archive in the repository so clean builds continue to work after upstream rotates its daily downloads. To use a compatible SDK you already have, set the CMake cache variable `FFMPEG_ROOT`. The app ships the shared codec DLLs and their license. [Matching FFmpeg sources](https://github.com/FFmpeg/FFmpeg/tree/660b5aa7db) · [Windows build recipes](https://github.com/FFmpeg-Builds/FFmpeg-Builds)

For a framework-dependent developer build, use a machine with the x64 .NET 8 Desktop Runtime and publish to a separate directory.

```powershell
dotnet publish DeskPortal.csproj -c Release -o obj/dev-publish
dotnet obj/dev-publish/DeskPortal.dll --self-test
dotnet obj/dev-publish/DeskPortal.dll --pointer-check
dotnet obj/dev-publish/DeskPortal.dll --caption-input-check
dotnet obj/dev-publish/DeskPortal.dll --capture-test
dotnet obj/dev-publish/DeskPortal.dll --codec-check
```

The first check exercises pairing, clipboard ordering and exclusion, and intact file transfer over loopback. The capture check briefly opens two test processes on an interactive desktop and exercises a window swap, remote mouse and keyboard input, an owned dialog, and recovery. Add `--video` to the capture check to exercise playback, pause, resume, and return in FFplay. Only this optional check requires `ffplay.exe` on PATH.

The codec check compares all three encoders at 2 and 14 Mbps using the same synthetic moving image, decodes their output, and checks H.264 negotiation with a limited peer. It also sends a 641 × 361 picture through the CPU and GPU encode paths and verifies that one-pixel detail and the edges survive.

The pointer check sends a one-second sweep at 1,000 samples per second. It measures acknowledgment round-trip time and verifies the final coordinates and button order. For a LAN measurement, run `--pointer-listen <local-IPv4>` on the sending PC and `--pointer-join <sender-IPv4>` on the other. Pass the printed invitation to the joining process on standard input. These checks exchange synthetic coordinates and leave both physical cursors available.

To check window dragging between two interactive desktops, run `--window-listen <local-IPv4> <port>` on one and `--window-join <listener-IPv4> <port>` on the other, passing the printed invitation on standard input. Each PC opens a test window, sends it across the edge, moves the received window locally, and drags it home.

The corresponding `--explorer-listen` and `--explorer-join` check opens a temporary folder in tabbed Windows 11 File Explorer on the joining PC. It drags the folder window through the right edge, exercises the tab and maximize controls on the receiver, and returns the original window.

For the 4K LAN check, start `--4k-listen <local-IPv4> <port>` on the receiver. Pass its printed invitation on standard input to `--4k-join <receiver-IPv4> <port> [AV1|HEVC|H264|Auto]` on the sender. The check streams an animated 3840×2160 window at 10 Mbps with a 60 fps cap. It defaults to AV1, verifies decoded dimensions and motion near the far edge, and reports a 12-second warmed interval followed by ten clicks timed until a reference patch visibly changes on the receiving desktop. It prints every click in order before the summary.

## Publishing a release

GitHub Actions builds and tests the x64 app on Windows Server 2022 for branch pushes, pull requests and manual runs. The workflow packages the same self-contained files into a per-user installer and a portable ZIP, tests a silent installation and uninstall, and uploads the packages with their SHA-256 checksums as a workflow artifact. [Release workflow](.github/workflows/release.yml)

To publish, push a version tag on the commit you want to ship. For example, the following commands publish version 1.2.3 to the repository’s **Releases** page after the checks pass.

```powershell
git tag v1.2.3
git push origin v1.2.3
```

A tag such as `v1.2.3-beta.1` produces a GitHub prerelease. Branch builds and manual runs produce downloadable workflow artifacts only. The release job uses GitHub’s built-in token with write access to repository contents.

To build the same packages locally, run this from the repository root in PowerShell 7 on Windows x64. The script downloads a checksum-pinned portable Inno Setup compiler, or accepts an existing compiler via `-IsccPath`. Output goes to `artifacts/releases/<version>/`.

```powershell
./scripts/Build-Release.ps1 -Version 1.2.3-beta.1
```
