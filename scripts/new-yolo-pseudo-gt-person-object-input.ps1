param(
    [string]$VideoPath = "",
    [Parameter(Mandatory = $true)]
    [string]$Frames,
    [string]$OutputDir = ".tmp\yolo-pseudo-gt\person-object-input",
    [int]$MaxFrames = 900,
    [switch]$AllowLargeFrameSet,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [int]$ScaleWidth = 0,
    [switch]$SkipImageExtraction,
    [string]$ExternalCommand = "",
    [string]$ExternalArgumentsTemplate = "",
    [string]$ExternalOutputCsv = "",
    [ValidateSet("Frame", "ScaledFrame")]
    [string]$ExternalOutputCoordinateSpace = "Frame",
    [int]$ExternalTimeoutSeconds = 180
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

function Stop-ProcessTree {
    param([int]$ProcessId)

    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction Stop)
        foreach ($child in $children) {
            Stop-ProcessTree -ProcessId ([int]$child.ProcessId)
        }
    }
    catch {
        Write-Warning "Failed to enumerate child processes for ${ProcessId}: $($_.Exception.Message)"
    }

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Warning "Failed to stop process ${ProcessId}: $($_.Exception.Message)"
    }
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
                Stop-ProcessTree -ProcessId $process.Id

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

function Get-VideoFrameSize {
    param([string]$SourceVideo)

    if ([string]::IsNullOrWhiteSpace($SourceVideo) -or -not (Test-Path $SourceVideo)) {
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

function Assert-ExternalPersonObjectCsv {
    param(
        [string]$Path,
        [object[]]$ManifestRows
    )

    $manifestFrames = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($row in $ManifestRows) {
        [void]$manifestFrames.Add([int]$row.frame)
    }

    $rows = @(Import-Csv $Path)
    $index = 0
    foreach ($row in $rows) {
        $frame = Read-RequiredIntValue $row @("frame", "Frame") "person-object" $index
        if (-not $manifestFrames.Contains($frame)) {
            throw "person-object CSV row $index references frame $frame outside the manifest."
        }

        $x = Read-RequiredDoubleValue $row @("x", "X") "person-object" $index
        $y = Read-RequiredDoubleValue $row @("y", "Y") "person-object" $index
        $width = Read-RequiredDoubleValue $row @("w", "W", "width", "Width") "person-object" $index
        $height = Read-RequiredDoubleValue $row @("h", "H", "height", "Height") "person-object" $index
        if ($width -le 0 -or $height -le 0) {
            throw "person-object CSV row $index has non-positive geometry: w=$width, h=$height"
        }

        $manifestFrame = @($ManifestRows | Where-Object { [int]$_.frame -eq $frame })[0]
        $frameWidth = [double]::Parse([string]$manifestFrame.frameWidth, [System.Globalization.CultureInfo]::InvariantCulture)
        $frameHeight = [double]::Parse([string]$manifestFrame.frameHeight, [System.Globalization.CultureInfo]::InvariantCulture)
        $centerX = $x + ($width / 2.0)
        $centerY = $y + ($height / 2.0)
        if ($centerX -lt 0 -or $centerX -gt $frameWidth -or
            $centerY -lt 0 -or $centerY -gt $frameHeight) {
            throw "person-object CSV row $index is outside the manifest frame bounds for frame $frame."
        }

        [void](Read-RequiredDoubleValue $row @("confidence", "conf", "Confidence") "person-object" $index)
        $index++
    }
}

function Format-DoubleValue {
    param([double]$Value)

    return $Value.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ScaledFrameHeight {
    param(
        [int]$SourceWidth,
        [int]$SourceHeight,
        [int]$TargetWidth
    )

    if ($TargetWidth -le 0) {
        return $SourceHeight
    }

    $height = [Math]::Max(1, [int][Math]::Round($SourceHeight * ($TargetWidth / [double]$SourceWidth)))
    if ($height -gt 1 -and ($height % 2) -ne 0) {
        $height += 1
    }

    return $height
}

function Convert-ExternalPersonObjectCsvCoordinateSpace {
    param(
        [string]$Path,
        [object[]]$ManifestRows,
        [string]$CoordinateSpace
    )

    if ($CoordinateSpace -eq "Frame") {
        return
    }

    $manifestByFrame = @{}
    foreach ($row in $ManifestRows) {
        $manifestByFrame[[int]$row.frame] = $row
    }

    $normalizedRows = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($row in (Import-Csv $Path)) {
        $frame = Read-RequiredIntValue $row @("frame", "Frame") "person-object" $index
        if (-not $manifestByFrame.ContainsKey($frame)) {
            throw "person-object CSV row $index references frame $frame outside the manifest."
        }

        $manifestFrame = $manifestByFrame[$frame]
        $frameWidth = [double]::Parse([string]$manifestFrame.frameWidth, [System.Globalization.CultureInfo]::InvariantCulture)
        $frameHeight = [double]::Parse([string]$manifestFrame.frameHeight, [System.Globalization.CultureInfo]::InvariantCulture)
        $scaledFrameWidth = [double]::Parse([string]$manifestFrame.scaledFrameWidth, [System.Globalization.CultureInfo]::InvariantCulture)
        $scaledFrameHeight = [double]::Parse([string]$manifestFrame.scaledFrameHeight, [System.Globalization.CultureInfo]::InvariantCulture)

        $x = Read-RequiredDoubleValue $row @("x", "X") "person-object" $index
        $y = Read-RequiredDoubleValue $row @("y", "Y") "person-object" $index
        $width = Read-RequiredDoubleValue $row @("w", "W", "width", "Width") "person-object" $index
        $height = Read-RequiredDoubleValue $row @("h", "H", "height", "Height") "person-object" $index

        $scaleX = $frameWidth / [Math]::Max(1.0, $scaledFrameWidth)
        $scaleY = $frameHeight / [Math]::Max(1.0, $scaledFrameHeight)

        $output = [ordered]@{}
        foreach ($property in $row.PSObject.Properties) {
            $name = $property.Name
            if ($name -match '^(x|y|w|h|width|height)$') {
                continue
            }

            $output[$name] = $property.Value
        }

        $output["x"] = Format-DoubleValue ($x * $scaleX)
        $output["y"] = Format-DoubleValue ($y * $scaleY)
        $output["w"] = Format-DoubleValue ($width * $scaleX)
        $output["h"] = Format-DoubleValue ($height * $scaleY)
        $output["inputCoordinateSpace"] = $CoordinateSpace
        $output["normalizedCoordinateSpace"] = "Frame"
        $normalizedRows.Add([pscustomobject]$output) | Out-Null
        $index++
    }

    $normalizedRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $Path
}

function Invoke-FfmpegFrameExtraction {
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
        $filter = "select='eq(n\,$($row.frame))'"
        if ($row.scaleWidth -gt 0) {
            $filter += ",scale=$($row.scaleWidth):-2"
        }

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
            $row.frameImagePath
        )

        if ($null -ne $ffmpeg) {
            $result = Invoke-NativeCapture -FilePath $ffmpeg.Source -Arguments $args
            if ($result.ExitCode -eq 0) {
                continue
            }

            if ($null -eq $wsl) {
                throw "ffmpeg person/object frame extraction failed: $($result.Output -join ' ')"
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
            (Convert-ToWslPath $row.frameImagePath)
        )
        $wslResult = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArgs
        if ($wslResult.ExitCode -ne 0) {
            throw "WSL ffmpeg person/object frame extraction failed: $($wslResult.Output -join ' ')"
        }
    }
}

function Invoke-ExternalModel {
    param(
        [string]$Command,
        [string]$ArgumentsTemplate,
        [string]$ManifestPath,
        [string]$FramesDir,
        [string]$OutputPath,
        [string]$Video,
        [string]$OutputCoordinateSpace,
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
        Replace("{framesDir}", $FramesDir).
        Replace("{output}", $OutputPath).
        Replace("{video}", $Video)

    $args = Split-ArgumentString $argumentText
    $result = Invoke-NativeCapture -FilePath $resolvedCommand -Arguments $args -TimeoutSeconds $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw "External high-precision person/object command failed with exit code $($result.ExitCode): $($result.Output -join ' ')"
    }

    if (-not (Test-Path $OutputPath)) {
        throw "External high-precision person/object command did not create output CSV: $OutputPath"
    }

    $manifestRows = @(Import-Csv $ManifestPath)
    Convert-ExternalPersonObjectCsvCoordinateSpace `
        -Path $OutputPath `
        -ManifestRows $manifestRows `
        -CoordinateSpace $OutputCoordinateSpace
    Assert-ExternalPersonObjectCsv -Path $OutputPath -ManifestRows $manifestRows
}

if ($ScaleWidth -lt 0) {
    throw "ScaleWidth must be 0 or greater."
}

if ($MaxFrames -lt 1) {
    throw "MaxFrames must be at least 1."
}

$framesToUse = @(Get-FrameList $Frames)
if ($framesToUse.Count -eq 0) {
    throw "At least one frame is required."
}

if (-not $AllowLargeFrameSet.IsPresent -and $framesToUse.Count -gt $MaxFrames) {
    throw "Pseudo-GT person/object input is limited to $MaxFrames frames by default. Use a <=30s problem span, a smaller frame list, or pass -AllowLargeFrameSet only for an intentional local audit."
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
$framesDir = Join-Path $resolvedOutputDir "frames"
$manifestPath = Join-Path $resolvedOutputDir "person-object-manifest.csv"
$summaryPath = Join-Path $resolvedOutputDir "person-object-input-summary.md"
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
New-Item -ItemType Directory -Force -Path $framesDir | Out-Null

$manifestRows = [System.Collections.Generic.List[object]]::new()
foreach ($frame in $framesToUse) {
    $frameImagePath = Join-Path $framesDir ("frame-{0:D06}.png" -f $frame)
    $scaledFrameWidth = if ($ScaleWidth -gt 0) { $ScaleWidth } else { $FrameWidth }
    $scaledFrameHeight = Get-ScaledFrameHeight -SourceWidth $FrameWidth -SourceHeight $FrameHeight -TargetWidth $ScaleWidth
    $manifestRows.Add([pscustomobject]@{
            frame = $frame
            frameWidth = $FrameWidth
            frameHeight = $FrameHeight
            scaleWidth = $ScaleWidth
            coordinateSpace = "original-frame"
            scaledFrameWidth = $scaledFrameWidth
            scaledFrameHeight = $scaledFrameHeight
            inputCoordinateSpace = if ($ScaleWidth -gt 0) { "scaled-frame" } else { "original-frame" }
            frameImagePath = $frameImagePath
            frameRelativePath = Get-RelativePathCompat $resolvedOutputDir $frameImagePath
        }) | Out-Null
}

$manifestRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $manifestPath

if (-not $SkipImageExtraction.IsPresent) {
    Invoke-FfmpegFrameExtraction -SourceVideo $resolvedVideoPath -Rows @($manifestRows)
}

if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Invoke-ExternalModel `
        -Command $ExternalCommand `
        -ArgumentsTemplate $ExternalArgumentsTemplate `
        -ManifestPath $manifestPath `
        -FramesDir $framesDir `
        -OutputPath $resolvedExternalOutputCsv `
        -Video $resolvedVideoPath `
        -OutputCoordinateSpace $ExternalOutputCoordinateSpace `
        -TimeoutSeconds $ExternalTimeoutSeconds
}

$summary = @(
    "# YOLO Pseudo-GT Person/Object Input",
    "",
    "This is test-only input for local high-precision person/object models. It is not part of the app runtime path and cannot decide face/nonface/miss labels by itself.",
    "",
    "- frames=$($framesToUse -join ',')",
    "- frameCount=$($framesToUse.Count)",
    "- maxFrames=$MaxFrames",
    "- largeFrameSetAllowed=$($AllowLargeFrameSet.IsPresent)",
    "- frameWidth=$FrameWidth",
    "- frameHeight=$FrameHeight",
    "- scaleWidth=$ScaleWidth",
    "- framesPrepared=$($manifestRows.Count)",
    "- manifest=$manifestPath",
    "- imageExtractionSkipped=$($SkipImageExtraction.IsPresent)",
    "- externalCommandUsed=$(-not [string]::IsNullOrWhiteSpace($ExternalCommand))",
    "- externalOutputCsv=$ExternalOutputCsv",
    "- externalOutputCoordinateSpace=$ExternalOutputCoordinateSpace",
    "",
    "External model output should be converted to PersonObjectCsv fields before running new-yolo-pseudo-gt-evidence.ps1: frame, detectionId, x, y, w, h, confidence.",
    "If external output uses ScaledFrame coordinates, this script normalizes it to original frame coordinates before validation.",
    "Every external person/object detection center must stay inside the original-frame manifest bounds for that frame.",
    "Person/object detections are auxiliary evidence only and must not be used as face ground truth without review CSV labels."
)

$summary | Set-Content -Encoding UTF8 -Path $summaryPath

Write-Host "[YoloPseudoGtPersonObjectInput] frames=$($manifestRows.Count), manifest=$manifestPath, skipImageExtraction=$($SkipImageExtraction.IsPresent)"
if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Write-Host "[YoloPseudoGtPersonObjectInput] externalOutput=$ExternalOutputCsv"
}
