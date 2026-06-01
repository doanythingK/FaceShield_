param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$PseudoGtReviewClosureCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [string]$PseudoGtReviewClosureSummary = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure-summary.md",
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$EvidenceReport = ".tmp\yolo-manual-gates\goal-evidence-report.md",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$UpdatePlan,
    [switch]$RunYoloState,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manualReadinessVerifier = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$fullGtReviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
$guiSmokeVerifier = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$completionAuditVerifier = Join-Path $repo "scripts\verify-yolo-completion-audit-state.ps1"
$evidenceReportWriter = Join-Path $repo "scripts\write-yolo-goal-evidence-report.ps1"
$yoloStateVerifier = Join-Path $repo "scripts\verify-yolo-state.ps1"
$pseudoGtReviewClosure = Join-Path $repo "scripts\close-yolo-pseudo-gt-review.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "$Name is not a file: $resolved"
    }

    if ($item.Length -le 0) {
        throw "$Name is empty: $resolved"
    }

    return $resolved
}

function Invoke-RequiredStep {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name script not found: $ScriptPath"
    }

    Write-Host "[YoloCompletionFinalizer] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }

    Write-Host "[YoloCompletionFinalizer] pass $Name"
}

function Update-GoalAuditMarker {
    param([string]$Path)

    $resolved = Assert-FileNonEmpty "plan document" $Path
    $text = Get-Content -Raw -Path $resolved
    $match = [regex]::Match(
        $text,
        '<!--\s*(?<marker>yolo-goal-audit-state:\s*.*?)\s*-->',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if (-not $match.Success) {
        throw "Plan is missing yolo-goal-audit-state marker: $resolved"
    }

    $marker = $match.Groups["marker"].Value
    foreach ($required in @(
        "backend=integrated",
        "default=FaceONNX",
        "recommendation=none",
        "track-hold-state=pass",
        "extended=fail",
        "extended-export=fail")) {
        if (-not $marker.Contains($required)) {
            throw "Goal audit marker missing required token before completion update: $required"
        }
    }

    if (-not $marker.Contains("complete=false")) {
        throw "Goal audit marker is not in pending state: missing complete=false"
    }
    if (-not ($marker.Contains("remaining=full-gt-label,gui-smoke") -or $marker.Contains("remaining=gui-smoke"))) {
        throw "Goal audit marker is not in pending state: missing supported remaining gate"
    }

    $updatedMarker = $marker
    $updatedMarker = $updatedMarker -replace 'complete=false', 'complete=true'
    $updatedMarker = $updatedMarker -replace 'remaining=full-gt-label,gui-smoke', 'remaining=none'
    $updatedMarker = $updatedMarker -replace 'remaining=gui-smoke', 'remaining=none'
    if ($updatedMarker -match 'completion-audit=[^;\s]+') {
        $updatedMarker = $updatedMarker -replace 'completion-audit=[^;\s]+', 'completion-audit=pass-complete'
    }
    else {
        $updatedMarker = "$updatedMarker; completion-audit=pass-complete"
    }
    foreach ($token in @(
        "pseudo-gt-test-only=pass",
        "pseudo-gt-review-closure=conditional-gated")) {
        if (-not $updatedMarker.Contains($token)) {
            $updatedMarker = "$updatedMarker; $token"
        }
    }

    $updatedComment = "<!-- $updatedMarker -->"
    $updatedText = $text.Substring(0, $match.Index) + $updatedComment + $text.Substring($match.Index + $match.Length)
    Set-Content -Encoding UTF8 -Path $resolved -Value $updatedText

    Write-Host "[YoloCompletionFinalizer] updated goal marker in $resolved"
}

function New-SyntheticArtifact {
    param([string]$Path)

    Set-Content -Encoding UTF8 -Path $Path -Value "synthetic finalizer artifact"
}

if ($SelfTest) {
    $selfTestDir = Join-Path $repo ".tmp\yolo-completion-finalizer-state"
    New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

    $plan = Join-Path $selfTestDir "pending-plan.md"
    @(
        "# Synthetic YOLO Finalizer Plan",
        "",
        "<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; short-span-only-goal=pass; obsolete-sweep-harness=removed; obsolete-ten-minute-runner=removed; pseudo-gt-test-only=pass; pseudo-gt-review-closure=conditional-gated; complete=false; remaining=full-gt-label,gui-smoke; completion-audit=pass-incomplete -->"
    ) | Set-Content -Encoding UTF8 -Path $plan

    $summary = Join-Path $selfTestDir "manual-gate-summary.md"
    @(
        "# Synthetic Manual Gate Summary",
        "- full-gt-label",
        "- gui-smoke",
        "- preview-track-hold",
        "- verify-yolo-full-gt-reviewed-state.ps1",
        "- verify-yolo-gui-smoke-state.ps1",
        "- verify-yolo-manual-readiness-state.ps1",
        "- open-yolo-manual-gates.ps1 -OpenApp",
        "- verify-yolo-state.ps1 -RequireComplete",
        "- completionPlanAction"
    ) | Set-Content -Encoding UTF8 -Path $summary

    $crop = Join-Path $selfTestDir "face-crop.png"
    $frame = Join-Path $selfTestDir "frame.png"
    $overlay = Join-Path $selfTestDir "frame-overlay.png"
    foreach ($artifact in @($crop, $frame, $overlay)) {
        New-SyntheticArtifact $artifact
    }

    $reviewCsv = Join-Path $selfTestDir "full-gt-review.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            gtId = "gt-face-1"
            label = "face"
            x = "100"
            y = "100"
            w = "80"
            h = "80"
            sourcePredictionId = "pred-face-1"
            sourceConfidence = "0.91"
            source = "prediction"
            cropPath = $crop
            reviewStatus = "pass"
            evidenceNotes = "synthetic reviewed face"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

    $frameCsv = Join-Path $selfTestDir "full-frame-review.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            frameImagePath = $frame
            overlayFrameImagePath = $overlay
            detectedCandidateCount = "1"
            candidateSummary = "pred-face-1"
            missedFaceCount = "0"
            missedFaceRowsAdded = "0"
            reviewStatus = "pass"
            evidenceNotes = "synthetic full-frame reviewed"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameCsv

    $predictionCsvPath = Join-Path $selfTestDir "predictions.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            predictionId = "pred-face-1"
            x = "100"
            y = "100"
            w = "80"
            h = "80"
            confidence = "0.91"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $predictionCsvPath

    $pseudoGtCsvPath = Join-Path $selfTestDir "pseudo-gt-candidates.csv"
    @(
        [pscustomobject]@{
            candidateId = "base-1-0"
            frame = "1"
            candidateType = "supportedFaceCandidate"
            source = "base-yolo"
            basePredictionId = "pred-face-1"
            tileDetectionId = "tile-face-1"
            verificationId = "verify-face-1"
            x = "100"
            y = "100"
            w = "80"
            h = "80"
            baseFaceConfidence = "0.91"
            tileFaceConfidence = "0.93"
            tileSupportCount = "2"
            faceVerificationConfidence = "0.90"
            faceVerificationDistance = "0.2"
            personConfidence = "0"
            personUpperOverlap = "0"
            bestIou = "1"
            centerDistanceRatio = "0"
            fpProbability = "0.05"
            missProbability = "0.01"
            pseudoGtReason = "synthetic finalizer closure"
            reviewLabel = ""
            reviewStatus = "pending-human"
            evidenceNotes = ""
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoGtCsvPath
    $pseudoGtClosureCsvPath = Join-Path $selfTestDir "pseudo-gt-review-closure.csv"
    $pseudoGtClosureSummaryPath = Join-Path $selfTestDir "pseudo-gt-review-closure-summary.md"

    $guiCsv = Join-Path $selfTestDir "manual-smoke-checklist.csv"
    $guiSpecs = @{
        "open-video" = @{ File = "open-video.png"; Type = "screenshot-or-recording" }
        "select-yolo-backend" = @{ File = "select-yolo-backend.png"; Type = "screenshot" }
        "download-yolo-model" = @{ File = "download-yolo-model.log"; Type = "screenshot-or-log" }
        "run-yolo-auto-detect" = @{ File = "run-yolo-auto-detect.log"; Type = "screenshot-or-log" }
        "preview-result" = @{ File = "preview-result.mp4"; Type = "screenshot-or-recording" }
        "preview-track-hold" = @{ File = "preview-track-hold.mp4"; Type = "recording" }
        "manual-edit" = @{ File = "manual-edit.png"; Type = "screenshot-or-recording" }
        "export" = @{ File = "export.mp4"; Type = "output-file" }
        "reopen-state" = @{ File = "reopen-state.png"; Type = "screenshot-or-recording" }
    }
    foreach ($step in $guiSpecs.Keys) {
        New-SyntheticArtifact (Join-Path $selfTestDir $guiSpecs[$step].File)
    }
    $guiRows = foreach ($step in @(
        "open-video",
        "select-yolo-backend",
        "download-yolo-model",
        "run-yolo-auto-detect",
        "preview-result",
        "preview-track-hold",
        "manual-edit",
        "export",
        "reopen-state"
    )) {
        [pscustomobject]@{
            stepId = $step
            status = "pass"
            evidenceType = $guiSpecs[$step].Type
            artifactPath = Join-Path $selfTestDir $guiSpecs[$step].File
            evidence = "synthetic evidence for $step"
            notes = "synthetic finalizer fixture"
        }
    }
    $guiRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $guiCsv

    $report = Join-Path $selfTestDir "goal-evidence-report.md"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
        -PlanPath $plan `
        -FullGtReviewCsv $reviewCsv `
        -FullFrameReviewCsv $frameCsv `
        -GuiChecklistCsv $guiCsv `
        -PredictionCsv $predictionCsvPath `
        -PseudoGtCsv $pseudoGtCsvPath `
        -PseudoGtReviewClosureCsv $pseudoGtClosureCsvPath `
        -PseudoGtReviewClosureSummary $pseudoGtClosureSummaryPath `
        -ManualGateSummary $summary `
        -EvidenceReport $report `
        -UpdatePlan
    if ($LASTEXITCODE -ne 0) {
        throw "Finalizer selftest failed with exit code $LASTEXITCODE"
    }

    $planText = Get-Content -Raw -Path $plan
    $reportText = Get-Content -Raw -Path $report
    if (-not $planText.Contains("complete=true") -or -not $planText.Contains("remaining=none")) {
        throw "Finalizer selftest did not update plan marker."
    }
    if (-not $reportText.Contains("ready-for-strict-completion-audit")) {
        throw "Finalizer selftest did not write completed evidence report."
    }
    if (-not (Test-Path $pseudoGtClosureCsvPath)) {
        throw "Finalizer selftest did not create pseudo-GT closure CSV."
    }
    $pseudoGtClosureRows = @(Import-Csv $pseudoGtClosureCsvPath)
    if ($pseudoGtClosureRows.Count -ne 1 -or $pseudoGtClosureRows[0].closureStatus -ne "closed") {
        throw "Finalizer selftest did not close pseudo-GT review rows."
    }

    Write-Host "[YoloCompletionFinalizer] pass selftest completed fixture"
    Write-Host "[YoloCompletionFinalizer] all requested checks passed"
    return
}

$planPath = Assert-FileNonEmpty "plan document" $PlanPath
$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$summaryPath = Assert-FileNonEmpty "manual gate summary" $ManualGateSummary
$predictionArgs = @()
$manualPredictionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
    $resolvedPredictionCsv = Assert-FileNonEmpty "prediction CSV" $PredictionCsv
    $predictionArgs += @("-PredictionCsv", $resolvedPredictionCsv)
    $manualPredictionArgs += @("-FullGtPredictionCsv", $resolvedPredictionCsv)
}
else {
    $resolvedPredictionLog = Assert-FileNonEmpty "prediction log" $PredictionLog
    $predictionArgs += @("-PredictionLog", $resolvedPredictionLog)
    $manualPredictionArgs += @("-FullGtPredictionLog", $resolvedPredictionLog)
}

$qualityArgs = @(
    "-MinIou", "$MinIou",
    "-MaxMisses", "$MaxMisses",
    "-MaxFalsePositives", "$MaxFalsePositives",
    "-MaxLowIou", "$MaxLowIou"
)
$allowQualityGateFailureArgs = @()
if ($AllowQualityGateFailure) {
    $allowQualityGateFailureArgs += "-AllowQualityGateFailure"
}

Invoke-RequiredStep "manual-readiness-completed-state" $manualReadinessVerifier (@(
    "-PlanPath", $planPath,
    "-FullGtReviewCsv", $reviewCsv,
    "-FullFrameReviewCsv", $frameCsv,
    "-GuiChecklistCsv", $guiCsv,
    "-FullGtMinIou", "$MinIou",
    "-FullGtMaxMisses", "$MaxMisses",
    "-FullGtMaxFalsePositives", "$MaxFalsePositives",
    "-FullGtMaxLowIou", "$MaxLowIou",
    "-AllowCompletedFullGt",
    "-AllowCompletedGuiSmoke"
) + $manualPredictionArgs + $allowQualityGateFailureArgs)

Invoke-RequiredStep "full-gt-reviewed-state" $fullGtReviewedVerifier (@(
    "-ReviewCsv", $reviewCsv,
    "-FullFrameReviewCsv", $frameCsv,
    "-RequireFullFrameReview",
    "-RequireEvidence",
    "-RequireArtifacts"
) + $predictionArgs + $qualityArgs + $allowQualityGateFailureArgs)

Invoke-RequiredStep "gui-smoke-state" $guiSmokeVerifier @(
    "-ChecklistCsv", $guiCsv,
    "-RequireManualPass"
)

$resolvedPseudoGtCsv = Resolve-RepoPath $PseudoGtCsv
if (-not (Test-Path $resolvedPseudoGtCsv)) {
    throw "Pseudo-GT candidate CSV is required before completing the YOLO goal: $resolvedPseudoGtCsv"
}

Invoke-RequiredStep "pseudo-gt-review-closure" $pseudoGtReviewClosure @(
    "-PseudoGtCsv", $resolvedPseudoGtCsv,
    "-ReviewCsv", $reviewCsv,
    "-FullFrameReviewCsv", $frameCsv,
    "-OutputCsv", (Resolve-RepoPath $PseudoGtReviewClosureCsv),
    "-SummaryPath", (Resolve-RepoPath $PseudoGtReviewClosureSummary),
    "-RequireAllClosed"
)

if (-not $UpdatePlan) {
    Write-Host "[YoloCompletionFinalizer] verified completed manual evidence. Pass -UpdatePlan to update yolo-goal-audit-state and run completion audit."
    Write-Host "[YoloCompletionFinalizer] all requested checks passed"
    return
}

Update-GoalAuditMarker $planPath

Invoke-RequiredStep "completion-audit-complete" $completionAuditVerifier (@(
    "-PlanDocument", $planPath,
    "-FullGtReviewCsv", $reviewCsv,
    "-FullFrameReviewCsv", $frameCsv,
    "-GuiChecklistCsv", $guiCsv,
    "-PseudoGtCsv", $resolvedPseudoGtCsv,
    "-PseudoGtReviewClosureCsv", (Resolve-RepoPath $PseudoGtReviewClosureCsv),
    "-ManualGateSummary", $summaryPath,
    "-RequireComplete"
) + $predictionArgs + $qualityArgs + $allowQualityGateFailureArgs)

Invoke-RequiredStep "goal-evidence-report-complete" $evidenceReportWriter (@(
    "-PlanDocument", $planPath,
    "-FullGtReviewCsv", $reviewCsv,
    "-FullFrameReviewCsv", $frameCsv,
    "-GuiChecklistCsv", $guiCsv,
    "-PseudoGtCsv", $resolvedPseudoGtCsv,
    "-PseudoGtReviewClosureCsv", (Resolve-RepoPath $PseudoGtReviewClosureCsv),
    "-ManualGateSummary", $summaryPath,
    "-OutputPath", $EvidenceReport,
    "-Verify",
    "-RequireComplete"
) + $predictionArgs + $qualityArgs + $allowQualityGateFailureArgs)

if ($RunYoloState) {
    $statePredictionArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
        $statePredictionArgs += @("-FullGtPredictionCsv", (Resolve-RepoPath $PredictionCsv))
    }
    else {
        $statePredictionArgs += @("-FullGtPredictionLog", (Resolve-RepoPath $PredictionLog))
    }
    $stateQualityGateArgs = @()
    if ($AllowQualityGateFailure) {
        $stateQualityGateArgs += "-AllowFullGtQualityGateFailure"
    }

    Invoke-RequiredStep "yolo-state-complete" $yoloStateVerifier (@(
        "-AllowCompletedFullGt",
        "-AllowCompletedGuiSmoke",
        "-RequireComplete",
        "-FullGtMinIou", "$MinIou",
        "-FullGtMaxMisses", "$MaxMisses",
        "-FullGtMaxFalsePositives", "$MaxFalsePositives",
        "-FullGtMaxLowIou", "$MaxLowIou"
    ) + $statePredictionArgs + $stateQualityGateArgs)
}

Write-Host "[YoloCompletionFinalizer] complete=true, remaining=none, evidenceReport=$(Resolve-RepoPath $EvidenceReport)"
Write-Host "[YoloCompletionFinalizer] all requested checks passed"
