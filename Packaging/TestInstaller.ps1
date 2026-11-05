param([string]$Version='2.0.2')
$ErrorActionPreference='Stop'
$stage=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'current-stage.txt') -Raw).Trim()
$manifest=Get-Content -LiteralPath (Join-Path $stage 'payload-manifest.json') -Raw | ConvertFrom-Json
$test=Join-Path $PSScriptRoot ('install-test-'+[guid]::NewGuid().ToString('N'))
$setup=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\Releases\JXOPlayer-$Version-Setup.exe")).Path
$report=Join-Path $PSScriptRoot 'installer-results.txt'
'' | Set-Content -LiteralPath $report
function Pass([string]$message){$message="PASS $message"; $message | Add-Content -LiteralPath $report; Write-Output $message}
function Install([string]$phase){
    $params='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS="" /DIR="'+$test+'" /LOG="'+(Join-Path $PSScriptRoot "install-$phase.log")+'"'
    $p=Start-Process -FilePath $setup -ArgumentList $params -WindowStyle Hidden -PassThru
    if(!$p.WaitForExit(60000)){$p.Kill();throw 'Installer timed out'}
    if($p.ExitCode -ne 0){throw "Installer failed: $($p.ExitCode)"}
}
Install 'first'
foreach($file in $manifest){
    $installed=Join-Path $test $file.Path
    if(!(Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $file.SHA256){throw "Installed payload mismatch: $($file.Path)"}
}
Pass "Clean installation: all $($manifest.Count) payload files match manifest"
if(Get-ChildItem -LiteralPath $test -Recurse -File | Where-Object {$_.Name -match '\.log$|\.cache\.|MediaInfo\.sdb|\.pdb$|test-results' -or $_.DirectoryName -match '\\(History|Playlist|Source|Logs)($|\\)'}){throw 'Unexpected personal/development data in installation'}
Pass 'No library cache, logs, history, test artifacts or developer directory'
$configFiles=@('JXOPlayer.config.json','Data\RandomPlayback.settings.json','Plugins\FloatingAxisControl.config.json','PotPlayer\PotPlayerMini64.ini')
$hashes=@{}
foreach($relative in $configFiles){
    $path=Join-Path $test $relative
    if($relative.EndsWith('.json')){
        $json=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $json | Add-Member -NotePropertyName 'PackagingPreservationTest' -NotePropertyValue 'user-owned-value'
        $json | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    }else{'; user-owned-value' | Add-Content -LiteralPath $path}
    $hashes[$relative]=(Get-FileHash -LiteralPath $path).Hash
}
Install 'upgrade'
foreach($relative in $configFiles){if((Get-FileHash -LiteralPath (Join-Path $test $relative)).Hash -ne $hashes[$relative]){throw "Upgrade overwrote configuration: $relative"}}
Pass 'Reinstallation preserves all four user settings files'
$uninstaller=Join-Path $test 'unins000.exe'
$p=Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru
if(!$p.WaitForExit(60000)){$p.Kill();throw 'Uninstall timed out'}
if($p.ExitCode -ne 0){throw "Uninstall failed: $($p.ExitCode)"}
foreach($relative in $configFiles){if((Get-FileHash -LiteralPath (Join-Path $test $relative)).Hash -ne $hashes[$relative]){throw "Uninstall removed user configuration: $relative"}}
if(Test-Path -LiteralPath (Join-Path $test 'JXOPlayer.exe')){throw 'Uninstall left application executable'}
if(Test-Path -LiteralPath (Join-Path $test 'Runtime\JXOPlayer.dll')){throw 'Uninstall left runtime assembly'}
Pass 'Uninstallation removes program and preserves user settings'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($test)) -ne $PSScriptRoot -or [IO.Path]::GetFileName($test) -notmatch '^install-test-[a-f0-9]{32}$'){throw 'Unexpected test cleanup path'}
Remove-Item -LiteralPath $test -Recurse -Force
Pass 'Isolated installation test cleaned up'
