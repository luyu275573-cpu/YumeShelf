param()
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$brandRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$brandAssets = Join-Path $brandRoot "src\YumeShelf\Assets"
[xml]$brandSvg = Get-Content -LiteralPath (Join-Path $brandAssets "YumeShelfIcon.svg") -Raw
$brandNs = [System.Xml.XmlNamespaceManager]::new($brandSvg.NameTable)
$brandNs.AddNamespace("s", "http://www.w3.org/2000/svg")
$brandRect = $brandSvg.SelectSingleNode("/s:svg/s:rect", $brandNs)
$brandGroup = $brandSvg.SelectSingleNode("/s:svg/s:g", $brandNs)
function Brush([string]$hex) { [System.Windows.Media.BrushConverter]::new().ConvertFromInvariantString($hex) }
function Save-Png($visual, [int]$width, [int]$height, [string]$path) {
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
function Draw-Mark($dc, [double]$x, [double]$y, [double]$size) {
    $dc.PushTransform([System.Windows.Media.TranslateTransform]::new($x, $y))
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 256, $size / 256))
    $border = [System.Windows.Media.Pen]::new((Brush $brandRect.fill), 0)
    $border.Brush = Brush $brandRect.stroke; $border.Thickness = [double]$brandRect.'stroke-width'
    $dc.DrawRoundedRectangle((Brush $brandRect.fill), $border,
        [System.Windows.Rect]::new([double]$brandRect.x, [double]$brandRect.y, [double]$brandRect.width, [double]$brandRect.height),
        [double]$brandRect.rx, [double]$brandRect.rx)
    $pen = [System.Windows.Media.Pen]::new((Brush $brandGroup.stroke), [double]$brandGroup.'stroke-width')
    $pen.StartLineCap = $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    foreach ($node in $brandGroup.SelectNodes("s:path", $brandNs)) { $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($node.d)) }
    $dc.Pop(); $dc.Pop()
}
function Draw-Text($dc, [string]$text, [double]$x, [double]$y, [double]$size, [string]$color, [bool]$bold = $false) {
    $weight = if ($bold) { [System.Windows.FontWeights]::SemiBold } else { [System.Windows.FontWeights]::Normal }
    $typeface = [System.Windows.Media.Typeface]::new([System.Windows.Media.FontFamily]::new("Segoe UI, Microsoft YaHei UI"), [System.Windows.FontStyles]::Normal, $weight, [System.Windows.FontStretches]::Normal)
    $formatted = [System.Windows.Media.FormattedText]::new($text, [System.Globalization.CultureInfo]::GetCultureInfo("zh-CN"), [System.Windows.FlowDirection]::LeftToRight, $typeface, $size, (Brush $color), 1)
    $dc.DrawText($formatted, [System.Windows.Point]::new($x, $y))
}
$iconVisual = [System.Windows.Media.DrawingVisual]::new()
$iconDc = $iconVisual.RenderOpen()
try { Draw-Mark $iconDc 0 0 512 } finally { $iconDc.Close() }
Save-Png $iconVisual 512 512 (Join-Path $brandAssets "YumeShelfIcon.png")

# A reviewable layout sheet generated from the exact same vector as the shipping icon.
$board = [System.Windows.Media.DrawingVisual]::new()
$dc = $board.RenderOpen()
try {
    $dc.DrawRectangle((Brush "#F4F5F7"), $null, [System.Windows.Rect]::new(0,0,1200,700))
    Draw-Text $dc "YumeShelf" 56 40 34 "#25272D" $true
    Draw-Text $dc "YS 字母标记 / 启动页版式" 58 94 16 "#777B85"
    $dc.DrawRoundedRectangle((Brush "#FFFFFF"), $null, [System.Windows.Rect]::new(48,150,352,494), 28,28)
    Draw-Mark $dc 116 208 216
    Draw-Text $dc "Y + S" 172 454 24 "#25272D" $true
    Draw-Text $dc "圆角 · 等粗笔画 · 黑白灰" 112 502 15 "#777B85"
    Draw-Mark $dc 148 560 24; Draw-Mark $dc 200 556 32; Draw-Mark $dc 260 548 48
    $dc.DrawRoundedRectangle((Brush "#FFFFFF"), $null, [System.Windows.Rect]::new(424,150,728,494), 28,28)
    Draw-Mark $dc 740 227 96
    Draw-Text $dc "YumeShelf" 660 345 48 "#25272D" $true
    Draw-Text $dc "一个更懂你的游戏盒子..." 665 417 20 "#777B85"
    Draw-Text $dc "应用名淡入  →  标语浮现  →  进入游戏库" 617 582 15 "#999CA4"
} finally { $dc.Close() }
$boardPath = Join-Path $brandRoot "docs\assets\yumeshelf-brand-layout.png"
New-Item -ItemType Directory -Path (Split-Path $boardPath) -Force | Out-Null
Save-Png $board 1200 700 $boardPath
Write-Output "Generated brand PNG and layout sheet from YumeShelfIcon.svg."
