param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\close-yolo-pseudo-gt-review.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-review-closure-verify"
$pseudoCsv = Join-Path $work "pseudo-gt-candidates.csv"
$reviewCsv = Join-Path $work "full-gt-review.csv"
$fullFrameCsv = Join-Path $work "full-frame-review.csv"
$outputCsv = Join-Path $work "pseudo-gt-review-closure.csv"
$summaryPath = Join-Path $work "pseudo-gt-review-closure-summary.md"
$unreviewedReviewCsv = Join-Path $work "full-gt-review-unreviewed.csv"
$unreviewedOutputCsv = Join-Path $work "pseudo-gt-review-closure-unreviewed.csv"
$unreviewedSummaryPath = Join-Path $work "pseudo-gt-review-closure-unreviewed-summary.md"
$mismatchReviewCsv = Join-Path $work "full-gt-review-label-mismatch.csv"
$mismatchOutputCsv = Join-Path $work "pseudo-gt-review-closure-label-mismatch.csv"
$mismatchSummaryPath = Join-Path $work "pseudo-gt-review-closure-label-mismatch-summary.md"
$sourceIdWrongGeometryReviewCsv = Join-Path $work "full-gt-review-source-id-wrong-geometry.csv"
$sourceIdWrongGeometryOutputCsv = Join-Path $work "pseudo-gt-review-closure-source-id-wrong-geometry.csv"
$sourceIdWrongGeometrySummaryPath = Join-Path $work "pseudo-gt-review-closure-source-id-wrong-geometry-summary.md"
$pendingStatusReviewCsv = Join-Path $work "full-gt-review-pending-status.csv"
$pendingStatusOutputCsv = Join-Path $work "pseudo-gt-review-closure-pending-status.csv"
$pendingStatusSummaryPath = Join-Path $work "pseudo-gt-review-closure-pending-status-summary.md"
$blankEvidenceReviewCsv = Join-Path $work "full-gt-review-blank-evidence.csv"
$blankEvidenceOutputCsv = Join-Path $work "pseudo-gt-review-closure-blank-evidence.csv"
$blankEvidenceSummaryPath = Join-Path $work "pseudo-gt-review-closure-blank-evidence-summary.md"
$incompleteFullFrameCsv = Join-Path $work "full-frame-review-incomplete-miss.csv"
$incompleteFullFrameOutputCsv = Join-Path $work "pseudo-gt-review-closure-incomplete-full-frame.csv"
$incompleteFullFrameSummaryPath = Join-Path $work "pseudo-gt-review-closure-incomplete-full-frame-summary.md"
$blankFullFrameEvidenceCsv = Join-Path $work "full-frame-review-blank-evidence.csv"
$blankFullFrameEvidenceOutputCsv = Join-Path $work "pseudo-gt-review-closure-blank-full-frame-evidence.csv"
$blankFullFrameEvidenceSummaryPath = Join-Path $work "pseudo-gt-review-closure-blank-full-frame-evidence-summary.md"
$explicitMissReviewCsv = Join-Path $work "full-gt-review-explicit-miss.csv"
$explicitMissOutputCsv = Join-Path $work "pseudo-gt-review-closure-explicit-miss.csv"
$explicitMissSummaryPath = Join-Path $work "pseudo-gt-review-closure-explicit-miss-summary.md"

function Assert-File {
    param([string]$Name, [string]$Path)

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloPseudoGtReviewClosureVerify] pass $Name"
}

function Invoke-ReviewClosure {
    param(
        [string]$ReviewPath,
        [string]$OutputPath,
        [string]$SummaryOutputPath,
        [string]$FullFrameReviewPath = $fullFrameCsv,
        [switch]$RequireAllClosed
    )

    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $script,
        "-PseudoGtCsv",
        $pseudoCsv,
        "-ReviewCsv",
        $ReviewPath,
        "-FullFrameReviewCsv",
        $FullFrameReviewPath,
        "-OutputCsv",
        $OutputPath,
        "-SummaryPath",
        $SummaryOutputPath
    )
    if ($RequireAllClosed) {
        $arguments += "-RequireAllClosed"
    }

    & powershell.exe @arguments 2>&1
}

