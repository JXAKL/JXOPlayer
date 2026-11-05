param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$runtime=Join-Path $app 'Runtime'
$sdk=Join-Path $PSScriptRoot '..\Tools\WindowsSDK'
foreach($name in 'Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll'){
    if(!(Test-Path -LiteralPath (Join-Path $sdk $name))){throw 'Missing Windows SDK dependencies; run PrepareWindowsBluetooth.ps1 first'}
    Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination (Join-Path $runtime $name) -Force
}
& (Join-Path $PSScriptRoot 'Compile.ps1') -Source (Join-Path $PSScriptRoot '..\Connections\WindowsBluetoothBackend.cs') -Output (Join-Path $runtime 'JXOPlayer.Bluetooth.dll') -ReferenceDirectory $runtime
Add-Type -Path (Join-Path $PSScriptRoot '..\Tools\dnlib.dll')
$helper=[dnlib.DotNet.ModuleDefMD]::Load((Join-Path $runtime 'JXOPlayer.Bluetooth.dll'))
try{
    $implementation=$helper.Types|Where-Object Name -eq 'WindowsBluetoothBackend'
    foreach($name in 'JXOPlayer','BtBridge'){
        $path=Join-Path $runtime ($name+'.dll');$module=[dnlib.DotNet.ModuleDefMD]::Load($path)
        try{
            $importer=[dnlib.DotNet.Importer]::new($module);$count=0
            foreach($type in $module.GetTypes()){
                if($type.FullName -notin 'NativeBluetoothSerialPort','BtBridge.Native'){continue}
                foreach($method in $type.Methods){
                    $existing=if($method.HasBody){@($method.Body.Instructions|Where-Object {$_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.DeclaringType.FullName -eq 'JXOPlayer.Compatibility.WindowsBluetoothBackend'})}else{@()}
                    $entry=if($method.HasImplMap -and $method.ImplMap.Module.Name -eq 'BLEDriverCore.dll'){[string]$method.ImplMap.Name}elseif($existing.Count -eq 1){[string]$existing[0].Operand.Name}else{continue}
                    $target=$implementation.Methods|Where-Object {$_.Name -eq $entry -and $_.IsPublic -and $_.IsStatic}
                    if(!$target){continue}
                    if($method.MethodSig.Params.Count -ne $target.MethodSig.Params.Count){throw "Bluetooth ABI parameter mismatch: $($method.Name)"}
                    if($method.MethodSig.RetType.FullName -ne $target.MethodSig.RetType.FullName){throw "Bluetooth ABI return type mismatch: $($method.Name)"}
                    $method.ImplMap=$null;$method.IsPinvokeImpl=$false;$method.ImplAttributes=[dnlib.DotNet.MethodImplAttributes]'IL, Managed'
                    $method.Body=[dnlib.DotNet.Emit.CilBody]::new()
                    foreach($parameter in $method.Parameters){$method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg,$parameter))}
                    $method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$importer.Import($target)))
                    $method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret));$count++
                }
            }
            if($count -lt 25){throw "Incomplete Bluetooth ABI replacement in $name ($count methods)"}
            if($name -eq 'BtBridge'){
                $decorate=$importer.Import(($implementation.Methods|Where-Object Name -eq 'DecorateResponse'))
                foreach($type in $module.GetTypes()){foreach($method in $type.Methods){if(!$method.HasBody){continue}
                    if($method.Body.Instructions|Where-Object {$_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'DecorateResponse'}){continue}
                    for($index=$method.Body.Instructions.Count-1;$index -ge 0;$index--){
                        $i=$method.Body.Instructions[$index]
                        if($i.Operand -is [dnlib.DotNet.IMethod] -and $i.Operand.FullName -like '*BtBridge.JsonProtocol::Serialize*'){
                            $method.Body.Instructions.Insert($index,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$decorate))
                            $method.Body.Instructions.Insert($index,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Dup))
                        }
                    }
                }}
            }
            if($module.GetTypes().Methods|Where-Object {$_.HasImplMap -and $_.ImplMap.Module.Name -eq 'BLEDriverCore.dll'}){throw 'Old Bluetooth native dependency remains'}
            $module.Write($path+'.bluetooth');"Patched $name Bluetooth ABI: $count methods"
        }finally{$module.Dispose()}
        Move-Item -LiteralPath ($path+'.bluetooth') -Destination $path -Force
        $depsPath=Join-Path $runtime ($name+'.deps.json');$deps=Get-Content -LiteralPath $depsPath -Raw|ConvertFrom-Json -AsHashtable
        $target=$deps.targets[$deps.runtimeTarget.name];$main=@($target.Keys|Where-Object {$_ -like ($name+'/*')})[0]
        foreach($dependency in 'JXOPlayer.Bluetooth','Microsoft.Windows.SDK.NET','WinRT.Runtime'){
            $version=if($dependency -eq 'JXOPlayer.Bluetooth'){'2.0.2'}else{'10.0.19041.57'}
            $target[$main].dependencies[$dependency]=$version
            $target[$dependency+'/'+$version]=@{runtime=@{($dependency+'.dll')=@{}}}
            $deps.libraries[$dependency+'/'+$version]=@{type='project';serviceable=$false;sha512=''}
        }
        $deps|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $depsPath -Encoding utf8NoBOM
    }
}finally{$helper.Dispose()}
