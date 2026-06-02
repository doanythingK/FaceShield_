param(
    [Parameter(Mandatory = $true)]
    [string]$PseudoGtCsv,
    [Parameter(Mandatory = $true)]
    [string]$ReviewCsv,
    [string]$FullFrameReviewCsv = "",
    [string]$OutputCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [string]$SummaryPath = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure-summary.md",
    [double]$MinReviewIou = 0.50,
    [switch]$RequireAllClosed
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
        [string]$Name,
        [object]$Default = ""
    )

    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return $Default
    }

    return $property.Value
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string]$Name,
        [double]$Default = 0.0
    )

    $value = Get-PropertyValue $Row $Name ""
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
        [string]$Name,
        [int]$Default = 0
    )

    $value = Get-PropertyValue $Row $Name ""
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Normalize-Label {
    param([string]$Label)

    if ([string]::IsNullOrWhiteSpace($Label)) {
        return ""
    }

    $normalized = $Label.Trim().ToLowerInvariant()
    if ($normalized -in @("face", "actualface", "true", "1")) {
        return "face"
    }

    if ($normalized -in @("nonface", "notface", "false", "0", "object", "background")) {
        return "nonface"
    }

    if ($normalized -eq "miss") {
        return "miss"
    }

    return $normalized
}

function Test-ReviewedStatus {
    param([string]$Status)

    if ([string]::IsNullOrWhiteSpace($Status)) {
        return $false
    }

    return $Status.Trim().ToLowerInvariant() -in @("pass", "reviewed", "complete", "completed", "closed", "done")
}

function Get-ExpectedReviewLabels {
    param([string]$CandidateType)

    switch ($CandidateType) {
        "supportedFaceCandidate" { return @("face") }
        "falsePositiveCandidate" { return @("nonface") }
        "missCandidate" { return @("face", "miss") }
        default { return @() }
    }
}

function New-Box {
    param([object]$Row)

    [pscustomobject]@{
        Frame = Read-IntValue $Row "frame"
        X = Read-DoubleValue $Row "x"
        Y = Read-DoubleValue $Row "y"
        W = Read-DoubleValue $Row "w"
        H = Read-DoubleValue $Row "h"
    }
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
    $width = [Math]::Max(0.0, $right - $left)
    $height = [Math]::Max(0.0, $bottom - $top)
    $intersection = $width * $height
    if ($intersection -le 0.0) {
        return 0.0
    }

    $areaA = [Math]::Max(0.0, $A.W) * [Math]::Max(0.0, $A.H)
    $areaB = [Math]::Max(0.0, $B.W) * [Math]::Max(0.0, $B.H)
    $union = $areaA + $areaB - $intersection
    if ($union -le 0.0) {
        return 0.0
    }

    return $intersection / $union
}

