# Generates release\esi.ico — the icon the Start Menu and Desktop shortcuts point at.
# Inno needs a real .ico; there was none in the tree, so every shortcut shipped with the
# default console icon (an empty `IconLocation`), which is what "no icon" meant to a user.
#
# Drawn rather than checked in as a binary so the mark can be regenerated at any size and so
# the repository does not carry an opaque blob nobody can edit.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # The app's own wordmark: a vertical accent bar beside a word, on near-black.
    $void = [int]($size * 0.20)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x12, 0x20))
    $radius = [int]($size * 0.18)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bg, $path)

    # A monitor, because that is what this program controls.
    $cx = [int]($size * 0.5)
    $cy = [int]($size * 0.46)
    $hw = [int]($size * 0.30)
    $hh = [int]($size * 0.22)
    $screen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x22, 0x33, 0x4E))
    $g.FillRectangle($screen, $cx - $hw, $cy - $hh, $hw * 2, $hh * 2)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x5F, 0xE3, 0xD1), [Math]::Max(1.0, $size / 64.0))
    $g.DrawRectangle($pen, $cx - $hw, $cy - $hh, $hw * 2, $hh * 2)
    $stand = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x5F, 0xE3, 0xD1))
    $g.FillRectangle($stand, $cx - [int]($hw * 0.35), $cy + $hh, [int]($hw * 0.7), [int]($size * 0.07))
    $g.FillRectangle($stand, $cx - [int]($hw * 0.6), $cy + $hh + [int]($size * 0.07), [int]($hw * 1.2), [int]($size * 0.05))

    # The accent bar down the left edge, same teal as the accent in the app.
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x5F, 0xE3, 0xD1))
    $g.FillRectangle($accent, [int]($size * 0.13), [int]($size * 0.18), [int]($size * 0.055), [int]($size * 0.64))

    $pen.Dispose(); $stand.Dispose(); $screen.Dispose(); $accent.Dispose(); $bg.Dispose(); $path.Dispose()
    $g.Dispose()
    return $bmp
}

# Let GDI+ write the container. A hand-assembled multi-size ICO with PNG-compressed entries
# was tried first and produced a file GDI+ itself would not open, which is not a risk worth
# taking for a shortcut icon. A single 256px image scales down for every other size.
$big = New-Mark 256
$hIcon = $big.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$target = 'E:\on off pc\release\esi.ico'
$fs = [System.IO.File]::Create($target)
try { $icon.Save($fs) } finally { $fs.Dispose() }
$icon.Dispose(); $big.Dispose()

"wrote $target ({0:N0} bytes)" -f (Get-Item $target).Length