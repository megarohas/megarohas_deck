# Copies the plugin into fifine Control Deck / StreamDock and restarts the deck software so it loads it.
# -ProfileScript <script>: run a profile-editing script while the software is closed (gets the profile folder as argument).
param([string]$ProfileScript)
$ErrorActionPreference = 'Stop'
$name = 'com.megarohas.deck.sdPlugin'
$data = "$env:APPDATA\HotSpot\StreamDock"
$app = Get-Process 'fifine Control Deck' -ErrorAction SilentlyContinue | Select-Object -First 1
$exe = if ($app) { $app.Path } else { "${env:ProgramFiles(x86)}\fifine Control Deck\fifine Control Deck.exe" }

if ($app) { Stop-Process -Id $app.Id; $app.WaitForExit(10000) | Out-Null }
Get-Process MegarohasDeck -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

$target = Join-Path "$data\plugins" $name
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item (Join-Path $PSScriptRoot $name) $target -Recurse
Write-Host "Installed $target"

if ($ProfileScript) {
    $ini = Get-Content "$data\config\StreamDockConfig.ini"
    $profile = ($ini | Select-String '^sdProfilePath=(.+)$' | Select-Object -First 1).Matches[0].Groups[1].Value -replace '\\\\', '\'
    & $ProfileScript $profile
}

Start-Process $exe
Write-Host "Started $exe"
