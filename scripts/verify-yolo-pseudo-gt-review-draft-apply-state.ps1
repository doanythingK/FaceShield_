param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$applyScript = Join-Path $repo "scripts\apply-yolo-pseudo-gt-review-draft.ps1"
$closureScript = Join-Path $repo "scripts\close-yolo-pseudo-gt-review.ps1"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloPseudoGtReviewDraftApplyVerify] pass $Name"
}

function Assert-FileExists {
    param(
        [string]$Name,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

if (-not (Test-Path $applyScript)) {
    throw "Pseudo-GT review draft apply script not found: $applyScript"
}
if (-not (Test-Path $closureScript)) {
    throw "Pseudo-GT closure script not found: $closureScript"
}

$applyText = Get-Content -Raw -Path $applyScript
Assert-Contains "apply script refuses suggestedLabel inference" $applyText "does not infer labels from suggestedLabel"
Assert-Contains "apply script keeps review csv ownership" $applyText "review CSV-owned"
Assert-Contains "apply script records candidate rule" $applyText "candidateRule=test-only-reference-not-final-gt"
Assert-Contains "apply script supports strict pending check" $applyText "Fill them or pass -AllowPartial"
Assert-Contains "apply script verifies final fields" $applyText "label/reviewStatus/evidenceNotes"
Assert-Contains "apply script accepts compact decision csv" $applyText "DecisionCsv"
Assert-Contains "apply script accepts compact full-frame decision csv" $applyText "FrameDecisionCsv"
Assert-Contains "apply script rejects duplicate decisions" $applyText "Duplicate pseudo-GT decision row"

$work = Join-Path $repo ".tmp\yolo-pseudo-gt-review-draft-apply-state"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$pseudoGtCsv = Join-Path $work "pseudo-gt-review-queue.csv"
@(
    [pscustomobject]@{
        reviewRank = "1"
        frame = "10"
        candidateId = "base-10-10-0"
        candidateType = "supportedFaceCandidate"
        expectedReviewLabel = "face"
        source = "base-yolo"
        basePredictionId = "10-0"
        tileDetectionId = "tile-10-0"
        verificationId = "verify-10-0"
        x = "100"
        y = "120"
        w = "80"
        h = "90"
        reviewPriorityScore = "0.95"
        auxiliaryPriorityBoost = "0"
        dominantProbability = "supportedFaceCandidate"
        baseFaceConfidence = "0.70"
        tileFaceConfidence = "0.91"
        tileSupportCount = "3"
        faceVerificationConfidence = "0.88"
        faceVerificationDistance = "0.05"
        personConfidence = "0"
        personUpperOverlap = "0"
        personObjectClass = ""
        tileEvidenceModel = "synthetic-face"
        tileEvidenceRunner = "test-only"
        faceVerificationEvidenceModel = "synthetic-verifier"
        faceVerificationEvidenceRunner = "test-only"
        personObjectEvidenceModel = ""
        personObjectEvidenceRunner = ""
        auxiliarySignalRole = ""
        supportFrameCount = "2"
        supportRowCount = "2"
        supportSources = "tile;verification"
        supportEvidenceIds = "tile-10-0;verify-10-0"
        bestIou = "0.9"
        centerDistanceRatio = "0.02"
        areaChangeRatio = "0.01"
        fpProbability = "0.02"
        missProbability = "0"
        reviewPriorityReason = "supported"
        pseudoGtReason = "base YOLO candidate supported by test-only face evidence"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    },
    [pscustomobject]@{
        reviewRank = "2"
        frame = "11"
        candidateId = "base-11-11-1"
        candidateType = "falsePositiveCandidate"
        expectedReviewLabel = "nonface"
        source = "base-yolo"
        basePredictionId = "11-1"
        tileDetectionId = ""
        verificationId = ""
        x = "200"
        y = "20"
        w = "100"
        h = "120"
        reviewPriorityScore = "0.80"
        auxiliaryPriorityBoost = "0"
        dominantProbability = "fpProbability"
        baseFaceConfidence = "0.52"
        tileFaceConfidence = "0"
        tileSupportCount = "0"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        personObjectClass = ""
        tileEvidenceModel = "synthetic-face"
        tileEvidenceRunner = "test-only"
        faceVerificationEvidenceModel = "synthetic-verifier"
        faceVerificationEvidenceRunner = "test-only"
        personObjectEvidenceModel = ""
        personObjectEvidenceRunner = ""
        auxiliarySignalRole = ""
        supportFrameCount = "0"
        supportRowCount = "0"
        supportSources = ""
        supportEvidenceIds = ""
        bestIou = "0"
        centerDistanceRatio = "99"
        areaChangeRatio = "99"
        fpProbability = "0.80"
        missProbability = "0"
        reviewPriorityReason = "unsupported"
        pseudoGtReason = "base YOLO candidate lacks high precision face support"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    },
    [pscustomobject]@{
        reviewRank = "3"
        frame = "12"
        candidateId = "miss-12-face-0"
        candidateType = "missCandidate"
        expectedReviewLabel = "miss"
        source = "tile-face"
        basePredictionId = ""
        tileDetectionId = "tile-12-0"
        verificationId = ""
        x = "300"
        y = "140"
        w = "50"
        h = "60"
        reviewPriorityScore = "0.70"
        auxiliaryPriorityBoost = "0"
        dominantProbability = "missProbability"
        baseFaceConfidence = "0"
        tileFaceConfidence = "0.81"
        tileSupportCount = "2"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        personObjectClass = ""
        tileEvidenceModel = "synthetic-face"
        tileEvidenceRunner = "test-only"
        faceVerificationEvidenceModel = ""
        faceVerificationEvidenceRunner = ""
        personObjectEvidenceModel = ""
        personObjectEvidenceRunner = ""
        auxiliarySignalRole = ""
        supportFrameCount = "2"
        supportRowCount = "2"
        supportSources = "tile"
        supportEvidenceIds = "tile-12-0"
        bestIou = "0"
        centerDistanceRatio = "99"
        areaChangeRatio = "99"
        fpProbability = "0"
        missProbability = "0.70"
        reviewPriorityReason = "miss"
        pseudoGtReason = "test-only high precision tile face was not matched by base YOLO"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoGtCsv

$draftReviewCsv = Join-Path $work "pseudo-gt-full-gt-review-draft.csv"
@(
    [pscustomobject]@{
        frame = "10"
        gtId = ""
        label = "face"
        x = "100"
        y = "120"
        w = "80"
        h = "90"
        sourcePredictionId = "10-0"
        sourceConfidence = "0.70"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = "pass"
        evidenceNotes = "Human reviewed crop/frame and confirmed this is a visible face."
        notes = "manual final label"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
    },
    [pscustomobject]@{
        frame = "11"
        gtId = ""
        label = "nonface"
        x = "200"
        y = "20"
        w = "100"
        h = "120"
        sourcePredictionId = "11-1"
        sourceConfidence = "0.52"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = "pass"
        evidenceNotes = "Human reviewed crop/frame and confirmed this is not a face."
        notes = "manual final label"
        suggestedLabel = "face"
        candidateRule = "test-only-reference-not-final-gt"
    },
    [pscustomobject]@{
        frame = "12"
        gtId = "miss-12-face-0"
        label = "miss"
        x = "300"
        y = "140"
        w = "50"
        h = "60"
        sourcePredictionId = ""
        sourceConfidence = "0"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = "pass"
        evidenceNotes = "Human reviewed full frame and confirmed missed visible face."
        notes = "manual missed face row"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftReviewCsv

$draftFrameCsv = Join-Path $work "pseudo-gt-full-frame-review-draft.csv"
@(
    [pscustomobject]@{
        frame = "12"
        frameImagePath = "frame-12.png"
        overlayFrameImagePath = "overlay-12.png"
        detectedCandidateCount = "2"
        candidateSummary = "synthetic"
        missedFaceCount = "1"
        missedFaceRowsAdded = "1"
        reviewStatus = "pass"
        evidenceNotes = "Human reviewed full frame and added one manual missed-face row."
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-12-face-0"
        suggestedMissedFaceRowsAdded = "0"
        candidateRule = "test-only-reference-not-final-gt"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftFrameCsv

$reviewCsv = Join-Path $work "full-gt-review.csv"
@(
    [pscustomobject]@{
        frame = "0"
        gtId = ""
        label = "face"
        x = "1"
        y = "1"
        w = "2"
        h = "2"
        sourcePredictionId = "0"
        sourceConfidence = "0.9"
        source = "seed"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = "pass"
        evidenceNotes = "seed reviewed row"
        notes = "seed"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

$frameReviewCsv = Join-Path $work "full-frame-review.csv"
@(
    [pscustomobject]@{
        frame = "12"
        frameImagePath = "frame-12.png"
        overlayFrameImagePath = "overlay-12.png"
        detectedCandidateCount = "2"
        candidateSummary = "synthetic"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "seed frame review"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameReviewCsv

$outReviewCsv = Join-Path $work "applied-full-gt-review.csv"
$outFrameCsv = Join-Path $work "applied-full-frame-review.csv"
$reportPath = Join-Path $work "apply-report.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $applyScript `
    -DraftReviewCsv $draftReviewCsv `
    -DraftFullFrameReviewCsv $draftFrameCsv `
    -ReviewCsv $reviewCsv `
    -FullFrameReviewCsv $frameReviewCsv `
    -OutputReviewCsv $outReviewCsv `
    -OutputFullFrameReviewCsv $outFrameCsv `
    -ReportPath $reportPath `
    -Verify
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT review draft apply selftest failed with exit code $LASTEXITCODE"
}

Assert-FileExists "applied review CSV" $outReviewCsv
Assert-FileExists "applied full-frame CSV" $outFrameCsv
Assert-FileExists "apply report" $reportPath

$appliedRows = @(Import-Csv $outReviewCsv)
if ($appliedRows.Count -ne 4) {
    throw "Expected four applied review rows including seed, actual=$($appliedRows.Count)"
}
if (@($appliedRows | Where-Object { $_.sourcePredictionId -eq "10-0" -and $_.label -eq "face" }).Count -ne 1) {
    throw "Applied review CSV did not preserve the human face label instead of suggestedLabel."
}
if (@($appliedRows | Where-Object { $_.sourcePredictionId -eq "11-1" -and $_.label -eq "nonface" }).Count -ne 1) {
    throw "Applied review CSV did not preserve the human nonface label instead of suggestedLabel."
}
if (@($appliedRows | Where-Object { $_.gtId -eq "miss-12-face-0" -and [string]::IsNullOrWhiteSpace($_.sourcePredictionId) -and $_.label -eq "miss" }).Count -ne 1) {
    throw "Applied review CSV did not append the manual missed-face row."
}

$closureCsv = Join-Path $work "closure.csv"
$closureSummary = Join-Path $work "closure-summary.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $closureScript `
    -PseudoGtCsv $pseudoGtCsv `
    -ReviewCsv $outReviewCsv `
    -FullFrameReviewCsv $outFrameCsv `
    -OutputCsv $closureCsv `
    -SummaryPath $closureSummary `
    -RequireAllClosed
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT closure failed after applying reviewed draft with exit code $LASTEXITCODE"
}

$closureRows = @(Import-Csv $closureCsv)
if (@($closureRows | Where-Object { $_.closureStatus -ne "closed" }).Count -ne 0) {
    throw "Expected all pseudo-GT closure rows to be closed after applying reviewed draft."
}
Assert-Contains "closure summary records closed rows" (Get-Content -Raw -Path $closureSummary) "closed=3"

$decisionDraftCsv = Join-Path $work "decision-pseudo-gt-full-gt-review-draft.csv"
@(
    [pscustomobject]@{
        frame = "10"
        gtId = ""
        label = ""
        x = "100"
        y = "120"
        w = "80"
        h = "90"
        sourcePredictionId = "10-0"
        sourceConfidence = "0.70"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "decision sheet target"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
        pseudoGt_candidateId = "base-10-10-0"
    },
    [pscustomobject]@{
        frame = "11"
        gtId = ""
        label = ""
        x = "200"
        y = "20"
        w = "100"
        h = "120"
        sourcePredictionId = "11-1"
        sourceConfidence = "0.52"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "decision sheet target"
        suggestedLabel = "face"
        candidateRule = "test-only-reference-not-final-gt"
        pseudoGt_candidateId = "base-11-11-1"
    },
    [pscustomobject]@{
        frame = "12"
        gtId = "miss-12-face-0"
        label = ""
        x = "300"
        y = "140"
        w = "50"
        h = "60"
        sourcePredictionId = ""
        sourceConfidence = "0"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "decision sheet target"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
        pseudoGt_candidateId = "miss-12-face-0"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $decisionDraftCsv

$decisionFrameDraftCsv = Join-Path $work "decision-pseudo-gt-full-frame-review-draft.csv"
@(
    [pscustomobject]@{
        frame = "12"
        frameImagePath = "frame-12.png"
        overlayFrameImagePath = "overlay-12.png"
        detectedCandidateCount = "2"
        candidateSummary = "synthetic"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-12-face-0"
        suggestedMissedFaceRowsAdded = "0"
        candidateRule = "test-only-reference-not-final-gt"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $decisionFrameDraftCsv

$decisionCsv = Join-Path $work "pseudo-gt-review-decision-sheet.csv"
@(
    [pscustomobject]@{
        frame = "10"
        pseudoGt_candidateId = "base-10-10-0"
        suggestedLabel = "nonface"
        label = "face"
        reviewStatus = "pass"
        evidenceNotes = "Human checked compact decision sheet and confirmed visible face."
    },
    [pscustomobject]@{
        frame = "11"
        pseudoGt_candidateId = "base-11-11-1"
        suggestedLabel = "face"
        label = "nonface"
        reviewStatus = "pass"
        evidenceNotes = "Human checked compact decision sheet and confirmed non-face."
    },
    [pscustomobject]@{
        frame = "12"
        pseudoGt_candidateId = "miss-12-face-0"
        suggestedLabel = "nonface"
        label = "miss"
        reviewStatus = "pass"
        evidenceNotes = "Human checked compact decision sheet and confirmed missed face."
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $decisionCsv

$frameDecisionCsv = Join-Path $work "pseudo-gt-full-frame-review-decision-sheet.csv"
@(
    [pscustomobject]@{
        frame = "12"
        missedFaceCount = "1"
        missedFaceRowsAdded = "1"
        reviewStatus = "pass"
        evidenceNotes = "Human checked full-frame decision sheet and added one missed-face row."
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameDecisionCsv

$decisionOutReviewCsv = Join-Path $work "decision-applied-full-gt-review.csv"
$decisionOutFrameCsv = Join-Path $work "decision-applied-full-frame-review.csv"
$decisionReport = Join-Path $work "decision-apply-report.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $applyScript `
    -DraftReviewCsv $decisionDraftCsv `
    -DraftFullFrameReviewCsv $decisionFrameDraftCsv `
    -DecisionCsv $decisionCsv `
    -FrameDecisionCsv $frameDecisionCsv `
    -ReviewCsv $reviewCsv `
    -FullFrameReviewCsv $frameReviewCsv `
    -OutputReviewCsv $decisionOutReviewCsv `
    -OutputFullFrameReviewCsv $decisionOutFrameCsv `
    -ReportPath $decisionReport `
    -Verify
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT compact decision sheet apply selftest failed with exit code $LASTEXITCODE"
}

$decisionAppliedRows = @(Import-Csv $decisionOutReviewCsv)
if (@($decisionAppliedRows | Where-Object { $_.sourcePredictionId -eq "10-0" -and $_.label -eq "face" }).Count -ne 1) {
    throw "Compact decision sheet apply did not merge human face label."
}
if (@($decisionAppliedRows | Where-Object { $_.sourcePredictionId -eq "11-1" -and $_.label -eq "nonface" }).Count -ne 1) {
    throw "Compact decision sheet apply did not merge human nonface label."
}
if (@($decisionAppliedRows | Where-Object { $_.gtId -eq "miss-12-face-0" -and $_.label -eq "miss" }).Count -ne 1) {
    throw "Compact decision sheet apply did not merge human miss label."
}
Assert-Contains "decision report records merged rows" (Get-Content -Raw -Path $decisionReport) "decisionRowsMerged=3"
Assert-Contains "decision report records merged frame rows" (Get-Content -Raw -Path $decisionReport) "frameDecisionRowsMerged=1"

$decisionClosureCsv = Join-Path $work "decision-closure.csv"
$decisionClosureSummary = Join-Path $work "decision-closure-summary.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $closureScript `
    -PseudoGtCsv $pseudoGtCsv `
    -ReviewCsv $decisionOutReviewCsv `
    -FullFrameReviewCsv $decisionOutFrameCsv `
    -OutputCsv $decisionClosureCsv `
    -SummaryPath $decisionClosureSummary `
    -RequireAllClosed
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT closure failed after applying compact decision sheet with exit code $LASTEXITCODE"
}
Assert-Contains "decision closure summary records closed rows" (Get-Content -Raw -Path $decisionClosureSummary) "closed=3"
Write-Host "[YoloPseudoGtReviewDraftApplyVerify] pass compact decision sheet apply and closure"

$incompleteDraftCsv = Join-Path $work "incomplete-pseudo-gt-full-gt-review-draft.csv"
@(
    [pscustomobject]@{
        frame = "10"
        gtId = ""
        label = ""
        x = "100"
        y = "120"
        w = "80"
        h = "90"
        sourcePredictionId = "10-0"
        sourceConfidence = "0.70"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "not reviewed"
        suggestedLabel = "face"
        candidateRule = "test-only-reference-not-final-gt"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $incompleteDraftCsv

$negativeReport = Join-Path $work "negative-apply-report.md"
$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $negativeOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $applyScript `
        -DraftReviewCsv $incompleteDraftCsv `
        -DraftFullFrameReviewCsv $draftFrameCsv `
        -ReviewCsv $reviewCsv `
        -FullFrameReviewCsv $frameReviewCsv `
        -OutputReviewCsv (Join-Path $work "negative-applied.csv") `
        -OutputFullFrameReviewCsv (Join-Path $work "negative-applied-frame.csv") `
        -ReportPath $negativeReport 2>&1
    $negativeExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
}
if ($negativeExit -eq 0) {
    throw "Strict apply unexpectedly accepted an incomplete pseudo-GT review draft."
}
Assert-Contains "negative output explains strict pending fields" ($negativeOutput | Out-String) "without final label/reviewStatus/evidenceNotes"

Write-Host "[YoloPseudoGtReviewDraftApplyVerify] all requested checks passed"
