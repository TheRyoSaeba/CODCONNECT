
$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir "license.rtf"

$lines = @(
    "CODConnect",
    "Made by Makimura",
    "",
    "This installer lays down the CODConnect desktop app, the CODConnect background service,",
    "and (when bundled) the Npcap and SoftEther components needed for the virtual LAN."
)

$body = ($lines | ForEach-Object { $_ -replace "\\", "\\" }) -join "\par`r`n"
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18`r`n$body\par`r`n}"

[System.IO.File]::WriteAllText($outPath, $rtf)
Write-Host "License written: $outPath"
