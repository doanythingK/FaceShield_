param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [string]$OutputPath = ".tmp\yolo-quality\yolo-quality-review-checklist.md",
    [int]$MaxDetectionRows = 32
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

function Escape-Cell {
    param([string]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return $Value.Replace("|", "\|").Replace("`r", " ").Replace("`n", " ")
}

function Read-MatchValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Default = "none"
    )

    $match = [regex]::Match($Text, $Pattern)
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $Default
}

function Split-FrameList {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq "none") {
        return @()
    }

    return @($Text -split "," | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
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

$lines = Get-Content -Path $resolvedLog
$autoSummary = @($lines | Where-Object { $_.StartsWith("[AutoRunSummary]") } | Select-Object -Last 1)
$trackPost = @($lines | Where-Object { $_.StartsWith("[SmokeFaceTrackPost]") } | Select-Object -Last 1)
$sceneGuard = @($lines | Where-Object { $_.StartsWith("[SmokeFaceTrackSceneCutGuard]") } | Select-Object -Last 1)
$strongCarryProbe = @($lines | Where-Object { $_.StartsWith("[SmokeYoloStrongCarrySceneCutProbe]") -or $_.StartsWith("[YoloStrongCarrySceneCutProbe]") } | Select-Object -Last 1)
$sparseSummary = @($lines | Where-Object { $_.StartsWith("[AutoMaskSparsePipe] done") } | Select-Object -Last 1)
$detectionSummary = @($lines | Where-Object { $_.StartsWith("[SmokeDetectionSummary]") } | Select-Object -Last 1)

$detections = New-Object System.Collections.Generic.List[object]
$detectionPattern = '\[SmokeDetection\].*label=([^,]+), frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=([0-9.\-]+), conf=([0-9.\-]+), cx=([0-9.\-]+), cy=([0-9.\-]+), areaRatio=([0-9.\-]+)(?:, aspectRatio=([0-9.\-]+))?'
foreach ($line in $lines) {
    $match = [regex]::Match($line, $detectionPattern)
    if (-not $match.Success) {
        continue
    }

    $width = [double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $height = [double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $aspectRatio = if ($match.Groups[13].Success) {
        [double]::Parse($match.Groups[13].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    } elseif ($height -gt 0.0) {
        $width / $height
    } else {
        0.0
    }

    $detections.Add([pscustomobject]@{
        Label = $match.Groups[1].Value
        Frame = [int]$match.Groups[2].Value
        Index = [int]$match.Groups[3].Value
        X = [double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Y = [double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        W = $width
        H = $height
        Area = [double]::Parse($match.Groups[8].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Confidence = [double]::Parse($match.Groups[9].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        CenterX = [double]::Parse($match.Groups[10].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        CenterY = [double]::Parse($match.Groups[11].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        AreaRatio = [double]::Parse($match.Groups[12].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        AspectRatio = $aspectRatio
    }) | Out-Null
}

$lostFramesText = if ($trackPost.Count -gt 0) { Read-MatchValue $trackPost[0] 'lostFrames=(.*?), removedShort=' } else { "none" }
$removedUnstableTailText = if ($trackPost.Count -gt 0) { Read-MatchValue $trackPost[0] 'removedUnstableTail=(.*?), removedEdgeTail=' } else { "none" }
$removedEdgeTailText = if ($trackPost.Count -gt 0) { Read-MatchValue $trackPost[0] 'removedEdgeTail=(.*?), removedLower=' } else { "none" }
$directCandidatesText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'directCandidates=([^,]+)' } else { "none" }
$postCutCandidatesText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'postCutCandidates=([^,]+)' } else { "none" }
$checkedPairsText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'checkedPairs=(.*?), maxDiff=' } else { "none" }
if ($checkedPairsText -eq "none" -and $sceneGuard.Count -gt 0) {
    $checkedPairsText = Read-MatchValue $sceneGuard[0] 'checkedPairs=(.*?), removed='
}
$maxDiffText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'maxDiff=(.*?), cutPairs=' } else { "none" }
$cutPairsText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'cutPairs=(.*?), removed=' } else { "none" }
$removedFramesText = if ($sceneGuard.Count -gt 0) { Read-MatchValue $sceneGuard[0] 'removedFrames=(.*?), threshold=' } else { "none" }
$probeCandidatesText = if ($strongCarryProbe.Count -gt 0) { Read-MatchValue $strongCarryProbe[0] 'candidates=([^,]+)' } else { "none" }
$probeCheckedPairsText = if ($strongCarryProbe.Count -gt 0) { Read-MatchValue $strongCarryProbe[0] 'checkedPairs=(.*?), maxDiff=' } else { "none" }
$probeMaxDiffText = if ($strongCarryProbe.Count -gt 0) { Read-MatchValue $strongCarryProbe[0] 'maxDiff=(.*?), cutPairs=' } else { "none" }
$probeCutPairsText = if ($strongCarryProbe.Count -gt 0) { Read-MatchValue $strongCarryProbe[0] 'cutPairs=(.*?), threshold=' } else { "none" }
$sparseCutPairsText = if ($sparseSummary.Count -gt 0) { Read-MatchValue $sparseSummary[0] 'sparseSceneCutPairs=(.*?), decodeMs=' } else { "none" }

$framesToReview = New-Object System.Collections.Generic.SortedSet[int]
foreach ($frame in (Split-FrameList $lostFramesText)) {
    [void]$framesToReview.Add($frame)
}
foreach ($frame in (Split-FrameList $removedFramesText)) {
    [void]$framesToReview.Add($frame)
}
foreach ($pair in @($checkedPairsText -split ",")) {
    if ($pair -match '^\d+->(\d+)$') {
        [void]$framesToReview.Add([int]$Matches[1])
    }
}
foreach ($pair in @($probeCheckedPairsText -split ",")) {
    if ($pair -match '^\d+->(\d+)$') {
        [void]$framesToReview.Add([int]$Matches[1])
    }
}
foreach ($pair in @($probeCutPairsText -split ",")) {
    if ($pair -match '^\d+->(\d+)$') {
        [void]$framesToReview.Add([int]$Matches[1])
    }
}
foreach ($pair in @($sparseCutPairsText -split ",")) {
    if ($pair -match '^(\d+)->(\d+)$') {
        [void]$framesToReview.Add([int]$Matches[1])
        [void]$framesToReview.Add([int]$Matches[2])
    }
}

$lowConfidence = @($detections | Sort-Object Confidence, AreaRatio, Frame | Select-Object -First $MaxDetectionRows)
$smallArea = @($detections | Sort-Object AreaRatio, Confidence, Frame | Select-Object -First $MaxDetectionRows)
$sourceLogText = Escape-Cell $LogPath
$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$reviewFramesText = @($framesToReview | Select-Object -First 40) -join ","
if ([string]::IsNullOrWhiteSpace($reviewFramesText)) {
    $reviewFramesText = "none"
}

$builder = New-Object System.Text.StringBuilder
[void]$builder.AppendLine("# YOLO Quality Review Checklist")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- Source log: ``$sourceLogText``")
[void]$builder.AppendLine("- Detection rows parsed: $($detections.Count)")
[void]$builder.AppendLine("- Generated: $generatedAt")
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Run Evidence")
foreach ($line in @($autoSummary + $trackPost + $sceneGuard + $strongCarryProbe + $sparseSummary + $detectionSummary)) {
    if (-not [string]::IsNullOrWhiteSpace($line)) {
        $escapedLine = Escape-Cell $line
        [void]$builder.AppendLine("- ``$escapedLine``")
    }
}
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Review Points")
[void]$builder.AppendLine("| Issue | Evidence | Frames to inspect | Decision note |")
[void]$builder.AppendLine("| --- | --- | --- | --- |")
[void]$builder.AppendLine("| Flicker / missed hold | ``lostFrames=$lostFramesText`` | ``$reviewFramesText`` | Confirm the target remains covered during short detector misses. |")
[void]$builder.AppendLine("| Scene-cut ghost mask | ``directCandidates=$directCandidatesText``, ``postCutCandidates=$postCutCandidatesText``, ``checkedPairs=$checkedPairsText``, ``maxDiff=$maxDiffText``, ``cutPairs=$cutPairsText``, ``removedFrames=$removedFramesText``, ``sparseSceneCutPairs=$sparseCutPairsText`` | ``$reviewFramesText`` | Confirm masks do not persist into a new scene. |")
[void]$builder.AppendLine("| Strong carry scene-cut probe | ``probeCandidates=$probeCandidatesText``, ``probeCheckedPairs=$probeCheckedPairsText``, ``probeMaxDiff=$probeMaxDiffText``, ``probeCutPairs=$probeCutPairsText`` | ``$reviewFramesText`` | Confirm high-confidence same-position candidates are reviewed through cut evidence and final carry cleanup, not confidence alone. |")
[void]$builder.AppendLine("| Unstable tail ghost mask | ``removedUnstableTail=$removedUnstableTailText`` | ``$reviewFramesText`` | Confirm low-confidence geometry jumps at the end of a track do not persist as masks. |")
[void]$builder.AppendLine("| Exit-edge ghost mask | ``removedEdgeTail=$removedEdgeTailText`` | ``$reviewFramesText`` | Confirm low-confidence edge tails do not continue as masks after the target leaves. |")
[void]$builder.AppendLine("| False positives | ``[SmokeDetection]`` candidate rows and ``[SmokeDetectionSummary]`` | see tables below | Label each reviewed crop/frame as ``face`` or ``nonface``; do not treat YOLO or FaceONNX as ground truth. |")
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Low-Confidence Candidates")
[void]$builder.AppendLine("| Frame | Index | Confidence | Center | AreaRatio | Aspect | Box |")
[void]$builder.AppendLine("| ---: | ---: | ---: | --- | ---: | ---: | --- |")
foreach ($row in $lowConfidence) {
    [void]$builder.AppendLine(("| {0} | {1} | {2:F3} | {3:F3},{4:F3} | {5:F6} | {6:F3} | x={7:F1}, y={8:F1}, w={9:F1}, h={10:F1} |" -f
        $row.Frame, $row.Index, $row.Confidence, $row.CenterX, $row.CenterY, $row.AreaRatio, $row.AspectRatio, $row.X, $row.Y, $row.W, $row.H))
}
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Small-Area Candidates")
[void]$builder.AppendLine("| Frame | Index | Confidence | Center | AreaRatio | Aspect | Box |")
[void]$builder.AppendLine("| ---: | ---: | ---: | --- | ---: | ---: | --- |")
foreach ($row in $smallArea) {
    [void]$builder.AppendLine(("| {0} | {1} | {2:F3} | {3:F3},{4:F3} | {5:F6} | {6:F3} | x={7:F1}, y={8:F1}, w={9:F1}, h={10:F1} |" -f
        $row.Frame, $row.Index, $row.Confidence, $row.CenterX, $row.CenterY, $row.AreaRatio, $row.AspectRatio, $row.X, $row.Y, $row.W, $row.H))
}
[void]$builder.AppendLine()

[void]$builder.AppendLine("## Manual Label Rule")
[void]$builder.AppendLine('- `face`: visible real face covered by the candidate box.')
[void]$builder.AppendLine('- `nonface`: background/object/body/text/partial artifact that should not be mosaicked.')
[void]$builder.AppendLine('- `miss`: visible face not covered by any candidate row; add a manual GT row before quality-gate claims.')

Set-Content -Encoding UTF8 -Path $resolvedOutput -Value $builder.ToString()
Write-Host "[YoloQualityReviewChecklist] wrote path=$resolvedOutput, detections=$($detections.Count), reviewFrames=$($framesToReview.Count), lowConfidence=$($lowConfidence.Count), smallArea=$($smallArea.Count)"
