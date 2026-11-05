param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\JXOPlayer'),[string]$Version='2.0.2')
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'BuildCleanPackage.ps1') -ApplicationDirectory $ApplicationDirectory
$stage=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'current-stage.txt') -Raw).Trim()
$compiler=Join-Path $PSScriptRoot 'Tools\InnoSetup\ISCC.exe'
& $compiler ('/DPayload='+ (Join-Path $stage 'JXOPlayer')) ('/DReleaseVersion='+$Version) (Join-Path $PSScriptRoot 'JXOPlayer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$release=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Releases'))
# Build a new archive instead of updating an existing ZIP that could contain stale files.
$archive=Join-Path $stage "JXOPlayer-$Version-Portable.zip"
& 'C:\Program Files\7-Zip\7z.exe' a -tzip -mx=5 -mcu=on $archive (Join-Path $stage 'JXOPlayer')
if($LASTEXITCODE -ne 0){throw 'ZIP creation failed'}
Move-Item -LiteralPath $archive -Destination (Join-Path $release "JXOPlayer-$Version-Portable.zip") -Force
Copy-Item -LiteralPath (Join-Path $stage 'payload-manifest.json') -Destination (Join-Path $PSScriptRoot 'release-manifest.json') -Force
Get-ChildItem -LiteralPath $release -File | Where-Object {$_.Name -in "JXOPlayer-$Version-Setup.exe","JXOPlayer-$Version-Portable.zip"} | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$_.Name
} | Set-Content -LiteralPath (Join-Path $release "SHA256-$Version.txt") -Encoding utf8NoBOM
