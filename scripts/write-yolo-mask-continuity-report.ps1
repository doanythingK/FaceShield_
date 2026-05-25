param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [string]$OutputPath = ".tmp\yolo-quality\yolo-mask-continuity-report.md",
    [int]$ShortGapMaxFrames = 3,
    [int]$IsolatedNeighborWindow = 1,
    [double]$LowConfidenceThreshold = 0.38,
    [double]$WeakNonEdgeThreshold = 0.50,
    [double]$UpperFrameCenterYThreshold = 0.12,
    [double]$EdgeMarginRatio = 0.02,
    [double]$FrameAspectRatio = 1.7777777777777777
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Join-Values {
    param([object[]]$Values)

    if ($Values.Count -eq 0) {
        return "none"
    }

    return ($Values -join ",")
}

$resolvedLog = Resolve-RepoPath $LogPath
if (-not (Test-Path $resolvedLog)) {
    throw "Log not found: $resolvedLog"
}

$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$pattern = '\[SmokeDetection\].*label=([^,]+), frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=([0-9.\-]+), conf=([0-9.\-]+), cx=([0-9.\-]+), cy=([0-9.\-]+), areaRatio=([0-9.\-]+)(?:, aspectRatio=([0-9.\-]+))?'
$rows = New-Object System.Collections.Generic.List[object]
foreach ($line in (Get-Content -Path $resolvedLog)) {
    $match = [regex]::Match($line, $pattern)
    if (-not $match.Success) {
        continue
    }

    $rows.Add([pscustomobject]@{
        Label = $match.Groups[1].Value
        Frame = [int]$match.Groups[2].Value
        Index = [int]$match.Groups[3].Value
        X = [double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Y = [double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        W = [double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        H = [double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Confidence = [double]::Parse($match.Groups[9].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        CenterX = [double]::Parse($match.Groups[10].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        CenterY = [double]::Parse($match.Groups[11].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        AreaRatio = [double]::Parse($match.Groups[12].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        AspectRatio = if ($match.Groups[13].Success) {
            [double]::Parse($match.Groups[13].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        } else {
            0.0
        }
    }) | Out-Null
}

$frames = @($rows | Select-Object -ExpandProperty Frame -Unique | Sort-Object)
$rowsByFrame = @{}
foreach ($frame in $frames) {
    $rowsByFrame[[int]$frame] = @($rows | Where-Object { $_.Frame -eq $frame } | Sort-Object Confidence -Descending)
}

function Get-BestRowForFrame {
    param([int]$Frame)

    if (-not $rowsByFrame.ContainsKey($Frame)) {
        return $null
    }

    return @($rowsByFrame[$Frame] | Select-Object -First 1)[0]
}

function Get-CenterShift {
    param(
        [object]$Previous,
        [object]$Next
    )

    if ($null -eq $Previous -or $null -eq $Next) {
        return 0.0
    }

    $dx = $Next.CenterX - $Previous.CenterX
    $dy = $Next.CenterY - $Previous.CenterY
    return [Math]::Sqrt($dx * $dx + $dy * $dy)
}

function Get-AreaChange {
    param(
        [object]$Previous,
        [object]$Next
    )

    if ($null -eq $Previous -or $null -eq $Next) {
        return 0.0
    }

    $a = [Math]::Max(0.000001, [double]$Previous.AreaRatio)
    $b = [Math]::Max(0.000001, [double]$Next.AreaRatio)
    return [Math]::Max($a / $b, $b / $a)
}

function Get-GapReviewHint {
    param(
        [double]$AreaChange,
        [double]$CenterShift,
        [double]$PreviousConfidence,
        [double]$NextConfidence
    )

    if ($AreaChange -ge 4.0 -or $CenterShift -ge 0.20) {
        return "large box jump; review before fill"
    }
    if ($PreviousConfidence -lt 0.38 -or $NextConfidence -lt 0.38) {
        return "weak endpoint; review flicker"
    }

    return "short gap; review flicker"
}

function Test-NormalizedEdgeTouch {
    param(
        [object]$Row,
        [double]$MarginRatio,
        [double]$FrameAspectRatio
    )

    if ($MarginRatio -le 0) {
        return $false
    }

    $dimensions = Get-InferredFrameDimensions $Row
    if ($null -ne $dimensions -and $dimensions.Width -gt 0 -and $dimensions.Height -gt 0) {
        $marginX = $dimensions.Width * $MarginRatio
        $marginY = $dimensions.Height * $MarginRatio
        return $Row.X -le $marginX -or
            $Row.Y -le $marginY -or
            ($Row.X + $Row.W) -ge $dimensions.Width - $marginX -or
            ($Row.Y + $Row.H) -ge $dimensions.Height - $marginY
    }

    if ($FrameAspectRatio -le 0 -or $Row.AspectRatio -le 0 -or $Row.AreaRatio -le 0) {
        return $false
    }

    $widthRatio = [Math]::Sqrt([Math]::Max(0.0, $Row.AreaRatio * $Row.AspectRatio / $FrameAspectRatio))
    $heightRatio = [Math]::Sqrt([Math]::Max(0.0, $Row.AreaRatio * $FrameAspectRatio / $Row.AspectRatio))
    $left = $Row.CenterX - ($widthRatio * 0.5)
    $right = $Row.CenterX + ($widthRatio * 0.5)
    $top = $Row.CenterY - ($heightRatio * 0.5)
    $bottom = $Row.CenterY + ($heightRatio * 0.5)

    return $left -le $MarginRatio -or
        $top -le $MarginRatio -or
        $right -ge 1.0 - $MarginRatio -or
        $bottom -ge 1.0 - $MarginRatio
}

function Get-InferredFrameDimensions {
    param([object]$Row)

    $centerX = [double]$Row.CenterX
    $centerY = [double]$Row.CenterY
    $width = 0.0
    $height = 0.0
    if ($centerX -gt 0) {
        $width = ([double]$Row.X + ([double]$Row.W * 0.5)) / $centerX
    }
    if ($centerY -gt 0) {
        $height = ([double]$Row.Y + ([double]$Row.H * 0.5)) / $centerY
    }

    if ($width -le 0 -or $height -le 0) {
        return $null
    }

    return [pscustomobject]@{
        Width = $width
        Height = $height
    }
}

$shortGaps = New-Object System.Collections.Generic.List[object]
$longGaps = New-Object System.Collections.Generic.List[object]
for ($i = 1; $i -lt $frames.Count; $i++) {
    $previous = [int]$frames[$i - 1]
    $current = [int]$frames[$i]
    $missing = $current - $previous - 1
    if ($missing -le 0) {
        continue
    }

    $previousRow = Get-BestRowForFrame $previous
    $nextRow = Get-BestRowForFrame $current
    $areaChange = Get-AreaChange $previousRow $nextRow
    $centerShift = Get-CenterShift $previousRow $nextRow
    $previousConfidence = if ($null -eq $previousRow) { 0.0 } else { [double]$previousRow.Confidence }
    $nextConfidence = if ($null -eq $nextRow) { 0.0 } else { [double]$nextRow.Confidence }

    $gap = [pscustomobject]@{
        PreviousFrame = $previous
        NextFrame = $current
        MissingFrames = $missing
        Range = if ($missing -eq 1) { [string]($previous + 1) } else { "{0}-{1}" -f ($previous + 1), ($current - 1) }
        PreviousConfidence = $previousConfidence
        NextConfidence = $nextConfidence
        AreaChange = $areaChange
        CenterShift = $centerShift
        Hint = Get-GapReviewHint $areaChange $centerShift $previousConfidence $nextConfidence
    }

    if ($missing -le $ShortGapMaxFrames) {
        $shortGaps.Add($gap) | Out-Null
    } else {
        $longGaps.Add($gap) | Out-Null
    }
}

$isolatedFrames = New-Object System.Collections.Generic.List[int]
foreach ($frame in $frames) {
    $hasNeighbor = $false
    foreach ($other in $frames) {
        if ($other -eq $frame) {
            continue
        }

        if ([Math]::Abs([int]$other - [int]$frame) -le $IsolatedNeighborWindow) {
            $hasNeighbor = $true
            break
        }
    }

    if (-not $hasNeighbor) {
        $isolatedFrames.Add([int]$frame) | Out-Null
    }
}

$lowConfidenceRows = @($rows | Where-Object { $_.Confidence -le $LowConfidenceThreshold } | Sort-Object Confidence, Frame, Index)
$weakNonEdgeRows = @($rows |
    Where-Object {
        $_.Confidence -le $WeakNonEdgeThreshold -and
        -not (Test-NormalizedEdgeTouch $_ $EdgeMarginRatio $FrameAspectRatio)
    } |
    Sort-Object Confidence, Frame, Index)
$upperWeakNonEdgeRows = @($weakNonEdgeRows |
    Where-Object { $_.CenterY -le $UpperFrameCenterYThreshold } |
    Sort-Object Confidence, Frame, Index)
$frameRange = if ($frames.Count -eq 0) { "none" } else { "{0}-{1}" -f $frames[0], $frames[$frames.Count - 1] }
$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

$builder = New-Object System.Text.StringBuilder
[void]$builder.AppendLine("# YOLO Final Mask Continuity Report")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- Source log: ``$LogPath``")
[void]$builder.AppendLine("- Generated: $generatedAt")
[void]$builder.AppendLine("- Final mask rows parsed: $($rows.Count)")
[void]$builder.AppendLine("- Final mask frames: $($frames.Count)")
[void]$builder.AppendLine("- Frame range: ``$frameRange``")
[void]$builder.AppendLine("- Short empty gaps: $($shortGaps.Count)")
[void]$builder.AppendLine("- Long empty gaps: $($longGaps.Count)")
[void]$builder.AppendLine("- Isolated final mask frames: $($isolatedFrames.Count)")
[void]$builder.AppendLine("- Low-confidence final masks: $($lowConfidenceRows.Count)")
[void]$builder.AppendLine("- Weak non-edge final masks: $($weakNonEdgeRows.Count)")
[void]$builder.AppendLine("- Upper-frame weak non-edge final masks: $($upperWeakNonEdgeRows.Count)")
[void]$builder.AppendLine()
[void]$builder.AppendLine("## Interpretation")
[void]$builder.AppendLine('- `[SmokeDetection]` rows in this smoke harness are final `FrameMaskProvider` face rectangles after tracking, scene-cut guard, and ROI refinement.')
[void]$builder.AppendLine("- Empty gaps and isolated frames are review targets, not automatic ground truth errors.")
[void]$builder.AppendLine('- Use the overlay video or review package to label each target as `face`, `nonface`, or `miss`.')
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Short Empty Gaps")
[void]$builder.AppendLine("| Previous | Next | Missing | Range | Confidence | AreaChange | CenterShift | Review hint |")
[void]$builder.AppendLine("| ---: | ---: | ---: | --- | --- | ---: | ---: | --- |")
foreach ($gap in $shortGaps | Select-Object -First 80) {
    [void]$builder.AppendLine(("| {0} | {1} | {2} | `{3}` | {4:F3}->{5:F3} | {6:F2} | {7:F3} | {8} |" -f
        $gap.PreviousFrame,
        $gap.NextFrame,
        $gap.MissingFrames,
        $gap.Range,
        $gap.PreviousConfidence,
        $gap.NextConfidence,
        $gap.AreaChange,
        $gap.CenterShift,
        $gap.Hint))
}
if ($shortGaps.Count -eq 0) {
    [void]$builder.AppendLine("| - | - | - | none | - | - | - | - |")
}
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Isolated Final Mask Frames")
[void]$builder.AppendLine("- Frames: ``$(Join-Values @($isolatedFrames | Select-Object -First 120))``")
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Low-Confidence Final Masks")
[void]$builder.AppendLine("| Frame | Index | Confidence | Center | AreaRatio | Aspect | Box |")
[void]$builder.AppendLine("| ---: | ---: | ---: | --- | ---: | ---: | --- |")
foreach ($row in $lowConfidenceRows | Select-Object -First 80) {
    [void]$builder.AppendLine(("| {0} | {1} | {2:F3} | {3:F3},{4:F3} | {5:F6} | {6:F3} | x={7:F1}, y={8:F1}, w={9:F1}, h={10:F1} |" -f
        $row.Frame, $row.Index, $row.Confidence, $row.CenterX, $row.CenterY, $row.AreaRatio, $row.AspectRatio, $row.X, $row.Y, $row.W, $row.H))
}
if ($lowConfidenceRows.Count -eq 0) {
    [void]$builder.AppendLine("| - | - | - | - | - | - | none |")
}
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Weak Non-Edge Final Masks")
[void]$builder.AppendLine("| Frame | Index | Confidence | Center | AreaRatio | Aspect | Review hint | Box |")
[void]$builder.AppendLine("| ---: | ---: | ---: | --- | ---: | ---: | --- | --- |")
foreach ($row in $weakNonEdgeRows | Select-Object -First 80) {
    $hint = if ($row.CenterY -le $UpperFrameCenterYThreshold) {
        "upper-frame weak non-edge; review false positive vs small face"
    } else {
        "weak non-edge; review false positive"
    }
    [void]$builder.AppendLine(("| {0} | {1} | {2:F3} | {3:F3},{4:F3} | {5:F6} | {6:F3} | {7} | x={8:F1}, y={9:F1}, w={10:F1}, h={11:F1} |" -f
        $row.Frame, $row.Index, $row.Confidence, $row.CenterX, $row.CenterY, $row.AreaRatio, $row.AspectRatio, $hint, $row.X, $row.Y, $row.W, $row.H))
}
if ($weakNonEdgeRows.Count -eq 0) {
    [void]$builder.AppendLine("| - | - | - | - | - | - | - | none |")
}

Set-Content -Encoding UTF8 -Path $resolvedOutput -Value $builder.ToString()
Write-Host "[YoloMaskContinuityReport] wrote path=$resolvedOutput, rows=$($rows.Count), frames=$($frames.Count), shortGaps=$($shortGaps.Count), isolated=$($isolatedFrames.Count), lowConfidence=$($lowConfidenceRows.Count), weakNonEdge=$($weakNonEdgeRows.Count), upperWeakNonEdge=$($upperWeakNonEdgeRows.Count)"
