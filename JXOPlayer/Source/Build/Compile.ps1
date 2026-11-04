param([string[]]$Source, [string]$Output, [switch]$Executable, [string]$ReferenceDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.CodeAnalysis
Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp
$basePath = if($ReferenceDirectory) { (Resolve-Path -LiteralPath $ReferenceDirectory).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\Runtime')).Path }
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath $basePath -Filter '*.dll') {
    if ($file.Name -match '^(Test.*|StartupRegression|PlaybackIntegration)\.dll$') { continue }
    try {
        [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    } catch { }
}
$trees = @($Source | ForEach-Object { [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_)) })
$kind = if ($Executable) { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication } else { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary }
$opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create([IO.Path]::GetFileNameWithoutExtension($Output), [Microsoft.CodeAnalysis.SyntaxTree[]]$trees, $refs, $opts)
$stream = [IO.File]::Create($Output)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | Where-Object { $_.Id -ne 'CS1701' -and $_.Severity -ne 'Hidden' } | ForEach-Object { $_.ToString() } | Select-Object -Unique -First 30
if (!$result.Success) { throw 'Compilation failed' }
'Compilation succeeded'
