<#
.SYNOPSIS
  生成工具自己的应用图标（多尺寸 .ico）。
.DESCRIPTION
  图标是本仓库自己画的（不引入第三方素材，无许可问题）：蓝色渐变圆角底 + 白色「上箭头入托盘」，
  语义对应「发布 / 安装」。脚本按 16/20/24/32/40/48/64/128/256 各渲染一张 PNG，组装成一个
  多尺寸 ICO（PNG 压缩格式，Vista 以后认），输出到 src/DotnetPublishTool.App/app.ico。
  换样式只要改下面的图形段；同时会在 .render-out/icons 下留一份预览图，方便目视核对。
.PARAMETER Preview
  额外输出各尺寸并排的预览图到 .render-out/icons/icon-preview.png。
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\make-app-icon.ps1 -Preview
#>
param(
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $root 'src\DotnetPublishTool.App\app.ico'
$previewDir = Join-Path $root '.render-out\icons'

function New-RoundedPath([int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AppIconBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 256.0

    # 底：圆角方块 + 蓝色渐变（与应用主题同色系）
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 96, 165, 250),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216),
        90.0)
    $rounded = New-RoundedPath $size $size ([int](56 * $scale))
    $graphics.FillPath($background, $rounded)

    # 图形：上箭头 + 托盘（发布 / 安装）
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single](20 * $scale))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $cx = 128 * $scale
    $graphics.DrawLine($pen, $cx, 72 * $scale, $cx, 146 * $scale)
    $graphics.DrawLine($pen, $cx - (30 * $scale), 102 * $scale, $cx, 72 * $scale)
    $graphics.DrawLine($pen, $cx + (30 * $scale), 102 * $scale, $cx, 72 * $scale)
    $graphics.DrawLine($pen, 76 * $scale, 156 * $scale, 76 * $scale, 186 * $scale)
    $graphics.DrawLine($pen, 180 * $scale, 156 * $scale, 180 * $scale, 186 * $scale)
    $graphics.DrawLine($pen, 76 * $scale, 186 * $scale, 180 * $scale, 186 * $scale)

    $pen.Dispose(); $background.Dispose(); $rounded.Dispose(); $graphics.Dispose()
    return $bitmap
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$blobs = @()
foreach ($size in $sizes) {
    $bitmap = New-AppIconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}

# ICO：6 字节头 + 每张 16 字节目录项 + PNG 数据（宽度 0 表示 256）
$file = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($blob in $blobs) {
    $dimension = if ($blob.Size -ge 256) { 0 } else { $blob.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$blob.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $blob.Bytes.Length
}
foreach ($blob in $blobs) { $writer.Write($blob.Bytes) }
$writer.Flush(); $writer.Dispose(); $file.Dispose()
Write-Host "已生成 $icoPath（$($blobs.Count) 个尺寸：$(($sizes) -join '/')，$((Get-Item $icoPath).Length) 字节）"

if ($Preview) {
    New-Item -ItemType Directory -Path $previewDir -Force | Out-Null
    $sheetSizes = 16, 32, 48, 64, 128, 256
    $width = 12
    foreach ($sheetSize in $sheetSizes) { $width += $sheetSize + 12 }
    $sheet = New-Object System.Drawing.Bitmap($width, 300)
    $sheetGraphics = [System.Drawing.Graphics]::FromImage($sheet)
    $sheetGraphics.Clear([System.Drawing.Color]::FromArgb(255, 243, 244, 246))
    $x = 12
    foreach ($size in $sheetSizes) {
        $bitmap = New-AppIconBitmap $size
        $sheetGraphics.DrawImage($bitmap, $x, 12, $size, $size)
        $bitmap.Dispose()
        $x += $size + 12
    }
    $sheetGraphics.Dispose()
    $previewPath = Join-Path $previewDir 'icon-preview.png'
    $sheet.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    Write-Host "预览图：$previewPath"
}
