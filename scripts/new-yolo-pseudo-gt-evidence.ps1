param(
    [string]$BasePredictionCsv = "",
    [string]$BasePredictionLog = "",
    [string]$TileFaceCsv = "",
    [string]$FaceVerificationCsv = "",
    [string]$PersonObjectCsv = "",
    [string]$OutputCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$SummaryPath = ".tmp\yolo-pseudo-gt\pseudo-gt-summary.md",
    [string]$ReviewQueueCsv = "",
    [double]$MinSupportIou = 0.35,
    [double]$MaxSupportCenterDistanceRatio = 0.80,
    [double]$MaxVerificationDistance = 0.75,
    [double]$MinVerificationConfidence = 0.55,
    [int]$TemporalSupportWindowFrames = 2
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

function Get-PropertyValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [object]$Default = ""
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }

    return $Default
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [double]$Default = 0.0
    )

    $value = Get-PropertyValue $Row $Names ""
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse(
            [string]$value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Read-IntValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [int]$Default = 0
    )

    $value = Get-PropertyValue $Row $Names ""
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function New-DetectionRow {
    param(
        [int]$Frame,
        [string]$Id,
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [double]$Confidence,
        [string]$Source,
        [int]$TileSupportCount = 0,
        [double]$VerificationDistance = 1.0
    )

    [pscustomobject]@{
        Frame = $Frame
        Id = $Id
        X = $X
        Y = $Y
        W = $W
        H = $H
        Confidence = $Confidence
        Source = $Source
        TileSupportCount = $TileSupportCount
        VerificationDistance = $VerificationDistance
    }
}

function Read-DetectionCsvRows {
    param(
        [string]$Path,
        [string]$Source
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Source CSV not found: $resolved"
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($row in (Import-Csv $resolved)) {
        $frame = Read-IntValue $row @("frame", "Frame")
        $id = [string](Get-PropertyValue $row @("predictionId", "detectionId", "verificationId", "sourcePredictionId", "id", "Id") "")
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = "$Source-$frame-$index"
        }

        $tileSupport = Read-IntValue $row @("tileSupportCount", "supportCount", "TileSupportCount") 1
        $distance = Read-DoubleValue $row @("faceVerificationDistance", "verificationDistance", "distance", "FaceVerificationDistance") 1.0

        $rows.Add((New-DetectionRow `
                    -Frame $frame `
                    -Id $id `
                    -X (Read-DoubleValue $row @("x", "X")) `
                    -Y (Read-DoubleValue $row @("y", "Y")) `
                    -W (Read-DoubleValue $row @("w", "W", "width", "Width")) `
                    -H (Read-DoubleValue $row @("h", "H", "height", "Height")) `
                    -Confidence (Read-DoubleValue $row @("confidence", "conf", "faceVerificationConfidence", "tileFaceConfidence", "Confidence")) `
                    -Source $Source `
                    -TileSupportCount $tileSupport `
                    -VerificationDistance $distance)) | Out-Null
        $index++
    }

    return @($rows)
}

