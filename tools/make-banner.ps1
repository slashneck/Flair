# Renders docs\banner.png and docs\social-preview.png from tools\banner.html with Microsoft Edge.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }
$page = ([Uri](Join-Path $PSScriptRoot 'banner.html')).AbsoluteUri
$profile = Join-Path $env:TEMP 'flair-banner-profile'
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 "--user-data-dir=$profile" --window-size=2560,840 "--screenshot=$root\docs\banner.png" $page | Out-Null
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 "--user-data-dir=$profile" --window-size=1280,640 "--screenshot=$root\docs\social-preview.png" "$page#social" | Out-Null
Write-Host 'Wrote docs\banner.png and docs\social-preview.png'
