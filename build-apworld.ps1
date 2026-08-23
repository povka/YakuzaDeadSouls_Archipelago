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

Compress-Archive -Path $src -DestinationPath $tmp -Force
Move-Item $tmp $out -Force
Write-Output "built $out ($((Get-Item $out).Length) bytes)"

if ($Deploy) {
    $dest = Join-Path $ArchipelagoPath "custom_worlds"
    if (-not (Test-Path $dest)) { throw "no custom_worlds at $dest" }
    Copy-Item $out (Join-Path $dest "$name.apworld") -Force
    Write-Output "deployed to $dest"
}
