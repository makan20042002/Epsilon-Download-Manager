param(
  [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'msix\Assets')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $root 'MakanDownloadManager\Assets\makan.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw "Missing application icon: $iconPath" }

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$icon = New-Object System.Drawing.Icon($iconPath)
$source = $icon.ToBitmap()

function New-EpsilonAsset([int]$width, [int]$height, [int]$iconSize, [string]$name) {
  $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 8, 120, 209))
    $x = [int](($width - $iconSize) / 2)
    $y = [int](($height - $iconSize) / 2)
    $graphics.DrawImage($source, $x, $y, $iconSize, $iconSize)
    $path = Join-Path $OutputDirectory $name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  }
  finally {
    $graphics.Dispose()
    $bitmap.Dispose()
  }
}

try {
  New-EpsilonAsset 44 44 36 'Square44x44Logo.png'
  New-EpsilonAsset 150 150 112 'Square150x150Logo.png'
  New-EpsilonAsset 310 150 116 'Wide310x150Logo.png'
  New-EpsilonAsset 50 50 40 'StoreLogo.png'
}
finally {
  $source.Dispose()
  $icon.Dispose()
}

Write-Host "MSIX assets generated in $OutputDirectory" -ForegroundColor Green
