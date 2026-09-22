# Stages the bundled Electron runtime the sidebar-sync helper runs on. Claude's
# own binary has asar integrity fused on, so Graft ships its own Electron rather
# than reusing Claude's. The download is a build artifact — it lands under dist/,
# which is gitignored — and install.ps1 carries it beside ClaudeGraft.exe, where
# SidebarStorage looks for electron\electron.exe.
#
# Run before packaging. Pass -Version to pin; the default takes the latest stable.
param([string]$Version = "")

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'dist\ClaudeGraft\electron'

if (-not $Version) {
    $release = Invoke-RestMethod 'https://api.github.com/repos/electron/electron/releases/latest' `
        -Headers @{ 'User-Agent' = 'claude-graft' }
    $Version = $release.tag_name.TrimStart('v')
}

$zip = Join-Path $env:TEMP "electron-v$Version-win32-x64.zip"
$url = "https://github.com/electron/electron/releases/download/v$Version/electron-v$Version-win32-x64.zip"
Write-Host "Downloading Electron $Version..."
Invoke-WebRequest $url -OutFile $zip

Write-Host "Extracting to $dest ..."
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Expand-Archive $zip $dest -Force
Remove-Item $zip -Force

if (-not (Test-Path (Join-Path $dest 'electron.exe'))) { throw "electron.exe missing after extract" }
Write-Host "Electron $Version staged in $dest"
