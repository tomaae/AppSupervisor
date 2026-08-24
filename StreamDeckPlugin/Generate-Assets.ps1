$ErrorActionPreference = "Stop"

$pluginRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $pluginRoot ".."))
$sourceIcon = Join-Path $workspaceRoot "AppSupervisor\App.ico"
$outputDirectory = Join-Path $pluginRoot `
    "com.tomaae.appsupervisor.sdPlugin\static\imgs\plugin"

Add-Type -AssemblyName System.Drawing.Common
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

function Write-ScaledIcon([int]$size, [string]$fileName) {
    $stream = [IO.File]::OpenRead($sourceIcon)
    try {
        $icon = [Drawing.Icon]::new($stream, 128, 128)
        try {
            $source = $icon.ToBitmap()
            try {
                $output = [Drawing.Bitmap]::new(
                    $size,
                    $size,
                    [Drawing.Imaging.PixelFormat]::Format32bppArgb
                )
                try {
                    $graphics = [Drawing.Graphics]::FromImage($output)
                    try {
                        $graphics.Clear([Drawing.Color]::Transparent)
                        $graphics.CompositingQuality =
                            [Drawing.Drawing2D.CompositingQuality]::HighQuality
                        $graphics.InterpolationMode =
                            [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                        $graphics.PixelOffsetMode =
                            [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                        $graphics.DrawImage($source, 0, 0, $size, $size)
                    }
                    finally {
                        $graphics.Dispose()
                    }

                    $output.Save(
                        (Join-Path $outputDirectory $fileName),
                        [Drawing.Imaging.ImageFormat]::Png
                    )
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $source.Dispose()
            }
        }
        finally {
            $icon.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

Write-ScaledIcon 256 "appsupervisor.png"
Write-ScaledIcon 512 "appsupervisor@2x.png"
