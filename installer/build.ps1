# Builds installer/build/CODCONNECT-Setup.msi. See installer/README.md.
#   powershell -File installer\build.ps1 -Version 1.0.0 -PrereqsDir C:\prereqs

param(
    [string]$PrereqsDir = "",
    [switch]$BundleNpcap,
    [string]$Version = "0.1.0",
    [string]$RendezvousUrl = "https://codconnect-rendezvous.onrender.com/",
    [string]$SoftEtherServerDir = "$env:ProgramFiles\SoftEther VPN Server",
    [string]$SoftEtherClientDir = "$env:ProgramFiles\SoftEther VPN Client"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

Write-Host "==> Publishing UI (self-contained single file)"
dotnet publish (Join-Path $repoRoot "src/CODConnect.UI/CODConnect.UI.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -o (Join-Path $buildDir "ui") `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "UI publish failed" }

Write-Host "==> Publishing Service (self-contained single file)"
dotnet publish (Join-Path $repoRoot "src/CODConnect.Service/CODConnect.Service.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -o (Join-Path $buildDir "service") `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

$uiExe = Join-Path $buildDir "ui/CODConnect.UI.exe"
$serviceExe = Join-Path $buildDir "service/CODConnect.Service.exe"
if (-not (Test-Path $uiExe)) { throw "UI single-file exe was not produced: $uiExe" }
if (-not (Test-Path $serviceExe)) { throw "Service single-file exe was not produced: $serviceExe" }

$icon = Join-Path $PSScriptRoot "assets/codconnect.ico"
$license = Join-Path $PSScriptRoot "assets/license.rtf"
$wixArgs = @(
    "build",
    (Join-Path $PSScriptRoot "Product.wxs"),
    "-o", (Join-Path $buildDir "CODCONNECT-Setup.msi"),
    "-arch", "x64",
    "-ext", "WixToolset.UI.wixext",
    "-ext", "WixToolset.Util.wixext",
    "-d", "ProductVersion=$Version",
    "-d", "UiExe=$uiExe",
    "-d", "ServiceExe=$serviceExe",
    "-d", "AppIcon=$icon",
    "-d", "LicenseRtf=$license",
    "-d", "RendezvousUrl=$RendezvousUrl"
)

if ($PrereqsDir -ne "") {
    $npcap = if ($BundleNpcap) { Get-ChildItem $PrereqsDir -Filter "npcap*.exe" | Select-Object -First 1 } else { $null }
    if ($BundleNpcap -and -not $npcap) { throw "PrereqsDir must contain npcap*.exe" }

    $softetherClient = Get-ChildItem $PrereqsDir -Filter "softether-vpnclient*.exe" | Select-Object -First 1
    if (-not $softetherClient) { throw "PrereqsDir must contain softether-vpnclient*.exe (official installer)" }

    $serverPayload = Join-Path $buildDir "prereqs/softether-server"
    foreach ($pair in @(
        @{ Name = "softether-server"; Exe = "vpnserver_x64.exe"; Source = $SoftEtherServerDir; Target = $serverPayload })) {
        $staged = Join-Path $PrereqsDir $pair.Name
        $source = if (Test-Path (Join-Path $staged $pair.Exe)) { $staged } else { $pair.Source }
        if (-not (Test-Path (Join-Path $source $pair.Exe))) {
            throw "SoftEther runtime not found: expected $($pair.Exe) in $staged or $source"
        }
        if (Test-Path $pair.Target) { Remove-Item $pair.Target -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $pair.Target | Out-Null
        foreach ($file in @($pair.Exe, "vpncmd_x64.exe", "hamcore.se2")) {
            Copy-Item (Join-Path $source $file) $pair.Target
        }
    }

    $wixArgs += @("-d", "IncludePrereqs=true",
        "-d", "IncludeNpcap=$(if ($npcap) { "true" } else { "false" })",
        "-d", "NpcapPayload=$(if ($npcap) { $npcap.FullName } else { '' })",
        "-d", "SoftEtherServerDir=$serverPayload",
        "-d", "SoftEtherClientPayload=$($softetherClient.FullName)")
    Write-Host "==> Bundling prerequisite payloads:"
    Write-Host "    $(if ($npcap) { "$($npcap.Name) (-BundleNpcap: private builds only)" } else { 'Npcap not bundled: users install it from npcap.com' })"
    Write-Host "    SoftEther VPN Server runtime  <- $serverPayload"
    Write-Host "    $($softetherClient.Name) (official installer, chained at the end of setup)"
}
else {
    $wixArgs += @("-d", "IncludePrereqs=false", "-d", "IncludeNpcap=false")
    Write-Host "==> No -PrereqsDir given: MSI ships without prerequisite payloads."
    Write-Host "    The service will report them missing; see installer/README.md."
}

Write-Host "==> Building MSI"
wix @wixArgs
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host "==> Done: $buildDir\CODCONNECT-Setup.msi"
