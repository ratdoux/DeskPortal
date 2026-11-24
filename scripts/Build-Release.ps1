#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z')]
    [string]$Version = '0.0.0-dev',
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$buildRoot = [IO.Directory]::CreateDirectory((Join-Path $root 'obj/release-build')).FullName
$scratch = [IO.Directory]::CreateDirectory((Join-Path $buildRoot ([guid]::NewGuid().ToString('N')))).FullName
$publish = Join-Path $scratch 'publish'
$assets = [IO.Directory]::CreateDirectory((Join-Path $scratch 'assets')).FullName
$name = "DeskPortal-$Version-win-x64"

Push-Location $root
try {
    & dotnet publish DeskPortal.csproj -c Release -r win-x64 --self-contained true -o $publish `
        "-p:Version=$Version" "-p:OutputPath=$scratch/build/" -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }

    $required = 'DeskPortal.exe','DeskPortal.dll','DeskPortal.runtimeconfig.json','DeskCapture.dll',
        'avcodec-62.dll','avutil-60.dll','swscale-9.dll','swresample-6.dll','FFmpeg-LICENSE.txt',
        'coreclr.dll','hostfxr.dll','System.Windows.Forms.dll','README.md','INSTALL.md',
        'licenses/Microsoft.NETCore.App/LICENSE.TXT','licenses/Microsoft.NETCore.App/THIRD-PARTY-NOTICES.TXT',
        'licenses/Microsoft.WindowsDesktop.App/LICENSE'
    foreach ($file in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Missing release file: $file" }
    }
    foreach ($check in '--self-test','--pointer-check') {
        $test = Start-Process -FilePath (Join-Path $publish 'DeskPortal.exe') -ArgumentList $check `
            -WorkingDirectory $publish -NoNewWindow -Wait -PassThru
        if ($test.ExitCode) { throw "$check failed ($($test.ExitCode))." }
    }

    if (!$IsccPath) {
        # Install the pinned compiler in portable mode inside this build's temporary directory.
        $bootstrap = Join-Path $scratch 'innosetup-6.7.4.exe'
        Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $bootstrap
        if ((Get-FileHash -LiteralPath $bootstrap).Hash -ne 'A549929319F9120716677C685535CA4671E4B937B5FDC87E2585F32D8EB30E8C') {
            throw 'Inno Setup compiler download failed its SHA-256 check.'
        }
        $compilerDir = Join-Path $scratch 'inno'
        $setup = Start-Process -FilePath $bootstrap -ArgumentList "/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$compilerDir`"" -WindowStyle Hidden -Wait -PassThru
        if ($setup.ExitCode) { throw "Inno Setup compiler setup failed ($($setup.ExitCode))." }
        $IsccPath = Join-Path $compilerDir 'ISCC.exe'
    }
    & $IsccPath '/Qp' "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$assets" "/F$name-Setup" 'installer/DeskPortal.iss'
    if ($LASTEXITCODE) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }

    [IO.Compression.ZipFile]::CreateFromDirectory($publish, (Join-Path $assets "$name.zip"))
    $assetNames = "$name-Setup.exe", "$name.zip"
    $hashes = foreach ($file in $assetNames) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $assets $file) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $file"
    }
    $hashes | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii
    $destination = [IO.Directory]::CreateDirectory((Join-Path $root "artifacts/releases/$Version")).FullName
    foreach ($file in ($assetNames + 'SHA256SUMS.txt')) {
        Copy-Item -LiteralPath (Join-Path $assets $file) -Destination $destination -Force
    }
    Write-Host "Release files are in $destination"
}
finally {
    Pop-Location
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($scratch)) -eq $buildRoot) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
