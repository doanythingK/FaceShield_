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
    [double]$MinTileFaceConfidence = 0.55,
    [int]$MinTileSupportCount = 2,
    [double]$MaxSupportCenterDistanceRatio = 0.80,
    [double]$MaxSupportAreaChangeRatio = 3.0,
    [double]$MaxVerificationDistance = 0.75,
    [double]$MinVerificationConfidence = 0.55,
    [double]$MinPersonObjectConfidence = 0.50,
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

function Get-CsvValueOrThrow {
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

    $value = Get-CsvValueOrThrow $Row $Names $Source $Index
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

    $value = Get-CsvValueOrThrow $Row $Names $Source $Index
    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    throw "$Source CSV row $Index has invalid integer value for $([string]::Join('/', $Names)): $value"
}

function Assert-RequiredDetectionCsvShape {
    param(
        [object]$Row,
        [string]$Source,
        [int]$Index
    )

    $frame = Read-RequiredIntValue $Row @("frame", "Frame") $Source $Index
    if ($frame -lt 0) {
        throw "$Source CSV row $Index has negative frame: $frame"
    }

    $width = Read-RequiredDoubleValue $Row @("w", "W", "width", "Width") $Source $Index
    $height = Read-RequiredDoubleValue $Row @("h", "H", "height", "Height") $Source $Index
    if ($width -le 0 -or $height -le 0) {
        throw "$Source CSV row $Index has non-positive geometry: w=$width, h=$height"
    }

    [void](Read-RequiredDoubleValue $Row @("x", "X") $Source $Index)
    [void](Read-RequiredDoubleValue $Row @("y", "Y") $Source $Index)

    switch ($Source) {
        "tile-face" {
            [void](Read-RequiredDoubleValue $Row @("confidence", "conf", "tileFaceConfidence", "Confidence") $Source $Index)
            [void](Read-RequiredIntValue $Row @("tileSupportCount", "supportCount", "TileSupportCount") $Source $Index)
        }
        "face-verification" {
            [void](Read-RequiredDoubleValue $Row @("faceVerificationConfidence", "confidence", "conf", "Confidence") $Source $Index)
            [void](Read-RequiredDoubleValue $Row @("faceVerificationDistance", "verificationDistance", "distance", "FaceVerificationDistance") $Source $Index)
        }
        "person-object" {
            [void](Read-RequiredDoubleValue $Row @("confidence", "conf", "Confidence") $Source $Index)
        }
        default {
            [void](Read-RequiredDoubleValue $Row @("confidence", "conf", "faceVerificationConfidence", "tileFaceConfidence", "Confidence") $Source $Index)
        }
    }
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
        [double]$VerificationDistance = 1.0,
        [string]$ClassLabel = "",
        [string]$EvidenceModel = "",
        [string]$EvidenceRunner = "",
        [string]$SourceCandidateId = "",
        [string]$SourceBasePredictionId = "",
        [double]$CenterXRatio = -1.0,
        [double]$CenterYRatio = -1.0,
        [double]$AreaRatio = -1.0,
        [double]$AspectRatio = -1.0
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
        ClassLabel = $ClassLabel
        EvidenceModel = $EvidenceModel
        EvidenceRunner = $EvidenceRunner
        SourceCandidateId = $SourceCandidateId
        SourceBasePredictionId = $SourceBasePredictionId
        CenterXRatio = $CenterXRatio
        CenterYRatio = $CenterYRatio
        AreaRatio = $AreaRatio
        AspectRatio = $AspectRatio
    }
}

function Format-OptionalDouble {
    param([double]$Value)

    if ($Value -lt 0) {
        return ""
    }

    return Format-Double $Value
}

function Get-GeometryTag {
    param([object]$Row)

    if ($Row.Source -ne "base-yolo-log" -and $Row.Source -ne "base-yolo-csv") {
        return ""
    }

    if ($Row.CenterYRatio -ge 0.0 -and $Row.AreaRatio -ge 0.0) {
        if ($Row.CenterYRatio -le 0.38 -and $Row.AreaRatio -ge 0.035 -and $Row.AreaRatio -le 0.12) {
            return "top-edge-large-review"
        }

        if ($Row.CenterYRatio -le 0.10 -and $Row.AreaRatio -le 0.0065) {
            return "top-edge-weak-review"
        }
    }

    return ""
}

function Join-UniqueNonEmpty {
    param([string[]]$Values)

    $items = @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($items.Count -eq 0) {
        return ""
    }

    return [string]::Join("+", $items)
}