function Invoke-ExpectedReviewClosureFailure {
    param(
        [string]$Name,
        [string]$ReviewPath,
        [string]$OutputPath,
        [string]$SummaryOutputPath,
        [string]$FullFrameReviewPath = $fullFrameCsv,
        [string]$ExpectedPattern
    )

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $failureOutput = Invoke-ReviewClosure `
            -ReviewPath $ReviewPath `
            -OutputPath $OutputPath `
            -SummaryOutputPath $SummaryOutputPath `
            -FullFrameReviewPath $FullFrameReviewPath `
            -RequireAllClosed
        $failureExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }

    if ($failureExitCode -eq 0) {
        throw "$Name expected RequireAllClosed to fail."
    }

    Assert-Contains $Name ($failureOutput | Out-String) $ExpectedPattern
}

Assert-File "review closure script" $script

New-Item -ItemType Directory -Force -Path $work | Out-Null

@(
    [pscustomobject]@{
        candidateId = "base-2-0"
        frame = 2
        candidateType = "supportedFaceCandidate"
        source = "base-yolo"
        basePredictionId = "0"
        tileDetectionId = "tile-face-2"
        verificationId = "verify-face-2"
        x = 10
        y = 20
        w = 50
        h = 60
        baseFaceConfidence = 0.41
        tileFaceConfidence = 0.92
        tileSupportCount = 3
        faceVerificationConfidence = 0.89
        faceVerificationDistance = 0.21
        personConfidence = 0
        personUpperOverlap = 0
        personObjectClass = ""
        tileEvidenceModel = "heavy-tile-face-v1"
        tileEvidenceRunner = "tile-runner-local"
        faceVerificationEvidenceModel = "arcface-local-v1"
        faceVerificationEvidenceRunner = "face-verify-runner"
        personObjectEvidenceModel = ""
        personObjectEvidenceRunner = ""
        auxiliarySignalRole = ""
        supportFrameCount = 2
        supportRowCount = 3
        supportSources = "tile+verification"
        supportEvidenceIds = "tile-face:2:tile-face-2;face-verification:2:verify-face-2"
        bestIou = 0.9
        centerDistanceRatio = 0.1
        areaChangeRatio = 1.02
        centerXRatio = 0.125
        centerYRatio = 0.15
        baseAreaRatio = 0.003
        aspectRatio = 0.833
        geometryTag = ""
        fpProbability = 0.1
        missProbability = 0
        pseudoGtReason = "supported"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    },
    [pscustomobject]@{
        candidateId = "base-6-0"
        frame = 6
        candidateType = "falsePositiveCandidate"
        source = "base-yolo"
        basePredictionId = "0"
        tileDetectionId = ""
        verificationId = ""
        x = 500
        y = 440
        w = 180
        h = 180
        baseFaceConfidence = 0.22
        tileFaceConfidence = 0
        tileSupportCount = 0
        faceVerificationConfidence = 0
        faceVerificationDistance = 1
        personConfidence = 0.72
        personUpperOverlap = 0.62
        personObjectClass = "person"
        tileEvidenceModel = ""
        tileEvidenceRunner = ""
        faceVerificationEvidenceModel = ""
        faceVerificationEvidenceRunner = ""
        personObjectEvidenceModel = "heavy-person-object-v1"
        personObjectEvidenceRunner = "person-object-runner"
        auxiliarySignalRole = "priority-only-not-face-evidence"
        supportFrameCount = 0
        supportRowCount = 0
        supportSources = ""
        bestIou = 0
        centerDistanceRatio = 99
        areaChangeRatio = 99
        centerXRatio = 0.795
        centerYRatio = 0.095
        baseAreaRatio = 0.055
        aspectRatio = 1
        geometryTag = "top-edge-large-review"
        fpProbability = 0.85
        missProbability = 0.1
        pseudoGtReason = "unsupported"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    },
    [pscustomobject]@{
        candidateId = "miss-7-tile-face-7"
        frame = 7
        candidateType = "missCandidate"
        source = "tile-face"
        basePredictionId = ""
        tileDetectionId = "tile-face-7"
        verificationId = ""
        x = 60
        y = 80
        w = 24
        h = 28
        baseFaceConfidence = 0
        tileFaceConfidence = 0.87
        tileSupportCount = 2
        faceVerificationConfidence = 0
        faceVerificationDistance = 1
        personConfidence = 0
        personUpperOverlap = 0
        personObjectClass = ""
        tileEvidenceModel = "heavy-tile-face-v1"
        tileEvidenceRunner = "tile-runner-local"
        faceVerificationEvidenceModel = ""
        faceVerificationEvidenceRunner = ""
        personObjectEvidenceModel = ""
        personObjectEvidenceRunner = ""
        auxiliarySignalRole = ""
        supportFrameCount = 2
        supportRowCount = 2
        supportSources = "tile"
        supportEvidenceIds = "tile-face:7:tile-face-7;tile-face:8:tile-face-8"
        bestIou = 0
        centerDistanceRatio = 99
        areaChangeRatio = 99
        centerXRatio = ""
        centerYRatio = ""
        baseAreaRatio = ""
        aspectRatio = ""
        geometryTag = ""
        fpProbability = 0
        missProbability = 0.82
        pseudoGtReason = "miss"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoCsv

@(
    [pscustomobject]@{ frame = 2; gtId = ""; label = "face"; x = 10; y = 20; w = 50; h = 60; sourcePredictionId = "0"; sourceConfidence = 0.41; source = "smoke-detection-log"; cropPath = ""; cropX = 0; cropY = 0; cropW = 80; cropH = 90; reviewStatus = "pass"; evidenceNotes = "visible face"; notes = "" },
    [pscustomobject]@{ frame = 6; gtId = ""; label = "nonface"; x = 500; y = 440; w = 180; h = 180; sourcePredictionId = "0"; sourceConfidence = 0.22; source = "smoke-detection-log"; cropPath = ""; cropX = 0; cropY = 0; cropW = 220; cropH = 220; reviewStatus = "pass"; evidenceNotes = "background artifact"; notes = "" },
    [pscustomobject]@{ frame = 7; gtId = "manual-miss-7"; label = "face"; x = 61; y = 81; w = 24; h = 28; sourcePredictionId = ""; sourceConfidence = ""; source = "manual-miss"; cropPath = ""; cropX = 0; cropY = 0; cropW = 80; cropH = 90; reviewStatus = "pass"; evidenceNotes = "visible missed face"; notes = "" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

@(
    [pscustomobject]@{ frame = 7; frameImagePath = ""; overlayFrameImagePath = ""; detectedCandidateCount = 0; candidateSummary = ""; missedFaceCount = 1; missedFaceRowsAdded = 1; reviewStatus = "pass"; evidenceNotes = "manual miss added"; notes = "" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $fullFrameCsv

$output = Invoke-ReviewClosure `
    -ReviewPath $reviewCsv `
    -OutputPath $outputCsv `
    -SummaryOutputPath $summaryPath `
    -RequireAllClosed
if ($LASTEXITCODE -ne 0) {
    throw "close-yolo-pseudo-gt-review.ps1 failed: $($output | Out-String)"
}

Assert-File "closure output CSV" $outputCsv
Assert-File "closure summary" $summaryPath

$rows = @(Import-Csv $outputCsv)
if ($rows.Count -ne 3) {
    throw "Expected 3 closure rows, actual=$($rows.Count)"
}

if (@($rows | Where-Object { $_.closureStatus -ne "closed" }).Count -ne 0) {
    throw "Expected all closure rows to be closed."
}

foreach ($column in @(
        "candidateId",
        "candidateType",
        "expectedReviewLabel",
        "closureStatus",
        "reviewLabel",
        "reviewStatus",
        "reviewIou",
        "eligibleReviewRowsOnFrame",
        "bestFrameReviewIou",
        "fullFrameEvidenceNotes",
        "personConfidence",
        "personUpperOverlap",
        "personObjectClass",
        "tileEvidenceModel",
        "tileEvidenceRunner",
        "faceVerificationEvidenceModel",
        "faceVerificationEvidenceRunner",
        "personObjectEvidenceModel",
        "personObjectEvidenceRunner",
        "auxiliarySignalRole",
        "supportFrameCount",
        "supportRowCount",
        "supportSources",
        "supportEvidenceIds",
        "bestIou",
        "centerDistanceRatio",
        "areaChangeRatio",
        "centerXRatio",
        "centerYRatio",
        "baseAreaRatio",
        "aspectRatio",
        "geometryTag",
        "closureReason")) {
    if ($null -eq $rows[0].PSObject.Properties[$column]) {
        throw "Missing closure column: $column"
    }
}

$supportedClosure = @($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" })[0]
if ($supportedClosure.supportFrameCount -ne "2" -or $supportedClosure.supportRowCount -ne "3" -or $supportedClosure.supportSources -ne "tile+verification") {
    throw "Expected closure output to preserve repeated pseudo-GT support evidence."
}

if ($supportedClosure.supportEvidenceIds -ne "tile-face:2:tile-face-2;face-verification:2:verify-face-2") {
    throw "Expected closure output to preserve concrete pseudo-GT support evidence ids."
}

if ($supportedClosure.reviewMatchMode -ne "sourcePredictionId+iou") {
    throw "Expected sourcePredictionId closure to require geometry IoU support, actual=$($supportedClosure.reviewMatchMode)."
}

if ($supportedClosure.areaChangeRatio -ne "1.02") {
    throw "Expected closure output to preserve support area-ratio evidence."
}

if ($supportedClosure.tileEvidenceModel -ne "heavy-tile-face-v1" -or $supportedClosure.faceVerificationEvidenceRunner -ne "face-verify-runner") {
    throw "Expected closure output to preserve tile/face-verification model and runner provenance."
}

$falsePositiveClosure = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" })[0]
if ($falsePositiveClosure.personUpperOverlap -ne "0.62") {
    throw "Expected closure output to preserve auxiliary person/object overlap evidence."
}

if ($falsePositiveClosure.personObjectClass -ne "person") {
    throw "Expected closure output to preserve auxiliary person/object class evidence."
}

if ($falsePositiveClosure.personObjectEvidenceModel -ne "heavy-person-object-v1" -or $falsePositiveClosure.personObjectEvidenceRunner -ne "person-object-runner") {
    throw "Expected closure output to preserve person/object model and runner provenance."
}

if ($falsePositiveClosure.auxiliarySignalRole -ne "priority-only-not-face-evidence") {
    throw "Expected closure output to preserve auxiliary signal role as priority-only."
}

if ($falsePositiveClosure.geometryTag -ne "top-edge-large-review") {
    throw "Expected closure output to preserve top-edge large geometry review tag."
}

if ($falsePositiveClosure.centerYRatio -ne "0.095" -or $falsePositiveClosure.baseAreaRatio -ne "0.055" -or $falsePositiveClosure.aspectRatio -ne "1") {
    throw "Expected closure output to preserve normalized top-edge geometry evidence."
}

$unreviewedReviewRows = @(Import-Csv $reviewCsv)
(@($unreviewedReviewRows | Where-Object { $_.frame -eq "6" }))[0].label = ""
$unreviewedReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $unreviewedReviewCsv

$unreviewedOutput = Invoke-ReviewClosure `
    -ReviewPath $unreviewedReviewCsv `
    -OutputPath $unreviewedOutputCsv `
    -SummaryOutputPath $unreviewedSummaryPath
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict unreviewed closure run to pass: $($unreviewedOutput | Out-String)"
}

