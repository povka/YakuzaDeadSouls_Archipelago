param(
    [string]$ArchipelagoPath = "D:\Dev_programs\Archipelago",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$name = "yakuza_dead_souls"
$src = Join-Path $repo "world\$name"
$out = Join-Path $repo "output\$name.apworld"
$tmp = Join-Path $repo "output\$name.zip"

if (-not (Test-Path $src)) { throw "no world source at $src" }

New-Item -ItemType Directory -Force (Join-Path $repo "output") | Out-Null
Remove-Item -Force $out, $tmp -ErrorAction SilentlyContinue

Get-ChildItem -Recurse -Force -Directory $src -Filter "__pycache__" |
    Remove-Item -Recurse -Force

# C# is the source of truth for every id, name and amount. Regenerate Data.py
# from it so a seed can never disagree with the client.
$client = Join-Path $repo "client/ApClient/bin/Debug/net10.0/ydsclient.exe"
if (Test-Path $client) {
    & $client --emit-world $src
    if ($LASTEXITCODE -ne 0) { throw "world generation failed" }
} else {
    Write-Warning "ydsclient not built - Data.py may be stale. Run: dotnet build"
}

Compress-Archive -Path $src -DestinationPath $tmp -Force
Move-Item $tmp $out -Force
Write-Output "built $out ($((Get-Item $out).Length) bytes)"

if ($Deploy) {
    $dest = Join-Path $ArchipelagoPath "custom_worlds"
    if (-not (Test-Path $dest)) { throw "no custom_worlds at $dest" }
    Copy-Item $out (Join-Path $dest "$name.apworld") -Force
    Write-Output "deployed to $dest"
}
