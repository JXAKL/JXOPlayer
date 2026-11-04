param([string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\..'),[switch]$RandomOnly,[switch]$PotPlayerOnly,[switch]$ReportedMediaOnly)
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$source=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$run=Join-Path $source ('test-run-'+[Guid]::NewGuid().ToString('N'))
$results=Join-Path $source 'Results'
New-Item -ItemType Directory -Path $run,(Join-Path $run 'Plugins'),(Join-Path $run 'Runtime'),(Join-Path $run 'Data') | Out-Null
New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $run 'Bin'),(Join-Path $run 'Fixtures') | Out-Null
Copy-Item -LiteralPath (Join-Path $app 'Bin\mpv.exe') -Destination (Join-Path $run 'Bin')
Copy-Item -LiteralPath (Join-Path $app 'Bin\DurationProbe.lua') -Destination (Join-Path $run 'Bin')
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Fixtures') -Filter 'duration-12s.*' | Copy-Item -Destination (Join-Path $run 'Fixtures')
function Disable-AutoConnections($value) {
    if($value -is [pscustomobject]) {
        foreach($p in $value.PSObject.Properties) {
            if($p.Name -in 'AutoConnectEnabled','AutoSearchOnStartup'){$p.Value=$false}
            else {Disable-AutoConnections $p.Value}
        }
    } elseif($value -is [array]){foreach($v in $value){Disable-AutoConnections $v}}
}
function Build-TestHost([string]$name,[string[]]$files) {
    $runtime=Join-Path $run 'Runtime'
    & (Join-Path $source 'Build\Compile.ps1') -Source $files -Output (Join-Path $runtime "$name.dll") -ReferenceDirectory $runtime -Executable
    $bytes=[IO.File]::ReadAllBytes((Join-Path $app 'JXOPlayer.exe'))
    $binding="Runtime/JXOPlayer.dll`0"
    $offset=[Text.Encoding]::Latin1.GetString($bytes).IndexOf($binding,[StringComparison]::Ordinal)
    if($offset -lt 0){throw 'Application host binding not found'}
    $new=[Text.Encoding]::UTF8.GetBytes("Runtime/$name.dll`0")
    for($i=$binding.Length;$i -lt $new.Length;$i++){if($bytes[$offset+$i] -ne 0){throw 'Unexpected apphost storage'}}
    [Array]::Clear($bytes,$offset,[Math]::Max($binding.Length,$new.Length))
    [Array]::Copy($new,0,$bytes,$offset,$new.Length)
    [IO.File]::WriteAllBytes((Join-Path $run "$name.exe"),$bytes)
    foreach($suffix in 'deps.json','runtimeconfig.json'){Copy-Item -LiteralPath (Join-Path $runtime "JXOPlayer.$suffix") -Destination (Join-Path $runtime "$name.$suffix")}
}
function Run-Test([string]$name,[string]$argument='',[int]$timeoutMs=25000) {
    $params=@{FilePath=(Join-Path $run "$name.exe");WindowStyle='Hidden';PassThru=$true;RedirectStandardOutput=(Join-Path $results "$name-output.txt");RedirectStandardError=(Join-Path $results "$name-error.txt")}
    if($argument){$params.ArgumentList=$argument}
    $process=Start-Process @params
    if(!$process.WaitForExit($timeoutMs)){ $process.Kill(); throw "Isolated $name test timed out" }
    if($process.ExitCode -ne 0){Get-Content -LiteralPath (Join-Path $results "$name-error.txt");throw "$name test failed ($($process.ExitCode))"}
}
try {
    Get-ChildItem -LiteralPath (Join-Path $app 'Runtime') -File | Where-Object { $_.Name -notmatch 'StartupRegression|TestFloatingAxis|startup-\d|test-results' } | Copy-Item -Destination (Join-Path $run 'Runtime')
    # Original device manager can scan on TargetsUpdated even with AutoSearchOnStartup=false.
    # Disable its worker ONLY in this disposable test copy, before any executable starts.
    Add-Type -Path (Join-Path $source 'Tools\dnlib.dll')
    $testAssembly=Join-Path $run 'Runtime\JXOPlayer.dll'
    $module=[dnlib.DotNet.ModuleDefMD]::Load($testAssembly)
    try{
        $manager=$module.Types | Where-Object Name -eq 'OSRDriverConnectionManager'
        $start=$manager.Methods | Where-Object Name -eq 'Start'
        if(!$start){throw 'Test device isolation entry point not found'}
        $start.Body=[dnlib.DotNet.Emit.CilBody]::new();$start.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
        if($PotPlayerOnly){
            # A distinct player name keeps the production application's native
            # (?i)(?>potplayer) discovery from ever attaching to this test player.
            $isolatedMatchers=0
            foreach($type in $module.GetTypes()){
                if($type.FullName -notlike '*MediaSource.ViewModels.PotPlayerMediaSource*'){continue}
                foreach($method in $type.Methods){if($method.HasBody){foreach($instruction in $method.Body.Instructions){
                    if($instruction.OpCode.Code -eq 'Ldstr' -and $instruction.Operand -eq '(?i)(?>potplayer)'){
                        $instruction.Operand='(?i)^JxoPlaybackProbe$';$isolatedMatchers++
                    }
                }}}
            }
            if($isolatedMatchers -ne 2){throw 'Unable to isolate both native player discovery paths'}
        }
        $module.Write($testAssembly+'.isolated')
    }finally{$module.Dispose()}
    Move-Item -LiteralPath ($testAssembly+'.isolated') -Destination $testAssembly -Force
    Copy-Item -LiteralPath (Join-Path $app 'Plugins\FloatingAxisControl.cs') -Destination (Join-Path $run 'Plugins')
    if($PotPlayerOnly){
        $isolatedPlugin=Join-Path $run 'Plugins\FloatingAxisControl.cs'
        $pluginText=[IO.File]::ReadAllText($isolatedPlugin)
        $productionMatcher='process.ProcessName.IndexOf("potplayer",StringComparison.OrdinalIgnoreCase)<0'
        if(!$pluginText.Contains($productionMatcher)){throw 'Automatic player discovery isolation point not found'}
        $pluginText=$pluginText.Replace($productionMatcher,'!string.Equals(process.ProcessName,"JxoPlaybackProbe",StringComparison.OrdinalIgnoreCase)')
        [IO.File]::WriteAllText($isolatedPlugin,$pluginText)
    }
    Copy-Item -LiteralPath (Join-Path $app 'JXOPlayer.exe') -Destination $run
    $config=Get-Content -LiteralPath (Join-Path $app 'JXOPlayer.config.json') -Raw | ConvertFrom-Json
    Disable-AutoConnections $config
    if($PotPlayerOnly){$config.LogLevel='Debug'}
    $config.Script.Repositories.'本地'.ScriptLibraries=@()
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $run 'JXOPlayer.config.json') -Encoding utf8NoBOM
    '{"Folders":[],"Relative":true}' | Set-Content -LiteralPath (Join-Path $run 'Data\RandomPlayback.settings.json') -Encoding utf8NoBOM
    if($ReportedMediaOnly){
        Build-TestHost 'CheckReportedMedia' @((Join-Path $run 'Plugins\FloatingAxisControl.cs'),(Join-Path $PSScriptRoot 'CheckReportedMedia.cs'))
        Run-Test 'CheckReportedMedia' ('"'+$app+'"') 45000
        Get-Content -LiteralPath (Join-Path $results 'CheckReportedMedia-output.txt')
        return
    }
    if($PotPlayerOnly){
        if(Get-Process | Where-Object ProcessName -Match '(?i)potplayer|^JxoPlaybackProbe$'){throw 'Close existing PotPlayer instances before running the isolated real-player test to avoid its single-instance forwarding.'}
        $original=(Resolve-Path (Join-Path $source '..')).Path
        $playerCopy=Join-Path $run 'PotPlayer';New-Item -ItemType Directory -Path $playerCopy,(Join-Path $run 'videos') | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $original 'PotPlayer') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -eq 'PotPlayerMini64.exe' } | Copy-Item -Destination $playerCopy
        Rename-Item -LiteralPath (Join-Path $playerCopy 'PotPlayerMini64.exe') -NewName 'JxoPlaybackProbe.exe'
        foreach($folder in 'Module','Skins','Language'){Copy-Item -LiteralPath (Join-Path $original "PotPlayer\$folder") -Destination $playerCopy -Recurse}
        "[Settings]`r`nAutoDownloadFile=0`r`nAutoUpdateStart=0`r`nCheckAutoUpdate=0`r`nPlaybackMode=1" | Set-Content -LiteralPath (Join-Path $playerCopy 'PotPlayerMini64.ini')
        Copy-Item -LiteralPath (Join-Path $playerCopy 'PotPlayerMini64.ini') -Destination (Join-Path $playerCopy 'JxoPlaybackProbe.ini')
        $fixture=Join-Path $run 'videos\强度 1.mp4'
        $ffmpeg=Join-Path $original 'OFS-DKSJ-3.2.0.3\OFS-DKSJ\ffmpeg.exe'
        if(!(Test-Path -LiteralPath $ffmpeg)){
            $ffmpeg=$env:FFMPEG_PATH
            if(!$ffmpeg){$ffmpeg=(Get-Command ffmpeg -ErrorAction SilentlyContinue).Source}
            if(!$ffmpeg -or !(Test-Path -LiteralPath $ffmpeg)){throw 'Set FFMPEG_PATH or add ffmpeg to PATH for the real-player test'}
        }
        & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=blue:s=160x90:r=10' -t 60 -c:v mpeg4 -y $fixture
        if($LASTEXITCODE -ne 0){throw 'Unable to generate synthetic video'}
        foreach($tier in 1,2,3,4,5){
            if($tier -ne 1){Copy-Item -LiteralPath $fixture -Destination (Join-Path $run "videos\强度 $tier.mp4")}
            $step=100000.0/($tier*50)
            $actions=for($i=0;$i -le [Math]::Floor(60000/$step);$i++){@{at=$i*$step;pos=($i%2)*100}}
            @{metadata=@{duration=60};actions=@($actions)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run "videos\强度 $tier.funscript") -Encoding utf8NoBOM
        }
        $correctFolder=Join-Path $run 'videos\correct-cut';New-Item -ItemType Directory -Path $correctFolder | Out-Null
        Move-Item -LiteralPath (Join-Path $run 'videos\强度 1.funscript') -Destination $correctFolder
        $wrongActions=for($i=0;$i -le 120;$i++){@{at=$i*1000;pos=($i%2)*100}}
        @{metadata=@{duration=120};actions=@($wrongActions)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'videos\强度 1.funscript') -Encoding utf8NoBOM
        '{"AutoStart":true,"Opacity":0.95,"Step":5}' | Set-Content -LiteralPath (Join-Path $run 'Plugins\FloatingAxisControl.config.json')
        # The native source matches process names containing "PotPlayer". Keep the
        # harness name distinct so a reconnect cannot attach to the test host itself.
        Build-TestHost 'PlaybackIntegration' @((Join-Path $PSScriptRoot 'TestPotPlayerPlayback.cs'))
        $testPlayer=Start-Process -FilePath (Join-Path $playerCopy 'JxoPlaybackProbe.exe') -WorkingDirectory $playerCopy -ArgumentList ('"'+$fixture+'"') -WindowStyle Hidden -PassThru
        try{Run-Test 'PlaybackIntegration' '' 120000}finally{
            $testPlayer.Refresh();if(!$testPlayer.HasExited){[void]$testPlayer.CloseMainWindow();if(!$testPlayer.WaitForExit(5000)){$testPlayer.Kill()}}
            if(Test-Path -LiteralPath (Join-Path $run 'potplayer-results.txt')){Copy-Item -LiteralPath (Join-Path $run 'potplayer-results.txt') -Destination $results;Get-Content -LiteralPath (Join-Path $results 'potplayer-results.txt')}
            if(Test-Path -LiteralPath (Join-Path $run 'Runtime\Logs\application.log')){Copy-Item -LiteralPath (Join-Path $run 'Runtime\Logs\application.log') -Destination (Join-Path $results 'PotPlayer-application.log')}
            if(Test-Path -LiteralPath (Join-Path $run 'Logs\application.log')){Copy-Item -LiteralPath (Join-Path $run 'Logs\application.log') -Destination (Join-Path $results 'PotPlayer-application.log')}
        }
        return
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'intensity-golden.json') -Destination $run
    Build-TestHost 'TestRandomPlayback' @((Join-Path $run 'Plugins\FloatingAxisControl.cs'),(Join-Path $PSScriptRoot 'TestRandomPlayback.cs'),(Join-Path $PSScriptRoot 'BridgeRegression.cs'),(Join-Path $PSScriptRoot 'TestDurationMatching.cs'),(Join-Path $PSScriptRoot 'TestScanPerformance.cs'),(Join-Path $PSScriptRoot 'TestAutoAdvance.cs'),(Join-Path $PSScriptRoot 'TestLocalMediaRelay.cs'))
    Run-Test 'TestRandomPlayback' '' 45000
    Copy-Item -LiteralPath (Join-Path $run 'random-results.txt') -Destination $results
    Copy-Item -LiteralPath (Join-Path $run 'scan-performance.txt') -Destination $results
    Get-Content -LiteralPath (Join-Path $results 'random-results.txt')
    if($RandomOnly){return}
    Build-TestHost 'TestFloatingAxis' @((Join-Path $run 'Plugins\FloatingAxisControl.cs'),(Join-Path $PSScriptRoot 'TestFloatingAxis.cs'),(Join-Path $PSScriptRoot 'TestStimulus.cs'),(Join-Path $PSScriptRoot 'TestHome.cs'))
    Run-Test 'TestFloatingAxis' '' 45000
    Copy-Item -LiteralPath (Join-Path $run 'test-results.txt') -Destination $results
    Copy-Item -LiteralPath (Join-Path $run 'tremor-samples.csv') -Destination $results
    Get-ChildItem -LiteralPath $run -Filter 'motion-mode-*.txt' | Copy-Item -Destination $results
    Get-Content -LiteralPath (Join-Path $results 'test-results.txt')
    '{"Enabled":false,"AutoStart":true,"Opacity":0.95,"Step":5,"PauseVideo":false}' | Set-Content -LiteralPath (Join-Path $run 'Plugins\FloatingAxisControl.config.json') -Encoding utf8NoBOM
    Build-TestHost 'StartupRegression' @((Join-Path $PSScriptRoot 'StartupRegression.cs'))
    foreach($phase in 1,2,3) {
        $report=Join-Path $run "Runtime\startup-$phase.txt"
        try { Run-Test 'StartupRegression' ([string]$phase) }
        finally { if(Test-Path -LiteralPath $report){Copy-Item -LiteralPath $report -Destination $results} }
        Get-Content -LiteralPath $report -Tail 1
    }
    Copy-Item -LiteralPath (Join-Path $run '主界面-悬浮控制.png'),(Join-Path $run '悬浮窗-热力图.png') -Destination $results
    # Exercise the distributed executable directly, without a substituted managed entry point.
    $process=Start-Process -FilePath (Join-Path $run 'JXOPlayer.exe') -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $process.Refresh() } while(!$process.HasExited -and $process.MainWindowTitle -ne 'JXOPlayer 2.0' -and [DateTime]::UtcNow -lt $deadline)
    if($process.HasExited -or $process.MainWindowTitle -ne 'JXOPlayer 2.0'){if(!$process.HasExited){$process.Kill()};throw 'Direct executable startup failed'}
    [void]$process.CloseMainWindow()
    if(!$process.WaitForExit(10000)){$process.Kill();throw 'Direct executable did not close cleanly'}
    if($process.ExitCode -ne 0){throw 'Direct executable returned an error'}
    'PASS direct executable opens JXOPlayer 2.0 and exits cleanly' | Set-Content -LiteralPath (Join-Path $results 'executable-startup.txt')
    Write-Output 'PASS direct executable startup and shutdown'
} finally {
    $full=[IO.Path]::GetFullPath($run)
    if([IO.Path]::GetDirectoryName($full) -ne $source -or [IO.Path]::GetFileName($full) -notmatch '^test-run-[a-f0-9]{32}$'){throw 'Unexpected test cleanup path'}
    # A timed-out reconnect test may have created a second player. Only terminate
    # processes whose executable belongs to this disposable run directory.
    foreach($owned in Get-Process){
        try{$ownedPath=$owned.Path}catch{continue}
        if($ownedPath -and $ownedPath.StartsWith($full+'\',[StringComparison]::OrdinalIgnoreCase)){
            if(!$owned.HasExited){[void]$owned.CloseMainWindow();if(!$owned.WaitForExit(2000)){$owned.Kill();[void]$owned.WaitForExit(2000)}}
        }
    }
    # MPV child exit / antivirus may briefly retain its executable handle.
    for($cleanupAttempt=0;$cleanupAttempt -lt 5;$cleanupAttempt++){
        try{if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force};break}
        catch{if($cleanupAttempt -eq 4){throw};Start-Sleep -Milliseconds 300}
    }
}
