param(
    [string]$VideoPath = "",
    [Parameter(Mandatory = $true)]
    [string]$Frames,
    [string]$OutputDir = ".tmp\yolo-pseudo-gt\tile-input",
    [int]$MaxFrames = 900,
    [switch]$AllowLargeFrameSet,
    [int]$TileColumns = 2,
    [int]$TileRows = 2,
    [double]$TileOverlapRatio = 0.20,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [switch]$SkipImageExtraction,
    [string]$ExternalCommand = "",
    [string]$ExternalArgumentsTemplate = "",
    [string]$ExternalOutputCsv = "",
    [int]$ExternalTimeoutSeconds = 0
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Invoke-NativeCapture {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 0
    )

    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try {
                    $process.Kill()
                }
                catch {
                    Write-Warning "Failed to kill timed out process: $($_.Exception.Message)"
                }

                $stdout = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
                $stderr = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
                return [pscustomobject]@{
                    ExitCode = -1
                    Output = @($stdout + $stderr + "Timed out after $TimeoutSeconds seconds.")
                }
            }
        }
        else {
            $process.WaitForExit()
        }

        $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
        $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
        $process.Refresh()
        $exitCode = if ($null -eq $process.ExitCode) { 0 } else { $process.ExitCode }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($stdoutText + $stderrText)
        }
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $stdoutPath, $stderrPath
    }
}

function Get-FrameList {
    param([string]$Text)

    $values = [System.Collections.Generic.SortedSet[int]]::new()
    foreach ($token in @($Text -split ",")) {
        $trimmed = $token.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($trimmed -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            if ($end -lt $start) {
                throw "Invalid frame range: $trimmed"
            }

            for ($frame = $start; $frame -le $end; $frame++) {
                [void]$values.Add($frame)
            }
        }
        elseif ($trimmed -match '^\d+$') {
            [void]$values.Add([int]$trimmed)
        }
        else {
            throw "Invalid frame token: $trimmed"
        }
    }

    return @($values)
}

function Get-TileStarts {
    param(
        [int]$Length,
        [int]$Count,
        [double]$OverlapRatio
    )

    if ($Count -le 1) {
        return @(0)
    }

    $tileLength = [Math]::Ceiling($Length / [double](1 + ($Count - 1) * (1.0 - $OverlapRatio)))
    $tileLength = [Math]::Max(1, [int]$tileLength)
    $step = [Math]::Max(1, [int][Math]::Round($tileLength * (1.0 - $OverlapRatio)))

    $starts = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $Count; $i++) {
        $start = $i * $step
        if ($i -eq $Count - 1 -or $start + $tileLength -gt $Length) {
            $start = [Math]::Max(0, $Length - $tileLength)
        }

        if (-not $starts.Contains($start)) {
            $starts.Add($start) | Out-Null
        }
    }

    return @($starts)
}

function Split-ArgumentString {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $matches = [regex]::Matches($Text, '"([^"]*)"|''([^'']*)''|(\S+)')
    $args = [System.Collections.Generic.List[string]]::new()
    foreach ($match in $matches) {
        if ($match.Groups[1].Success) {
            $args.Add($match.Groups[1].Value) | Out-Null
        }
        elseif ($match.Groups[2].Success) {
            $args.Add($match.Groups[2].Value) | Out-Null
        }
        elseif ($match.Groups[3].Success) {
            $args.Add($match.Groups[3].Value) | Out-Null
        }
    }

    return @($args)
}

function Get-RequiredCsvValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }

    throw "$Source CSV row $Index missing required value: $([string]::Join('/', $Names))"
}

function Read-RequiredDoubleValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    $value = Get-RequiredCsvValue $Row $Names $Source $Index
    $parsed = 0.0
    if ([double]::TryParse(
            [string]$value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    throw "$Source CSV row $Index has invalid numeric value for $([string]::Join('/', $Names)): $value"
}

function Read-RequiredIntValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    $value = Get-RequiredCsvValue $Row $Names $Source $Index
    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    throw "$Source CSV row $Index has invalid integer value for $([string]::Join('/', $Names)): $value"
}

function Get-OptionalCsvValue {
    param(
        [object]$Row,
        [string[]]$Names
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }

    return ""
}

