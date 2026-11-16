# Smoke test of a server package: unpacks it to Build\ServerPackage\smoke, gives it port 6979 (not the usual 6969),
# starts ConsoleApp.exe hidden, asks /client/server/info, stops it.
#   powershell -ExecutionPolicy Bypass -File Tools\installer\smoke_server.ps1 <package zip>
param([string]$Zip)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dir = Join-Path $root 'Build\ServerPackage\smoke'
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Zip).Path, $dir)
$base = Get-Content -Raw (Join-Path $dir 'Files\configs\server_base.json')
$cfg = [Text.RegularExpressions.Regex]::Replace($base, '"port":\s*6969', '"port": 6979', 1)
[IO.File]::WriteAllText((Join-Path $dir 'Files\configs\server.json'), $cfg)
$p = Start-Process -FilePath (Join-Path $dir 'ConsoleApp.exe') -WorkingDirectory $dir -WindowStyle Hidden -PassThru
try {
    $answer = $null
    for ($i = 0; $i -lt 30 -and -not $answer; $i++) {
        Start-Sleep -Seconds 1
        if ($p.HasExited) { throw "server exited with code $($p.ExitCode)" }
        try {
            $client = New-Object Net.WebClient
            $bytes = $client.UploadData('http://127.0.0.1:6979/client/server/info', 'POST', [byte[]]@())
            $answer = $bytes
        } catch { }
    }
    if (-not $answer) { throw 'no answer on port 6979 after 30 s' }
    # answers are zlib-compressed: skip the 2-byte header, inflate
    $ms = New-Object IO.MemoryStream(, $answer)
    [void]$ms.ReadByte(); [void]$ms.ReadByte()
    $text = (New-Object IO.StreamReader((New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)))).ReadToEnd()
    Write-Host "server/info: $text"
    Write-Host 'SMOKE: PASS'
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
