param([string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference='Stop'
$source=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$parts=@((Join-Path $source 'FloatingAxisControl.cs')) + @(foreach($folder in 'RandomPlayback','MotionControl'){Get-ChildItem -LiteralPath (Join-Path $source $folder) -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName})
$text='// Generated from Source by BuildPlugin.ps1. Edit source modules, then rebuild.'+"`r`n"
$text+=($parts | ForEach-Object { [IO.File]::ReadAllText($_) }) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $app 'Plugins\FloatingAxisControl.cs'),$text)
Write-Output 'Built combined runtime plugin from the maintained source modules.'
