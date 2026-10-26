# Copies the plugin into the deck software (fifine Control Deck or Mirabox StreamDock — both use
# %APPDATA%\HotSpot\StreamDock\plugins) and restarts the software so it loads the plugin.
# Run from the repo (after build.ps1) or from the unpacked release zip.
# -ProfileScript <script>: run a profile-editing script while the software is closed (gets the profile folder as argument).
param([string]$ProfileScript)
$ErrorActionPreference = 'Stop'
$name = 'com.megarohas.deck.sdPlugin'
$data = "$env:APPDATA\HotSpot\StreamDock"
$known = @(
    "${env:ProgramFiles(x86)}\fifine Control Deck\fifine Control Deck.exe",
    "${env:ProgramFiles(x86)}\StreamDock\StreamDock.exe",
    "$env:ProgramFiles\StreamDock\StreamDock.exe"
)
$app = Get-Process 'fifine Control Deck', 'StreamDock' -ErrorAction SilentlyContinue | Select-Object -First 1
$exe = if ($app) { $app.Path } else { $known | Where-Object { Test-Path $_ } | Select-Object -First 1 }

if (-not (Test-Path (Join-Path $PSScriptRoot "$name\MegarohasDeck.exe"))) { throw "MegarohasDeck.exe is missing - run build.ps1 first" }
if ($app) { Stop-Process -Id $app.Id; $app.WaitForExit(10000) | Out-Null }
Get-Process MegarohasDeck -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

$target = Join-Path "$data\plugins" $name
New-Item -ItemType Directory -Force "$data\plugins" | Out-Null
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item (Join-Path $PSScriptRoot $name) $target -Recurse
Write-Host "Installed $target"

if ($ProfileScript) {
    $ini = Get-Content "$data\config\StreamDockConfig.ini"
    $profile = ($ini | Select-String '^sdProfilePath=(.+)$' | Select-Object -First 1).Matches[0].Groups[1].Value -replace '\\\\', '\'
    & $ProfileScript $profile
}

if ($exe) { Start-Process $exe; Write-Host "Started $exe" }
else { Write-Host 'Deck software not found - start it yourself; the keys are in the "megarohas" category.' }