$unreviewedRows = @(Import-Csv $unreviewedOutputCsv)
$unreviewedSummaryText = Get-Content -Raw -Path $unreviewedSummaryPath
Assert-Contains "summary records unreviewed row" $unreviewedSummaryText "unreviewed=1"
if (@($unreviewedRows | Where-Object { $_.closureStatus -eq "unreviewed" }).Count -ne 1) {
    throw "Expected exactly one unreviewed closure row."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks unreviewed rows" `
    -ReviewPath $unreviewedReviewCsv `
    -OutputPath $unreviewedOutputCsv `
    -SummaryOutputPath $unreviewedSummaryPath `
    -ExpectedPattern "unreviewed=1"

$mismatchReviewRows = @(Import-Csv $reviewCsv)
(@($mismatchReviewRows | Where-Object { $_.frame -eq "6" }))[0].label = "face"
$mismatchReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $mismatchReviewCsv

$mismatchOutput = Invoke-ReviewClosure `
    -ReviewPath $mismatchReviewCsv `
    -OutputPath $mismatchOutputCsv `
    -SummaryOutputPath $mismatchSummaryPath
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict label-mismatch closure run to pass: $($mismatchOutput | Out-String)"
}

$mismatchRows = @(Import-Csv $mismatchOutputCsv)
$mismatchSummaryText = Get-Content -Raw -Path $mismatchSummaryPath
Assert-Contains "summary records label mismatch row" $mismatchSummaryText "labelMismatch=1"
if (@($mismatchRows | Where-Object { $_.closureStatus -eq "label-mismatch" }).Count -ne 1) {
    throw "Expected exactly one label-mismatch closure row."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks label mismatch rows" `
    -ReviewPath $mismatchReviewCsv `
    -OutputPath $mismatchOutputCsv `
    -SummaryOutputPath $mismatchSummaryPath `
    -ExpectedPattern "labelMismatch=1"

$sourceIdWrongGeometryReviewRows = @(Import-Csv $reviewCsv)
$sourceIdWrongGeometryRow = (@($sourceIdWrongGeometryReviewRows | Where-Object { $_.frame -eq "2" }))[0]
$sourceIdWrongGeometryRow.x = 300
$sourceIdWrongGeometryRow.y = 300
$sourceIdWrongGeometryRow.w = 50
$sourceIdWrongGeometryRow.h = 60
$sourceIdWrongGeometryReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $sourceIdWrongGeometryReviewCsv

$sourceIdWrongGeometryOutput = Invoke-ReviewClosure `
    -ReviewPath $sourceIdWrongGeometryReviewCsv `
    -OutputPath $sourceIdWrongGeometryOutputCsv `
    -SummaryOutputPath $sourceIdWrongGeometrySummaryPath
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict source-id wrong-geometry closure run to pass: $($sourceIdWrongGeometryOutput | Out-String)"
}

