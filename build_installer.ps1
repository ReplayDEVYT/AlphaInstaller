# Builds the installer: Build\Installer\Escape from Tarkov Alpha Installer.exe (one file, .NET Framework 4.8, which
# Windows 10/11 have; nothing to install for players).
#   powershell -ExecutionPolicy Bypass -File Tools\installer\build_installer.ps1 [-Feed <updates.json URL>] [-News <news.json URL>]
# -Feed defaults to the URL in Unity\Assets\StreamingAssets\Reconstruction\update_feed.txt (the game's own feed), -News to
# news.json next to it. Both can be overridden later without a rebuild: installer.cfg next to the exe (feed=..., news=...).
# The UI sprites (Tools\installer\Assets) come from the project: extract_ui_sprites.py; the font: otf_to_ttf.py.
param([string]$Feed = '', [string]$News = '', [string]$Out = 'Build\Installer', [string]$Version = '', [string]$LauncherRepo = 'ReplayDEVYT/AlphaInstaller')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
# the folder holding TarkovAlphaServer, wherever this script was moved to
$root = $here
while ($root -and -not (Test-Path (Join-Path $root 'TarkovAlphaServer'))) { $root = Split-Path -Parent $root }
if (-not $root) { throw 'project root (the folder with TarkovAlphaServer) not found' }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$gen = Join-Path $Out 'gen'
New-Item -ItemType Directory -Force $gen | Out-Null

if (-not $Feed) {
    $feedFile = Join-Path $root 'Unity\Assets\StreamingAssets\Reconstruction\update_feed.txt'
    if (Test-Path $feedFile) {
        $Feed = (Get-Content $feedFile | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') } | Select-Object -First 1)
        if ($Feed) { $Feed = $Feed.Trim() } else { $Feed = '' }
    }
}
if (-not $Feed) { Write-Warning 'No feed URL: the installer needs installer.cfg (feed=<URL>) next to it.' }
# the launcher's version (shown in the window, compared with the latest release for self-updates): version.txt
if (-not $Version) { $Version = (Get-Content (Join-Path $here 'version.txt') -TotalCount 1).Trim() }
function CsString([string]$s) { '"' + $s.Replace('\', '\\').Replace('"', '\"') + '"' }
Set-Content -LiteralPath (Join-Path $gen 'Config.g.cs') -Encoding UTF8 -Value @"
namespace EftInstaller
{
    static partial class Config
    {
        const string BuiltFeed = $(CsString $Feed);
        const string BuiltNews = $(CsString $News);
        const string BuiltVersion = $(CsString $Version);
        const string BuiltLauncherRepo = $(CsString $LauncherRepo);
    }
}
"@

# icon from the game's own 256 px icon (ProjectSettings default icon)
$icon = Join-Path $gen 'app.ico'
$png = Join-Path $root 'Unity\Assets\Resources\icons\eft_add_icon2_256_sprite.png'
& python -I -c "import sys; from PIL import Image; Image.open(sys.argv[1]).convert('RGBA').save(sys.argv[2], sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])" $png $icon
if ($LASTEXITCODE -ne 0) { throw 'icon conversion failed (needs Pillow)' }

$manifest = Join-Path $gen 'app.manifest'
Set-Content -LiteralPath $manifest -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="EftAlpha.Installer"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security><requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3"><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware></windowsSettings>
  </application>
  <dependency><dependentAssembly><assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/></dependentAssembly></dependency>
</assembly>
'@

$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw 'No .NET SDK found.' }
$csc = Join-Path $Matches[2] (Join-Path $Matches[1] 'Roslyn\bincore\csc.dll')
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $Out 'Escape from Tarkov Alpha Installer.exe'
$rsp = Join-Path $gen 'installer.rsp'
$lines = New-Object System.Collections.Generic.List[string]
foreach ($o in '-nologo', '-nostdlib+', '-target:winexe', '-langversion:7.3', '-optimize+', '-debug-', '-utf8output', '-platform:anycpu') { $lines.Add($o) }
$lines.Add("-out:`"$exe`"")
$lines.Add("-win32icon:`"$icon`"")
$lines.Add("-win32manifest:`"$manifest`"")
foreach ($r in 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Web.Extensions') {
    $lines.Add("-r:`"$(Join-Path $fw "$r.dll")`"")
}
$assets = Join-Path $here 'Assets'
foreach ($f in Get-ChildItem $assets -File | Where-Object { $_.Extension -in '.png', '.ttf' }) {
    $lines.Add("-resource:`"$($f.FullName)`",Assets.$($f.Name)")
}
$lines.Add("-resource:`"$icon`",Assets.app.ico")
# 7-Zip for RAR/7z downloads (Tools\installer\7zip, copied from a 7-Zip install; see Archive.cs)
foreach ($name in '7z.exe', '7z.dll', 'License.txt') {
    $f = Join-Path $here "7zip\$name"
    if (-not (Test-Path $f)) { throw "missing $f (copy it from C:\Program Files\7-Zip)" }
    $lines.Add("-resource:`"$f`",SevenZip.$name")
}
foreach ($f in Get-ChildItem (Join-Path $here 'src') -Filter *.cs) { $lines.Add("`"$($f.FullName)`"") }
$lines.Add("`"$(Join-Path $gen 'Config.g.cs')`"")
[IO.File]::WriteAllLines($rsp, $lines)
& dotnet $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
Write-Host ("Launcher {3}: {0} ({1:N0} KB), feed '{2}', updates from github.com/{4}" -f $exe, ((Get-Item $exe).Length / 1KB), $Feed, $Version, $LauncherRepo)
