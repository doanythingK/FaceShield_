param(
    [string]$ReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = "",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$RequireEvidence,
    [switch]$RequireFullFrameReview,
    [switch]$AllowUnreviewed,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$labelVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-label-state.ps1"

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

function Assert-ReviewedCsv {
    param(
        [string]$Path,
        [bool]$RequireEvidenceNotes,
        [bool]$AllowMissingReview
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Review CSV not found: $resolved"
    }

    $rows = @(Import-Csv $resolved)
    if ($rows.Count -eq 0) {
        throw "Review CSV has no rows: $resolved"
    }

    $validFaceLabels = @("face", "actualface", "true", "1")
    $validNonFaceLabels = @("nonface", "false", "0")
    $reviewedRows = 0
    $faceRows = 0
    $nonFaceRows = 0
    $unreviewedRows = 0

    foreach ($row in $rows) {
        $label = if ($null -ne $row.PSObject.Properties["label"]) { $row.label } else { "" }
        $reviewStatus = if ($null -ne $row.PSObject.Properties["reviewStatus"]) { $row.reviewStatus } else { "" }
        $evidenceNotes = if ($null -ne $row.PSObject.Properties["evidenceNotes"]) { $row.evidenceNotes } else { "" }

        if ([string]::IsNullOrWhiteSpace($label)) {
            $unreviewedRows++
            continue
        }

        $normalizedLabel = $label.Trim().ToLowerInvariant()
        if ($normalizedLabel -in $validFaceLabels) {
            $faceRows++
        }
        elseif ($normalizedLabel -in $validNonFaceLabels) {
            $nonFaceRows++
        }
        else {
            throw "Unsupported label '$label' at frame=$($row.frame), sourcePredictionId=$($row.sourcePredictionId)"
        }

        $reviewedRows++
        if (-not $AllowMissingReview -and [string]::IsNullOrWhiteSpace($reviewStatus)) {
            throw "reviewStatus is required at frame=$($row.frame), sourcePredictionId=$($row.sourcePredictionId)"
        }

        if ($RequireEvidenceNotes -and [string]::IsNullOrWhiteSpace($evidenceNotes)) {
            throw "evidenceNotes is required at frame=$($row.frame), sourcePredictionId=$($row.sourcePredictionId)"
        }
    }

    if (-not $AllowMissingReview -and $unreviewedRows -gt 0) {
        throw "Review CSV has unreviewed rows: $unreviewedRows"
    }

    if ($faceRows -eq 0) {
        throw "Review CSV has no face labels."
    }

    [pscustomobject]@{
        Rows = $rows.Count
        Reviewed = $reviewedRows
        Face = $faceRows
        NonFace = $nonFaceRows
        Unreviewed = $unreviewedRows
        Path = $resolved
    }
}

function Assert-FullFrameReviewCsv {
    param(
        [string]$Path,
        [int]$ManualMissRows,
        [hashtable]$ManualMissRowsByFrame,
        [bool]$RequireEvidenceNotes,
        [bool]$AllowMissingReview
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Full-frame review CSV not found: $resolved"
    }

    $rows = @(Import-Csv $resolved)
    if ($rows.Count -eq 0) {
        throw "Full-frame review CSV has no rows: $resolved"
    }

    $reviewedRows = 0
    $unreviewedRows = 0
    $declaredMisses = 0
    $declaredRowsAdded = 0
    foreach ($row in $rows) {
        $frame = 0
        if ($null -eq $row.PSObject.Properties["frame"] -or -not [int]::TryParse($row.frame, [ref]$frame)) {
            throw "Full-frame review row has invalid frame value: $($row.frame)"
        }

        $missedFaceCount = if ($null -ne $row.PSObject.Properties["missedFaceCount"]) { $row.missedFaceCount } else { "" }
        $missedFaceRowsAdded = if ($null -ne $row.PSObject.Properties["missedFaceRowsAdded"]) { $row.missedFaceRowsAdded } else { "" }
        $reviewStatus = if ($null -ne $row.PSObject.Properties["reviewStatus"]) { $row.reviewStatus } else { "" }
        $evidenceNotes = if ($null -ne $row.PSObject.Properties["evidenceNotes"]) { $row.evidenceNotes } else { "" }

        if ([string]::IsNullOrWhiteSpace($missedFaceCount)) {
            $unreviewedRows++
            continue
        }

        $missCount = 0
        if (-not [int]::TryParse($missedFaceCount.Trim(), [ref]$missCount) -or $missCount -lt 0) {
            throw "Invalid missedFaceCount '$missedFaceCount' at frame=$($row.frame)"
        }

        $rowsAdded = 0
        if ([string]::IsNullOrWhiteSpace($missedFaceRowsAdded)) {
            if (-not $AllowMissingReview) {
                throw "missedFaceRowsAdded is required at frame=$($row.frame)"
            }
        }
        elseif (-not [int]::TryParse($missedFaceRowsAdded.Trim(), [ref]$rowsAdded) -or $rowsAdded -lt 0) {
            throw "Invalid missedFaceRowsAdded '$missedFaceRowsAdded' at frame=$($row.frame)"
        }

        if ($missCount -ne $rowsAdded) {
            throw "Full-frame review frame=$frame has missedFaceCount=$missCount but missedFaceRowsAdded=$rowsAdded."
        }

        $manualRowsForFrame = if ($ManualMissRowsByFrame.ContainsKey($frame)) { $ManualMissRowsByFrame[$frame] } else { 0 }
        if ($rowsAdded -ne $manualRowsForFrame) {
            throw "Full-frame review frame=$frame declares missedFaceRowsAdded=$rowsAdded but review CSV has $manualRowsForFrame manual missed-face rows for that frame."
        }

        if (-not $AllowMissingReview -and [string]::IsNullOrWhiteSpace($reviewStatus)) {
            throw "full-frame reviewStatus is required at frame=$($row.frame)"
        }

        if ($RequireEvidenceNotes -and [string]::IsNullOrWhiteSpace($evidenceNotes)) {
            throw "full-frame evidenceNotes is required at frame=$($row.frame)"
        }

        $declaredMisses += $missCount
        $declaredRowsAdded += $rowsAdded
        $reviewedRows++
    }

    if (-not $AllowMissingReview -and $unreviewedRows -gt 0) {
        throw "Full-frame review CSV has unreviewed rows: $unreviewedRows"
    }

    if ($declaredMisses -ne $ManualMissRows) {
        throw "Full-frame review declares $declaredMisses missed faces but review CSV has $ManualMissRows manual missed-face rows."
    }

    if ($declaredRowsAdded -ne $ManualMissRows) {
        throw "Full-frame review declares $declaredRowsAdded missed-face rows added but review CSV has $ManualMissRows manual missed-face rows."
    }

    [pscustomobject]@{
        Rows = $rows.Count
        Reviewed = $reviewedRows
        Unreviewed = $unreviewedRows
        DeclaredMisses = $declaredMisses
        DeclaredRowsAdded = $declaredRowsAdded
        Path = $resolved
    }
}

function Invoke-LabelVerifier {
    param(
        [string]$GtCsvPath,
        [string]$PredCsvPath,
        [string]$PredLogPath,
        [double]$Iou,
        [int]$Misses,
        [int]$FalsePositives,
        [int]$LowIou
    )

    $args = @(
        "-GtCsv", $GtCsvPath,
        "-MinIou", "$Iou",
        "-MaxMisses", "$Misses",
        "-MaxFalsePositives", "$FalsePositives",
        "-MaxLowIou", "$LowIou",
        "-RequireData"
    )

    if (-not [string]::IsNullOrWhiteSpace($PredCsvPath)) {
        $args += @("-PredictionCsv", $PredCsvPath)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PredLogPath)) {
        $args += @("-PredictionLog", $PredLogPath)
    }
    else {
        throw "PredictionCsv or PredictionLog is required."
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $labelVerifier @args
    if ($LASTEXITCODE -ne 0) {
        throw "Full GT label verifier failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $labelVerifier)) {
    throw "Full GT label verifier not found: $labelVerifier"
}

if ($SelfTest) {
    $selfTestDir = Join-Path $repo ".tmp\yolo-full-gt-reviewed-state"
    New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

    $reviewPath = Join-Path $selfTestDir "reviewed.csv"
    @(
        [pscustomobject]@{ frame = 10; gtId = "gt-a"; label = "face"; x = "100"; y = "100"; w = "80"; h = "80"; sourcePredictionId = "pred-a"; sourceConfidence = "0.92"; cropPath = "synthetic-a.png"; reviewStatus = "pass"; evidenceNotes = "synthetic face" },
        [pscustomobject]@{ frame = 20; gtId = ""; label = "nonface"; x = "300"; y = "200"; w = "40"; h = "40"; sourcePredictionId = "pred-fp"; sourceConfidence = "0.30"; cropPath = "synthetic-b.png"; reviewStatus = "pass"; evidenceNotes = "synthetic nonface" }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewPath

    $predictionPath = Join-Path $selfTestDir "predictions.csv"
    @(
        [pscustomobject]@{ frame = 10; predictionId = "pred-a"; x = "104"; y = "104"; w = "78"; h = "78"; confidence = "0.92" },
        [pscustomobject]@{ frame = 20; predictionId = "pred-fp"; x = "300"; y = "200"; w = "40"; h = "40"; confidence = "0.30" }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $predictionPath

    $frameReviewPath = Join-Path $selfTestDir "full-frame-review.csv"
    @(
        [pscustomobject]@{ frame = 10; frameImagePath = "synthetic-frame-a.png"; detectedCandidateCount = "1"; missedFaceCount = "0"; missedFaceRowsAdded = "0"; reviewStatus = "pass"; evidenceNotes = "synthetic frame reviewed" },
        [pscustomobject]@{ frame = 20; frameImagePath = "synthetic-frame-b.png"; detectedCandidateCount = "1"; missedFaceCount = "0"; missedFaceRowsAdded = "0"; reviewStatus = "pass"; evidenceNotes = "synthetic frame reviewed" }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameReviewPath

    $summary = Assert-ReviewedCsv -Path $reviewPath -RequireEvidenceNotes:$true -AllowMissingReview:$false
    $frameSummary = Assert-FullFrameReviewCsv -Path $frameReviewPath -ManualMissRows 0 -ManualMissRowsByFrame @{} -RequireEvidenceNotes:$true -AllowMissingReview:$false
    Invoke-LabelVerifier -GtCsvPath $reviewPath -PredCsvPath $predictionPath -PredLogPath "" -Iou $MinIou -Misses 0 -FalsePositives 1 -LowIou 0

    $missReviewPath = Join-Path $selfTestDir "reviewed-with-manual-miss.csv"
    @(
        [pscustomobject]@{ frame = 10; gtId = "gt-a"; label = "face"; x = "100"; y = "100"; w = "80"; h = "80"; sourcePredictionId = "pred-a"; sourceConfidence = "0.92"; source = "prediction"; cropPath = "synthetic-a.png"; reviewStatus = "pass"; evidenceNotes = "synthetic face" },
        [pscustomobject]@{ frame = 20; gtId = ""; label = "nonface"; x = "300"; y = "200"; w = "40"; h = "40"; sourcePredictionId = "pred-fp"; sourceConfidence = "0.30"; source = "prediction"; cropPath = "synthetic-b.png"; reviewStatus = "pass"; evidenceNotes = "synthetic nonface" },
        [pscustomobject]@{ frame = 30; gtId = "manual-miss-a"; label = "face"; x = "500"; y = "200"; w = "50"; h = "50"; sourcePredictionId = ""; sourceConfidence = ""; source = "manual-missed"; cropPath = ""; reviewStatus = "pass"; evidenceNotes = "synthetic manual missed face" }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $missReviewPath

    $missFrameReviewPath = Join-Path $selfTestDir "full-frame-review-with-miss.csv"
    @(
        [pscustomobject]@{ frame = 10; frameImagePath = "synthetic-frame-a.png"; detectedCandidateCount = "1"; missedFaceCount = "0"; missedFaceRowsAdded = "0"; reviewStatus = "pass"; evidenceNotes = "synthetic frame reviewed" },
        [pscustomobject]@{ frame = 20; frameImagePath = "synthetic-frame-b.png"; detectedCandidateCount = "1"; missedFaceCount = "0"; missedFaceRowsAdded = "0"; reviewStatus = "pass"; evidenceNotes = "synthetic frame reviewed" },
        [pscustomobject]@{ frame = 30; frameImagePath = "synthetic-frame-c.png"; detectedCandidateCount = "0"; missedFaceCount = "1"; missedFaceRowsAdded = "1"; reviewStatus = "pass"; evidenceNotes = "synthetic missed face row added" }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $missFrameReviewPath

    $missSummary = Assert-ReviewedCsv -Path $missReviewPath -RequireEvidenceNotes:$true -AllowMissingReview:$false
    $missFrameSummary = Assert-FullFrameReviewCsv -Path $missFrameReviewPath -ManualMissRows 1 -ManualMissRowsByFrame @{ 30 = 1 } -RequireEvidenceNotes:$true -AllowMissingReview:$false
    Invoke-LabelVerifier -GtCsvPath $missReviewPath -PredCsvPath $predictionPath -PredLogPath "" -Iou $MinIou -Misses 1 -FalsePositives 1 -LowIou 0

    Write-Host "[YoloFullGtReviewedVerify] pass selftest rows=$($summary.Rows), reviewed=$($summary.Reviewed), face=$($summary.Face), nonFace=$($summary.NonFace), unreviewed=$($summary.Unreviewed)"
    Write-Host "[YoloFullGtFrameReviewedVerify] pass selftest rows=$($frameSummary.Rows), reviewed=$($frameSummary.Reviewed), declaredMisses=$($frameSummary.DeclaredMisses), declaredRowsAdded=$($frameSummary.DeclaredRowsAdded), unreviewed=$($frameSummary.Unreviewed)"
    Write-Host "[YoloFullGtFrameReviewedVerify] pass manual-miss selftest rows=$($missSummary.Rows), reviewed=$($missSummary.Reviewed), declaredMisses=$($missFrameSummary.DeclaredMisses), declaredRowsAdded=$($missFrameSummary.DeclaredRowsAdded), unreviewed=$($missFrameSummary.Unreviewed)"
    Write-Host "[YoloFullGtReviewedVerify] all requested checks passed"
    return
}

$reviewSummary = Assert-ReviewedCsv -Path $ReviewCsv -RequireEvidenceNotes:$RequireEvidence.IsPresent -AllowMissingReview:$AllowUnreviewed.IsPresent
$manualMissRows = 0
if ($RequireFullFrameReview) {
    if ([string]::IsNullOrWhiteSpace($FullFrameReviewCsv)) {
        $reviewDir = Split-Path -Parent $reviewSummary.Path
        $FullFrameReviewCsv = Join-Path $reviewDir "full-frame-review.csv"
    }

    $reviewRows = @(Import-Csv $reviewSummary.Path)
    $manualMissRowsByFrame = @{}
    $manualMissRows = 0
    foreach ($manualRow in @($reviewRows | Where-Object {
        $label = if ($null -ne $_.PSObject.Properties["label"]) { $_.label } else { "" }
        $sourcePredictionId = if ($null -ne $_.PSObject.Properties["sourcePredictionId"]) { $_.sourcePredictionId } else { "" }
        $source = if ($null -ne $_.PSObject.Properties["source"]) { $_.source } else { "" }
        $normalizedLabel = $label.Trim().ToLowerInvariant()
        ($normalizedLabel -in @("face", "actualface", "true", "1")) -and
            ([string]::IsNullOrWhiteSpace($sourcePredictionId) -or $source -eq "manual-missed")
    })) {
        $frame = 0
        if (-not [int]::TryParse($manualRow.frame, [ref]$frame)) {
            throw "Manual missed-face row has invalid frame value: $($manualRow.frame)"
        }

        if (-not $manualMissRowsByFrame.ContainsKey($frame)) {
            $manualMissRowsByFrame[$frame] = 0
        }

        $manualMissRowsByFrame[$frame]++
        $manualMissRows++
    }

    $frameSummary = Assert-FullFrameReviewCsv -Path $FullFrameReviewCsv -ManualMissRows $manualMissRows -ManualMissRowsByFrame $manualMissRowsByFrame -RequireEvidenceNotes:$RequireEvidence.IsPresent -AllowMissingReview:$AllowUnreviewed.IsPresent
}
Invoke-LabelVerifier -GtCsvPath $reviewSummary.Path -PredCsvPath (Resolve-RepoPath $PredictionCsv) -PredLogPath (Resolve-RepoPath $PredictionLog) -Iou $MinIou -Misses $MaxMisses -FalsePositives $MaxFalsePositives -LowIou $MaxLowIou

Write-Host "[YoloFullGtReviewedVerify] pass rows=$($reviewSummary.Rows), reviewed=$($reviewSummary.Reviewed), face=$($reviewSummary.Face), nonFace=$($reviewSummary.NonFace), unreviewed=$($reviewSummary.Unreviewed)"
if ($RequireFullFrameReview) {
    Write-Host "[YoloFullGtFrameReviewedVerify] pass rows=$($frameSummary.Rows), reviewed=$($frameSummary.Reviewed), declaredMisses=$($frameSummary.DeclaredMisses), declaredRowsAdded=$($frameSummary.DeclaredRowsAdded), manualMissRows=$manualMissRows, unreviewed=$($frameSummary.Unreviewed)"
}
Write-Host "[YoloFullGtReviewedVerify] all requested checks passed"
