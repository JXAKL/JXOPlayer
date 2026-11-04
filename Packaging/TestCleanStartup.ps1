param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
$test=Join-Path $PSScriptRoot ('test-'+[guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $Payload -Destination $test -Recurse
$source=Join-Path $PSScriptRoot '..\JXOPlayer\Source'
Add-Type -Path (Join-Path $source 'Tools\dnlib.dll')
$assembly=Join-Path $test 'Runtime\JXOPlayer.dll'
$module=[dnlib.DotNet.ModuleDefMD]::Load($assembly)
try{
    $manager=$module.Types | Where-Object Name -eq 'OSRDriverConnectionManager'
    $method=$manager.Methods | Where-Object Name -eq 'Start'
    if(!$method){throw 'Cannot isolate test from hardware'}
    $method.Body=[dnlib.DotNet.Emit.CilBody]::new()
    $method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $module.Write($assembly+'.test')
}finally{$module.Dispose()}
Move-Item -LiteralPath ($assembly+'.test') -Destination $assembly -Force
& (Join-Path $source 'Build\Compile.ps1') -Source (Join-Path $PSScriptRoot 'CleanStartup.cs') -Output (Join-Path $test 'Runtime\CleanStartup.dll') -ReferenceDirectory (Join-Path $test 'Runtime') -Executable
$bytes=[IO.File]::ReadAllBytes((Join-Path $test 'JXOPlayer.exe'))
$binding="Runtime/JXOPlayer.dll`0"
$offset=[Text.Encoding]::Latin1.GetString($bytes).IndexOf($binding,[StringComparison]::Ordinal)
if($offset -lt 0){throw 'Missing app host binding'}
$new=[Text.Encoding]::UTF8.GetBytes("Runtime/CleanStartup.dll`0")
for($i=$binding.Length;$i -lt $new.Length;$i++){if($bytes[$offset+$i] -ne 0){throw 'Unexpected apphost storage'}}
[Array]::Clear($bytes,$offset,$new.Length);[Array]::Copy($new,0,$bytes,$offset,$new.Length)
[IO.File]::WriteAllBytes((Join-Path $test 'CleanStartup.exe'),$bytes)
foreach($suffix in 'deps.json','runtimeconfig.json'){Copy-Item -LiteralPath (Join-Path $test "Runtime\JXOPlayer.$suffix") -Destination (Join-Path $test "Runtime\CleanStartup.$suffix")}
$p=Start-Process -FilePath (Join-Path $test 'CleanStartup.exe') -WorkingDirectory $test -WindowStyle Hidden -PassThru
if(!$p.WaitForExit(40000)){$p.Kill();throw 'Clean startup test timeout'}
Get-Content -LiteralPath (Join-Path $test 'clean-startup.txt')
Copy-Item -LiteralPath (Join-Path $test 'clean-startup.txt') -Destination (Join-Path $PSScriptRoot 'clean-startup-results.txt')
$test | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'last-test.txt')
if($p.ExitCode -ne 0){throw "Clean startup failed: $($p.ExitCode)"}
