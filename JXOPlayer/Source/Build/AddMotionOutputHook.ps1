# Add a nullable per-sample filter after native range mapping. The native fixed
# update scheduler, dirty filtering, interpolation timing and transport stay intact.
function Add-MotionOutputHook($module) {
    if($module.GetTypes() | Where-Object Name -eq 'MotionOutputHook'){return}
    $axis=$module.GetTypes() | Where-Object FullName -Match '\.Common\.DeviceAxis$'
    $target=$module.GetTypes() | Where-Object Name -eq 'AbstractOutputTarget'
    $getValues=$target.Methods | Where-Object Name -eq 'GetValues'
    if(!$axis -or !$getValues){throw 'Native output pipeline not found'}
    $hook=[dnlib.DotNet.TypeDefUser]::new([string]$axis.Namespace,'MotionOutputHook',$module.CorLibTypes.Object.TypeDefOrRef)
    $hook.Attributes=[dnlib.DotNet.TypeAttributes] 'Public, Abstract, Sealed, BeforeFieldInit'
    $module.Types.Add($hook)
    $funcRef=$module.GetTypeRefs() | Where-Object FullName -eq 'System.Func`3' | Select-Object -First 1
    if(!$funcRef){$funcRef=[dnlib.DotNet.TypeRefUser]::new($module,'System','Func`3',$module.CorLibTypes.AssemblyRef)}
    $delegateSig=[dnlib.DotNet.GenericInstSig]::new([dnlib.DotNet.ClassSig]::new($funcRef),[dnlib.DotNet.TypeSig[]]@($module.CorLibTypes.Double,[dnlib.DotNet.ClassSig]::new($axis),$module.CorLibTypes.Double))
    $filter=[dnlib.DotNet.FieldDefUser]::new('Filter',[dnlib.DotNet.FieldSig]::new($delegateSig),[dnlib.DotNet.FieldAttributes]'Public, Static')
    $hook.Fields.Add($filter)
    $apply=[dnlib.DotNet.MethodDefUser]::new('Apply',[dnlib.DotNet.MethodSig]::CreateStatic($module.CorLibTypes.Double,$module.CorLibTypes.Double,[dnlib.DotNet.ClassSig]::new($axis)),[dnlib.DotNet.MethodAttributes]'Public, Static, HideBySig')
    $hook.Methods.Add($apply); $apply.Body=[dnlib.DotNet.Emit.CilBody]::new()
    $local=[dnlib.DotNet.Emit.Local]::new($delegateSig); $apply.Body.Variables.Add($local)
    $invoke=[dnlib.DotNet.MemberRefUser]::new($module,'Invoke',[dnlib.DotNet.MethodSig]::CreateInstance([dnlib.DotNet.GenericVar]::new(2),[dnlib.DotNet.GenericVar]::new(0),[dnlib.DotNet.GenericVar]::new(1)),[dnlib.DotNet.TypeSpecUser]::new($delegateSig))
    $fallback=[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0)
    $code=$apply.Body.Instructions
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Volatile))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldsfld,$filter))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Stloc,$local))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brfalse,$fallback))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Callvirt,$invoke))
    $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $code.Add($fallback); $code.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $getValues.Body.SimplifyMacros($getValues.Parameters)
    $code=$getValues.Body.Instructions
    $axisLocal=$getValues.Body.Variables | Where-Object { $_.Type.FullName -eq $axis.FullName } | Select-Object -First 1
    $lerp=@($code | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'Lerp' })
    if(!$axisLocal -or $lerp.Count -ne 1){throw 'Unexpected native range mapping body'}
    $at=$code.IndexOf($lerp[0])+1
    $code.Insert($at++,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$axisLocal))
    $code.Insert($at,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$apply))
    $getValues.Body.OptimizeMacros()
}
