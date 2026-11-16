# Packages TarkovAlphaServer for the installer's SERVER part:
#   powershell -ExecutionPolicy Bypass -File Tools\installer\package_server.ps1 [-Out Build\ServerPackage]
# Copies the server source to <Out>\src (the server repo's own bin/obj stay untouched), publishes ConsoleApp
# self-contained for win-x64 (players need no .NET install), adds Ref\ as _References (the game assemblies the server
# loads through path.txt), leaves out per-server state (profiles, logs, ServerResponses, TraderStock.json) and
# clears matchmaking.gameServerPath (the installer points it at the installed game). Result:
# <Out>\server_<yyyyMMdd_HHmm>.zip with the server at the zip root. Publish it with
#   python -I Tools\patch\make_patch.py server <site> <zip> --name <name> [--server-url NA=<link> --server-url EU=<link>]
param([string]$Out = 'Build\ServerPackage', [switch]$Open)
$ErrorActionPreference = 'Stop'
# the folder holding TarkovAlphaServer, wherever this script was moved to
$root = $PSScriptRoot
while ($root -and -not (Test-Path (Join-Path $root 'TarkovAlphaServer'))) { $root = Split-Path -Parent $root }
if (-not $root) { throw 'project root (the folder with TarkovAlphaServer) not found' }
$server = Join-Path $root 'TarkovAlphaServer'
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
$src = Join-Path $Out 'src'
$stage = Join-Path $Out 'TarkovServer'

Write-Host "Copying source to $src"
New-Item -ItemType Directory -Force $src | Out-Null
# mirrored in place (/MIR removes files deleted from the source): deleting the folder fails while a build process
# still has it open. robocopy exit codes below 8 are success
& robocopy $server $src /MIR /NFL /NDL /NJH /NJS /NP /XD bin obj .git .vs /XF *.user | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

Write-Host 'Publishing ConsoleApp (Release, win-x64, self-contained)'
if (Test-Path $stage) {
    try { Remove-Item -Recurse -Force $stage -ErrorAction Stop }
    catch { $stage = Join-Path $Out ('TarkovServer_' + (Get-Date -Format 'yyyyMMdd_HHmmss')) } # old one still in use
}
# no build servers left running afterwards (they keep the source folder open)
& dotnet publish (Join-Path $src 'Applications\ConsoleApp\ConsoleApp.csproj') -c Release -r win-x64 --self-contained true -o $stage -nologo -v q --disable-build-servers -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$refs = Join-Path $stage '_References'
New-Item -ItemType Directory -Force $refs | Out-Null
Copy-Item (Join-Path $server 'Ref\*.dll') $refs
Set-Content -LiteralPath (Join-Path $stage 'path.txt') -Value '_References' -NoNewline -Encoding ASCII
foreach ($name in 'profiles', 'logs', 'ServerResponses', 'TraderStock.json', 'REQUESTED.txt') {
    $p = Join-Path $stage $name
    if (Test-Path $p) { Remove-Item -Recurse -Force $p }
}
# gameServerPath of this machine means nothing on a player's; the installer sets it
$cfg = Join-Path $stage 'Files\configs\server.json'
if (Test-Path $cfg) {
    $text = [IO.File]::ReadAllText($cfg)
    $text = [Text.RegularExpressions.Regex]::Replace($text, '("gameServerPath"\s*:\s*)"[^"]*"', '$1""')
    [IO.File]::WriteAllText($cfg, $text, (New-Object Text.UTF8Encoding($false)))
}

$zip = Join-Path $Out ('EFT-Alpha-Server_{0}.zip' -f (Get-Date -Format 'yyyy-MM-dd_HHmm'))
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# entries written one by one: ZipFile.CreateFromDirectory of Windows PowerShell 5 (.NET 4.x) stores backslashes,
# which other unzip tools treat as part of the file name
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $prefix = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $name = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$size = (Get-Item $zip).Length / 1MB
Write-Host ('Server package: {0} ({1:N1} MB)' -f $zip, $size)
Write-Host 'Next: upload this zip (a GitHub release or any direct link), then put its link into HostSite with'
Write-Host ('  python -I Tools\patch\make_patch.py server HostSite\versions "{0}" --name {1} --url <link>' -f $zip, (Get-Date -Format 'yyyy-MM-dd'))
if ($Open) { & explorer.exe "/select,$zip" }
