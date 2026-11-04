param([Parameter(Mandatory)][string]$OriginalAssembly,[Parameter(Mandatory)][string]$OriginalExecutable,[string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$runtime=Join-Path $app 'Runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
& (Join-Path $PSScriptRoot 'MakeIcon.ps1') -ImagePath (Join-Path $app 'Plugins\jiaxin.jpg') -IconPath (Join-Path $runtime 'JXOPlayer.ico')
& (Join-Path $PSScriptRoot 'Rebrand.ps1') -InputAssembly $OriginalAssembly -OutputAssembly (Join-Path $runtime 'JXOPlayer.dll') -ImagePath (Join-Path $app 'Plugins\jiaxin.jpg')
$binding=[IO.Path]::GetFileName($OriginalAssembly)+[char]0
$bytes=[IO.File]::ReadAllBytes($OriginalExecutable)
$offset=[Text.Encoding]::Latin1.GetString($bytes).IndexOf($binding,[StringComparison]::Ordinal)
$new=[Text.Encoding]::UTF8.GetBytes("Runtime/JXOPlayer.dll`0")
if($offset -lt 0){throw 'Original application host binding not found'}
for($i=$binding.Length;$i -lt $new.Length;$i++){if($bytes[$offset+$i] -ne 0){throw 'Unexpected application host storage'}}
[Array]::Clear($bytes,$offset,[Math]::Max($binding.Length,$new.Length));[Array]::Copy($new,0,$bytes,$offset,$new.Length)
$exe=Join-Path $app 'JXOPlayer.exe';[IO.File]::WriteAllBytes($exe,$bytes)
Add-Type -Path (Join-Path $PSScriptRoot 'NativeResources.cs')
[NativeResources]::Apply($exe,(Join-Path $runtime 'JXOPlayer.ico'))
[NativeResources]::Apply((Join-Path $runtime 'JXOPlayer.dll'),(Join-Path $runtime 'JXOPlayer.ico'))
& (Join-Path $PSScriptRoot 'BuildPlugin.ps1') -ApplicationDirectory $app