function Format-Double {
    param([double]$Value)
    return $Value.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ClosureCount {
    param(
        [object[]]$Rows,
        [string]$CandidateType,
        [string]$Status = ""
    )

    return @($Rows | Where-Object {
            $_.candidateType -eq $CandidateType -and
            ([string]::IsNullOrWhiteSpace($Status) -or $_.closureStatus -eq $Status)
        }).Count
}

function New-CandidateTypeBreakdownLine {
    param(
        [object[]]$Rows,
        [string]$CandidateType
    )

    $total = Get-ClosureCount -Rows $Rows -CandidateType $CandidateType
    $closedCount = Get-ClosureCount -Rows $Rows -CandidateType $CandidateType -Status "closed"
    $unreviewedCount = Get-ClosureCount -Rows $Rows -CandidateType $CandidateType -Status "unreviewed"
    $mismatchCount = Get-ClosureCount -Rows $Rows -CandidateType $CandidateType -Status "label-mismatch"
    return "- ${CandidateType}: total=$total, closed=$closedCount, unreviewed=$unreviewedCount, labelMismatch=$mismatchCount"
}

function Find-BestReviewMatch {
    param(
        [object]$Candidate,
        [object[]]$ReviewRows,
        [bool]$PreferManualMiss
    )

    $candidateFrame = Read-IntValue $Candidate "frame"
    $candidateBox = New-Box $Candidate
    $basePredictionId = [string](Get-PropertyValue $Candidate "basePredictionId" "")

    $best = $null
    $bestIou = 0.0
    foreach ($row in @($ReviewRows | Where-Object { (Read-IntValue $_ "frame") -eq $candidateFrame })) {
        $sourcePredictionId = [string](Get-PropertyValue $row "sourcePredictionId" "")
        if (-not $PreferManualMiss -and -not [string]::IsNullOrWhiteSpace($basePredictionId) -and $sourcePredictionId -eq $basePredictionId) {
            $reviewBox = New-Box $row
            $sourcePredictionIou = Get-Iou $candidateBox $reviewBox
            if ($sourcePredictionIou -ge $MinReviewIou) {
                return [pscustomobject]@{ Row = $row; Iou = $sourcePredictionIou; MatchMode = "sourcePredictionId+iou" }
            }

            continue
        }

        if ($PreferManualMiss -and -not [string]::IsNullOrWhiteSpace($sourcePredictionId)) {
            continue
        }

        $reviewBox = New-Box $row
        $iou = Get-Iou $candidateBox $reviewBox
        if ($iou -gt $bestIou) {
            $best = $row
            $bestIou = $iou
        }
    }

    if ($null -ne $best -and $bestIou -ge $MinReviewIou) {
        return [pscustomobject]@{ Row = $best; Iou = $bestIou; MatchMode = "iou" }
    }

    return $null
}

function Get-FrameReviewDiagnostics {
    param(
        [object]$Candidate,
        [object[]]$ReviewRows,
        [bool]$PreferManualMiss
    )

    $candidateFrame = Read-IntValue $Candidate "frame"
    $candidateBox = New-Box $Candidate
    $eligibleRows = [System.Collections.Generic.List[object]]::new()
    $bestIou = 0.0

    foreach ($row in @($ReviewRows | Where-Object { (Read-IntValue $_ "frame") -eq $candidateFrame })) {
        $sourcePredictionId = [string](Get-PropertyValue $row "sourcePredictionId" "")
        if ($PreferManualMiss -and -not [string]::IsNullOrWhiteSpace($sourcePredictionId)) {
            continue
        }

        $eligibleRows.Add($row) | Out-Null
        $reviewBox = New-Box $row
        $bestIou = [Math]::Max($bestIou, (Get-Iou $candidateBox $reviewBox))
    }

    [pscustomobject]@{
        EligibleRowsOnFrame = $eligibleRows.Count
        BestFrameReviewIou = $bestIou
    }
}

$pseudoPath = Resolve-RepoPath $PseudoGtCsv
$reviewPath = Resolve-RepoPath $ReviewCsv
$frameReviewPath = Resolve-RepoPath $FullFrameReviewCsv
$outputPath = Resolve-RepoPath $OutputCsv
$summaryPathResolved = Resolve-RepoPath $SummaryPath

foreach ($required in @($pseudoPath, $reviewPath)) {
    if (-not (Test-Path $required)) {
        throw "Required CSV not found: $required"
    }
}

$pseudoRows = @(Import-Csv $pseudoPath)
$reviewRows = @(Import-Csv $reviewPath)
if ($pseudoRows.Count -eq 0) {
    throw "Pseudo-GT CSV has no rows: $pseudoPath"
}
if ($reviewRows.Count -eq 0) {
    throw "Review CSV has no rows: $reviewPath"
}

$fullFrameRows = @()
if (-not [string]::IsNullOrWhiteSpace($frameReviewPath)) {
    if (-not (Test-Path $frameReviewPath)) {
        throw "Full-frame review CSV not found: $frameReviewPath"
    }
    $fullFrameRows = @(Import-Csv $frameReviewPath)
}

$closureRows = [System.Collections.Generic.List[object]]::new()
foreach ($candidate in $pseudoRows) {
    $candidateType = [string](Get-PropertyValue $candidate "candidateType" "")
    $expectedLabels = @(Get-ExpectedReviewLabels $candidateType)
    $expectedLabel = $expectedLabels -join "|"

    $preferManualMiss = $candidateType -eq "missCandidate"
    $match = Find-BestReviewMatch -Candidate $candidate -ReviewRows $reviewRows -PreferManualMiss $preferManualMiss
    $frameReviewDiagnostics = Get-FrameReviewDiagnostics -Candidate $candidate -ReviewRows $reviewRows -PreferManualMiss $preferManualMiss
    $reviewLabel = ""
    $reviewStatus = ""
    $reviewEvidenceNotes = ""
    $reviewSourcePredictionId = ""
    $reviewIou = 0.0
    $matchMode = "none"
    $reviewStatusAccepted = $false
    if ($null -ne $match) {
        $reviewLabel = Normalize-Label ([string](Get-PropertyValue $match.Row "label" ""))
        $reviewStatus = [string](Get-PropertyValue $match.Row "reviewStatus" "")
        $reviewStatusAccepted = Test-ReviewedStatus $reviewStatus
        $reviewEvidenceNotes = [string](Get-PropertyValue $match.Row "evidenceNotes" "")
        $reviewSourcePredictionId = [string](Get-PropertyValue $match.Row "sourcePredictionId" "")
        $reviewIou = $match.Iou
        $matchMode = $match.MatchMode
    }

    $fullFrameReviewStatus = ""
    $fullFrameMissedFaceCount = ""
    $fullFrameMissedRowsAdded = ""
    $fullFrameEvidenceNotes = ""
    $fullFrameContinuityCandidateTypes = ""
    $fullFrameContinuityCandidateReasons = ""
    $fullFrameContinuityReviewPriority = ""
    $fullFrameContinuityCandidateRanges = ""
    $fullFrameContinuityReviewHints = ""
    $fullFrameMissScanClosed = $true
    if ($candidateType -eq "missCandidate" -and $fullFrameRows.Count -gt 0) {
        $fullFrameMissScanClosed = $false
        $frame = Read-IntValue $candidate "frame"
        $frameReview = @($fullFrameRows | Where-Object { (Read-IntValue $_ "frame") -eq $frame } | Select-Object -First 1)
        if ($frameReview.Count -gt 0) {
            $fullFrameReviewStatus = [string](Get-PropertyValue $frameReview[0] "reviewStatus" "")
            $fullFrameMissedFaceCount = [string](Get-PropertyValue $frameReview[0] "missedFaceCount" "")
            $fullFrameMissedRowsAdded = [string](Get-PropertyValue $frameReview[0] "missedFaceRowsAdded" "")
            $fullFrameEvidenceNotes = [string](Get-PropertyValue $frameReview[0] "evidenceNotes" "")
            $fullFrameContinuityCandidateTypes = [string](Get-PropertyValue $frameReview[0] "continuityCandidateTypes" "")
            $fullFrameContinuityCandidateReasons = [string](Get-PropertyValue $frameReview[0] "continuityCandidateReasons" "")
            $fullFrameContinuityReviewPriority = [string](Get-PropertyValue $frameReview[0] "continuityReviewPriority" "")
            $fullFrameContinuityCandidateRanges = [string](Get-PropertyValue $frameReview[0] "continuityCandidateRanges" "")
            $fullFrameContinuityReviewHints = [string](Get-PropertyValue $frameReview[0] "continuityReviewHints" "")
            $fullFrameMissedRowsAddedValue = 0
            $fullFrameMissScanClosed = (Test-ReviewedStatus $fullFrameReviewStatus) -and
                [int]::TryParse([string]$fullFrameMissedRowsAdded, [ref]$fullFrameMissedRowsAddedValue) -and
                $fullFrameMissedRowsAddedValue -gt 0 -and
                -not [string]::IsNullOrWhiteSpace($fullFrameEvidenceNotes)
        }
    }

    $closureStatus = "unreviewed"
    $closureReason = "no matching reviewed row"
    if ($null -eq $match -and $frameReviewDiagnostics.EligibleRowsOnFrame -eq 0) {
        $closureReason = "no eligible review row on candidate frame"
    }
    elseif ($null -eq $match) {
        $closureReason = "no matching reviewed geometry on candidate frame"
    }
    if ($null -ne $match -and [string]::IsNullOrWhiteSpace($reviewLabel)) {
        $closureStatus = "unreviewed"
        $closureReason = "matching row has no label"
    }
    elseif ($null -ne $match -and -not $reviewStatusAccepted) {
        $closureStatus = "unreviewed"
        $closureReason = "matching row reviewStatus '$reviewStatus' is not a completed review state"
    }
    elseif ($null -ne $match -and [string]::IsNullOrWhiteSpace($reviewEvidenceNotes)) {
        $closureStatus = "unreviewed"
        $closureReason = "matching row has no evidenceNotes"
    }
    elseif ($null -ne $match -and $reviewLabel -notin $expectedLabels) {
        $closureStatus = "label-mismatch"
        $closureReason = "review CSV label '$reviewLabel' does not match expected '$expectedLabel'"
    }
    elseif ($null -ne $match -and -not $fullFrameMissScanClosed) {
        $closureStatus = "unreviewed"
        $closureReason = "missCandidate requires completed full-frame missed-face scan with missedFaceRowsAdded > 0 and evidenceNotes"
    }
    elseif ($null -ne $match) {
        $closureStatus = "closed"
        $closureReason = "review CSV label and reviewStatus match expected pseudo-GT candidate closure"
    }

    $closureRows.Add([pscustomobject]@{
            candidateId = $candidate.candidateId
            frame = $candidate.frame
            candidateType = $candidateType
            expectedReviewLabel = $expectedLabel
            closureStatus = $closureStatus
            reviewLabel = $reviewLabel
            reviewStatus = $reviewStatus
            reviewSourcePredictionId = $reviewSourcePredictionId
            reviewMatchMode = $matchMode
            reviewIou = Format-Double $reviewIou
            eligibleReviewRowsOnFrame = $frameReviewDiagnostics.EligibleRowsOnFrame
            bestFrameReviewIou = Format-Double $frameReviewDiagnostics.BestFrameReviewIou
            fullFrameReviewStatus = $fullFrameReviewStatus
            fullFrameMissedFaceCount = $fullFrameMissedFaceCount
            fullFrameMissedRowsAdded = $fullFrameMissedRowsAdded
            fullFrameEvidenceNotes = $fullFrameEvidenceNotes
            fullFrameContinuityCandidateTypes = $fullFrameContinuityCandidateTypes
            fullFrameContinuityCandidateReasons = $fullFrameContinuityCandidateReasons
            fullFrameContinuityReviewPriority = $fullFrameContinuityReviewPriority
            fullFrameContinuityCandidateRanges = $fullFrameContinuityCandidateRanges
            fullFrameContinuityReviewHints = $fullFrameContinuityReviewHints
            baseFaceConfidence = $candidate.baseFaceConfidence
            tileFaceConfidence = $candidate.tileFaceConfidence
            tileSupportCount = $candidate.tileSupportCount
            faceVerificationConfidence = $candidate.faceVerificationConfidence
            faceVerificationDistance = $candidate.faceVerificationDistance
            personConfidence = $candidate.personConfidence
            personUpperOverlap = $candidate.personUpperOverlap
            personObjectClass = $candidate.personObjectClass
            tileEvidenceModel = $candidate.tileEvidenceModel
            tileEvidenceRunner = $candidate.tileEvidenceRunner
            faceVerificationEvidenceModel = $candidate.faceVerificationEvidenceModel
            faceVerificationEvidenceRunner = $candidate.faceVerificationEvidenceRunner
            personObjectEvidenceModel = $candidate.personObjectEvidenceModel
            personObjectEvidenceRunner = $candidate.personObjectEvidenceRunner
            auxiliarySignalRole = $candidate.auxiliarySignalRole
            supportFrameCount = $candidate.supportFrameCount
            supportRowCount = $candidate.supportRowCount
            supportSources = $candidate.supportSources
            supportEvidenceIds = $candidate.supportEvidenceIds
            bestIou = $candidate.bestIou
            centerDistanceRatio = $candidate.centerDistanceRatio
            areaChangeRatio = $candidate.areaChangeRatio
            centerXRatio = $candidate.centerXRatio
            centerYRatio = $candidate.centerYRatio
            baseAreaRatio = $candidate.baseAreaRatio
            aspectRatio = $candidate.aspectRatio
            geometryTag = $candidate.geometryTag
            fpProbability = $candidate.fpProbability
            missProbability = $candidate.missProbability
            pseudoGtReason = $candidate.pseudoGtReason
            reviewEvidenceNotes = $reviewEvidenceNotes
            closureReason = $closureReason
        }) | Out-Null
}

foreach ($path in @($outputPath, $summaryPathResolved)) {
    $dir = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

$closureRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath

$closed = @($closureRows | Where-Object { $_.closureStatus -eq "closed" }).Count
$unreviewed = @($closureRows | Where-Object { $_.closureStatus -eq "unreviewed" }).Count
$mismatch = @($closureRows | Where-Object { $_.closureStatus -eq "label-mismatch" }).Count
$supportedClosed = @($closureRows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" -and $_.closureStatus -eq "closed" }).Count
$falsePositiveClosed = @($closureRows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.closureStatus -eq "closed" }).Count
$missClosed = @($closureRows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.closureStatus -eq "closed" }).Count
$closureRowArray = $closureRows.ToArray()
$candidateTypeBreakdown = foreach ($candidateTypeName in @("supportedFaceCandidate", "falsePositiveCandidate", "missCandidate")) {
    New-CandidateTypeBreakdownLine -Rows $closureRowArray -CandidateType $candidateTypeName
}
$unreviewedReasonBreakdown = @($closureRows |
        Where-Object { $_.closureStatus -eq "unreviewed" } |
        Group-Object closureReason |
        Sort-Object -Property @{ Expression = "Count"; Descending = $true }, Name |
        Select-Object -First 8)
$unreviewedPreview = @($closureRows |
        Where-Object { $_.closureStatus -eq "unreviewed" } |
        Sort-Object { [int]$_.frame }, candidateId |
        Select-Object -First 12)
$closedEvidencePreview = @($closureRows |
        Where-Object { $_.closureStatus -eq "closed" } |
        Sort-Object { [int]$_.frame }, candidateId |
        Select-Object -First 12)

$summary = @(
    "# YOLO Pseudo-GT Review Closure",
    "",
    "This is test-only evidence. The app runtime path does not read this file.",
    "",
    "- pseudoGtCsv=$PseudoGtCsv",
    "- reviewCsv=$ReviewCsv",
    "- fullFrameReviewCsv=$FullFrameReviewCsv",
    "- candidates=$($closureRows.Count)",
    "- closed=$closed",
    "- unreviewed=$unreviewed",
    "- labelMismatch=$mismatch",
    "- supportedFaceClosed=$supportedClosed",
    "- falsePositiveClosed=$falsePositiveClosed",
    "- missClosed=$missClosed",
    "- minReviewIou=$($MinReviewIou.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture))",
    "",
    "## Candidate Type Breakdown",
    "",
    $candidateTypeBreakdown,
    "",
    "## Unreviewed Reason Breakdown",
    "",
    $(if ($unreviewedReasonBreakdown.Count -eq 0) {
        "- none"
    }
    else {
        $unreviewedReasonBreakdown | ForEach-Object { "- $($_.Name): $($_.Count)" }
    }),
    "",
    "## First Unreviewed Candidates",
    "",
    $(if ($unreviewedPreview.Count -eq 0) {
        "- none"
    }
    else {
        $unreviewedPreview | ForEach-Object {
            "- candidateId=$($_.candidateId), frame=$($_.frame), candidateType=$($_.candidateType), expected=$($_.expectedReviewLabel), reason=$($_.closureReason), fpProbability=$($_.fpProbability), missProbability=$($_.missProbability)"
        }
    }),
    "",
    "## Closed Evidence Snapshot",
    "",
    $(if ($closedEvidencePreview.Count -eq 0) {
        "- none"
    }
    else {
        $closedEvidencePreview | ForEach-Object {
            "- candidateId=$($_.candidateId), frame=$($_.frame), candidateType=$($_.candidateType), label=$($_.reviewLabel), base=$($_.baseFaceConfidence), tile=$($_.tileFaceConfidence)/support=$($_.tileSupportCount), verify=$($_.faceVerificationConfidence)/dist=$($_.faceVerificationDistance), iou=$($_.bestIou), center=$($_.centerDistanceRatio), area=$($_.areaChangeRatio), fpProbability=$($_.fpProbability), missProbability=$($_.missProbability), supportEvidenceIds=$($_.supportEvidenceIds), continuity=$($_.fullFrameContinuityCandidateTypes)"
        }
    }),
    "",
    "A pseudo-GT candidate is final only when the matching review CSV row has a human label, completed reviewStatus, and evidenceNotes.",
    "For missCandidate rows, the matching row should be a manual face/miss row in full-gt-review.csv and, when present, full-frame-review.csv must record a completed missed-face scan with missedFaceRowsAdded > 0 and evidenceNotes."
)
$summary | Set-Content -Encoding UTF8 -Path $summaryPathResolved

Write-Host "[YoloPseudoGtReviewClosure] candidates=$($closureRows.Count), closed=$closed, unreviewed=$unreviewed, labelMismatch=$mismatch, output=$OutputCsv"
Write-Host "[YoloPseudoGtReviewClosure] summary=$SummaryPath"

if ($RequireAllClosed.IsPresent -and ($unreviewed -gt 0 -or $mismatch -gt 0)) {
    throw "Pseudo-GT review closure is incomplete: unreviewed=$unreviewed, labelMismatch=$mismatch"
}