function Get-DetectionMatchKey {
    param([object]$Row)

    return "$($Row.Source):$($Row.Frame):$($Row.Id)"
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
        Assert-RequiredDetectionCsvShape $row $Source $index

        $frame = Read-IntValue $row @("frame", "Frame")
        $id = [string](Get-PropertyValue $row @("predictionId", "detectionId", "verificationId", "sourcePredictionId", "id", "Id") "")
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = "$Source-$frame-$index"
        }

        $tileSupport = Read-IntValue $row @("tileSupportCount", "supportCount", "TileSupportCount") 1
        $distance = Read-DoubleValue $row @("faceVerificationDistance", "verificationDistance", "distance", "FaceVerificationDistance") 1.0
        $classLabel = [string](Get-PropertyValue $row @("class", "Class", "label", "Label", "name", "Name", "category", "Category") "")
        $evidenceModel = [string](Get-PropertyValue $row @("evidenceModel", "modelName", "modelId", "model", "runnerModel", "externalModel") "")
        $evidenceRunner = [string](Get-PropertyValue $row @("evidenceRunner", "runnerName", "runner", "externalRunner", "modelRunner") "")
        $sourceCandidateId = [string](Get-PropertyValue $row @("candidateId", "sourceCandidateId") "")
        $sourceBasePredictionId = [string](Get-PropertyValue $row @("basePredictionId", "sourceBasePredictionId") "")
        $centerXRatio = Read-DoubleValue $row @("centerXRatio", "CenterXRatio", "centerX", "CenterX", "cx", "Cx") -1.0
        $centerYRatio = Read-DoubleValue $row @("centerYRatio", "CenterYRatio", "centerY", "CenterY", "cy", "Cy") -1.0
        $areaRatio = Read-DoubleValue $row @("areaRatio", "AreaRatio") -1.0
        $aspectRatio = Read-DoubleValue $row @("aspectRatio", "AspectRatio") -1.0

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
                    -VerificationDistance $distance `
                    -ClassLabel $classLabel `
                    -EvidenceModel $evidenceModel `
                    -EvidenceRunner $evidenceRunner `
                    -SourceCandidateId $sourceCandidateId `
                    -SourceBasePredictionId $sourceBasePredictionId `
                    -CenterXRatio $centerXRatio `
                    -CenterYRatio $centerYRatio `
                    -AreaRatio $areaRatio `
                    -AspectRatio $aspectRatio)) | Out-Null
        $index++
    }

    return $rows.ToArray()
}

function Read-BasePredictionLogRows {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Base prediction log not found: $resolved"
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $pattern = '\[SmokeDetection\].*frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=[0-9.\-]+, conf=([0-9.\-]+)(?:, cx=([0-9.\-]+), cy=([0-9.\-]+), areaRatio=([0-9.\-]+), aspectRatio=([0-9.\-]+))?'
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
                    -Source "base-yolo-log" `
                    -CenterXRatio $(if ($match.Groups[8].Success) { [double]::Parse($match.Groups[8].Value, [System.Globalization.CultureInfo]::InvariantCulture) } else { -1.0 }) `
                    -CenterYRatio $(if ($match.Groups[9].Success) { [double]::Parse($match.Groups[9].Value, [System.Globalization.CultureInfo]::InvariantCulture) } else { -1.0 }) `
                    -AreaRatio $(if ($match.Groups[10].Success) { [double]::Parse($match.Groups[10].Value, [System.Globalization.CultureInfo]::InvariantCulture) } else { -1.0 }) `
                    -AspectRatio $(if ($match.Groups[11].Success) { [double]::Parse($match.Groups[11].Value, [System.Globalization.CultureInfo]::InvariantCulture) } else { -1.0 }))) | Out-Null
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

function Get-AreaChangeRatio {
    param(
        [object]$A,
        [object]$B
    )

    $areaA = [Math]::Max(1.0, $A.W * $A.H)
    $areaB = [Math]::Max(1.0, $B.W * $B.H)
    return [Math]::Max($areaA, $areaB) / [Math]::Min($areaA, $areaB)
}

function Test-FaceGeometrySupportValues {
    param(
        [double]$Iou,
        [double]$CenterDistanceRatio,
        [double]$AreaChangeRatio
    )

    if ($Iou -ge $MinSupportIou) {
        return $true
    }

    return $CenterDistanceRatio -le $MaxSupportCenterDistanceRatio -and
        $AreaChangeRatio -le $MaxSupportAreaChangeRatio
}

function Test-SourceBindingAllowsMatch {
    param(
        [object]$Target,
        [object]$Candidate
    )

    $verification = $null
    $base = $null
    if ($Target.Source -eq "face-verification" -and ($Candidate.Source -eq "base-yolo-log" -or $Candidate.Source -eq "base-yolo-csv")) {
        $verification = $Target
        $base = $Candidate
    }
    elseif ($Candidate.Source -eq "face-verification" -and ($Target.Source -eq "base-yolo-log" -or $Target.Source -eq "base-yolo-csv")) {
        $verification = $Candidate
        $base = $Target
    }
    else {
        return $true
    }

    if ($verification.Frame -ne $base.Frame) {
        return $true
    }

    $sourceCandidateId = [string]$verification.SourceCandidateId
    $sourceBasePredictionId = [string]$verification.SourceBasePredictionId
    if ([string]::IsNullOrWhiteSpace($sourceCandidateId) -and [string]::IsNullOrWhiteSpace($sourceBasePredictionId)) {
        return $true
    }

    $basePredictionId = [string]$base.Id
    $baseCandidateId = "base-$($base.Frame)-$basePredictionId"
    if (-not [string]::IsNullOrWhiteSpace($sourceBasePredictionId) -and $sourceBasePredictionId -eq $basePredictionId) {
        return $true
    }

    return -not [string]::IsNullOrWhiteSpace($sourceCandidateId) -and
        ($sourceCandidateId -eq $baseCandidateId -or $sourceCandidateId -eq $basePredictionId)
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
        if (-not (Test-SourceBindingAllowsMatch $Target $candidate)) {
            continue
        }
        if (-not (Test-FaceEvidenceMetricSupport $candidate)) {
            continue
        }

        $iou = Get-Iou $Target $candidate
        $centerDistance = Get-CenterDistanceRatio $Target $candidate
        $areaChangeRatio = Get-AreaChangeRatio $Target $candidate
        $supported = $iou -ge $MinIou -or
            ($centerDistance -le $MaxCenterDistanceRatio -and $areaChangeRatio -le $MaxSupportAreaChangeRatio)
        if (-not $supported) {
            continue
        }

        $score = $iou - ($centerDistance * 0.1) - ($areaChangeRatio * 0.001) + ($candidate.Confidence * 0.01)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                Iou = $iou
                CenterDistanceRatio = $centerDistance
                AreaChangeRatio = $areaChangeRatio
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
        if (-not (Test-SourceBindingAllowsMatch $Target $candidate)) {
            continue
        }
        if (-not (Test-FaceEvidenceMetricSupport $candidate)) {
            continue
        }

        $iou = Get-Iou $Target $candidate
        $centerDistance = Get-CenterDistanceRatio $Target $candidate
        $areaChangeRatio = Get-AreaChangeRatio $Target $candidate
        $passesGeometry = Test-FaceGeometrySupportValues $iou $centerDistance $areaChangeRatio
        if (-not $passesGeometry) {
            continue
        }

        $score = $iou + $candidate.Confidence - $candidate.VerificationDistance - ($centerDistance * 0.1) - ($areaChangeRatio * 0.001)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                Iou = $iou
                CenterDistanceRatio = $centerDistance
                AreaChangeRatio = $areaChangeRatio
                Score = $score
            }
        }
    }

    return $best
}

function Find-BestComparison {
    param(
        [object]$Target,
        [object[]]$Candidates
    )

    $best = $null
    foreach ($candidate in @($Candidates | Where-Object { $_.Frame -eq $Target.Frame })) {
        if (-not (Test-SourceBindingAllowsMatch $Target $candidate)) {
            continue
        }
        if (-not (Test-FaceEvidenceMetricSupport $candidate)) {
            continue
        }

        $iou = Get-Iou $Target $candidate
        $centerDistance = Get-CenterDistanceRatio $Target $candidate
        $areaChangeRatio = Get-AreaChangeRatio $Target $candidate
        $score = $iou - ($centerDistance * 0.1) - ([Math]::Log([Math]::Max(1.0, $areaChangeRatio)) * 0.05) + ($candidate.Confidence * 0.01)
        if ($null -eq $best -or $score -gt $best.Score) {
            $best = [pscustomobject]@{
                Row = $candidate
                Iou = $iou
                CenterDistanceRatio = $centerDistance
                AreaChangeRatio = $areaChangeRatio
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
        if (-not (Test-PersonClassLabel $candidate.ClassLabel)) {
            continue
        }
        if ($candidate.Confidence -lt $MinPersonObjectConfidence) {
            continue
        }

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

function Test-PersonClassLabel {
    param([string]$ClassLabel)

    if ([string]::IsNullOrWhiteSpace($ClassLabel)) {
        return $true
    }

    $normalized = $ClassLabel.Trim().ToLowerInvariant()
    return $normalized -eq "person" -or
        $normalized -eq "human" -or
        $normalized -eq "people" -or
        $normalized -eq "man" -or
        $normalized -eq "woman"
}

function Test-FaceGeometrySupport {
    param(
        [object]$Target,
        [object]$Candidate
    )

    $iou = Get-Iou $Target $Candidate
    $centerDistance = Get-CenterDistanceRatio $Target $Candidate
    $areaChangeRatio = Get-AreaChangeRatio $Target $Candidate
    return Test-FaceGeometrySupportValues $iou $centerDistance $areaChangeRatio
}

function Test-VerificationMetricSupport {
    param([object]$Candidate)

    return $Candidate.Confidence -ge $MinVerificationConfidence -and
        $Candidate.VerificationDistance -le $MaxVerificationDistance
}

function Test-TileFaceMetricSupport {
    param([object]$Candidate)

    return $Candidate.Confidence -ge $MinTileFaceConfidence -and
        $Candidate.TileSupportCount -ge $MinTileSupportCount
}

function Test-FaceEvidenceMetricSupport {
    param([object]$Candidate)

    if ($Candidate.Source -eq "tile-face") {
        return Test-TileFaceMetricSupport $Candidate
    }

    if ($Candidate.Source -eq "face-verification") {
        return Test-VerificationMetricSupport $Candidate
    }

    return $true
}

function Get-TemporalFaceSupport {
    param(
        [object]$Target,
        [object[]]$TileCandidates,
        [object[]]$VerificationCandidates
    )

    $frames = [System.Collections.Generic.HashSet[int]]::new()
    $sources = [System.Collections.Generic.HashSet[string]]::new()
    $evidenceIds = [System.Collections.Generic.HashSet[string]]::new()
    $rowCount = 0

    foreach ($candidate in @($TileCandidates | Where-Object { [Math]::Abs($_.Frame - $Target.Frame) -le $TemporalSupportWindowFrames })) {
        if (-not (Test-TileFaceMetricSupport $candidate)) {
            continue
        }
        if (-not (Test-FaceGeometrySupport $Target $candidate)) {
            continue
        }

        [void]$frames.Add($candidate.Frame)
        [void]$sources.Add("tile")
        [void]$evidenceIds.Add((Get-DetectionMatchKey $candidate))
        $rowCount++
    }

    foreach ($candidate in @($VerificationCandidates | Where-Object { [Math]::Abs($_.Frame - $Target.Frame) -le $TemporalSupportWindowFrames })) {
        if (-not (Test-SourceBindingAllowsMatch $Target $candidate)) {
            continue
        }
        if (-not (Test-FaceGeometrySupport $Target $candidate)) {
            continue
        }
        if (-not (Test-VerificationMetricSupport $candidate)) {
            continue
        }

        [void]$frames.Add($candidate.Frame)
        [void]$sources.Add("verification")
        [void]$evidenceIds.Add((Get-DetectionMatchKey $candidate))
        $rowCount++
    }

    $sourceValues = @($sources | Sort-Object)
    $evidenceIdValues = @($evidenceIds | Sort-Object)
    return [pscustomobject]@{
        FrameCount = $frames.Count
        RowCount = $rowCount
        Sources = if ($sourceValues.Count -gt 0) { [string]::Join("+", $sourceValues) } else { "" }
        EvidenceIds = if ($evidenceIdValues.Count -gt 0) { [string]::Join(";", $evidenceIdValues) } else { "" }
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

function Get-MinMatchAreaChangeRatio {
    param([object[]]$Matches)

    return Get-MinMatchProperty $Matches "AreaChangeRatio" 99.0
}

function Get-MinMatchProperty {
    param(
        [object[]]$Matches,
        [string]$PropertyName,
        [double]$DefaultValue
    )

    $best = $DefaultValue
    foreach ($match in @($Matches)) {
        if ($null -eq $match -or $null -eq $match.PSObject.Properties[$PropertyName]) {
            continue
        }

        $best = [Math]::Min($best, [double]$match.PSObject.Properties[$PropertyName].Value)
    }

    return $best
}

if ([string]::IsNullOrWhiteSpace($BasePredictionCsv) -and [string]::IsNullOrWhiteSpace($BasePredictionLog)) {
    throw "BasePredictionCsv or BasePredictionLog is required."
}

if ([string]::IsNullOrWhiteSpace($TileFaceCsv) -and [string]::IsNullOrWhiteSpace($FaceVerificationCsv)) {
    throw "TileFaceCsv or FaceVerificationCsv is required for pseudo-GT support."
}

$baseRows = @(if (-not [string]::IsNullOrWhiteSpace($BasePredictionCsv)) {
        Read-DetectionCsvRows $BasePredictionCsv "base-yolo-csv"
    }
    else {
        Read-BasePredictionLogRows $BasePredictionLog
    })

$tileRows = @(if (-not [string]::IsNullOrWhiteSpace($TileFaceCsv)) {
        Read-DetectionCsvRows $TileFaceCsv "tile-face"
    })

$verificationRows = @(if (-not [string]::IsNullOrWhiteSpace($FaceVerificationCsv)) {
        Read-DetectionCsvRows $FaceVerificationCsv "face-verification"
    })

$personRows = @(if (-not [string]::IsNullOrWhiteSpace($PersonObjectCsv)) {
        Read-DetectionCsvRows $PersonObjectCsv "person-object"
    })

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
    $comparisonMatch = Find-BestComparison $base @($tileRows + $verificationRows)

    $hasTileSupport = $null -ne $tileMatch
    $hasVerificationSupport = $null -ne $verificationMatch
    $hasFaceSupport = $hasTileSupport -or $hasVerificationSupport
    if ($hasTileSupport) {
        [void]$matchedTileIds.Add((Get-DetectionMatchKey $tileMatch.Row))
    }
    if ($hasVerificationSupport) {
        [void]$matchedVerificationIds.Add((Get-DetectionMatchKey $verificationMatch.Row))
    }

    $candidateType = if ($hasFaceSupport) { "supportedFaceCandidate" } else { "falsePositiveCandidate" }
    $tileConfidence = if ($hasTileSupport) { $tileMatch.Row.Confidence } else { 0.0 }
    $tileSupportCount = if ($hasTileSupport) { [Math]::Max(1, $tileMatch.Row.TileSupportCount) } else { 0 }
    $verificationConfidence = if ($hasVerificationSupport) { $verificationMatch.Row.Confidence } else { 0.0 }
    $verificationDistance = if ($hasVerificationSupport) { $verificationMatch.Row.VerificationDistance } else { 1.0 }
    $personConfidence = if ($null -ne $personMatch) { $personMatch.Row.Confidence } else { 0.0 }
    $personUpperOverlap = if ($null -ne $personMatch) { $personMatch.UpperOverlap } else { 0.0 }
    $personObjectClass = if ($null -ne $personMatch) { $personMatch.Row.ClassLabel } else { "" }
    $auxiliarySignalRole = if ($personUpperOverlap -gt 0) { "priority-only-not-face-evidence" } else { "" }
    $tileEvidenceModel = if ($hasTileSupport) { $tileMatch.Row.EvidenceModel } else { "" }
    $tileEvidenceRunner = if ($hasTileSupport) { $tileMatch.Row.EvidenceRunner } else { "" }
    $faceVerificationEvidenceModel = if ($hasVerificationSupport) { $verificationMatch.Row.EvidenceModel } else { "" }
    $faceVerificationEvidenceRunner = if ($hasVerificationSupport) { $verificationMatch.Row.EvidenceRunner } else { "" }
    $faceVerificationSourceCandidateId = if ($hasVerificationSupport) { $verificationMatch.Row.SourceCandidateId } else { "" }
    $faceVerificationSourceBasePredictionId = if ($hasVerificationSupport) { $verificationMatch.Row.SourceBasePredictionId } else { "" }
    $personObjectEvidenceModel = if ($null -ne $personMatch) { $personMatch.Row.EvidenceModel } else { "" }
    $personObjectEvidenceRunner = if ($null -ne $personMatch) { $personMatch.Row.EvidenceRunner } else { "" }
    $supportMatches = @($tileMatch, $verificationMatch)
    $geometryTag = Get-GeometryTag $base
    $bestIou = if ($hasFaceSupport) {
        [Math]::Max(
            $(if ($hasTileSupport) { $tileMatch.Iou } else { 0.0 }),
            $(if ($hasVerificationSupport) { $verificationMatch.Iou } else { 0.0 }))
    } elseif ($null -ne $comparisonMatch) {
        $comparisonMatch.Iou
    } else {
        0.0
    }
    $bestCenterDistance = if ($hasFaceSupport) {
        Get-MinMatchProperty $supportMatches "CenterDistanceRatio" 99.0
    } elseif ($null -ne $comparisonMatch) {
        $comparisonMatch.CenterDistanceRatio
    } else {
        99.0
    }
    $bestAreaChangeRatio = if ($hasFaceSupport) {
        Get-MinMatchAreaChangeRatio $supportMatches
    } elseif ($null -ne $comparisonMatch) {
        $comparisonMatch.AreaChangeRatio
    } else {
        99.0
    }

    $fpProbability = if ($hasFaceSupport) {
        [Math]::Max(0.0, 0.25 - ($tileSupportCount * 0.05) - ($temporalSupport.FrameCount * 0.04))
    }
    else {
        [Math]::Min(0.98, 0.55 + ((1.0 - [Math]::Min(1.0, $base.Confidence)) * 0.30))
    }
    $missProbability = if ($hasFaceSupport) { 0.0 } else { 0.10 }

    $reason = if ($hasFaceSupport) {
        "base YOLO candidate is supported by test-only high-precision face evidence; final label still requires review CSV"
    }
    elseif ($null -ne $comparisonMatch) {
        "base YOLO candidate has same-frame high-precision face evidence but fails IoU/center/area support thresholds; person/object support is auxiliary only"
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
            personObjectClass = $personObjectClass
            tileEvidenceModel = $tileEvidenceModel
            tileEvidenceRunner = $tileEvidenceRunner
            faceVerificationEvidenceModel = $faceVerificationEvidenceModel
            faceVerificationEvidenceRunner = $faceVerificationEvidenceRunner
            faceVerificationSourceCandidateId = $faceVerificationSourceCandidateId
            faceVerificationSourceBasePredictionId = $faceVerificationSourceBasePredictionId
            personObjectEvidenceModel = $personObjectEvidenceModel
            personObjectEvidenceRunner = $personObjectEvidenceRunner
            auxiliarySignalRole = $auxiliarySignalRole
            supportFrameCount = $temporalSupport.FrameCount
            supportRowCount = $temporalSupport.RowCount
            supportSources = $temporalSupport.Sources
            supportEvidenceIds = $temporalSupport.EvidenceIds
            bestIou = Format-Double $bestIou
            centerDistanceRatio = Format-Double $bestCenterDistance
            areaChangeRatio = Format-Double $bestAreaChangeRatio
            centerXRatio = Format-OptionalDouble $base.CenterXRatio
            centerYRatio = Format-OptionalDouble $base.CenterYRatio
            baseAreaRatio = Format-OptionalDouble $base.AreaRatio
            aspectRatio = Format-OptionalDouble $base.AspectRatio
            geometryTag = $geometryTag
            fpProbability = Format-Double $fpProbability
            missProbability = Format-Double $missProbability
            pseudoGtReason = $reason
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = "pseudo-GT candidate only; final face/nonface/miss must be copied into the review CSV after visual confirmation"
        }) | Out-Null
}

foreach ($tile in $tileRows) {
    if (-not (Test-TileFaceMetricSupport $tile)) {
        continue
    }

    if ($matchedTileIds.Contains((Get-DetectionMatchKey $tile))) {
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
    $personObjectClass = if ($null -ne $personMatch) { $personMatch.Row.ClassLabel } else { "" }
    $auxiliarySignalRole = if ($personUpperOverlap -gt 0) { "priority-only-not-face-evidence" } else { "" }
    $verificationConfidence = if ($null -ne $verificationMatch) { $verificationMatch.Row.Confidence } else { 0.0 }
    $verificationDistance = if ($null -ne $verificationMatch) { $verificationMatch.Row.VerificationDistance } else { 1.0 }
    $faceVerificationEvidenceModel = if ($null -ne $verificationMatch) { $verificationMatch.Row.EvidenceModel } else { "" }
    $faceVerificationEvidenceRunner = if ($null -ne $verificationMatch) { $verificationMatch.Row.EvidenceRunner } else { "" }
    $faceVerificationSourceCandidateId = if ($null -ne $verificationMatch) { $verificationMatch.Row.SourceCandidateId } else { "" }
    $faceVerificationSourceBasePredictionId = if ($null -ne $verificationMatch) { $verificationMatch.Row.SourceBasePredictionId } else { "" }
    $personObjectEvidenceModel = if ($null -ne $personMatch) { $personMatch.Row.EvidenceModel } else { "" }
    $personObjectEvidenceRunner = if ($null -ne $personMatch) { $personMatch.Row.EvidenceRunner } else { "" }
    if ($null -ne $verificationMatch) {
        [void]$matchedVerificationIds.Add((Get-DetectionMatchKey $verificationMatch.Row))
    }
    [void]$matchedTileIds.Add((Get-DetectionMatchKey $tile))
    $missProbability = [Math]::Min(0.98, 0.55 + ([Math]::Min(1.0, $tile.Confidence) * 0.30) + ([Math]::Min(3, $temporalSupport.FrameCount) * 0.03))

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
            personObjectClass = $personObjectClass
            tileEvidenceModel = $tile.EvidenceModel
            tileEvidenceRunner = $tile.EvidenceRunner
            faceVerificationEvidenceModel = $faceVerificationEvidenceModel
            faceVerificationEvidenceRunner = $faceVerificationEvidenceRunner
            faceVerificationSourceCandidateId = $faceVerificationSourceCandidateId
            faceVerificationSourceBasePredictionId = $faceVerificationSourceBasePredictionId
            personObjectEvidenceModel = $personObjectEvidenceModel
            personObjectEvidenceRunner = $personObjectEvidenceRunner
            auxiliarySignalRole = $auxiliarySignalRole
            supportFrameCount = $temporalSupport.FrameCount
            supportRowCount = $temporalSupport.RowCount
            supportSources = $temporalSupport.Sources
            supportEvidenceIds = $temporalSupport.EvidenceIds
            bestIou = if ($null -ne $verificationMatch) { Format-Double $verificationMatch.Iou } else { "0" }
            centerDistanceRatio = if ($null -ne $verificationMatch) { Format-Double $verificationMatch.CenterDistanceRatio } else { "99" }
            areaChangeRatio = if ($null -ne $verificationMatch) { Format-Double $verificationMatch.AreaChangeRatio } else { "99" }
            centerXRatio = Format-OptionalDouble $tile.CenterXRatio
            centerYRatio = Format-OptionalDouble $tile.CenterYRatio
            baseAreaRatio = ""
            aspectRatio = Format-OptionalDouble $tile.AspectRatio
            geometryTag = ""
            fpProbability = "0"
            missProbability = Format-Double $missProbability
            pseudoGtReason = "test-only high-precision tile face was not matched by base YOLO; verification support is recorded when present; person/object support is auxiliary only"
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = "pseudo-GT miss candidate only; add/confirm as miss in review CSV after visual confirmation"
        }) | Out-Null
}

foreach ($verification in $verificationRows) {
    if (-not (Test-VerificationMetricSupport $verification)) {
        continue
    }

    if ($matchedVerificationIds.Contains((Get-DetectionMatchKey $verification))) {
        continue
    }

    $baseMatch = Find-BestVerification $verification $baseRows
    if ($null -ne $baseMatch) {
        continue
    }

    $tileMatch = Find-BestMatch $verification $tileRows $MinSupportIou $MaxSupportCenterDistanceRatio
    if ($null -ne $tileMatch -and $matchedTileIds.Contains((Get-DetectionMatchKey $tileMatch.Row))) {
        continue
    }

    $personMatch = Find-BestPersonSupport $verification $personRows
    $temporalSupport = Get-TemporalFaceSupport $verification $tileRows $verificationRows
    $personConfidence = if ($null -ne $personMatch) { $personMatch.Row.Confidence } else { 0.0 }
    $personUpperOverlap = if ($null -ne $personMatch) { $personMatch.UpperOverlap } else { 0.0 }
    $personObjectClass = if ($null -ne $personMatch) { $personMatch.Row.ClassLabel } else { "" }
    $auxiliarySignalRole = if ($personUpperOverlap -gt 0) { "priority-only-not-face-evidence" } else { "" }
    $tileConfidence = if ($null -ne $tileMatch) { $tileMatch.Row.Confidence } else { 0.0 }
    $tileSupportCount = if ($null -ne $tileMatch) { [Math]::Max(1, $tileMatch.Row.TileSupportCount) } else { 0 }
    $tileEvidenceModel = if ($null -ne $tileMatch) { $tileMatch.Row.EvidenceModel } else { "" }
    $tileEvidenceRunner = if ($null -ne $tileMatch) { $tileMatch.Row.EvidenceRunner } else { "" }
    $personObjectEvidenceModel = if ($null -ne $personMatch) { $personMatch.Row.EvidenceModel } else { "" }
    $personObjectEvidenceRunner = if ($null -ne $personMatch) { $personMatch.Row.EvidenceRunner } else { "" }
    $missProbability = [Math]::Min(0.98, 0.55 + ([Math]::Min(1.0, $verification.Confidence) * 0.30) + ([Math]::Min(3, $temporalSupport.FrameCount) * 0.03))
    [void]$matchedVerificationIds.Add((Get-DetectionMatchKey $verification))
    if ($null -ne $tileMatch) {
        [void]$matchedTileIds.Add((Get-DetectionMatchKey $tileMatch.Row))
    }

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
            personObjectClass = $personObjectClass
            tileEvidenceModel = $tileEvidenceModel
            tileEvidenceRunner = $tileEvidenceRunner
            faceVerificationEvidenceModel = $verification.EvidenceModel
            faceVerificationEvidenceRunner = $verification.EvidenceRunner
            faceVerificationSourceCandidateId = $verification.SourceCandidateId
            faceVerificationSourceBasePredictionId = $verification.SourceBasePredictionId
            personObjectEvidenceModel = $personObjectEvidenceModel
            personObjectEvidenceRunner = $personObjectEvidenceRunner
            auxiliarySignalRole = $auxiliarySignalRole
            supportFrameCount = $temporalSupport.FrameCount
            supportRowCount = $temporalSupport.RowCount
            supportSources = $temporalSupport.Sources
            supportEvidenceIds = $temporalSupport.EvidenceIds
            bestIou = if ($null -ne $tileMatch) { Format-Double $tileMatch.Iou } else { "0" }
            centerDistanceRatio = if ($null -ne $tileMatch) { Format-Double $tileMatch.CenterDistanceRatio } else { "99" }
            areaChangeRatio = if ($null -ne $tileMatch) { Format-Double $tileMatch.AreaChangeRatio } else { "99" }
            centerXRatio = Format-OptionalDouble $verification.CenterXRatio
            centerYRatio = Format-OptionalDouble $verification.CenterYRatio
            baseAreaRatio = ""
            aspectRatio = Format-OptionalDouble $verification.AspectRatio
            geometryTag = ""
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
$sourceBoundFaceVerificationRows = @($orderedRows | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.faceVerificationSourceCandidateId) -or
        -not [string]::IsNullOrWhiteSpace([string]$_.faceVerificationSourceBasePredictionId)
    }).Count

$reviewQueueSourceRows = @($orderedRows | ForEach-Object {
        $fpProbability = Read-CandidateDouble $_ "fpProbability"
        $missProbability = Read-CandidateDouble $_ "missProbability"
        $personUpperOverlap = [Math]::Min(1.0, [Math]::Max(0.0, (Read-CandidateDouble $_ "personUpperOverlap")))
        $auxiliaryPriorityBoost = [Math]::Min(0.15, $personUpperOverlap * 0.15)
        $geometryTag = [string]$_.geometryTag
        $geometryPriorityBoost = if ($_.candidateType -eq "falsePositiveCandidate" -and -not [string]::IsNullOrWhiteSpace($geometryTag)) { 0.08 } else { 0.0 }
        $priorityScore = [Math]::Min(1.0, [Math]::Max($fpProbability, $missProbability) + $auxiliaryPriorityBoost + $geometryPriorityBoost)
        $priorityGroup = if ($_.candidateType -eq "supportedFaceCandidate") { 1 } else { 0 }
        $dominantProbability = if ($missProbability -gt $fpProbability) { "missProbability" } else { "fpProbability" }
        $priorityReason = switch ($_.candidateType) {
            "falsePositiveCandidate" { "review likely false-positive first; base YOLO lacks test-only face support" }
            "missCandidate" { "review likely miss first; high-precision face evidence was not matched by base YOLO" }
            default { "supported face candidate; lower priority unless visual QA needs confirmation" }
        }
        if ($auxiliaryPriorityBoost -gt 0) {
            $priorityReason = "$priorityReason; auxiliary person/object support raises review priority but does not decide face/nonface"
        }
        if ($geometryPriorityBoost -gt 0) {
            $priorityReason = "$priorityReason; geometry tag '$geometryTag' raises review priority but does not decide face/nonface"
        }

        [pscustomobject]@{
            Row = $_
            PriorityGroup = $priorityGroup
            PriorityScore = $priorityScore
            AuxiliaryPriorityBoost = $auxiliaryPriorityBoost
            GeometryPriorityBoost = $geometryPriorityBoost
            DominantProbability = $dominantProbability
            PriorityReason = $priorityReason
        }
    } | Sort-Object @{ Expression = "PriorityGroup"; Ascending = $true }, @{ Expression = "PriorityScore"; Descending = $true }, @{ Expression = { $_.Row.frame }; Ascending = $true }, @{ Expression = { $_.Row.candidateId }; Ascending = $true })

$reviewRank = 1
$reviewQueueRows = @($reviewQueueSourceRows | ForEach-Object {
        $row = $_.Row
        $expectedReviewLabel = switch ($row.candidateType) {
            "falsePositiveCandidate" { "nonface" }
            "missCandidate" { "miss" }
            default { "face" }
        }
        $queueRow = [pscustomobject]@{
            reviewRank = $reviewRank
            frame = $row.frame
            candidateId = $row.candidateId
            candidateType = $row.candidateType
            expectedReviewLabel = $expectedReviewLabel
            source = $row.source
            basePredictionId = $row.basePredictionId
            tileDetectionId = $row.tileDetectionId
            verificationId = $row.verificationId
            x = $row.x
            y = $row.y
            w = $row.w
            h = $row.h
            reviewPriorityScore = Format-Double $_.PriorityScore
            auxiliaryPriorityBoost = Format-Double $_.AuxiliaryPriorityBoost
            geometryPriorityBoost = Format-Double $_.GeometryPriorityBoost
            dominantProbability = $_.DominantProbability
            baseFaceConfidence = $row.baseFaceConfidence
            tileFaceConfidence = $row.tileFaceConfidence
            tileSupportCount = $row.tileSupportCount
            faceVerificationConfidence = $row.faceVerificationConfidence
            faceVerificationDistance = $row.faceVerificationDistance
            personConfidence = $row.personConfidence
            personUpperOverlap = $row.personUpperOverlap
            personObjectClass = $row.personObjectClass
            tileEvidenceModel = $row.tileEvidenceModel
            tileEvidenceRunner = $row.tileEvidenceRunner
            faceVerificationEvidenceModel = $row.faceVerificationEvidenceModel
            faceVerificationEvidenceRunner = $row.faceVerificationEvidenceRunner
            faceVerificationSourceCandidateId = $row.faceVerificationSourceCandidateId
            faceVerificationSourceBasePredictionId = $row.faceVerificationSourceBasePredictionId
            personObjectEvidenceModel = $row.personObjectEvidenceModel
            personObjectEvidenceRunner = $row.personObjectEvidenceRunner
            auxiliarySignalRole = $row.auxiliarySignalRole
            supportFrameCount = $row.supportFrameCount
            supportRowCount = $row.supportRowCount
            supportSources = $row.supportSources
            supportEvidenceIds = $row.supportEvidenceIds
            bestIou = $row.bestIou
            centerDistanceRatio = $row.centerDistanceRatio
            areaChangeRatio = $row.areaChangeRatio
            centerXRatio = $row.centerXRatio
            centerYRatio = $row.centerYRatio
            baseAreaRatio = $row.baseAreaRatio
            aspectRatio = $row.aspectRatio
            geometryTag = $row.geometryTag
            fpProbability = $row.fpProbability
            missProbability = $row.missProbability
            reviewPriorityReason = $_.PriorityReason
            pseudoGtReason = $row.pseudoGtReason
            reviewStatus = $row.reviewStatus
            evidenceNotes = $row.evidenceNotes
        }
        $script:reviewRank++
        $queueRow
    })

$reviewQueueRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewQueuePath

$supported = @($orderedRows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" }).Count
$falsePositive = @($orderedRows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" }).Count
$miss = @($orderedRows | Where-Object { $_.candidateType -eq "missCandidate" }).Count
$modelProvenanceRows = @($orderedRows | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string](Join-UniqueNonEmpty @($_.tileEvidenceModel, $_.faceVerificationEvidenceModel, $_.personObjectEvidenceModel)))
    }).Count
$runnerProvenanceRows = @($orderedRows | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string](Join-UniqueNonEmpty @($_.tileEvidenceRunner, $_.faceVerificationEvidenceRunner, $_.personObjectEvidenceRunner)))
    }).Count
$geometryTaggedRows = @($orderedRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.geometryTag) }).Count
$geometryTags = @($orderedRows | ForEach-Object { [string]$_.geometryTag } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
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
    "- modelProvenanceRows=$modelProvenanceRows",
    "- runnerProvenanceRows=$runnerProvenanceRows",
    "- sourceBoundFaceVerificationRows=$sourceBoundFaceVerificationRows",
    "- geometryTaggedRows=$geometryTaggedRows",
    "- geometryTags=$(if ($geometryTags.Count -gt 0) { [string]::Join(',', $geometryTags) } else { 'none' })",
    "- evidenceProvenance=optional-evidenceModel/evidenceRunner",
    "- minSupportIou=$(Format-Double $MinSupportIou)",
    "- minTileFaceConfidence=$(Format-Double $MinTileFaceConfidence)",
    "- minTileSupportCount=$MinTileSupportCount",
    "- maxSupportCenterDistanceRatio=$(Format-Double $MaxSupportCenterDistanceRatio)",
    "- maxSupportAreaChangeRatio=$(Format-Double $MaxSupportAreaChangeRatio)",
    "- maxVerificationDistance=$(Format-Double $MaxVerificationDistance)",
    "- minVerificationConfidence=$(Format-Double $MinVerificationConfidence)",
    "- minPersonObjectConfidence=$(Format-Double $MinPersonObjectConfidence)",
    "- temporalSupportWindowFrames=$TemporalSupportWindowFrames",
    "- inputValidation=strict-required-columns",
    "- topReviewCandidates=$(if ($topReviewFrames.Count -gt 0) { [string]::Join(';', $topReviewFrames) } else { 'none' })",
    "",
    "Final labels must be copied into the review CSV only after visual confirmation."
)

$summary | Set-Content -Encoding UTF8 -Path $summaryPathResolved

Write-Host "[YoloPseudoGtEvidence] rows=$($orderedRows.Count), supportedFaceCandidate=$supported, falsePositiveCandidate=$falsePositive, missCandidate=$miss, output=$OutputCsv"
Write-Host "[YoloPseudoGtEvidence] summary=$SummaryPath"
Write-Host "[YoloPseudoGtEvidence] reviewQueue=$ReviewQueueCsv"
