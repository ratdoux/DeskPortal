; Compiled by scripts/Build-Release.ps1 from the same publish folder as the ZIP.
[Setup]
AppId={{B70B6DA7-0829-4BD3-9EBF-0684D93A3A2E}
AppName=DeskPortal
AppVersion={#AppVersion}
VersionInfoVersion={#GetVersionNumbersString(PublishDir + "\DeskPortal.exe")}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\DeskPortal
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.18362
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\DeskPortal.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\DeskPortal"; Filename: "{app}\DeskPortal.exe"

[Run]
Filename: "{app}\DeskPortal.exe"; Description: "Launch DeskPortal"; Flags: nowait postinstall skipifsilent
