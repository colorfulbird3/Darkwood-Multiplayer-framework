param([string]$GameDir)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadRoot = Join-Path $packageRoot 'Payload'
$versionFile = Join-Path $payloadRoot 'dmf-version.txt'
$version = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -Raw).Trim() } else { 'unknown' }

function Test-DarkwoodDirectory([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path 'Darkwood.exe')) -and (Test-Path -LiteralPath (Join-Path $Path 'Darkwood_Data'))
}

if (-not (Test-DarkwoodDirectory $GameDir)) {
    $parent = Split-Path $packageRoot -Parent
    if (Test-DarkwoodDirectory $parent) { $GameDir = $parent }
}

while (-not (Test-DarkwoodDirectory $GameDir)) {
    Write-Host 'Enter the Darkwood game directory, for example F:\SteamLibrary\steamapps\common\Darkwood' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Darkwood directory').Trim().Trim('"')
}

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (Get-Process Darkwood -ErrorAction SilentlyContinue) { throw 'Darkwood is running. Close the game completely, then run the installer again.' }

$backupRoot = Join-Path $GameDir ('DarkwoodMP_Backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$pluginNames = @(
    'DarkwoodMultiplayerFramework.dll','DarkwoodMultiplayerRenderSupport.dll',
    'DarkwoodMultiplayerFramework.Core.dll','DarkwoodMultiplayerFramework.Actions.dll',
    'DarkwoodMultiplayerFramework.Protocol.dll','DarkwoodMultiplayerFramework.Network.dll',
    'DarkwoodMultiplayerFramework.Entities.dll','DarkwoodMultiplayerFramework.Snapshots.dll',
    'DarkwoodMultiplayerFramework.DarkwoodAdapter.dll','Mirror.dll','Mirror.Components.dll','Mirror.Transports.dll'
)

foreach ($name in $pluginNames) {
    $path = Join-Path $GameDir ('BepInEx\plugins\' + $name)
    if (Test-Path -LiteralPath $path) {
        $backupPlugins = Join-Path $backupRoot 'BepInEx\plugins'
        New-Item -ItemType Directory -Path $backupPlugins -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupPlugins $name) -Force
        Remove-Item -LiteralPath $path -Force
    }
}

foreach ($source in Get-ChildItem -LiteralPath $payloadRoot -Recurse -File) {
    $relative = $source.FullName.Substring($payloadRoot.Length).TrimStart('\')
    $destination = Join-Path $GameDir $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
    Unblock-File -LiteralPath $destination -ErrorAction SilentlyContinue
}

Write-Host "Darkwood Multiplayer Framework $version installation completed." -ForegroundColor Green
Write-Host "Backup: $backupRoot"
