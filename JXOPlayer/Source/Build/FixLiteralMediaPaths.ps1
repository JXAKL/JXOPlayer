# Treat rooted filesystem paths literally before URL decoding. Patch the current
# application in place so unrelated native fixes and settings are retained.
function Update-LiteralMediaPaths($module) {
    Add-MediaPathResolutionHook $module
    Add-MediaOpenPathHook $module
    $builder=$module.GetTypes() | Where-Object Name -eq 'MediaResourceInfoBuilder'
    $uri=$builder.Methods | Where-Object Name -eq 'TryParseUri'
    $path=$builder.Methods | Where-Object Name -eq 'TryParsePath'
    if(!$uri -or !$path){throw 'Media path parser not found'}
    $pathHook=$module.GetTypes() | Where-Object Name -eq 'MediaPathResolutionHook'
    $resolve=$pathHook.Methods | Where-Object Name -eq 'Apply'
    if(!@($uri.Body.Instructions | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.DeclaringType.Name -eq 'MediaPathResolutionHook' }).Count){
        $uri.Body.Instructions.Insert(0,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1))
        $uri.Body.Instructions.Insert(1,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$resolve))
        $uri.Body.Instructions.Insert(2,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Starg,$uri.Parameters[1]))
    }
    if(@($uri.Body.Instructions | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'IsPathFullyQualified' }).Count){return}
    $qualified=@($path.Body.Instructions | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'IsPathFullyQualified' })[0].Operand
    if(!$qualified){throw 'Target Path.IsPathFullyQualified reference not found'}
    $code=$uri.Body.Instructions;$original=$code[0]
    $prefix=@(
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$qualified),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brfalse,$original),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_2),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$path),
        [dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    # Keep hook resolution ahead of the rooted-path test for local relay URLs.
    $original=$code[3];$prefix[2].Operand=$original
    for($index=0;$index -lt $prefix.Length;$index++){$code.Insert($index+3,$prefix[$index])}
    $uri.Body.SimplifyBranches();$uri.Body.OptimizeBranches()
}

function Add-MediaOpenPathHook($module) {
    $hook=$module.GetTypes() | Where-Object Name -eq 'MediaPathResolutionHook'
    if($hook.Fields | Where-Object Name -eq 'Prepare'){return}
    $delegate=($hook.Fields | Where-Object Name -eq 'Resolve').FieldSig.Type
    $field=[dnlib.DotNet.FieldDefUser]::new('Prepare',[dnlib.DotNet.FieldSig]::new($delegate),[dnlib.DotNet.FieldAttributes]'Public, Static');$hook.Fields.Add($field)
    $apply=[dnlib.DotNet.MethodDefUser]::new('ForPlayer',[dnlib.DotNet.MethodSig]::CreateStatic($module.CorLibTypes.String,$module.CorLibTypes.String),[dnlib.DotNet.MethodAttributes]'Public, Static, HideBySig');$hook.Methods.Add($apply)
    $apply.Body=[dnlib.DotNet.Emit.CilBody]::new();$local=[dnlib.DotNet.Emit.Local]::new($delegate);[void]$apply.Body.Variables.Add($local)
    $invoke=[dnlib.DotNet.MemberRefUser]::new($module,'Invoke',[dnlib.DotNet.MethodSig]::CreateInstance([dnlib.DotNet.GenericVar]::new(1),[dnlib.DotNet.GenericVar]::new(0)),[dnlib.DotNet.TypeSpecUser]::new($delegate))
    $fallback=[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0)
    $body=$apply.Body.Instructions
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldsfld,$field))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Stloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brfalse,$fallback))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Callvirt,$invoke))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $body.Add($fallback);$body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $player=$module.GetTypes() | Where-Object Name -eq 'PotPlayerMediaSource'
    $method=$player.Methods | Where-Object Name -like '*SetValueString*'
    if(!$method -or $method.Parameters[3].Type.FullName -ne 'System.String'){throw 'Unexpected PotPlayer path sender signature'}
    $method.Body.Instructions.Insert(0,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg,$method.Parameters[3]))
    $method.Body.Instructions.Insert(1,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$apply))
    $method.Body.Instructions.Insert(2,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Starg,$method.Parameters[3]))
    $method.Body.SimplifyBranches();$method.Body.OptimizeBranches()
}

function Add-MediaPathResolutionHook($module) {
    if($module.GetTypes() | Where-Object Name -eq 'MediaPathResolutionHook'){return}
    $builder=$module.GetTypes() | Where-Object Name -eq 'MediaResourceInfoBuilder'
    $method=$builder.Methods | Where-Object Name -eq 'TryParsePath'
    if(!$method){throw 'Native path resolution point not found'}
    $hook=[dnlib.DotNet.TypeDefUser]::new('JXOPlayer.Common','MediaPathResolutionHook',$module.CorLibTypes.Object.TypeDefOrRef)
    $hook.Attributes=[dnlib.DotNet.TypeAttributes]'Public, Abstract, Sealed, BeforeFieldInit';$module.Types.Add($hook)
    $func=[dnlib.DotNet.TypeRefUser]::new($module,'System','Func`2',$module.CorLibTypes.AssemblyRef)
    $delegate=[dnlib.DotNet.GenericInstSig]::new([dnlib.DotNet.ClassSig]::new($func),[dnlib.DotNet.TypeSig[]]@($module.CorLibTypes.String,$module.CorLibTypes.String))
    $field=[dnlib.DotNet.FieldDefUser]::new('Resolve',[dnlib.DotNet.FieldSig]::new($delegate),[dnlib.DotNet.FieldAttributes]'Public, Static');$hook.Fields.Add($field)
    $apply=[dnlib.DotNet.MethodDefUser]::new('Apply',[dnlib.DotNet.MethodSig]::CreateStatic($module.CorLibTypes.String,$module.CorLibTypes.String),[dnlib.DotNet.MethodAttributes]'Public, Static, HideBySig');$hook.Methods.Add($apply)
    $apply.Body=[dnlib.DotNet.Emit.CilBody]::new();$local=[dnlib.DotNet.Emit.Local]::new($delegate);$apply.Body.Variables.Add($local)
    $invoke=[dnlib.DotNet.MemberRefUser]::new($module,'Invoke',[dnlib.DotNet.MethodSig]::CreateInstance([dnlib.DotNet.GenericVar]::new(1),[dnlib.DotNet.GenericVar]::new(0)),[dnlib.DotNet.TypeSpecUser]::new($delegate))
    $fallback=[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0)
    $body=$apply.Body.Instructions
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldsfld,$field))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Stloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brfalse,$fallback))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldloc,$local))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Callvirt,$invoke))
    $body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $body.Add($fallback);$body.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    $method.Body.Instructions.Insert(0,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_1))
    $method.Body.Instructions.Insert(1,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call,$apply))
    $method.Body.Instructions.Insert(2,[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Starg,$method.Parameters[1]))
    $method.Body.SimplifyBranches();$method.Body.OptimizeBranches()
}