function Assert-ExternalTileFaceCsv {
    param(
        [string]$Path,
        [object[]]$ManifestRows
    )

    $manifestKeys = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $ManifestRows) {
        [void]$manifestKeys.Add("$([int]$row.frame):$([int]$row.tileIndex)")
    }

    $rows = @(Import-Csv $Path)
    $index = 0
    foreach ($row in $rows) {
        $frame = Read-RequiredIntValue $row @("frame", "Frame") "tile-face" $index
        $tileIndexValue = Get-OptionalCsvValue $row @("tileIndex", "sourceTileIndex", "manifestTileIndex", "TileIndex")
        if ([string]::IsNullOrWhiteSpace($tileIndexValue)) {
            throw "tile-face CSV row $index missing required value: tileIndex/sourceTileIndex/manifestTileIndex"
        }

        $tileIndex = 0
        if (-not [int]::TryParse($tileIndexValue, [ref]$tileIndex)) {
            throw "tile-face CSV row $index has invalid integer value for tileIndex/sourceTileIndex/manifestTileIndex: $tileIndexValue"
        }

        if (-not $manifestKeys.Contains("$($frame):$tileIndex")) {
            throw "tile-face CSV row $index references frame/tile $frame/$tileIndex outside the manifest."
        }

        $x = Read-RequiredDoubleValue $row @("x", "X") "tile-face" $index
        $y = Read-RequiredDoubleValue $row @("y", "Y") "tile-face" $index
        $width = Read-RequiredDoubleValue $row @("w", "W", "width", "Width") "tile-face" $index
        $height = Read-RequiredDoubleValue $row @("h", "H", "height", "Height") "tile-face" $index
        if ($width -le 0 -or $height -le 0) {
            throw "tile-face CSV row $index has non-positive geometry: w=$width, h=$height"
        }

        $manifestTile = @($ManifestRows | Where-Object { [int]$_.frame -eq $frame -and [int]$_.tileIndex -eq $tileIndex })[0]
        $tileX = [double]::Parse([string]$manifestTile.tileX, [System.Globalization.CultureInfo]::InvariantCulture)
        $tileY = [double]::Parse([string]$manifestTile.tileY, [System.Globalization.CultureInfo]::InvariantCulture)
        $tileW = [double]::Parse([string]$manifestTile.tileW, [System.Globalization.CultureInfo]::InvariantCulture)
        $tileH = [double]::Parse([string]$manifestTile.tileH, [System.Globalization.CultureInfo]::InvariantCulture)
        $centerX = $x + ($width / 2.0)
        $centerY = $y + ($height / 2.0)
        if ($centerX -lt $tileX -or $centerX -gt ($tileX + $tileW) -or
            $centerY -lt $tileY -or $centerY -gt ($tileY + $tileH)) {
            throw "tile-face CSV row $index is outside the manifest tile for frame/tile $frame/$tileIndex."
        }

        [void](Read-RequiredDoubleValue $row @("confidence", "conf", "tileFaceConfidence", "Confidence") "tile-face" $index)
        $supportCount = Read-RequiredIntValue $row @("tileSupportCount", "supportCount", "TileSupportCount") "tile-face" $index
        if ($supportCount -lt 1) {
            throw "tile-face CSV row $index must have tileSupportCount >= 1."
        }

        $index++
    }
}

function Invoke-FfmpegTileExtraction {
    param(
        [string]$SourceVideo,
        [object[]]$Rows
    )

    if ([string]::IsNullOrWhiteSpace($SourceVideo)) {
        throw "VideoPath is required unless -SkipImageExtraction is used."
    }

    if (-not (Test-Path $SourceVideo)) {
        throw "VideoPath not found: $SourceVideo"
    }

    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue

    if ($null -eq $ffmpeg -and $null -eq $wsl) {
        throw "ffmpeg was not found in Windows PATH and wsl.exe was not available."
    }

    foreach ($row in $Rows) {
        $filter = "select='eq(n\,$($row.frame))',crop=$($row.tileW):$($row.tileH):$($row.tileX):$($row.tileY),scale=$($row.tileW):$($row.tileH)"
        $args = @(
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            $SourceVideo,
            "-vf",
            $filter,
            "-frames:v",
            "1",
            $row.tileImagePath
        )

        if ($null -ne $ffmpeg) {
            $result = Invoke-NativeCapture -FilePath $ffmpeg.Source -Arguments $args
            if ($result.ExitCode -eq 0) {
                continue
            }

            if ($null -eq $wsl) {
                throw "ffmpeg tile extraction failed: $($result.Output -join ' ')"
            }
        }

        $wslArgs = @(
            "--exec",
            "ffmpeg",
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            (Convert-ToWslPath $SourceVideo),
            "-vf",
            $filter,
            "-frames:v",
            "1",
            (Convert-ToWslPath $row.tileImagePath)
        )
        $wslResult = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArgs
        if ($wslResult.ExitCode -ne 0) {
            throw "WSL ffmpeg tile extraction failed: $($wslResult.Output -join ' ')"
        }
    }
}

