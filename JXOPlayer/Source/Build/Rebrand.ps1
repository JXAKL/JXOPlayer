param(
    [string]$InputAssembly,
    [string]$OutputAssembly,
    [string]$ImagePath,
    [string]$ToolDirectory = (Join-Path $PSScriptRoot '..\Tools')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $ToolDirectory 'dnlib.dll')
$bamlAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $ToolDirectory 'ICSharpCode.BamlDecompiler.dll'))
$reader = $bamlAssembly.GetType('ICSharpCode.BamlDecompiler.Baml.BamlReader').GetMethod('ReadDocument')
$writer = $bamlAssembly.GetType('ICSharpCode.BamlDecompiler.Baml.BamlWriter').GetMethod('WriteDocument')

function Rename-Text([string]$text) {
    [regex]::Replace($text.Replace($originalBrand,'JXOPlayer').Replace($originalBrand.ToLowerInvariant(),'jxoplayer').Replace($originalVersion,'2.0.0.0').Replace('${basedir}\Logs','${basedir}\..\Logs'),'(?i)(?<=assets/)[^/]*player\.png','jxoplayer.png')
}
function Clear-Range($method, [int]$start, [int]$end) {
    $instructions = $method.Body.Instructions
    $remove = @($instructions | Where-Object { $_.Offset -ge $start -and $_.Offset -lt $end })
    foreach ($instruction in $remove) { [void]$instructions.Remove($instruction) }
}
function Set-ReturnString($method, [string]$value) {
    $method.Body = [dnlib.DotNet.Emit.CilBody]::new()
    $method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldstr, $value))
    $method.Body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
}
function Update-AttributeStrings($attributes) {
    foreach ($attribute in $attributes) {
        for ($i=0; $i -lt $attribute.ConstructorArguments.Count; $i++) {
            $argument = $attribute.ConstructorArguments[$i]
            if ($argument.Value -is [dnlib.DotNet.UTF8String] -or $argument.Value -is [string]) {
                $value = Rename-Text ([string]$argument.Value)
                if ($attribute.TypeFullName -match 'Assembly(FileVersion|InformationalVersion)Attribute') { $value = '2.0.0.0' }
                $attribute.ConstructorArguments[$i] = [dnlib.DotNet.CAArgument]::new($argument.Type, [dnlib.DotNet.UTF8String]::new($value))
            }
        }
    }
}
function Update-Baml([byte[]]$bytes, [string]$name) {
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $doc = $reader.Invoke($null, @($stream, [Threading.CancellationToken]::None))
    $stream.Dispose()
    if ($name -eq 'rootview.baml') {
        # Remove the entire help button, retaining shared BAML metadata IDs.
        $stack = [Collections.Generic.List[int]]::new()
        $buttonStart = -1
        for ($i=0; $i -lt $doc.Count; $i++) {
            $r=$doc[$i]; $kind=[string]$r.Type
            if ($kind -eq 'ElementStart') { $stack.Add($i) }
            elseif ($kind -eq 'ElementEnd') { $stack.RemoveAt($stack.Count-1) }
            elseif ($kind -eq 'Text' -and $r.Value -eq 'OnInformationClick') {
                $buttonStart = $stack[$stack.Count-2]; break
            }
        }
        if ($buttonStart -lt 0 -or $doc[$buttonStart].TypeId -ne 65481) { throw 'Help button structure changed' }
        $depth=0; $buttonEnd=-1
        for($i=$buttonStart; $i -lt $doc.Count; $i++) {
            if ([string]$doc[$i].Type -eq 'ElementStart') { $depth++ }
            if ([string]$doc[$i].Type -eq 'ElementEnd') { $depth--; if($depth -eq 0){ $buttonEnd=$i; break } }
        }
        if($buttonEnd -lt 0){throw 'Unbalanced help subtree'}
        for($i=$buttonEnd;$i -ge $buttonStart;$i--) {
            if([string]$doc[$i].Type -notin @('AssemblyInfo','TypeInfo','TypeSerializerInfo','AttributeInfo','StringInfo','PIMapping')) { $doc.RemoveAt($i) }
        }
        Write-Output "Removed help button and animation from $name" | Out-Host
    }
    foreach($r in $doc) {
        foreach($p in $r.GetType().GetProperties()) {
            if($p.PropertyType -eq [string] -and $p.CanWrite) {
                $old=$p.GetValue($r)
                if($null -ne $old) { $new=Rename-Text $old; if($new -ne $old) { $p.SetValue($r,$new) } }
            }
        }
        if($name -eq 'rootview.baml' -and [string]$r.Type -eq 'PropertyWithConverter' -and $r.AttributeId -eq 0) { $r.Value='JXOPlayer 2.0' }
    }
    $output=[IO.MemoryStream]::new()
    $writer.Invoke($null,@($doc,$output)) | Out-Null
    $result=$output.ToArray(); $output.Dispose()
    return ,$result
}

