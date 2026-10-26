# Builds MegarohasDeck.exe into the plugin folder with the C# compiler that ships with .NET Framework 4.
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName
$out = Join-Path $PSScriptRoot 'com.megarohas.deck.sdPlugin\MegarohasDeck.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /reference:System.Web.Extensions.dll /reference:System.Drawing.dll "/out:$out" $src
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "Built $out"
