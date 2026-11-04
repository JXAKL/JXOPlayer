# Filter native local candidates BEFORE its first-name-wins axis selection.
function Add-DurationMatchingHook($module) {
    if($module.GetTypes() | Where-Object Name -eq 'LocalScriptMatchHook'){return}
    $resultType=$module.GetTypes() | Where-Object { $_.Name -eq 'ScriptReaderResult' }
    $repository=$module.GetTypes() | Where-Object Name -eq 'LocalScriptRepository'
    $method=$repository.Methods | Where-Object Name -eq 'SearchForScripts'
    if(!$method -or !$resultType){throw 'Native local script lookup not found'}
    $method.Body.SimplifyMacros($method.Parameters)
    $listLocal=$method.Body.Variables | Where-Object { $_.Type.FullName -match 'List.*ScriptReaderResult' } | Select-Object -First 1
    if(!$listLocal){throw 'Candidate script list not found'}
    # The first ToLookup call starts name/axis selection after enumeration.
    $code=$method.Body.Instructions
    $lookup=@($code | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'ToLookup' })[0]
    if(!$lookup){throw 'Native script matching stage not found'}
    $at=$code.IndexOf($lookup)
    while($at -ge 0 -and !($code[$at].OpCode.Code -eq 'Ldloc' -and $code[$at].Operand -eq $listLocal)){$at--}
    if($at -lt 0){throw 'Candidate lookup stack changed'}
    $entry=$code[$at]
    $ns=([string]$resultType.Namespace) -replace '\.Script$','.Common'
    $hook=[dnlib.DotNet.TypeDefUser]::new($ns,'LocalScriptMatchHook',$module.CorLibTypes.Object.TypeDefOrRef)
    $hook.Attributes=[dnlib.DotNet.TypeAttributes]'Public, Abstract, Sealed, BeforeFieldInit';$module.Types.Add($hook)
    $func=[dnlib.DotNet.TypeRefUser]::new($module,'System','Func`4',$module.CorLibTypes.AssemblyRef)
    $listSig=$listLocal.Type
    $delegate=[dnlib.DotNet.GenericInstSig]::new([dnlib.DotNet.ClassSig]::new($func),[dnlib.DotNet.TypeSig[]]@($module.CorLibTypes.String,$module.CorLibTypes.String,$listSig,$listSig))
    $field=[dnlib.DotNet.FieldDefUser]::new('Filter',[dnlib.DotNet.FieldSig]::new($delegate),[dnlib.DotNet.FieldAttributes]'Public, Static');$hook.Fields.Add($field)
    $apply=[dnlib.DotNet.MethodDefUser]::new('Apply',[dnlib.DotNet.MethodSig]::CreateStatic($listSig,$module.CorLibTypes.String,$module.CorLibTypes.String,$listSig),[dnlib.DotNet.MethodAttributes]'Public, Static, HideBySig');$hook.Methods.Add($apply)
    $apply.Body=[dnlib.DotNet.Emit.CilBody]::new();$local=[dnlib.DotNet.Emit.Local]::new($delegate);$apply.Body.Variables.Add($local)
    $invoke=[dnlib.DotNet.MemberRefUser]::new($module,'Invoke',[dnlib.DotNet.MethodSig]::CreateInstance([dnlib.DotNet.GenericVar]::new(3),[dnlib.DotNet.GenericVar]::new(0),[dnlib.DotNet.GenericVar]::new(1),[dnlib.DotNet.GenericVar]::new(2)),[dnlib.DotNet.TypeSpecUser]::new($delegate))
    $ctor=[dnlib.DotNet.MemberRefUser]::new($module,'.ctor',[dnlib.DotNet.MethodSig]::CreateInstance($module.CorLibTypes.Void),[dnlib.DotNet.TypeSpecUser]::new($listSig))
    $fallback=[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Newobj,$ctor)
    $body=$apply.Body.Instructions
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Volatile))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldsfld,$field))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Stloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brfalse,$fallback))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    foreach($op in 'Ldarg_0','Ldarg_1','Ldarg_2'){$body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::$op))}
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Callvirt,$invoke));$body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $body.Add($fallback);$body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    # Repurpose the existing branch target so no path can skip the filter.
    $entry.OpCode=[dnlib.DotNet.Emit.OpCodes]::Ldarg_1;$entry.Operand=$null
    $at++
    foreach($ins in @([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_2),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$listLocal),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$apply),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Stloc,$listLocal),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$listLocal))){$code.Insert($at++,$ins)}
    $method.Body.OptimizeMacros();$method.Body.SimplifyBranches();$method.Body.OptimizeBranches()
}