$sourceIdWrongGeometryRows = @(Import-Csv $sourceIdWrongGeometryOutputCsv)
$sourceIdWrongGeometrySummaryText = Get-Content -Raw -Path $sourceIdWrongGeometrySummaryPath
Assert-Contains "summary records wrong source id geometry row" $sourceIdWrongGeometrySummaryText "unreviewed=1"
if (@($sourceIdWrongGeometryRows | Where-Object { $_.closureStatus -eq "unreviewed" -and $_.reviewMatchMode -eq "none" }).Count -ne 1) {
    throw "Expected sourcePredictionId with wrong geometry to leave one closure row unreviewed."
}
if (@($sourceIdWrongGeometryRows | Where-Object { $_.closureStatus -eq "unreviewed" -and $_.closureReason -eq "no matching reviewed geometry on candidate frame" -and $_.eligibleReviewRowsOnFrame -eq "1" }).Count -ne 1) {
    throw "Expected sourcePredictionId wrong-geometry closure to report frame review coverage and geometry mismatch."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks source id wrong geometry rows" `
    -ReviewPath $sourceIdWrongGeometryReviewCsv `
    -OutputPath $sourceIdWrongGeometryOutputCsv `
    -SummaryOutputPath $sourceIdWrongGeometrySummaryPath `
    -ExpectedPattern "unreviewed=1"

$pendingStatusReviewRows = @(Import-Csv $reviewCsv)
(@($pendingStatusReviewRows | Where-Object { $_.frame -eq "2" }))[0].reviewStatus = "pending-human"
$pendingStatusReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pendingStatusReviewCsv

$pendingStatusOutput = Invoke-ReviewClosure `
    -ReviewPath $pendingStatusReviewCsv `
    -OutputPath $pendingStatusOutputCsv `
    -SummaryOutputPath $pendingStatusSummaryPath
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict pending-status closure run to pass: $($pendingStatusOutput | Out-String)"
}

