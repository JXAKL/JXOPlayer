# Apply to the loaded application module before writing it. No host-runtime imports:
# all signatures use the target assembly's existing .NET references.
function Update-PotPlayerTransport($module) {
    $player=$module.GetTypes() | Where-Object Name -eq 'PotPlayerMediaSource'
    $method=$player.Methods | Where-Object Name -Like '*SetValueString*'
    if(!$method){throw 'PotPlayer filename transport method not found'}
    $code=$method.Body.Instructions
    $write=@($code | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.DeclaringType.FullName -eq 'System.Runtime.InteropServices.Marshal' -and $_.Operand.Name -eq 'WriteByte' })
    if($write.Count -eq 0 -and @($code | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'WriteInt16' }).Count -eq 1){return}
    if($write.Count -ne 1){throw 'Unexpected PotPlayer terminator writer'}
    $sizes=@()
    for($i=0;$i -lt $code.Count-2;$i++){
        if($code[$i].OpCode.Code -eq 'Ldc_I4_1' -and $code[$i+1].OpCode.Code -eq 'Add' -and
           (($code[$i+2].Operand -is [dnlib.DotNet.IMethod] -and $code[$i+2].Operand.Name -eq 'AllocCoTaskMem') -or
            ($code[$i+2].Operand -is [dnlib.DotNet.IField] -and $code[$i+2].Operand.Name -eq 'cbData'))){$sizes+=,$code[$i]}
    }
    if($sizes.Count -ne 2){throw 'Unexpected PotPlayer filename buffer sizes'}
    foreach($size in $sizes){$size.OpCode=[dnlib.DotNet.Emit.OpCodes]::Ldc_I4_2}
    $signature=[dnlib.DotNet.MethodSig]::CreateStatic($module.CorLibTypes.Void,$module.CorLibTypes.IntPtr,$module.CorLibTypes.Int32,$module.CorLibTypes.Int16)
    $write[0].Operand=[dnlib.DotNet.MemberRefUser]::new($module,'WriteInt16',$signature,$write[0].Operand.DeclaringType)
}
