param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\..'),[switch]$PublishCache,[string]$AlgorithmSource)
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$source=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$run=Join-Path $source ('scan-run-'+[Guid]::NewGuid().ToString('N'))
$results=Join-Path $source 'Results'
$cache=Join-Path $results 'library-scan-benchmark.cache.json'
$original=Join-Path $app 'Data\RandomPlayback.cache.json'
$settings=Join-Path $app 'Data\RandomPlayback.settings.json'
$originalHash=if(Test-Path -LiteralPath $original){(Get-FileHash -LiteralPath $original).Hash}else{''}
$settingsHash=(Get-FileHash -LiteralPath $settings).Hash
New-Item -ItemType Directory -Path $run,(Join-Path $run 'Runtime'),(Join-Path $run 'Bin') | Out-Null
if(Test-Path -LiteralPath $original){Copy-Item -LiteralPath $original -Destination $cache -Force}
try{
    Get-ChildItem -LiteralPath (Join-Path $app 'Runtime') -File | Copy-Item -Destination (Join-Path $run 'Runtime')
    Copy-Item -LiteralPath (Join-Path $app 'Bin\mpv.exe'),(Join-Path $app 'Bin\DurationProbe.lua') -Destination (Join-Path $run 'Bin')
    $algorithms=if($AlgorithmSource){(Resolve-Path -LiteralPath $AlgorithmSource).Path}else{Join-Path $source 'RandomPlayback'}
    $files=@('DurationMatching.cs','ContainerDurationReader.cs','IntensityLibrary.cs','LibraryCacheSession.cs','MotionIdentity.cs','LocalMediaRelay.cs') | ForEach-Object {Join-Path $algorithms $_} | Where-Object {Test-Path -LiteralPath $_}
    & (Join-Path $source 'Build\Compile.ps1') -Source ($files+@(Join-Path $PSScriptRoot 'MeasureLibraryScan.cs')) -Output (Join-Path $run 'Runtime\ScanLibrary.dll') -ReferenceDirectory (Join-Path $run 'Runtime') -Executable
    $bytes=[IO.File]::ReadAllBytes((Join-Path $app 'JXOPlayer.exe'))
    $binding="Runtime/JXOPlayer.dll`0"
    $offset=[Text.Encoding]::Latin1.GetString($bytes).IndexOf($binding,[StringComparison]::Ordinal)
    if($offset -lt 0){throw 'Application host binding not found'}
    $new=[Text.Encoding]::UTF8.GetBytes("Runtime/ScanLibrary.dll`0")
    for($i=$binding.Length;$i -lt $new.Length;$i++){if($bytes[$offset+$i] -ne 0){throw 'Unexpected application host storage'}}
    [Array]::Clear($bytes,$offset,[Math]::Max($binding.Length,$new.Length));[Array]::Copy($new,0,$bytes,$offset,$new.Length)
    [IO.File]::WriteAllBytes((Join-Path $run 'ScanLibrary.exe'),$bytes)
    foreach($suffix in 'deps.json','runtimeconfig.json'){Copy-Item -LiteralPath (Join-Path $run "Runtime\JXOPlayer.$suffix") -Destination (Join-Path $run "Runtime\ScanLibrary.$suffix")}
    $arguments=@($app,$cache,(Join-Path $results 'library-scan-benchmark.json')) | ForEach-Object {'"'+$_+'"'}
    $process=Start-Process -FilePath (Join-Path $run 'ScanLibrary.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $results 'library-scan-progress.txt') -RedirectStandardError (Join-Path $results 'library-scan-errors.txt')
    if(!$process.WaitForExit(1800000)){$process.Kill();throw 'Library measurement timed out'}
    if($process.ExitCode -ne 0){Get-Content -LiteralPath (Join-Path $results 'library-scan-errors.txt');throw 'Library measurement failed'}
    Get-Content -LiteralPath (Join-Path $results 'library-scan-benchmark.json')
    if($PublishCache){
        $currentHash=if(Test-Path -LiteralPath $original){(Get-FileHash -LiteralPath $original).Hash}else{''}
        if($currentHash -ne $originalHash -or (Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or (Get-Process -Name 'JXOPlayer' -ErrorAction SilentlyContinue)){throw 'Settings/cache changed or application running; measured cache was not installed'}
        Copy-Item -LiteralPath $cache -Destination ($original+'.measured') -Force
        Move-Item -LiteralPath ($original+'.measured') -Destination $original -Force
        'Measured cache installed. User settings preserved.'
    }
}finally{
    if($process -and !$process.HasExited){$process.Kill();[void]$process.WaitForExit(3000)}
    $full=[IO.Path]::GetFullPath($run)
    if([IO.Path]::GetDirectoryName($full) -ne $source -or [IO.Path]::GetFileName($full) -notmatch '^scan-run-[a-f0-9]{32}$'){throw 'Unexpected measurement cleanup path'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
