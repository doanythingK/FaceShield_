param(
    [string]$ReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$RequireEvidence,
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

    $summary = Assert-ReviewedCsv -Path $reviewPath -RequireEvidenceNotes:$true -AllowMissingReview:$false
    Invoke-LabelVerifier -GtCsvPath $reviewPath -PredCsvPath $predictionPath -PredLogPath "" -Iou $MinIou -Misses 0 -FalsePositives 1 -LowIou 0
    Write-Host "[YoloFullGtReviewedVerify] pass selftest rows=$($summary.Rows), reviewed=$($summary.Reviewed), face=$($summary.Face), nonFace=$($summary.NonFace), unreviewed=$($summary.Unreviewed)"
    Write-Host "[YoloFullGtReviewedVerify] all requested checks passed"
    return
}

$reviewSummary = Assert-ReviewedCsv -Path $ReviewCsv -RequireEvidenceNotes:$RequireEvidence.IsPresent -AllowMissingReview:$AllowUnreviewed.IsPresent
Invoke-LabelVerifier -GtCsvPath $reviewSummary.Path -PredCsvPath (Resolve-RepoPath $PredictionCsv) -PredLogPath (Resolve-RepoPath $PredictionLog) -Iou $MinIou -Misses $MaxMisses -FalsePositives $MaxFalsePositives -LowIou $MaxLowIou

Write-Host "[YoloFullGtReviewedVerify] pass rows=$($reviewSummary.Rows), reviewed=$($reviewSummary.Reviewed), face=$($reviewSummary.Face), nonFace=$($reviewSummary.NonFace), unreviewed=$($reviewSummary.Unreviewed)"
Write-Host "[YoloFullGtReviewedVerify] all requested checks passed"