$pendingStatusRows = @(Import-Csv $pendingStatusOutputCsv)
$pendingStatusSummaryText = Get-Content -Raw -Path $pendingStatusSummaryPath
Assert-Contains "summary records pending review status row" $pendingStatusSummaryText "unreviewed=1"
if (@($pendingStatusRows | Where-Object { $_.closureStatus -eq "unreviewed" -and $_.closureReason -match "pending-human" }).Count -ne 1) {
    throw "Expected pending reviewStatus to keep one closure row unreviewed."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks pending review status rows" `
    -ReviewPath $pendingStatusReviewCsv `
    -OutputPath $pendingStatusOutputCsv `
    -SummaryOutputPath $pendingStatusSummaryPath `
    -ExpectedPattern "unreviewed=1"

$blankEvidenceReviewRows = @(Import-Csv $reviewCsv)
(@($blankEvidenceReviewRows | Where-Object { $_.frame -eq "2" }))[0].evidenceNotes = ""
$blankEvidenceReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $blankEvidenceReviewCsv

$blankEvidenceOutput = Invoke-ReviewClosure `
    -ReviewPath $blankEvidenceReviewCsv `
    -OutputPath $blankEvidenceOutputCsv `
    -SummaryOutputPath $blankEvidenceSummaryPath
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict blank-evidence closure run to pass: $($blankEvidenceOutput | Out-String)"
}

