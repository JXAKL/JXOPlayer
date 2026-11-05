param()
$ErrorActionPreference='Stop'
$dir=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Tools\WindowsSDK'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$package=Join-Path $dir 'sdk.nupkg'
if(!(Test-Path -LiteralPath $package)){
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.net.ref/10.0.19041.57/microsoft.windows.sdk.net.ref.10.0.19041.57.nupkg' -OutFile $package -TimeoutSec 60
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
if((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '90B18F2CF7D7A23F5B0BFE2B86D8DDA45881481F677CE36ECCAF6E9AB50B6B4B'){throw 'Windows SDK package checksum mismatch'}
$zip=[IO.Compression.ZipFile]::OpenRead($package)
try{foreach($name in 'Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll'){
    [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('lib/net8.0/'+$name),(Join-Path $dir $name),$true)
}}finally{$zip.Dispose()}
