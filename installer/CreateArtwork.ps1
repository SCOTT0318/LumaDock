$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$artworkDirectory = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Path $artworkDirectory -Force | Out-Null
$banner = New-Object System.Drawing.Bitmap 492,942
$canvas = [System.Drawing.Graphics]::FromImage($banner)
$canvas.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$canvas.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$dark = [System.Drawing.ColorTranslator]::FromHtml('#14212C')
$mint = [System.Drawing.ColorTranslator]::FromHtml('#85E0CD')
$canvas.Clear($dark)
$mintBrush = New-Object System.Drawing.SolidBrush $mint
$whiteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#EAF7F7'))
$mutedBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#8CA6B7'))
$cardBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#1D3442'))
$linePen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#2C4857')),2
$monitorPen = New-Object System.Drawing.Pen $mint,5
$labelFont = New-Object System.Drawing.Font 'Segoe UI',10,([System.Drawing.FontStyle]::Bold)
$brandFont = New-Object System.Drawing.Font 'Segoe UI',27,([System.Drawing.FontStyle]::Bold)
$bodyFont = New-Object System.Drawing.Font 'Malgun Gothic',12,([System.Drawing.FontStyle]::Regular)
$numberFont = New-Object System.Drawing.Font 'Segoe UI',40,([System.Drawing.FontStyle]::Regular)
try {
    $canvas.DrawString('WINDOWS', $labelFont, $mintBrush, 46, 54)
    $canvas.DrawString('LumaDock', $brandFont, $whiteBrush, 43, 102)
    $canvas.DrawLine($linePen,48,242,444,242)
    $canvas.FillEllipse($cardBrush,100,380,290,290)
    $canvas.DrawEllipse($linePen,80,360,330,330)
    $canvas.DrawEllipse($linePen,60,340,370,370)
    $canvas.FillRectangle($cardBrush,115,435,262,161)
    $canvas.DrawRectangle($monitorPen,115,435,262,161)
    $canvas.DrawLine($monitorPen,246,599,246,626)
    $canvas.DrawLine($monitorPen,203,633,289,633)
    $canvas.DrawString('72', $numberFont, $whiteBrush, 153, 470)
    $canvas.DrawString('%', $bodyFont, $mintBrush, 264, 517)
    $canvas.DrawLine($linePen,144,568,349,568)
    $canvas.DrawLine($monitorPen,144,568,291,568)
    $canvas.FillEllipse($mintBrush,282,559,18,18)
    $canvas.DrawString('0.0.1 BETA', $labelFont, $mutedBrush, 47, 816)
    $banner.Save((Join-Path $artworkDirectory 'wizard.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $canvas.Dispose(); $banner.Dispose(); $mintBrush.Dispose(); $whiteBrush.Dispose(); $mutedBrush.Dispose(); $cardBrush.Dispose(); $linePen.Dispose(); $monitorPen.Dispose(); $labelFont.Dispose(); $brandFont.Dispose(); $bodyFont.Dispose(); $numberFont.Dispose()
}
$small = New-Object System.Drawing.Bitmap 96,96
$smallCanvas = [System.Drawing.Graphics]::FromImage($small)
$smallCanvas.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$smallCanvas.Clear([System.Drawing.Color]::Transparent)
$smallPen = New-Object System.Drawing.Pen $mint,5
try {
    $smallCanvas.DrawRectangle($smallPen,14,18,68,45)
    $smallCanvas.DrawLine($smallPen,48,64,48,77)
    $smallCanvas.DrawLine($smallPen,31,80,65,80)
    $small.Save((Join-Path $artworkDirectory 'wizard-small.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally { $smallCanvas.Dispose(); $small.Dispose(); $smallPen.Dispose() }