$blankEvidenceRows = @(Import-Csv $blankEvidenceOutputCsv)
$blankEvidenceSummaryText = Get-Content -Raw -Path $blankEvidenceSummaryPath
Assert-Contains "summary records blank evidence notes row" $blankEvidenceSummaryText "unreviewed=1"
if (@($blankEvidenceRows | Where-Object { $_.closureStatus -eq "unreviewed" -and $_.closureReason -match "evidenceNotes" }).Count -ne 1) {
    throw "Expected blank evidenceNotes to keep one closure row unreviewed."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks blank evidence notes rows" `
    -ReviewPath $blankEvidenceReviewCsv `
    -OutputPath $blankEvidenceOutputCsv `
    -SummaryOutputPath $blankEvidenceSummaryPath `
    -ExpectedPattern "unreviewed=1"

$incompleteFullFrameRows = @(Import-Csv $fullFrameCsv)
(@($incompleteFullFrameRows | Where-Object { $_.frame -eq "7" }))[0].missedFaceRowsAdded = 0
$incompleteFullFrameRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $incompleteFullFrameCsv

$incompleteFullFrameOutput = Invoke-ReviewClosure `
    -ReviewPath $reviewCsv `
    -OutputPath $incompleteFullFrameOutputCsv `
    -SummaryOutputPath $incompleteFullFrameSummaryPath `
    -FullFrameReviewPath $incompleteFullFrameCsv
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict incomplete full-frame closure run to pass: $($incompleteFullFrameOutput | Out-String)"
}

$incompleteFullFrameRowsOut = @(Import-Csv $incompleteFullFrameOutputCsv)
$incompleteFullFrameSummaryText = Get-Content -Raw -Path $incompleteFullFrameSummaryPath
Assert-Contains "summary records incomplete full-frame miss scan" $incompleteFullFrameSummaryText "unreviewed=1"
if (@($incompleteFullFrameRowsOut | Where-Object { $_.closureReason -match "missed-face scan" }).Count -ne 1) {
    throw "Expected incomplete full-frame missed-face scan to keep missCandidate unreviewed."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks incomplete full-frame miss scan" `
    -ReviewPath $reviewCsv `
    -OutputPath $incompleteFullFrameOutputCsv `
    -SummaryOutputPath $incompleteFullFrameSummaryPath `
    -FullFrameReviewPath $incompleteFullFrameCsv `
    -ExpectedPattern "unreviewed=1"

$blankFullFrameEvidenceRows = @(Import-Csv $fullFrameCsv)
(@($blankFullFrameEvidenceRows | Where-Object { $_.frame -eq "7" }))[0].evidenceNotes = ""
$blankFullFrameEvidenceRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $blankFullFrameEvidenceCsv

$blankFullFrameEvidenceOutput = Invoke-ReviewClosure `
    -ReviewPath $reviewCsv `
    -OutputPath $blankFullFrameEvidenceOutputCsv `
    -SummaryOutputPath $blankFullFrameEvidenceSummaryPath `
    -FullFrameReviewPath $blankFullFrameEvidenceCsv
if ($LASTEXITCODE -ne 0) {
    throw "Expected non-strict blank full-frame evidence closure run to pass: $($blankFullFrameEvidenceOutput | Out-String)"
}

$blankFullFrameEvidenceRowsOut = @(Import-Csv $blankFullFrameEvidenceOutputCsv)
$blankFullFrameEvidenceSummaryText = Get-Content -Raw -Path $blankFullFrameEvidenceSummaryPath
Assert-Contains "summary records blank full-frame evidence notes row" $blankFullFrameEvidenceSummaryText "unreviewed=1"
if (@($blankFullFrameEvidenceRowsOut | Where-Object { $_.closureReason -match "evidenceNotes" }).Count -ne 1) {
    throw "Expected blank full-frame evidenceNotes to keep missCandidate unreviewed."
}
Invoke-ExpectedReviewClosureFailure `
    -Name "strict mode blocks blank full-frame evidence notes rows" `
    -ReviewPath $reviewCsv `
    -OutputPath $blankFullFrameEvidenceOutputCsv `
    -SummaryOutputPath $blankFullFrameEvidenceSummaryPath `
    -FullFrameReviewPath $blankFullFrameEvidenceCsv `
    -ExpectedPattern "unreviewed=1"

$explicitMissReviewRows = @(Import-Csv $reviewCsv)
(@($explicitMissReviewRows | Where-Object { $_.frame -eq "7" }))[0].label = "miss"
$explicitMissReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $explicitMissReviewCsv

$explicitMissOutput = Invoke-ReviewClosure `
    -ReviewPath $explicitMissReviewCsv `
    -OutputPath $explicitMissOutputCsv `
    -SummaryOutputPath $explicitMissSummaryPath `
    -RequireAllClosed
if ($LASTEXITCODE -ne 0) {
    throw "Expected explicit miss label closure run to pass: $($explicitMissOutput | Out-String)"
}

$explicitMissRows = @(Import-Csv $explicitMissOutputCsv)
if (@($explicitMissRows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.reviewLabel -eq "miss" -and $_.expectedReviewLabel -eq "face|miss" -and $_.closureStatus -eq "closed" }).Count -ne 1) {
    throw "Expected explicit miss label to close the missCandidate row."
}

