param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\JXOPlayer'))
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$stage=Join-Path $PSScriptRoot ('stage-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $stage 'JXOPlayer'
New-Item -ItemType Directory -Path $payload | Out-Null
foreach($folder in 'Runtime','Plugins','Bin','Data','PotPlayer','PotPlayer\Module','PotPlayer\Skins','PotPlayer\Language'){
    New-Item -ItemType Directory -Path (Join-Path $payload $folder) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $app 'JXOPlayer.exe') -Destination $payload
# Allow only runtime files, never logs, debug symbols, generated test hosts or settings.
Get-ChildItem -LiteralPath (Join-Path $app 'Runtime') -File | Where-Object {
    ($_.Extension -in '.dll','.exe','.ico' -or $_.Name -in 'JXOPlayer.deps.json','JXOPlayer.runtimeconfig.json','BtBridge.deps.json','BtBridge.runtimeconfig.json') -and
    $_.Name -notmatch '^(Test|StartupRegression|PlaybackIntegration|CheckReportedMedia)'
} | Copy-Item -Destination (Join-Path $payload 'Runtime')
foreach($file in 'FloatingAxisControl.cs','jiaxin.jpg'){Copy-Item -LiteralPath (Join-Path $app "Plugins\$file") -Destination (Join-Path $payload 'Plugins')}
foreach($file in 'mpv.exe','DurationProbe.lua'){Copy-Item -LiteralPath (Join-Path $app "Bin\$file") -Destination (Join-Path $payload 'Bin')}
Get-ChildItem -LiteralPath (Join-Path $app 'PotPlayer') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in 'PotPlayerMini64.exe','License.txt','LGPL.TXT' } | Copy-Item -Destination (Join-Path $payload 'PotPlayer')
foreach($folder in 'Module','Skins'){
    Get-ChildItem -LiteralPath (Join-Path $app "PotPlayer\$folder") -File | Where-Object {$_.Extension -in '.dll','.exe','.bin','.dsf'} | Copy-Item -Destination (Join-Path $payload "PotPlayer\$folder")
}
foreach($name in 'Chinese(Simplified).ini','Chinese(Traditional).ini','English.ini','Japanese.ini'){
    Copy-Item -LiteralPath (Join-Path $app "PotPlayer\Language\$name") -Destination (Join-Path $payload 'PotPlayer\Language')
}
# These defaults are authored from scratch; no personal configuration is read.
@'
{
  "SelectedDevice": "TCode-0.3",
  "OutputTarget": {
    "AutoSearchOnStartup": false,
    "Items": [
      {"$index":0,"$type":"JXOPlayer.OutputTarget.ViewModels.SerialOutputTarget, JXOPlayer","AutoConnectEnabled":false},
      {"$index":0,"$type":"JXOPlayer.OutputTarget.ViewModels.BluetoothOutputTarget, JXOPlayer","AutoConnectEnabled":false},
      {"$index":0,"$type":"JXOPlayer.OutputTarget.ViewModels.UdpOutputTarget, JXOPlayer","AutoConnectEnabled":false}
    ]
  },
  "Script": {"Repositories":{"本地":{"ScriptLibraries":[]}}},
  "MediaSource": {
    "Items":["PotPlayer","MPV","Intiface"],
    "ActiveItem":"PotPlayer",
    "PotPlayer":{"AutoConnectEnabled":false,"AutoStartEnabled":true},
    "MPV":{"AutoConnectEnabled":false,"AutoStartEnabled":false,"Executable":"Bin\\mpv.exe"}
  }
}
'@ | Set-Content -LiteralPath (Join-Path $payload 'JXOPlayer.config.json') -Encoding utf8NoBOM
'{"Folders":[],"Relative":true,"AutoAdvance":false}' | Set-Content -LiteralPath (Join-Path $payload 'Data\RandomPlayback.settings.json') -Encoding utf8NoBOM
'{"AutoStart":true,"Opacity":0.9,"Step":5,"Collapsed":false,"PauseVideo":false}' | Set-Content -LiteralPath (Join-Path $payload 'Plugins\FloatingAxisControl.config.json') -Encoding utf8NoBOM
"[Settings]`r`nAutoDownloadFile=0`r`nAutoUpdateStart=0`r`nCheckAutoUpdate=0`r`nPlaybackMode=1" | Set-Content -LiteralPath (Join-Path $payload 'PotPlayer\PotPlayerMini64.ini') -Encoding utf8NoBOM
'JXOPlayer 2.0 clean distribution; build 2026-11-01' | Set-Content -LiteralPath (Join-Path $payload '.clean-distribution') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '快速开始.txt') -Destination $payload
$manifest=@(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($payload,$_.FullName);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'payload-manifest.json') -Encoding utf8NoBOM
$stage | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'current-stage.txt') -Encoding utf8NoBOM
Write-Output "Clean payload: $payload"
Write-Output "Files: $($manifest.Count); size: $([math]::Round(($manifest | Measure-Object Bytes -Sum).Sum/1MB,1)) MiB"