function Read-BasePredictionLogRows {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Base prediction log not found: $resolved"
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $pattern = '\[SmokeDetection\].*frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=[0-9.\-]+, conf=([0-9.\-]+)'
    foreach ($line in (Get-Content -Path $resolved)) {
        $match = [regex]::Match($line, $pattern)
        if (-not $match.Success) {
            continue
        }

        $frame = [int]$match.Groups[1].Value
        $index = $match.Groups[2].Value
        $rows.Add((New-DetectionRow `
                    -Frame $frame `
                    -Id "$frame-$index" `
                    -X ([double]::Parse($match.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -Y ([double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -W ([double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -H ([double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -Confidence ([double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -Source "base-yolo-log")) | Out-Null
    }

    return @($rows)
}

function Get-Iou {
    param(
        [object]$A,
        [object]$B
    )

    $left = [Math]::Max($A.X, $B.X)
    $top = [Math]::Max($A.Y, $B.Y)
    $right = [Math]::Min($A.X + $A.W, $B.X + $B.W)
    $bottom = [Math]::Min($A.Y + $A.H, $B.Y + $B.H)
    $intersection = [Math]::Max(0.0, $right - $left) * [Math]::Max(0.0, $bottom - $top)
    if ($intersection -le 0) {
        return 0.0
    }

    $areaA = [Math]::Max(0.0, $A.W) * [Math]::Max(0.0, $A.H)
    $areaB = [Math]::Max(0.0, $B.W) * [Math]::Max(0.0, $B.H)
    $union = $areaA + $areaB - $intersection
    if ($union -le 0) {
        return 0.0
    }

    return $intersection / $union
}

function Get-CenterDistanceRatio {
    param(
        [object]$A,
        [object]$B
    )

    $ax = $A.X + ($A.W / 2.0)
    $ay = $A.Y + ($A.H / 2.0)
    $bx = $B.X + ($B.W / 2.0)
    $by = $B.Y + ($B.H / 2.0)
    $distance = [Math]::Sqrt([Math]::Pow($ax - $bx, 2.0) + [Math]::Pow($ay - $by, 2.0))
    $scale = [Math]::Max(1.0, [Math]::Max([Math]::Max($A.W, $A.H), [Math]::Max($B.W, $B.H)))
    return $distance / $scale
}

function Get-FaceAreaOverlap {
    param(
        [object]$Face,
        [object]$Region,
        [double]$RegionHeightRatio = 1.0
    )

    $regionH = [Math]::Max(0.0, $Region.H * $RegionHeightRatio)
    $left = [Math]::Max($Face.X, $Region.X)
    $top = [Math]::Max($Face.Y, $Region.Y)
    $right = [Math]::Min($Face.X + $Face.W, $Region.X + $Region.W)
    $bottom = [Math]::Min($Face.Y + $Face.H, $Region.Y + $regionH)
    $intersection = [Math]::Max(0.0, $right - $left) * [Math]::Max(0.0, $bottom - $top)
    $faceArea = [Math]::Max(1.0, $Face.W * $Face.H)
    return $intersection / $faceArea
}

function Find-BestMatch {
    param(
        [object]$Target,
        [object[]]$Candidates,
        [double]$MinIou,
        [double]$MaxCenterDistanceRatio
    )

    $best = $null
    foreach ($candidate in @($Candidates | Where-Object { $_.Frame -eq $Target.Frame })) {
        $iou = Get-Iou $Target $candidate
        $centerDistance = Get-CenterDistanceRatio $Target $candidate
        $supported = $iou -ge $MinIou -or $centerDistance -le $MaxCenterDistanceRatio
        if (-not $supported) {
            continue
        }

        $score = $iou - ($centerDistance * 0.1) + ($candidate.Confidence * 0.01)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                Iou = $iou
                CenterDistanceRatio = $centerDistance
                Score = $score
            }
        }
    }

    return $best
}

function Find-BestVerification {
    param(
        [object]$Target,
        [object[]]$Candidates
    )

    $best = $null
    foreach ($candidate in @($Candidates | Where-Object { $_.Frame -eq $Target.Frame })) {
        $iou = Get-Iou $Target $candidate
        $centerDistance = Get-CenterDistanceRatio $Target $candidate
        $passesGeometry = $iou -ge $MinSupportIou -or $centerDistance -le $MaxSupportCenterDistanceRatio
        $passesMetric = $candidate.Confidence -ge $MinVerificationConfidence -or $candidate.VerificationDistance -le $MaxVerificationDistance
        if (-not ($passesGeometry -and $passesMetric)) {
            continue
        }

        $score = $iou + $candidate.Confidence - $candidate.VerificationDistance - ($centerDistance * 0.1)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                Iou = $iou
                CenterDistanceRatio = $centerDistance
                Score = $score
            }
        }
    }

    return $best
}

function Find-BestPersonSupport {
    param(
        [object]$Target,
        [object[]]$Candidates
    )

    $best = $null
    foreach ($candidate in @($Candidates | Where-Object { $_.Frame -eq $Target.Frame })) {
        $upperOverlap = Get-FaceAreaOverlap $Target $candidate 0.45
        if ($upperOverlap -le 0) {
            continue
        }

        $score = $upperOverlap + ($candidate.Confidence * 0.01)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                UpperOverlap = $upperOverlap
                Score = $score
            }
        }
    }

    return $best
}

function Test-FaceGeometrySupport {
    param(
        [object]$Target,
        [object]$Candidate
    )

    $iou = Get-Iou $Target $Candidate
    $centerDistance = Get-CenterDistanceRatio $Target $Candidate
    return ($iou -ge $MinSupportIou -or $centerDistance -le $MaxSupportCenterDistanceRatio)
}

function Test-VerificationMetricSupport {
    param([object]$Candidate)

    return ($Candidate.Confidence -ge $MinVerificationConfidence -or $Candidate.VerificationDistance -le $MaxVerificationDistance)
}

function Get-TemporalFaceSupport {
    param(
        [object]$Target,
        [object[]]$TileCandidates,
        [object[]]$VerificationCandidates
    )

    $frames = [System.Collections.Generic.HashSet[int]]::new()
    $sources = [System.Collections.Generic.HashSet[string]]::new()
    $rowCount = 0

    foreach ($candidate in @($TileCandidates | Where-Object { [Math]::Abs($_.Frame - $Target.Frame) -le $TemporalSupportWindowFrames })) {
        if (-not (Test-FaceGeometrySupport $Target $candidate)) {
            continue
        }

        [void]$frames.Add($candidate.Frame)
        [void]$sources.Add("tile")
        $rowCount++
    }

    foreach ($candidate in @($VerificationCandidates | Where-Object { [Math]::Abs($_.Frame - $Target.Frame) -le $TemporalSupportWindowFrames })) {
        if (-not (Test-FaceGeometrySupport $Target $candidate)) {
            continue
        }
        if (-not (Test-VerificationMetricSupport $candidate)) {
            continue
        }

        [void]$frames.Add($candidate.Frame)
        [void]$sources.Add("verification")
        $rowCount++
    }

    $sourceValues = @($sources | Sort-Object)
    return [pscustomobject]@{
        FrameCount = $frames.Count
        RowCount = $rowCount
        Sources = if ($sourceValues.Count -gt 0) { [string]::Join("+", $sourceValues) } else { "" }
    }
}

function Format-Double {
    param([double]$Value)
    return $Value.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-CandidateDouble {
    param(
        [object]$Row,
        [string]$Name
    )

    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return 0.0
    }

    $parsed = 0.0
    if ([double]::TryParse(
            [string]$property.Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return 0.0
}

if ([string]::IsNullOrWhiteSpace($BasePredictionCsv) -and [string]::IsNullOrWhiteSpace($BasePredictionLog)) {
    throw "BasePredictionCsv or BasePredictionLog is required."
}

if ([string]::IsNullOrWhiteSpace($TileFaceCsv) -and [string]::IsNullOrWhiteSpace($FaceVerificationCsv)) {
    throw "TileFaceCsv or FaceVerificationCsv is required for pseudo-GT support."
}

$baseRows = if (-not [string]::IsNullOrWhiteSpace($BasePredictionCsv)) {
    @(Read-DetectionCsvRows $BasePredictionCsv "base-yolo-csv")
}
else {
    @(Read-BasePredictionLogRows $BasePredictionLog)
}

$tileRows = if (-not [string]::IsNullOrWhiteSpace($TileFaceCsv)) {
    @(Read-DetectionCsvRows $TileFaceCsv "tile-face")
}
else {
    @()
}

$verificationRows = if (-not [string]::IsNullOrWhiteSpace($FaceVerificationCsv)) {
    @(Read-DetectionCsvRows $FaceVerificationCsv "face-verification")
}
else {
    @()
}

$personRows = if (-not [string]::IsNullOrWhiteSpace($PersonObjectCsv)) {
    @(Read-DetectionCsvRows $PersonObjectCsv "person-object")
}
else {
    @()
}

if ($baseRows.Count -eq 0) {
    throw "No base YOLO prediction rows were found."
}

if (($tileRows.Count + $verificationRows.Count) -eq 0) {
    throw "No pseudo-GT face support rows were found."
}

$candidateRows = [System.Collections.Generic.List[object]]::new()
$matchedTileIds = [System.Collections.Generic.HashSet[string]]::new()
$matchedVerificationIds = [System.Collections.Generic.HashSet[string]]::new()

foreach ($base in $baseRows) {
    $tileMatch = Find-BestMatch $base $tileRows $MinSupportIou $MaxSupportCenterDistanceRatio
    $verificationMatch = Find-BestVerification $base $verificationRows
    $personMatch = Find-BestPersonSupport $base $personRows
    $temporalSupport = Get-TemporalFaceSupport $base $tileRows $verificationRows

    $hasTileSupport = $null -ne $tileMatch
    $hasVerificationSupport = $null -ne $verificationMatch
    $hasFaceSupport = $hasTileSupport -or $hasVerificationSupport
    if ($hasTileSupport) {
        [void]$matchedTileIds.Add($tileMatch.Row.Id)
    }
    if ($hasVerificationSupport) {
        [void]$matchedVerificationIds.Add($verificationMatch.Row.Id)
    }

    $candidateType = if ($hasFaceSupport) { "supportedFaceCandidate" } else { "falsePositiveCandidate" }
    $tileConfidence = if ($hasTileSupport) { $tileMatch.Row.Confidence } else { 0.0 }
    $tileSupportCount = if ($hasTileSupport) { [Math]::Max(1, $tileMatch.Row.TileSupportCount) } else { 0 }
    $verificationConfidence = if ($hasVerificationSupport) { $verificationMatch.Row.Confidence } else { 0.0 }
    $verificationDistance = if ($hasVerificationSupport) { $verificationMatch.Row.VerificationDistance } else { 1.0 }
    $personConfidence = if ($null -ne $personMatch) { $personMatch.Row.Confidence } else { 0.0 }
    $personUpperOverlap = if ($null -ne $personMatch) { $personMatch.UpperOverlap } else { 0.0 }
    $bestIou = [Math]::Max(
        $(if ($hasTileSupport) { $tileMatch.Iou } else { 0.0 }),
        $(if ($hasVerificationSupport) { $verificationMatch.Iou } else { 0.0 }))
    $bestCenterDistance = if ($hasTileSupport) {
        $tileMatch.CenterDistanceRatio
    }
    elseif ($hasVerificationSupport) {
        $verificationMatch.CenterDistanceRatio
    }
    else {
        99.0
    }

    $fpProbability = if ($hasFaceSupport) {
        [Math]::Max(0.0, 0.25 - ($tileSupportCount * 0.05) - ($temporalSupport.FrameCount * 0.04))
    }
    else {
        [Math]::Min(0.98, 0.55 + ((1.0 - [Math]::Min(1.0, $base.Confidence)) * 0.30) - ($personUpperOverlap * 0.10))
    }
    $missProbability = if ($hasFaceSupport) { 0.0 } else { 0.10 }

    $reason = if ($hasFaceSupport) {
        "base YOLO candidate is supported by test-only high-precision face evidence; final label still requires review CSV"
    }
    else {
        "base YOLO candidate lacks tile/verification face support; person/object support is auxiliary only"
    }

    $candidateRows.Add([pscustomobject]@{
            candidateId = "base-$($base.Frame)-$($base.Id)"
            frame = $base.Frame
            candidateType = $candidateType
            source = "base-yolo"
            basePredictionId = $base.Id
            tileDetectionId = if ($hasTileSupport) { $tileMatch.Row.Id } else { "" }
            verificationId = if ($hasVerificationSupport) { $verificationMatch.Row.Id } else { "" }
            x = Format-Double $base.X
            y = Format-Double $base.Y
            w = Format-Double $base.W
            h = Format-Double $base.H
            baseFaceConfidence = Format-Double $base.Confidence
            tileFaceConfidence = Format-Double $tileConfidence
            tileSupportCount = $tileSupportCount
            faceVerificationConfidence = Format-Double $verificationConfidence
            faceVerificationDistance = Format-Double $verificationDistance
            personConfidence = Format-Double $personConfidence
            personUpperOverlap = Format-Double $personUpperOverlap
            supportFrameCount = $temporalSupport.FrameCount
            supportSources = $temporalSupport.Sources
            bestIou = Format-Double $bestIou
            centerDistanceRatio = Format-Double $bestCenterDistance
            fpProbability = Format-Double $fpProbability
            missProbability = Format-Double $missProbability
            pseudoGtReason = $reason
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = "pseudo-GT candidate only; final face/nonface/miss must be copied into the review CSV after visual confirmation"
        }) | Out-Null
}

foreach ($tile in $tileRows) {
    if ($matchedTileIds.Contains($tile.Id)) {
        continue
    }

    $baseMatch = Find-BestMatch $tile $baseRows $MinSupportIou $MaxSupportCenterDistanceRatio
    if ($null -ne $baseMatch) {
        continue
    }

    $personMatch = Find-BestPersonSupport $tile $personRows
    $verificationMatch = Find-BestVerification $tile $verificationRows
    $temporalSupport = Get-TemporalFaceSupport $tile $tileRows $verificationRows
    $personConfidence = if ($null -ne $personMatch) { $personMatch.Row.Confidence } else { 0.0 }
    $personUpperOverlap = if ($null -ne $personMatch) { $personMatch.UpperOverlap } else { 0.0 }
    $verificationConfidence = if ($null -ne $verificationMatch) { $verificationMatch.Row.Confidence } else { 0.0 }
    $verificationDistance = if ($null -ne $verificationMatch) { $verificationMatch.Row.VerificationDistance } else { 1.0 }
    if ($null -ne $verificationMatch) {
        [void]$matchedVerificationIds.Add($verificationMatch.Row.Id)
    }
    $missProbability = [Math]::Min(0.98, 0.55 + ([Math]::Min(1.0, $tile.Confidence) * 0.30) + ([Math]::Min(1.0, $personUpperOverlap) * 0.10) + ([Math]::Min(3, $temporalSupport.FrameCount) * 0.03))

    $candidateRows.Add([pscustomobject]@{
            candidateId = "miss-$($tile.Frame)-$($tile.Id)"
            frame = $tile.Frame
            candidateType = "missCandidate"
            source = "tile-face"
            basePredictionId = ""
            tileDetectionId = $tile.Id
            verificationId = if ($null -ne $verificationMatch) { $verificationMatch.Row.Id } else { "" }
            x = Format-Double $tile.X
            y = Format-Double $tile.Y
            w = Format-Double $tile.W
            h = Format-Double $tile.H
            baseFaceConfidence = "0"
            tileFaceConfidence = Format-Double $tile.Confidence
            tileSupportCount = [Math]::Max(1, $tile.TileSupportCount)
            faceVerificationConfidence = Format-Double $verificationConfidence
            faceVerificationDistance = Format-Double $verificationDistance
            personConfidence = Format-Double $personConfidence
            personUpperOverlap = Format-Double $personUpperOverlap
            supportFrameCount = $temporalSupport.FrameCount
            supportSources = $temporalSupport.Sources
            bestIou = if ($null -ne $verificationMatch) { Format-Double $verificationMatch.Iou } else { "0" }
            centerDistanceRatio = if ($null -ne $verificationMatch) { Format-Double $verificationMatch.CenterDistanceRatio } else { "99" }
            fpProbability = "0"
            missProbability = Format-Double $missProbability
            pseudoGtReason = "test-only high-precision tile face was not matched by base YOLO; verification support is recorded when present; person/object support is auxiliary only"
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = "pseudo-GT miss candidate only; add/confirm as miss in review CSV after visual confirmation"
        }) | Out-Null
}

foreach ($verification in $verificationRows) {
    if ($matchedVerificationIds.Contains($verification.Id)) {
        continue
    }

    $baseMatch = Find-BestVerification $verification $baseRows
    if ($null -ne $baseMatch) {
        continue
    }

    $tileMatch = Find-BestMatch $verification $tileRows $MinSupportIou $MaxSupportCenterDistanceRatio
    if ($null -ne $tileMatch -and $matchedTileIds.Contains($tileMatch.Row.Id)) {
        continue
    }

    $personMatch = Find-BestPersonSupport $verification $personRows
    $temporalSupport = Get-TemporalFaceSupport $verification $tileRows $verificationRows
    $personConfidence = if ($null -ne $personMatch) { $personMatch.Row.Confidence } else { 0.0 }
    $personUpperOverlap = if ($null -ne $personMatch) { $personMatch.UpperOverlap } else { 0.0 }
    $tileConfidence = if ($null -ne $tileMatch) { $tileMatch.Row.Confidence } else { 0.0 }
    $tileSupportCount = if ($null -ne $tileMatch) { [Math]::Max(1, $tileMatch.Row.TileSupportCount) } else { 0 }
    $missProbability = [Math]::Min(0.98, 0.55 + ([Math]::Min(1.0, $verification.Confidence) * 0.30) + ([Math]::Min(1.0, $personUpperOverlap) * 0.10) + ([Math]::Min(3, $temporalSupport.FrameCount) * 0.03))

    $candidateRows.Add([pscustomobject]@{
            candidateId = "miss-$($verification.Frame)-$($verification.Id)"
            frame = $verification.Frame
            candidateType = "missCandidate"
            source = "face-verification"
            basePredictionId = ""
            tileDetectionId = if ($null -ne $tileMatch) { $tileMatch.Row.Id } else { "" }
            verificationId = $verification.Id
            x = Format-Double $verification.X
            y = Format-Double $verification.Y
            w = Format-Double $verification.W
            h = Format-Double $verification.H
            baseFaceConfidence = "0"
            tileFaceConfidence = Format-Double $tileConfidence
            tileSupportCount = $tileSupportCount
            faceVerificationConfidence = Format-Double $verification.Confidence
            faceVerificationDistance = Format-Double $verification.VerificationDistance
            personConfidence = Format-Double $personConfidence
            personUpperOverlap = Format-Double $personUpperOverlap
            supportFrameCount = $temporalSupport.FrameCount
            supportSources = $temporalSupport.Sources
            bestIou = if ($null -ne $tileMatch) { Format-Double $tileMatch.Iou } else { "0" }
            centerDistanceRatio = if ($null -ne $tileMatch) { Format-Double $tileMatch.CenterDistanceRatio } else { "99" }
            fpProbability = "0"
            missProbability = Format-Double $missProbability
            pseudoGtReason = "test-only high-quality face verification was not matched by base YOLO; tile support is recorded when present; person/object support is auxiliary only"
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = "pseudo-GT miss candidate only; add/confirm as miss in review CSV after visual confirmation"
        }) | Out-Null
}

if ($candidateRows.Count -eq 0) {
    throw "No pseudo-GT candidate rows were produced."
}

$outputPath = Resolve-RepoPath $OutputCsv
$summaryPathResolved = Resolve-RepoPath $SummaryPath
if ([string]::IsNullOrWhiteSpace($ReviewQueueCsv)) {
    $outputParent = Split-Path -Parent $OutputCsv
    if ([string]::IsNullOrWhiteSpace($outputParent)) {
        $ReviewQueueCsv = "pseudo-gt-review-queue.csv"
    }
    else {
        $ReviewQueueCsv = Join-Path $outputParent "pseudo-gt-review-queue.csv"
    }
}

$reviewQueuePath = Resolve-RepoPath $ReviewQueueCsv
foreach ($path in @($outputPath, $summaryPathResolved, $reviewQueuePath)) {
    $dir = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

$orderedRows = @($candidateRows | Sort-Object frame, candidateType, candidateId)
$orderedRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath

$reviewQueueSourceRows = @($orderedRows | ForEach-Object {
        $fpProbability = Read-CandidateDouble $_ "fpProbability"
        $missProbability = Read-CandidateDouble $_ "missProbability"
        $priorityScore = [Math]::Max($fpProbability, $missProbability)
        $priorityGroup = if ($_.candidateType -eq "supportedFaceCandidate") { 1 } else { 0 }
        $dominantProbability = if ($missProbability -gt $fpProbability) { "missProbability" } else { "fpProbability" }
        $priorityReason = switch ($_.candidateType) {
            "falsePositiveCandidate" { "review likely false-positive first; base YOLO lacks test-only face support" }
            "missCandidate" { "review likely miss first; high-precision face evidence was not matched by base YOLO" }
            default { "supported face candidate; lower priority unless visual QA needs confirmation" }
        }

        [pscustomobject]@{
            Row = $_
            PriorityGroup = $priorityGroup
            PriorityScore = $priorityScore
            DominantProbability = $dominantProbability
            PriorityReason = $priorityReason
        }
    } | Sort-Object @{ Expression = "PriorityGroup"; Ascending = $true }, @{ Expression = "PriorityScore"; Descending = $true }, @{ Expression = { $_.Row.frame }; Ascending = $true }, @{ Expression = { $_.Row.candidateId }; Ascending = $true })

$reviewRank = 1
$reviewQueueRows = @($reviewQueueSourceRows | ForEach-Object {
        $row = $_.Row
        $queueRow = [pscustomobject]@{
            reviewRank = $reviewRank
            frame = $row.frame
            candidateId = $row.candidateId
            candidateType = $row.candidateType
            source = $row.source
            reviewPriorityScore = Format-Double $_.PriorityScore
            dominantProbability = $_.DominantProbability
            baseFaceConfidence = $row.baseFaceConfidence
            tileFaceConfidence = $row.tileFaceConfidence
            tileSupportCount = $row.tileSupportCount
            faceVerificationConfidence = $row.faceVerificationConfidence
            faceVerificationDistance = $row.faceVerificationDistance
            personConfidence = $row.personConfidence
            personUpperOverlap = $row.personUpperOverlap
            supportFrameCount = $row.supportFrameCount
            supportSources = $row.supportSources
            bestIou = $row.bestIou
            centerDistanceRatio = $row.centerDistanceRatio
            fpProbability = $row.fpProbability
            missProbability = $row.missProbability
            reviewPriorityReason = $_.PriorityReason
            pseudoGtReason = $row.pseudoGtReason
            reviewStatus = $row.reviewStatus
        }
        $script:reviewRank++
        $queueRow
    })

$reviewQueueRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewQueuePath

$supported = @($orderedRows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" }).Count
$falsePositive = @($orderedRows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" }).Count
$miss = @($orderedRows | Where-Object { $_.candidateType -eq "missCandidate" }).Count
$topReviewFrames = @($reviewQueueRows | Where-Object { $_.candidateType -ne "supportedFaceCandidate" } | Select-Object -First 10 | ForEach-Object { "$($_.frame):$($_.candidateType):$($_.candidateId):$($_.reviewPriorityScore)" })

$summary = @(
    "# YOLO Pseudo-GT Evidence",
    "",
    "This is test-only evidence. It does not run in the app default runtime path and does not complete the final face/nonface/miss decision.",
    "",
    "- baseRows=$($baseRows.Count)",
    "- tileFaceRows=$($tileRows.Count)",
    "- faceVerificationRows=$($verificationRows.Count)",
    "- personObjectRows=$($personRows.Count)",
    "- outputRows=$($orderedRows.Count)",
    "- reviewQueue=$ReviewQueueCsv",
    "- supportedFaceCandidate=$supported",
    "- falsePositiveCandidate=$falsePositive",
    "- missCandidate=$miss",
    "- minSupportIou=$(Format-Double $MinSupportIou)",
    "- maxSupportCenterDistanceRatio=$(Format-Double $MaxSupportCenterDistanceRatio)",
    "- maxVerificationDistance=$(Format-Double $MaxVerificationDistance)",
    "- minVerificationConfidence=$(Format-Double $MinVerificationConfidence)",
    "- temporalSupportWindowFrames=$TemporalSupportWindowFrames",
    "- topReviewCandidates=$(if ($topReviewFrames.Count -gt 0) { [string]::Join(';', $topReviewFrames) } else { 'none' })",
    "",
    "Final labels must be copied into the review CSV only after visual confirmation."
)

$summary | Set-Content -Encoding UTF8 -Path $summaryPathResolved

Write-Host "[YoloPseudoGtEvidence] rows=$($orderedRows.Count), supportedFaceCandidate=$supported, falsePositiveCandidate=$falsePositive, missCandidate=$miss, output=$OutputCsv"
Write-Host "[YoloPseudoGtEvidence] summary=$SummaryPath"
Write-Host "[YoloPseudoGtEvidence] reviewQueue=$ReviewQueueCsv"