$scriptText = Get-Content -Raw -Path $script
$summaryText = Get-Content -Raw -Path $summaryPath
$guideText = Get-Content -Raw -Path $guide

Assert-Contains "script matches source prediction ids" $scriptText "sourcePredictionId"
Assert-Contains "script requires source prediction id geometry" $scriptText "sourcePredictionId\+iou"
Assert-Contains "script records frame review diagnostics" $scriptText "Get-FrameReviewDiagnostics"
Assert-Contains "script records eligible frame review rows" $scriptText "eligibleReviewRowsOnFrame"
Assert-Contains "script records best frame review iou" $scriptText "bestFrameReviewIou"
Assert-Contains "script distinguishes geometry mismatch" $scriptText "no matching reviewed geometry on candidate frame"
Assert-Contains "script supports manual miss iou matching" $scriptText "PreferManualMiss"
Assert-Contains "script accepts explicit miss closure label" $scriptText '"missCandidate"\s*\{\s*return @\("face",\s*"miss"\)'
Assert-Contains "script preserves repeated support evidence" $scriptText "supportFrameCount"
Assert-Contains "script preserves repeated support row evidence" $scriptText "supportRowCount"
Assert-Contains "script preserves concrete support evidence ids" $scriptText "supportEvidenceIds"
Assert-Contains "script preserves geometry evidence" $scriptText "centerDistanceRatio"
Assert-Contains "script preserves area ratio evidence" $scriptText "areaChangeRatio"
Assert-Contains "script preserves auxiliary signal role evidence" $scriptText "auxiliarySignalRole"
Assert-Contains "script writes candidate type breakdown" $scriptText "Candidate Type Breakdown"
Assert-Contains "script writes unreviewed reason breakdown" $scriptText "Unreviewed Reason Breakdown"
Assert-Contains "script writes first unreviewed candidates" $scriptText "First Unreviewed Candidates"
Assert-Contains "script requires completed review status" $scriptText "Test-ReviewedStatus"
Assert-Contains "script requires review evidence notes" $scriptText "matching row has no evidenceNotes"
Assert-Contains "script requires completed full-frame miss scan" $scriptText "missedFaceRowsAdded > 0"
Assert-Contains "script enforces require all closed" $scriptText "RequireAllClosed"
Assert-Contains "summary records closed count" $summaryText "closed=3"
Assert-Contains "summary records no unreviewed rows" $summaryText "unreviewed=0"
Assert-Contains "summary records miss closure" $summaryText "missClosed=1"
Assert-Contains "summary records supported candidate breakdown" $summaryText "supportedFaceCandidate: total=1, closed=1, unreviewed=0, labelMismatch=0"
Assert-Contains "summary records false positive candidate breakdown" $summaryText "falsePositiveCandidate: total=1, closed=1, unreviewed=0, labelMismatch=0"
Assert-Contains "summary records miss candidate breakdown" $summaryText "missCandidate: total=1, closed=1, unreviewed=0, labelMismatch=0"
Assert-Contains "summary records no unreviewed reasons" $summaryText "Unreviewed Reason Breakdown[\s\S]*- none"
Assert-Contains "unreviewed summary records reason breakdown" $unreviewedSummaryText "Unreviewed Reason Breakdown[\s\S]*matching row has no label: 1"
Assert-Contains "unreviewed summary records first candidate" $unreviewedSummaryText "First Unreviewed Candidates[\s\S]*candidateId=base-6-0[\s\S]*candidateType=falsePositiveCandidate"
Assert-Contains "guide documents review closure" $guideText "close-yolo-pseudo-gt-review\.ps1"
Assert-Contains "guide documents explicit miss label closure" $guideText "face/nonface/miss"

Write-Host "[YoloPseudoGtReviewClosureVerify] all requested checks passed"