function Invoke-ExternalModel {
    param(
        [string]$Command,
        [string]$ArgumentsTemplate,
        [string]$ManifestPath,
        [string]$TilesDir,
        [string]$OutputPath,
        [string]$Video,
        [int]$TimeoutSeconds
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw "ExternalOutputCsv is required when ExternalCommand is used."
    }

    $resolvedCommand = Resolve-RepoPath $Command
    if (-not (Test-Path $resolvedCommand)) {
        $commandInfo = Get-Command $Command -ErrorAction SilentlyContinue
        if ($null -eq $commandInfo) {
            throw "ExternalCommand not found: $Command"
        }

        $resolvedCommand = $commandInfo.Source
    }

    $template = if ([string]::IsNullOrWhiteSpace($ArgumentsTemplate)) {
        "-ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
    }
    else {
        $ArgumentsTemplate
    }

    $argumentText = $template.
        Replace("{manifest}", $ManifestPath).
        Replace("{tilesDir}", $TilesDir).
        Replace("{output}", $OutputPath).
        Replace("{video}", $Video)

    $args = Split-ArgumentString $argumentText
    $result = Invoke-NativeCapture -FilePath $resolvedCommand -Arguments $args -TimeoutSeconds $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw "External high-precision model command failed with exit code $($result.ExitCode): $($result.Output -join ' ')"
    }

    if (-not (Test-Path $OutputPath)) {
        throw "External high-precision model command did not create output CSV: $OutputPath"
    }

    Assert-ExternalTileFaceCsv -Path $OutputPath -ManifestRows @(Import-Csv $ManifestPath)
}

