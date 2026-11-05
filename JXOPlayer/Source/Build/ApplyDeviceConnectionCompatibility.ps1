param([string]$ApplicationDirectory=(Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$runtime=Join-Path $app 'Runtime'
& (Join-Path $PSScriptRoot 'Compile.ps1') -Source (Join-Path $PSScriptRoot '..\Connections\ConnectionCompatibility.cs') -Output (Join-Path $runtime 'JXOPlayer.Connections.dll') -ReferenceDirectory $runtime
Add-Type -Path (Join-Path $PSScriptRoot '..\Tools\dnlib.dll')
$file=Join-Path $runtime 'JXOPlayer.dll'
$module=[dnlib.DotNet.ModuleDefMD]::Load($file)
$helper=[dnlib.DotNet.ModuleDefMD]::Load((Join-Path $runtime 'JXOPlayer.Connections.dll'))
try{
    $importer=[dnlib.DotNet.Importer]::new($module)
    $implementation=$helper.Types | Where-Object Name -eq 'ConnectionCompatibility'
    $connect=$importer.Import(($implementation.Methods|Where-Object Name -eq 'Connect'))
    $visible=$importer.Import(($implementation.Methods|Where-Object Name -eq 'SerialVisible'))
    $known=$importer.Import(($implementation.Methods|Where-Object Name -eq 'KnownSerial'))
    $bridge=$module.GetTypes()|Where-Object FullName -eq 'JXOPlayer.OutputTarget.ViewModels.BtBridgeClient'
    $scan=$bridge.Methods|Where-Object Name -eq 'ScanAsync'
    $scanEntry=$importer.Import(($implementation.Methods|Where-Object Name -eq 'ScanBridge'))
    $scanCall=[dnlib.DotNet.MethodSpecUser]::new($scanEntry,[dnlib.DotNet.GenericInstMethodSig]::new($scan.MethodSig.RetType.GenericArguments[0]))
    $scan.Body=[dnlib.DotNet.Emit.CilBody]::new()
    foreach($parameter in $scan.Parameters){$scan.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg,$parameter))}
    $scan.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$scanCall))
    $scan.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    foreach($name in 'SerialOutputTarget','BluetoothOutputTarget','UdpOutputTarget'){
        $type=$module.GetTypes()|Where-Object FullName -eq "JXOPlayer.OutputTarget.ViewModels.$name"
        if(!$type){throw "Missing native type: $name"}
        if($type.Methods|Where-Object Name -eq 'OnConnectingLegacyAsync'){continue}
        $original=$type.Methods|Where-Object Name -eq 'OnConnectingAsync'
        $wrapper=[dnlib.DotNet.MethodDefUser]::new('OnConnectingAsync',$original.MethodSig.Clone(),$original.Attributes)
        $original.Name='OnConnectingLegacyAsync';$original.Attributes=[dnlib.DotNet.MethodAttributes]'Private, HideBySig'
        $wrapper.Body=[dnlib.DotNet.Emit.CilBody]::new();$type.Methods.Add($wrapper)
        $wrapper.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0))
        $wrapper.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1))
        $wrapper.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$connect))
        $wrapper.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    }
    $serial=$module.GetTypes()|Where-Object FullName -eq 'JXOPlayer.OutputTarget.ViewModels.SerialOutputTarget'
    $predicate=$serial.Methods|Where-Object Name -eq 'IsCh340Port'
    $predicate.Body=[dnlib.DotNet.Emit.CilBody]::new()
    foreach($instruction in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$known),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$predicate.Body.Instructions.Add($instruction)}
    $show=$serial.Methods|Where-Object Name -eq 'IsVisibleSerialPort'
    if(!$show){
        $show=[dnlib.DotNet.MethodDefUser]::new('IsVisibleSerialPort',$predicate.MethodSig.Clone(),[dnlib.DotNet.MethodAttributes]'Private, Static, HideBySig');$serial.Methods.Add($show)
        $show.Body=[dnlib.DotNet.Emit.CilBody]::new()
        foreach($instruction in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$visible),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$show.Body.Instructions.Add($instruction)}
    }
    # Broaden the UI list and the final port-open check only. The legacy automatic
    # handshake still considers known devices and never scans arbitrary COM ports.
    foreach($type in @($serial)+@($serial.GetTypes())){
        foreach($method in $type.Methods){if(!$method.HasBody){continue}
            if($method.Name -notlike '*DoRefreshPorts*' -and $type.Name -notlike '*DoRefreshPorts*' -and !($type -eq $serial -and $method.Name -eq 'Run')){continue}
            foreach($i in $method.Body.Instructions){if($i.Operand -eq $predicate){$i.Operand=$show}}
        }
    }
    $bluetooth=$module.GetTypes()|Where-Object FullName -eq 'JXOPlayer.OutputTarget.ViewModels.BluetoothOutputTarget'
    $filter=$bluetooth.Methods|Where-Object Name -eq 'FilterPreferredPorts'
    $filter.Body=[dnlib.DotNet.Emit.CilBody]::new();$filter.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0));$filter.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $capable=$bluetooth.Methods|Where-Object Name -eq 'IsBluetoothConnectableDevice'
    $preferred=$bluetooth.Methods|Where-Object Name -eq 'MatchesPreferredBluetoothName'
    $automatic=$bluetooth.Methods|Where-Object Name -eq 'IsAutomaticBluetoothCandidate'
    if(!$automatic){
        $automatic=[dnlib.DotNet.MethodDefUser]::new('IsAutomaticBluetoothCandidate',$capable.MethodSig.Clone(),[dnlib.DotNet.MethodAttributes]'Private, Static, HideBySig');$bluetooth.Methods.Add($automatic);$automatic.Body=[dnlib.DotNet.Emit.CilBody]::new()
        foreach($instruction in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$capable),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$preferred),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::And),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$automatic.Body.Instructions.Add($instruction)}
    }
    foreach($type in @($bluetooth)+@($bluetooth.GetTypes())){
        foreach($method in $type.Methods){if(!$method.HasBody){continue}
            if($type.Name -notlike '*OnConnectingAsync*' -and $method.Name -notin 'SelectPreferredBluetoothPortForQuickConnect','SelectPreferredBluetoothPortLegacy'){continue}
            foreach($i in $method.Body.Instructions){if($i.Operand -eq $capable){$i.Operand=$automatic}}
        }
    }
    if(!($bluetooth.Methods|Where-Object Name -eq 'SelectPreferredBluetoothPortLegacy')){
        $original=$bluetooth.Methods|Where-Object Name -eq 'SelectPreferredBluetoothPortForQuickConnect'
        $wrapper=[dnlib.DotNet.MethodDefUser]::new('SelectPreferredBluetoothPortForQuickConnect',$original.MethodSig.Clone(),$original.Attributes)
        # Existing callers must reference the wrapper, including dialog state machines.
        foreach($type in $module.GetTypes()){foreach($method in $type.Methods){if($method.HasBody){foreach($i in $method.Body.Instructions){if($i.Operand -eq $original){$i.Operand=$wrapper}}}}}
        $original.Name='SelectPreferredBluetoothPortLegacy'
        $bluetooth.Methods.Add($wrapper);$wrapper.Body=[dnlib.DotNet.Emit.CilBody]::new()
        $pick=$importer.Import(($implementation.Methods|Where-Object Name -eq 'BluetoothQuickPick'))
        foreach($i in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$pick),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Castclass,$original.MethodSig.RetType.TypeDefOrRef),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$wrapper.Body.Instructions.Add($i)}
    }
    $dialog=$module.GetTypes()|Where-Object FullName -eq 'JXOPlayer.UI.Dialogs.ViewModels.OutputTargetConnectionDialog'
    if(!($dialog.Methods|Where-Object Name -eq 'SelectBluetoothLegacyAsync')){
        $original=$dialog.Methods|Where-Object Name -eq 'SelectBluetoothAsync'
        $wrapper=[dnlib.DotNet.MethodDefUser]::new('SelectBluetoothAsync',$original.MethodSig.Clone(),$original.Attributes)
        foreach($type in $module.GetTypes()){foreach($method in $type.Methods){if($method.HasBody){foreach($i in $method.Body.Instructions){if($i.Operand -eq $original){$i.Operand=$wrapper}}}}}
        $original.Name='SelectBluetoothLegacyAsync';$dialog.Methods.Add($wrapper);$wrapper.Body=[dnlib.DotNet.Emit.CilBody]::new()
        $open=$importer.Import(($implementation.Methods|Where-Object Name -eq 'BluetoothDialog'))
        foreach($i in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$open),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$wrapper.Body.Instructions.Add($i)}
    }
    foreach($method in $dialog.Methods){if($method.HasBody){foreach($i in $method.Body.Instructions){
        if($i.OpCode.Code -eq 'Ldstr' -and $i.Operand -like '直接点击蓝牙卡片会优先*'){$i.Operand='首次使用请展开列表选择自己的蓝牙设备；再次点击卡片会连接已选设备。经典蓝牙需提供 SPP，BLE 需提供兼容的串口服务。'}
    }}}
    $hint=$dialog.Methods|Where-Object Name -eq 'get_BluetoothPickerHint'
    $hint.Body=[dnlib.DotNet.Emit.CilBody]::new()
    $getHint=$importer.Import(($implementation.Methods|Where-Object Name -eq 'BluetoothHint'))
    foreach($i in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$getHint),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))){$hint.Body.Instructions.Add($i)}
    $run=$serial.Methods|Where-Object {$_.Name -eq 'Run' -and $_.HasBody}
    if(!($run.Body.Instructions|Where-Object {$_.Operand -eq $show}) -or ($run.Body.Instructions|Where-Object {$_.Operand -eq $predicate})){throw 'Serial final port-open guard was not patched'}
    $quick=$bluetooth.Methods|Where-Object Name -eq 'SelectPreferredBluetoothPortLegacy'
    if(!($quick.Body.Instructions|Where-Object {$_.Operand -eq $automatic})){throw 'Bluetooth quick selection guard was not patched'}
    $module.Write($file+'.connections')
}finally{$module.Dispose();$helper.Dispose()}
Move-Item -LiteralPath ($file+'.connections') -Destination $file -Force
$depsPath=Join-Path $runtime 'JXOPlayer.deps.json'
$deps=Get-Content -LiteralPath $depsPath -Raw|ConvertFrom-Json -AsHashtable
$target=$deps.targets[$deps.runtimeTarget.name]
$main=@($target.Keys|Where-Object {$_ -like 'JXOPlayer/*'})[0]
$target[$main].dependencies['JXOPlayer.Connections']='2.0.1'
$target['JXOPlayer.Connections/2.0.1']=@{runtime=@{'JXOPlayer.Connections.dll'=@{}}}
$deps.libraries['JXOPlayer.Connections/2.0.1']=@{type='project';serviceable=$false;sha512=''}
$deps|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $depsPath -Encoding utf8NoBOM
'Applied manual device connection compatibility and explicit failure messages.'
