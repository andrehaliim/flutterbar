<#
.SYNOPSIS
  Draws the FlutterBar app icon and writes it as a multi-size .ico (and, optionally, PNG previews).

  The design lives in this script (there is no SVG master): a rounded tile with three diagonal Flutter-blue ribbons and,
  on top, the widget as a dark pill with the three session buttons in their dark-theme colours (run, hot reload, stop).
  Coordinates are in a 64 x 64 design space. The pill edges are snapped to whole pixels at every size, and sizes up to
  24 px use a larger pill and larger glyphs so the three buttons stay readable.

.PARAMETER Out        Output .ico (default: src/FlutterBar.App/Assets/flutterbar.ico).
.PARAMETER PreviewDir Optional folder that receives one PNG per size (flutterbar-<size>.png), for a visual check.
.PARAMETER Sizes      Icon sizes in pixels. 256 is stored as PNG, smaller sizes as 32-bit bitmaps.

  Uses WPF (PresentationCore) only, nothing to install. Compatible with Windows PowerShell 5.1.
#>
param(
    [string]$Out = '',
    [string]$PreviewDir = '',
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Out -eq '') { $Out = Join-Path $scriptDir '..\src\FlutterBar.App\Assets\flutterbar.ico' }
$Out = [IO.Path]::GetFullPath($Out)

function New-Brush([string]$hex) {
    $brush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex))
    $brush.Freeze()
    return $brush
}

function New-RoundPen($brush, [double]$thickness) {
    $pen = New-Object Windows.Media.Pen ($brush, $thickness)
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $pen.Freeze()
    return $pen
}

$sky = New-Brush '#54C5F8'      # tile
$midBlue = New-Brush '#29B6F6'  # diagonal ribbon
$navy = New-Brush '#01579B'     # corner ribbon
$pill = New-Brush '#202020'     # Fb.Background (dark)
$green = New-Brush '#6CCB5F'    # Fb.Green (dark): run
$yellow = New-Brush '#FCE100'   # Fb.Yellow (dark): hot reload
$red = New-Brush '#FF6B6B'      # Fb.Red (dark): stop

$ribbon = [Windows.Media.Geometry]::Parse('M64,14 L64,42 L42,64 L14,64 Z')
$corner = [Windows.Media.Geometry]::Parse('M64,42 L64,64 L42,64 Z')
# Glyphs are centred on 0,0 and about 10 units wide.
$play = [Windows.Media.Geometry]::Parse('M-3.8,-6 L5.2,0 L-3.8,6 Z')
$bolt = [Windows.Media.Geometry]::Parse('M1.5,-7.5 L-5,1.5 L-0.8,1.5 L-2,7.5 L5,-1.8 L0.8,-1.8 Z')

function Snap([double]$value) { return [Math]::Round($value, [MidpointRounding]::AwayFromZero) }

function New-IconBitmap([int]$size) {
    $s = $size / 64.0
    if ($size -le 24) { $inset = 4.0; $top = 16.0; $radius = 10.0; $glyph = 1.1; $spacing = 0.32 }
    else { $inset = 5.0; $top = 19.0; $radius = 9.0; $glyph = 1.0; $spacing = 0.305 }

    # Pill in design units, with its edges on whole pixels.
    $pillX = (Snap ($inset * $s)) / $s
    $pillY = (Snap ($top * $s)) / $s
    $pillW = 64 - 2 * $pillX
    $pillH = 64 - 2 * $pillY
    $spread = $pillW * $spacing

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($s, $s)))

    $dc.PushClip((New-Object Windows.Media.RectangleGeometry ((New-Object Windows.Rect (0, 0, 64, 64)), 14, 14)))
    $dc.DrawRectangle($sky, $null, (New-Object Windows.Rect (0, 0, 64, 64)))
    $dc.DrawGeometry($midBlue, $null, $ribbon)
    $dc.DrawGeometry($navy, $null, $corner)
    $dc.Pop()

    $dc.DrawRoundedRectangle($pill, $null, (New-Object Windows.Rect ($pillX, $pillY, $pillW, $pillH)), $radius, $radius)

    $dc.PushTransform((New-Object Windows.Media.TranslateTransform ((32 - $spread), 32)))
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($glyph, $glyph)))
    $dc.DrawGeometry($green, (New-RoundPen $green 1.6), $play)
    $dc.Pop(); $dc.Pop()

    $dc.PushTransform((New-Object Windows.Media.TranslateTransform (32, 32)))
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($glyph, $glyph)))
    $dc.DrawGeometry($yellow, (New-RoundPen $yellow 1.0), $bolt)
    $dc.Pop(); $dc.Pop()

    $dc.PushTransform((New-Object Windows.Media.TranslateTransform ((32 + $spread), 32)))
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($glyph, $glyph)))
    $dc.DrawRoundedRectangle($red, $null, (New-Object Windows.Rect (-5, -5, 10, 10)), 1.8, 1.8)
    $dc.Pop(); $dc.Pop()

    $dc.Pop()
    $dc.Close()

    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap ($size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32))
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function ConvertTo-Png($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# Classic icon image: BITMAPINFOHEADER (height doubled), 32-bit BGRA rows bottom-up with straight alpha, then an empty AND mask.
function ConvertTo-IconDib($bitmap) {
    $size = $bitmap.PixelWidth
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap ($bitmap, ([Windows.Media.PixelFormats]::Bgra32), $null, 0)
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $stream
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]($pixels.Length + $maskStride * $size))
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $stride, $stride) }
    $writer.Write((New-Object byte[] ($maskStride * $size)))
    $writer.Flush()
    return , $stream.ToArray()
}

$images = @()
foreach ($size in ($Sizes | Sort-Object)) {
    if ($size -lt 16 -or $size -gt 256) { throw "Icon size $size is outside 16..256." }
    $bitmap = New-IconBitmap $size
    $png = ConvertTo-Png $bitmap
    if ($PreviewDir -ne '') {
        if (-not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Force $PreviewDir | Out-Null }
        [IO.File]::WriteAllBytes((Join-Path ([IO.Path]::GetFullPath($PreviewDir)) "flutterbar-$size.png"), $png)
    }
    if ($size -eq 256) { $data = $png } else { $data = ConvertTo-IconDib $bitmap }
    $images += [pscustomobject]@{ Size = $size; Data = $data }
}

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }

$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
    $w.Write([byte]$dimension)
    $w.Write([byte]$dimension)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$image.Data.Length)
    $w.Write([uint32]$offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $w.Write([byte[]]$image.Data) }
$w.Flush()
[IO.File]::WriteAllBytes($Out, $ico.ToArray())

Write-Host "Wrote $Out ($($images.Count) sizes: $(($images | ForEach-Object { $_.Size }) -join ', '); $($ico.Length) bytes)"
