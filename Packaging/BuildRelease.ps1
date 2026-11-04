param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\JXOPlayer'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'BuildCleanPackage.ps1') -ApplicationDirectory $ApplicationDirectory
$stage=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'current-stage.txt') -Raw).Trim()
$compiler=Join-Path $PSScriptRoot 'Tools\InnoSetup\ISCC.exe'
& $compiler ('/DPayload='+ (Join-Path $stage 'JXOPlayer')) (Join-Path $PSScriptRoot 'JXOPlayer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$release=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Releases'))
# Build a new archive instead of updating an existing ZIP that could contain stale files.
$archive=Join-Path $stage 'JXOPlayer-2.0-Portable.zip'
& 'C:\Program Files\7-Zip\7z.exe' a -tzip -mx=5 $archive (Join-Path $stage 'JXOPlayer')
if($LASTEXITCODE -ne 0){throw 'ZIP creation failed'}
Move-Item -LiteralPath $archive -Destination (Join-Path $release 'JXOPlayer-2.0-Portable.zip') -Force
Copy-Item -LiteralPath (Join-Path $stage 'payload-manifest.json') -Destination (Join-Path $PSScriptRoot 'release-manifest.json') -Force
Get-ChildItem -LiteralPath $release -File | Where-Object {$_.Name -in 'JXOPlayer-2.0-Setup.exe','JXOPlayer-2.0-Portable.zip'} | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$_.Name
} | Set-Content -LiteralPath (Join-Path $release 'SHA256.txt') -Encoding utf8NoBOM