$module = [dnlib.DotNet.ModuleDefMD]::Load($InputAssembly)
$originalBrand=[string]$module.Assembly.Name; $originalVersion=[string]$module.Assembly.Version
try {
    # Resolve serialized typeof(...) attribute arguments before changing names.
    # Otherwise dnlib's lazy parser can retain an obsolete assembly reference.
    $module.LoadEverything($null)
    . (Join-Path $PSScriptRoot 'FixPotPlayerTransport.ps1')
    Update-PotPlayerTransport $module
    . (Join-Path $PSScriptRoot 'FixLiteralMediaPaths.ps1')
    Update-LiteralMediaPaths $module
    . (Join-Path $PSScriptRoot 'AddMotionOutputHook.ps1')
    Add-MotionOutputHook $module
    . (Join-Path $PSScriptRoot 'AddDurationMatchingHook.ps1')
    Add-DurationMatchingHook $module
    # Helper classes in plugins need not have a base-class clause.
    $compilerPredicate=$module.GetTypes() | ForEach-Object Methods | Where-Object { $_.Name -eq '<InternalCompile>b__22_0' }
    if(!$compilerPredicate){throw 'Plugin discovery predicate not found'}
    $code=$compilerPredicate.Body.Instructions
    $baseCall=@($code | Where-Object { $_.Operand -is [dnlib.DotNet.IMethod] -and $_.Operand.Name -eq 'get_BaseList' })
    if($baseCall.Count -ne 1){throw 'Unexpected plugin discovery predicate'}
    $at=$code.IndexOf($baseCall[0])+1;$next=$code[$at]
    $guard=@([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Dup),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Brtrue,$next),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Pop),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldc_I4_0),[dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
    foreach($instruction in $guard){$code.Insert($at++,$instruction)}
    # Runtime assemblies now live in Runtime; the bundled interactive player stays beside the launcher.
    $pot=$module.GetTypes() | Where-Object Name -eq 'PotPlayerMediaSource'
    $finder=$pot.Methods | Where-Object Name -eq 'GetInstallationPath'
    $getProcess=$module.GetMemberRefs() | Where-Object { $_.Name -eq 'get_ProcessPath' -and $_.DeclaringType.FullName -eq 'System.Environment' } | Select-Object -First 1
    $getFolder=$module.GetMemberRefs() | Where-Object { $_.Name -eq 'GetDirectoryName' -and $_.DeclaringType.FullName -eq 'System.IO.Path' -and $_.MethodSig.Params[0].FullName -eq 'System.String' } | Select-Object -First 1
    if(!$getProcess -or !$getFolder){throw 'Required runtime path members not found'}
    foreach($instruction in $finder.Body.Instructions){
        if($instruction.Operand -is [dnlib.DotNet.IMethod] -and $instruction.Operand.Name -eq 'get_CurrentDomain'){$instruction.OpCode=[dnlib.DotNet.Emit.OpCodes]::Call;$instruction.Operand=$getProcess}
        elseif($instruction.Operand -is [dnlib.DotNet.IMethod] -and $instruction.Operand.Name -eq 'get_BaseDirectory'){$instruction.OpCode=[dnlib.DotNet.Emit.OpCodes]::Call;$instruction.Operand=$getFolder}
    }
    $root = $module.Types | Where-Object Name -eq 'RootViewModel'
    $handle = $root.Methods | Where-Object Name -eq 'Handle'
    # These ranges are checked against the original method before modification.
    if (($handle.Body.Instructions | Where-Object Offset -eq 0xC7).Operand -ne 'DisablePopup') { throw 'Unexpected settings handler' }
    Clear-Range $handle 0x0F 0x25
    Clear-Range $handle 0xC5 0xFD
    foreach($p in @($root.Properties | Where-Object { $_.Name -in 'Information','DisablePopup' })) {
        [void]$root.Methods.Remove($p.GetMethod); [void]$root.Methods.Remove($p.SetMethod); [void]$root.Properties.Remove($p)
    }
    foreach($f in @($root.Fields | Where-Object { $_.Name -match '<(Information|DisablePopup)>' })) { [void]$root.Fields.Remove($f) }
    foreach($m in @($root.Methods | Where-Object { $_.Name -eq 'OnInformationClick' -or $_.Name -like '<Handle>*' })) { [void]$root.Methods.Remove($m) }
    foreach($t in @($root.NestedTypes | Where-Object { $_.Name -like '*<Handle>*' })) { [void]$root.NestedTypes.Remove($t) }
    foreach($t in @($module.Types | Where-Object { $_.Name -in 'InformationView','InformationViewModel' })) { [void]$module.Types.Remove($t) }
    $version = $module.Types | Where-Object Name -eq 'VersionDisplay'
    Set-ReturnString ($version.Methods | Where-Object Name -eq 'get_ShortVersion') '2.0'
    Set-ReturnString ($version.Methods | Where-Object Name -eq 'get_WindowTitleVersion') ''
    # Git metadata is no longer displayed or used; remove stale generated data.
    $git = $module.Types | Where-Object Name -eq 'GitVersionInformation'
    $gitUses = @($module.GetTypes() | Where-Object Name -ne 'GitVersionInformation' | ForEach-Object Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.Operand -is [dnlib.DotNet.IField] -and $_.Operand.DeclaringType.Name -eq 'GitVersionInformation' })
    foreach($ins in $gitUses) {
        if($ins.OpCode.Code -ne 'Ldsfld'){throw 'Unexpected version metadata usage'}
        $value=if($ins.Operand.Name -eq 'FullBuildMetaData'){'JXOPlayer 2.0'}else{'2.0.0'}
        $ins.OpCode=[dnlib.DotNet.Emit.OpCodes]::Ldstr; $ins.Operand=$value
    }
    [void]$module.Types.Remove($git)
    foreach($t in $module.GetTypes()) {
        $t.Namespace = Rename-Text ([string]$t.Namespace)
        $t.Name = Rename-Text ([string]$t.Name)
        Update-AttributeStrings $t.CustomAttributes
        foreach($p in $t.Properties){ $p.Name=Rename-Text ([string]$p.Name); Update-AttributeStrings $p.CustomAttributes }
        foreach($e in $t.Events){ $e.Name=Rename-Text ([string]$e.Name) }
        foreach($f in $t.Fields) { $f.Name=Rename-Text ([string]$f.Name); if($f.HasConstant -and $f.Constant.Value -is [string]) { $f.Constant.Value = Rename-Text $f.Constant.Value } }
        foreach($m in $t.Methods) {
            $m.Name=Rename-Text ([string]$m.Name)
            Update-AttributeStrings $m.CustomAttributes
            if($m.HasBody) {
                foreach($ins in $m.Body.Instructions) { if($ins.OpCode.Code -eq 'Ldstr') { $ins.Operand = Rename-Text ([string]$ins.Operand) } }
                $m.Body.SimplifyBranches(); $m.Body.OptimizeBranches()
            }
        }
    }
    foreach($ref in $module.GetTypeRefs()){ $ref.Namespace=Rename-Text ([string]$ref.Namespace); $ref.Name=Rename-Text ([string]$ref.Name) }
    foreach($ref in $module.GetMemberRefs()){ $ref.Name=Rename-Text ([string]$ref.Name) }
    foreach($ref in $module.GetAssemblyRefs()){ if($ref.Name -eq $originalBrand){ $ref.Name='JXOPlayer'; $ref.Version=[version]'2.0.0.0' } }
    $module.Assembly.Name='JXOPlayer'; $module.Assembly.Version=[version]'2.0.0.0'; $module.Name='JXOPlayer.dll'
    Update-AttributeStrings $module.Assembly.CustomAttributes
    Update-AttributeStrings $module.CustomAttributes

    # Lossless format conversion of the provided avatar for WPF and ICO resources.
    Add-Type -AssemblyName System.Drawing.Common
    $avatar=[Drawing.Image]::FromFile($ImagePath)
    $pngStream=[IO.MemoryStream]::new(); $avatar.Save($pngStream,[Drawing.Imaging.ImageFormat]::Png)
    $png=$pngStream.ToArray(); $pngStream.Dispose(); $avatar.Dispose()
    foreach($resource in @($module.Resources)) {
        if($resource -isnot [dnlib.DotNet.EmbeddedResource]){continue}
        if([string]$resource.Name -notlike '*.g.resources'){continue}
        $resourceReader=[Resources.ResourceReader]::new($resource.CreateReader().AsStream())
        $resourceStream=[IO.MemoryStream]::new()
        $resourceWriter=[Resources.ResourceWriter]::new($resourceStream)
        foreach($entry in $resourceReader.GetEnumerator()) {
            $key=[string]$entry.Key
            if($key -eq 'ui/controls/views/informationview.baml'){continue}
            $value=$entry.Value
            if($value -is [IO.Stream]) { $copy=[IO.MemoryStream]::new(); $value.CopyTo($copy); $data=$copy.ToArray(); $copy.Dispose() }
            elseif($value -is [byte[]]) { $data=$value }
            else { $resourceWriter.AddResource((Rename-Text $key),$value); continue }
            if($key.EndsWith('.baml')){ $data=Update-Baml $data $key }
            if($key -match '^assets/.*player\.png$'){ $data=$png }
            if($key -eq 'app.ico'){ $data=[IO.File]::ReadAllBytes((Join-Path (Split-Path $OutputAssembly) 'JXOPlayer.ico')) }
            $resourceWriter.AddResource((Rename-Text $key),[IO.MemoryStream]::new($data,$false),$true)
        }
        $resourceWriter.Generate()
        $newResource=[dnlib.DotNet.EmbeddedResource]::new((Rename-Text ([string]$resource.Name)),$resourceStream.ToArray(),$resource.Attributes)
        $index=$module.Resources.IndexOf($resource); $module.Resources[$index]=$newResource
        $resourceReader.Dispose(); $resourceWriter.Dispose(); $resourceStream.Dispose()
    }
    $options=[dnlib.DotNet.Writer.ModuleWriterOptions]::new($module)
    $module.Write($OutputAssembly,$options)
    Write-Output 'Rebranded assembly and WPF resources saved; help classes and popup logic removed.'
} finally { $module.Dispose() }
