param([string]$ImagePath,[string]$IconPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing.Common
$source=[Drawing.Image]::FromFile($ImagePath)
$sizes=@(16,24,32,48,64,128,256)
$images=@()
try {
    foreach($size in $sizes) {
        $bitmap=[Drawing.Bitmap]::new($size,$size)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source,0,0,$size,$size)
        $stream=[IO.MemoryStream]::new(); $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
        $images+=,$stream.ToArray()
        $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
} finally { $source.Dispose() }
$file=[IO.File]::Create($IconPath); $writer=[IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $edge=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$edge);$writer.Write([byte]$edge);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$images[$i].Length
    }
    foreach($png in $images){$writer.Write([byte[]]$png)}
} finally { $writer.Dispose(); $file.Dispose() }
