# Builds the plugin and packs dist\megarohas_deck-v<version>.zip (plugin folder + install.ps1 + README + LICENSE);
# the version comes from the manifest. -Publish also creates the GitHub release v<version> with the zip (needs gh).
param([switch]$Publish, [string]$NotesFile)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\build.ps1
$version = (Get-Content com.megarohas.deck.sdPlugin\manifest.json -Raw | ConvertFrom-Json).Version
$stage = Join-Path $PSScriptRoot "dist\megarohas_deck-v$version"
$zip = "$stage.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item com.megarohas.deck.sdPlugin (Join-Path $stage 'com.megarohas.deck.sdPlugin') -Recurse
Get-ChildItem (Join-Path $stage 'com.megarohas.deck.sdPlugin') -Recurse -Include *.log, debug | Remove-Item
Copy-Item install.ps1, README.md, LICENSE $stage
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Packed $zip"

if ($Publish) {
    $args = @("v$version", $zip, '--title', "megarohas deck v$version")
    if ($NotesFile) { $args += @('--notes-file', $NotesFile) } else { $args += '--generate-notes' }
    gh release create @args
}