function Get-VideoFrameSize {
    param([string]$SourceVideo)

    if ([string]::IsNullOrWhiteSpace($SourceVideo)) {
        return $null
    }

    if (-not (Test-Path $SourceVideo)) {
        return $null
    }

    $ffprobe = Get-Command ffprobe -ErrorAction SilentlyContinue
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    $arguments = @(
        "-v",
        "error",
        "-select_streams",
        "v:0",
        "-show_entries",
        "stream=width,height",
        "-of",
        "csv=s=x:p=0",
        $SourceVideo
    )

    if ($null -ne $ffprobe) {
        $result = Invoke-NativeCapture -FilePath $ffprobe.Source -Arguments $arguments
        if ($result.ExitCode -eq 0 -and ($result.Output -join "`n") -match '(\d+)x(\d+)') {
            return [pscustomobject]@{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
        }
    }

    if ($null -ne $wsl) {
        $wslArguments = @(
            "--exec",
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=s=x:p=0",
            (Convert-ToWslPath $SourceVideo)
        )
        $result = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArguments
        if ($result.ExitCode -eq 0 -and ($result.Output -join "`n") -match '(\d+)x(\d+)') {
            return [pscustomobject]@{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
        }
    }

    return $null
}

function Get-RelativePathCompat {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseUriPath = if ($BasePath.EndsWith([IO.Path]::DirectorySeparatorChar)) { $BasePath } else { $BasePath + [IO.Path]::DirectorySeparatorChar }
    $baseUri = [Uri]::new($baseUriPath)
    $targetUri = [Uri]::new($TargetPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

if ($TileColumns -lt 1 -or $TileRows -lt 1) {
    throw "TileColumns and TileRows must be at least 1."
}

if ($TileOverlapRatio -lt 0.0 -or $TileOverlapRatio -ge 0.90) {
    throw "TileOverlapRatio must be between 0.0 and 0.90."
}

if ($MaxFrames -lt 1) {
    throw "MaxFrames must be at least 1."
}

$framesToUse = @(Get-FrameList $Frames)
if ($framesToUse.Count -eq 0) {
    throw "At least one frame is required."
}

if (-not $AllowLargeFrameSet.IsPresent -and $framesToUse.Count -gt $MaxFrames) {
    throw "Pseudo-GT tile input is limited to $MaxFrames frames by default. Use a <=30s problem span, a smaller frame list, or pass -AllowLargeFrameSet only for an intentional local audit."
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
$tilesDir = Join-Path $resolvedOutputDir "tiles"
$manifestPath = Join-Path $resolvedOutputDir "tile-manifest.csv"
$summaryPath = Join-Path $resolvedOutputDir "tile-input-summary.md"
$resolvedVideoPath = Resolve-RepoPath $VideoPath
$resolvedExternalOutputCsv = Resolve-RepoPath $ExternalOutputCsv

if ($FrameWidth -lt 1 -or $FrameHeight -lt 1) {
    $frameSize = Get-VideoFrameSize -SourceVideo $resolvedVideoPath
    if ($null -eq $frameSize) {
        throw "FrameWidth and FrameHeight must be provided when video dimensions cannot be read."
    }

    $FrameWidth = $frameSize.Width
    $FrameHeight = $frameSize.Height
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $tilesDir | Out-Null

$xStarts = @(Get-TileStarts -Length $FrameWidth -Count $TileColumns -OverlapRatio $TileOverlapRatio)
$yStarts = @(Get-TileStarts -Length $FrameHeight -Count $TileRows -OverlapRatio $TileOverlapRatio)
$tileWidth = [Math]::Min($FrameWidth, [Math]::Ceiling($FrameWidth / [double](1 + ($TileColumns - 1) * (1.0 - $TileOverlapRatio))))
$tileHeight = [Math]::Min($FrameHeight, [Math]::Ceiling($FrameHeight / [double](1 + ($TileRows - 1) * (1.0 - $TileOverlapRatio))))

$manifestRows = [System.Collections.Generic.List[object]]::new()
foreach ($frame in $framesToUse) {
    $tileIndex = 0
    foreach ($y in $yStarts) {
        foreach ($x in $xStarts) {
            $w = [Math]::Min([int]$tileWidth, $FrameWidth - $x)
            $h = [Math]::Min([int]$tileHeight, $FrameHeight - $y)
            $tileImagePath = Join-Path $tilesDir ("frame-{0:D06}-tile-{1:D02}.png" -f $frame, $tileIndex)
            $manifestRows.Add([pscustomobject]@{
                    frame = $frame
                    tileIndex = $tileIndex
                    tileX = $x
                    tileY = $y
                    tileW = $w
                    tileH = $h
                    frameWidth = $FrameWidth
                    frameHeight = $FrameHeight
                    tileImagePath = $tileImagePath
                    tileRelativePath = Get-RelativePathCompat $resolvedOutputDir $tileImagePath
                }) | Out-Null
            $tileIndex++
        }
    }
}

$manifestRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $manifestPath

if (-not $SkipImageExtraction.IsPresent) {
    Invoke-FfmpegTileExtraction -SourceVideo $resolvedVideoPath -Rows @($manifestRows)
}

if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Invoke-ExternalModel `
        -Command $ExternalCommand `
        -ArgumentsTemplate $ExternalArgumentsTemplate `
        -ManifestPath $manifestPath `
        -TilesDir $tilesDir `
        -OutputPath $resolvedExternalOutputCsv `
        -Video $resolvedVideoPath `
        -TimeoutSeconds $ExternalTimeoutSeconds
}

$summary = @(
    "# YOLO Pseudo-GT Tile Input",
    "",
    "This is test-only input for local high-precision face/person/object models. It is not part of the app runtime path.",
    "",
    "- frames=$($framesToUse -join ',')",
    "- frameCount=$($framesToUse.Count)",
    "- maxFrames=$MaxFrames",
    "- largeFrameSetAllowed=$($AllowLargeFrameSet.IsPresent)",
    "- tileColumns=$TileColumns",
    "- tileRows=$TileRows",
    "- tileOverlapRatio=$($TileOverlapRatio.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture))",
    "- frameWidth=$FrameWidth",
    "- frameHeight=$FrameHeight",
    "- tiles=$($manifestRows.Count)",
    "- manifest=$manifestPath",
    "- imageExtractionSkipped=$($SkipImageExtraction.IsPresent)",
    "- externalCommandUsed=$(-not [string]::IsNullOrWhiteSpace($ExternalCommand))",
    "- externalOutputCsv=$ExternalOutputCsv",
    "",
    "External model output should be converted to TileFaceCsv, FaceVerificationCsv, or PersonObjectCsv before running new-yolo-pseudo-gt-evidence.ps1.",
    "External tile-face output from this manifest must include tileIndex/sourceTileIndex/manifestTileIndex. The detection center must stay inside that manifest tile."
)

$summary | Set-Content -Encoding UTF8 -Path $summaryPath

Write-Host "[YoloPseudoGtTileInput] frames=$($framesToUse.Count), tiles=$($manifestRows.Count), manifest=$manifestPath, skipImageExtraction=$($SkipImageExtraction.IsPresent)"
if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Write-Host "[YoloPseudoGtTileInput] externalOutput=$ExternalOutputCsv"
}
