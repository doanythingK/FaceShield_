param(
    [string]$PlanDocument = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$PseudoGtReviewClosureCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$RequireComplete,
    [switch]$SelfTest
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

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloCompletionAuditVerify] pass $Name"
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

function Get-YoloGoalAuditMarker {
    param([string]$Text)

    $matches = [regex]::Matches($Text, "<!--\s*yolo-goal-audit-state:[\s\S]*?-->")
    if ($matches.Count -eq 0) {
        throw "Plan document is missing yolo-goal-audit-state marker."
    }

    return $matches[$matches.Count - 1].Value
}

function Invoke-RequiredVerifier {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name verifier not found: $ScriptPath"
    }

    Write-Host "[YoloCompletionAuditVerify] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }

    Write-Host "[YoloCompletionAuditVerify] pass $Name"
}

function Get-FilledCount {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    }).Count
}

function Test-ReviewedStatus {
    param([string]$Status)

    if ([string]::IsNullOrWhiteSpace($Status)) {
        return $false
    }

    return $Status.Trim().ToLowerInvariant() -in @("pass", "reviewed", "complete", "completed", "closed", "done")
}

function Test-PseudoGtClosureStrictlyClosed {
    param([object]$Row)

    if ($null -eq $Row.PSObject.Properties["closureStatus"] -or
        $Row.closureStatus.Trim().ToLowerInvariant() -ne "closed") {
        return $false
    }

    if ($null -eq $Row.PSObject.Properties["reviewStatus"] -or
        -not (Test-ReviewedStatus ([string]$Row.reviewStatus))) {
        return $false
    }

    if ($null -eq $Row.PSObject.Properties["reviewEvidenceNotes"] -or
        [string]::IsNullOrWhiteSpace([string]$Row.reviewEvidenceNotes)) {
        return $false
    }

    if ($null -eq $Row.PSObject.Properties["reviewMatchMode"] -or
        [string]::IsNullOrWhiteSpace([string]$Row.reviewMatchMode)) {
        return $false
    }

    $reviewMatchMode = $Row.reviewMatchMode.Trim()
    if ($reviewMatchMode -ne "sourcePredictionId+iou" -and
        $reviewMatchMode.StartsWith("sourcePredictionId", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if ($null -eq $Row.PSObject.Properties["reviewIou"]) {
        return $false
    }

    $reviewIou = 0.0
    if (-not [double]::TryParse(
            [string]$Row.reviewIou,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$reviewIou) -or
        $reviewIou -lt $MinIou) {
        return $false
    }

    if ($null -ne $Row.PSObject.Properties["expectedReviewLabel"] -and
        $null -ne $Row.PSObject.Properties["reviewLabel"] -and
        -not [string]::IsNullOrWhiteSpace([string]$Row.expectedReviewLabel)) {
        $reviewLabel = $Row.reviewLabel.Trim().ToLowerInvariant()
        $expectedLabels = @(([string]$Row.expectedReviewLabel -split "\|") | ForEach-Object {
                $_.Trim().ToLowerInvariant()
            } | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            })
        if ($expectedLabels.Count -gt 0 -and $reviewLabel -notin $expectedLabels) {
            return $false
        }
    }

    $candidateType = if ($null -ne $Row.PSObject.Properties["candidateType"]) {
        $Row.candidateType.Trim().ToLowerInvariant()
    }
    else {
        ""
    }

    if ($candidateType -eq "misscandidate") {
        if ($null -eq $Row.PSObject.Properties["fullFrameReviewStatus"] -or
            -not (Test-ReviewedStatus ([string]$Row.fullFrameReviewStatus))) {
            return $false
        }

        if ($null -eq $Row.PSObject.Properties["fullFrameEvidenceNotes"] -or
            [string]::IsNullOrWhiteSpace([string]$Row.fullFrameEvidenceNotes)) {
            return $false
        }

        if ($null -eq $Row.PSObject.Properties["fullFrameMissedRowsAdded"]) {
            return $false
        }

        $missedRowsAdded = 0
        if (-not [int]::TryParse([string]$Row.fullFrameMissedRowsAdded, [ref]$missedRowsAdded) -or
            $missedRowsAdded -le 0) {
            return $false
        }
    }

    return $true
}

function Assert-PseudoGtReviewClosure {
    param(
        [string]$CandidateCsv,
        [string]$ClosureCsv,
        [switch]$RequireCandidates
    )

    $candidatePath = Resolve-RepoPath $CandidateCsv
    if (-not (Test-Path $candidatePath)) {
        if ($RequireCandidates) {
            throw "Pseudo-GT candidate CSV is required for complete audit: $candidatePath"
        }

        Write-Host "[YoloCompletionAuditVerify] pseudoGtReviewClosure=skipped-no-candidates, pseudoGtCsv=$candidatePath"
        return "skipped-no-candidates"
    }

    $closurePath = Assert-FileNonEmpty "pseudo-GT review closure CSV" $ClosureCsv
    $candidateRows = @(Import-Csv $candidatePath)
    $closureRows = @(Import-Csv $closurePath)

    if ($candidateRows.Count -eq 0) {
        throw "Pseudo-GT candidate CSV has no rows: $candidatePath"
    }
    if ($closureRows.Count -ne $candidateRows.Count) {
        throw "Pseudo-GT closure row count mismatch: candidates=$($candidateRows.Count), closureRows=$($closureRows.Count), closureCsv=$closurePath"
    }

    $openRows = @($closureRows | Where-Object {
        -not (Test-PseudoGtClosureStrictlyClosed $_)
    })
    if ($openRows.Count -gt 0) {
        throw "Pseudo-GT review closure is incomplete: openRows=$($openRows.Count), closureCsv=$closurePath"
    }

    Write-Host "[YoloCompletionAuditVerify] pseudoGtReviewClosure=closed, candidates=$($candidateRows.Count), closureCsv=$closurePath"
    return "closed"
}

if ($SelfTest) {
    $selfTestDir = Join-Path $repo ".tmp\yolo-completion-audit-state"
    New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

    $planPath = Join-Path $selfTestDir "complete-plan.md"
    @(
        "# Synthetic YOLO Completion Audit Plan",
        "",
        "<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; pseudo-gt-test-only=pass; pseudo-gt-review-closure=conditional-gated; complete=true; remaining=none; completion-audit=pass-complete -->"
    ) | Set-Content -Encoding UTF8 -Path $planPath

    $summaryPath = Join-Path $selfTestDir "manual-gate-summary.md"
    @(
        "# Synthetic Manual Gate Summary",
        "",
        "- full-gt-label",
        "- gui-smoke",
        "- preview-track-hold",
        "- verify-yolo-full-gt-reviewed-state.ps1",
        "- verify-yolo-gui-smoke-state.ps1",
        "- verify-yolo-manual-readiness-state.ps1",
        "- open-yolo-manual-gates.ps1 -OpenApp",
        "- verify-yolo-state.ps1 -RequireComplete",
        "- completionPlanAction"
    ) | Set-Content -Encoding UTF8 -Path $summaryPath

    $cropPath = Join-Path $selfTestDir "face-crop.png"
    $framePath = Join-Path $selfTestDir "frame.png"
    $overlayPath = Join-Path $selfTestDir "frame-overlay.png"
    foreach ($artifactPath in @($cropPath, $framePath, $overlayPath)) {
        Set-Content -Encoding UTF8 -Path $artifactPath -Value "synthetic completion audit artifact"
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
            cropPath = $cropPath
            reviewStatus = "pass"
            evidenceNotes = "synthetic reviewed face"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

    $frameCsv = Join-Path $selfTestDir "full-frame-review.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            frameImagePath = $framePath
            overlayFrameImagePath = $overlayPath
            detectedCandidateCount = "1"
            candidateSummary = "pred-face-1"
            missedFaceCount = "0"
            missedFaceRowsAdded = "0"
            reviewStatus = "pass"
            evidenceNotes = "synthetic full-frame reviewed"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameCsv

    $predictionCsv = Join-Path $selfTestDir "predictions.csv"
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
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $predictionCsv

    $pseudoGtCsv = Join-Path $selfTestDir "pseudo-gt-candidates.csv"
    @(
        [pscustomobject]@{
            candidateId = "base-1-0"
            frame = "1"
            candidateType = "supportedFaceCandidate"
            basePredictionId = "pred-face-1"
            x = "100"
            y = "100"
            w = "80"
            h = "80"
            baseFaceConfidence = "0.91"
            tileFaceConfidence = "0.93"
            tileSupportCount = "2"
            personConfidence = "0"
            personUpperOverlap = "0"
            fpProbability = "0.05"
            missProbability = "0.01"
            pseudoGtReason = "synthetic supported face"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoGtCsv

    $pseudoGtClosureCsv = Join-Path $selfTestDir "pseudo-gt-review-closure.csv"
    @(
        [pscustomobject]@{
            candidateId = "base-1-0"
            frame = "1"
            candidateType = "supportedFaceCandidate"
            expectedReviewLabel = "face"
            closureStatus = "closed"
            reviewLabel = "face"
            reviewStatus = "pass"
            reviewSourcePredictionId = "pred-face-1"
            reviewMatchMode = "sourcePredictionId+iou"
            reviewIou = "1"
            reviewEvidenceNotes = "synthetic reviewed face"
            fullFrameEvidenceNotes = ""
            closureReason = "synthetic closure"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoGtClosureCsv

    $guiChecklistCsv = Join-Path $selfTestDir "manual-smoke-checklist.csv"
    $guiArtifacts = @{
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
        $artifact = $guiArtifacts[$step]
        $artifactPath = Join-Path $selfTestDir $artifact.File
        Set-Content -Encoding UTF8 -Path $artifactPath -Value "synthetic GUI smoke artifact for $step"

        [pscustomobject]@{
            stepId = $step
            status = "pass"
            evidenceType = $artifact.Type
            artifactPath = $artifactPath
            evidence = "synthetic evidence for $step"
            notes = "synthetic completion audit fixture"
        }
    }
    $guiRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $guiChecklistCsv

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
            -PlanDocument $planPath `
            -FullGtReviewCsv $reviewCsv `
            -FullFrameReviewCsv $frameCsv `
            -GuiChecklistCsv $guiChecklistCsv `
            -PredictionCsv $predictionCsv `
            -PseudoGtCsv $pseudoGtCsv `
            -PseudoGtReviewClosureCsv $pseudoGtClosureCsv `
            -ManualGateSummary $summaryPath `
            -MinIou 0.50 `
            -MaxMisses 0 `
            -MaxFalsePositives 0 `
            -MaxLowIou 0 `
            -RequireComplete 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($exitCode -ne 0) {
        throw "Completion audit complete PredictionCsv selftest failed with exit code $exitCode"
    }
    if ($text -notmatch "complete=true" -or $text -notmatch "full-gt-reviewed-state" -or $text -notmatch "gui-smoke-state" -or $text -notmatch "pseudoGtReviewClosure=closed") {
        throw "Completion audit complete PredictionCsv selftest did not exercise expected branches. output=$text"
    }

    Write-Host "[YoloCompletionAuditVerify] pass selftest complete PredictionCsv path"

    $loosePseudoGtClosureCsv = Join-Path $selfTestDir "loose-pseudo-gt-review-closure.csv"
    @(
        [pscustomobject]@{
            candidateId = "base-1-0"
            frame = "1"
            candidateType = "supportedFaceCandidate"
            expectedReviewLabel = "face"
            closureStatus = "closed"
            reviewLabel = "face"
            reviewStatus = "pass"
            reviewSourcePredictionId = "pred-face-1"
            reviewMatchMode = "sourcePredictionId"
            reviewIou = "1"
            reviewEvidenceNotes = ""
            fullFrameEvidenceNotes = ""
            closureReason = "synthetic loose closure"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $loosePseudoGtClosureCsv

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $looseClosureOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
            -PlanDocument $planPath `
            -FullGtReviewCsv $reviewCsv `
            -FullFrameReviewCsv $frameCsv `
            -GuiChecklistCsv $guiChecklistCsv `
            -PredictionCsv $predictionCsv `
            -PseudoGtCsv $pseudoGtCsv `
            -PseudoGtReviewClosureCsv $loosePseudoGtClosureCsv `
            -ManualGateSummary $summaryPath `
            -MinIou 0.50 `
            -MaxMisses 0 `
            -MaxFalsePositives 0 `
            -MaxLowIou 0 `
            -RequireComplete 2>&1
        $looseClosureExitCode = $LASTEXITCODE
        $looseClosureText = ($looseClosureOutput | Out-String)
        Write-Host $looseClosureText
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($looseClosureExitCode -eq 0) {
        throw "Completion audit loose pseudo-GT closure negative selftest unexpectedly passed."
    }
    if ($looseClosureText -notmatch "Pseudo-GT review closure is incomplete") {
        throw "Completion audit loose pseudo-GT closure negative selftest failed for an unexpected reason. output=$looseClosureText"
    }

    Write-Host "[YoloCompletionAuditVerify] pass selftest loose pseudo-GT closure cannot RequireComplete"

    $misleadingBodyPlanPath = Join-Path $selfTestDir "misleading-body-complete-plan.md"
    @(
        "# Synthetic YOLO Completion Audit Negative Plan",
        "",
        "This prose intentionally says complete=true and remaining=none outside the goal marker.",
        "The audit must ignore this prose when RequireComplete is used.",
        "",
        "<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; pseudo-gt-test-only=pass; pseudo-gt-review-closure=conditional-gated; complete=false; remaining=full-gt-label,gui-smoke; completion-audit=pass-incomplete -->"
    ) | Set-Content -Encoding UTF8 -Path $misleadingBodyPlanPath

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $markerOnlyOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
            -PlanDocument $misleadingBodyPlanPath `
            -FullGtReviewCsv $reviewCsv `
            -FullFrameReviewCsv $frameCsv `
            -GuiChecklistCsv $guiChecklistCsv `
            -PredictionCsv $predictionCsv `
            -ManualGateSummary $summaryPath `
            -MinIou 0.50 `
            -MaxMisses 0 `
            -MaxFalsePositives 0 `
            -MaxLowIou 0 `
            -RequireComplete 2>&1
        $markerOnlyExitCode = $LASTEXITCODE
        $markerOnlyText = ($markerOnlyOutput | Out-String)
        Write-Host $markerOnlyText
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($markerOnlyExitCode -eq 0) {
        throw "Completion audit marker-only negative selftest unexpectedly passed."
    }
    if ($markerOnlyText -notmatch "goal marked complete missing text: complete=true") {
        throw "Completion audit marker-only negative selftest failed for an unexpected reason. output=$markerOnlyText"
    }

    Write-Host "[YoloCompletionAuditVerify] pass selftest marker-only complete state"

    $missingPseudoGtPlanPath = Join-Path $selfTestDir "missing-pseudo-gt-complete-plan.md"
    @(
        "# Synthetic YOLO Completion Audit Missing Pseudo-GT Plan",
        "",
        "<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; pseudo-gt-test-only=pass; pseudo-gt-review-closure=conditional-gated; complete=true; remaining=none; completion-audit=pass-complete -->"
    ) | Set-Content -Encoding UTF8 -Path $missingPseudoGtPlanPath

    $missingPseudoGtCsv = Join-Path $selfTestDir "missing-pseudo-gt-candidates.csv"
    if (Test-Path $missingPseudoGtCsv) {
        Remove-Item -Force -Path $missingPseudoGtCsv
    }

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $missingPseudoGtOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
            -PlanDocument $missingPseudoGtPlanPath `
            -FullGtReviewCsv $reviewCsv `
            -FullFrameReviewCsv $frameCsv `
            -GuiChecklistCsv $guiChecklistCsv `
            -PredictionCsv $predictionCsv `
            -PseudoGtCsv $missingPseudoGtCsv `
            -PseudoGtReviewClosureCsv $pseudoGtClosureCsv `
            -ManualGateSummary $summaryPath `
            -MinIou 0.50 `
            -MaxMisses 0 `
            -MaxFalsePositives 0 `
            -MaxLowIou 0 `
            -RequireComplete 2>&1
        $missingPseudoGtExitCode = $LASTEXITCODE
        $missingPseudoGtText = ($missingPseudoGtOutput | Out-String)
        Write-Host $missingPseudoGtText
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($missingPseudoGtExitCode -eq 0) {
        throw "Completion audit missing pseudo-GT negative selftest unexpectedly passed."
    }
    if ($missingPseudoGtText -notmatch "Pseudo-GT candidate CSV is required for complete audit") {
        throw "Completion audit missing pseudo-GT negative selftest failed for an unexpected reason. output=$missingPseudoGtText"
    }

    Write-Host "[YoloCompletionAuditVerify] pass selftest missing pseudo-GT cannot RequireComplete"

    $pendingPlanPath = Join-Path $selfTestDir "pending-evidence-complete-plan.md"
    @(
        "# Synthetic YOLO Completion Audit Negative Plan",
        "",
        "<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; pseudo-gt-test-only=pass; pseudo-gt-review-closure=conditional-gated; complete=true; remaining=none; completion-audit=pass-complete -->"
    ) | Set-Content -Encoding UTF8 -Path $pendingPlanPath

    $pendingReviewCsv = Join-Path $selfTestDir "pending-full-gt-review.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            gtId = "gt-face-1"
            label = ""
            x = "100"
            y = "100"
            w = "80"
            h = "80"
            sourcePredictionId = "pred-face-1"
            sourceConfidence = "0.91"
            source = "prediction"
            cropPath = $cropPath
            reviewStatus = ""
            evidenceNotes = ""
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pendingReviewCsv

    $pendingFrameCsv = Join-Path $selfTestDir "pending-full-frame-review.csv"
    @(
        [pscustomobject]@{
            frame = "1"
            frameImagePath = $framePath
            overlayFrameImagePath = $overlayPath
            detectedCandidateCount = "1"
            candidateSummary = "pred-face-1"
            missedFaceCount = ""
            missedFaceRowsAdded = ""
            reviewStatus = ""
            evidenceNotes = ""
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pendingFrameCsv

    $pendingGuiChecklistCsv = Join-Path $selfTestDir "pending-manual-smoke-checklist.csv"
    $guiRows | ForEach-Object {
        $_.status = ""
        $_.evidenceType = ""
        $_.artifactPath = ""
        $_.evidence = ""
        $_
    } | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pendingGuiChecklistCsv

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $pendingOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
            -PlanDocument $pendingPlanPath `
            -FullGtReviewCsv $pendingReviewCsv `
            -FullFrameReviewCsv $pendingFrameCsv `
            -GuiChecklistCsv $pendingGuiChecklistCsv `
            -PredictionCsv $predictionCsv `
            -ManualGateSummary $summaryPath `
            -MinIou 0.50 `
            -MaxMisses 0 `
            -MaxFalsePositives 0 `
            -MaxLowIou 0 `
            -RequireComplete 2>&1
        $pendingExitCode = $LASTEXITCODE
        $pendingText = ($pendingOutput | Out-String)
        Write-Host $pendingText
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($pendingExitCode -eq 0) {
        throw "Completion audit pending evidence negative selftest unexpectedly passed."
    }
    if ($pendingText -notmatch "unreviewed rows|manual checklist|status|evidence") {
        throw "Completion audit pending evidence negative selftest failed for an unexpected reason. output=$pendingText"
    }

    Write-Host "[YoloCompletionAuditVerify] pass selftest pending evidence cannot RequireComplete"
    Write-Host "[YoloCompletionAuditVerify] all requested checks passed"
    return
}

$planPath = Assert-FileNonEmpty "plan document" $PlanDocument
$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$predictionCsvPath = ""
if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
    $predictionCsvPath = Assert-FileNonEmpty "prediction CSV" $PredictionCsv
}
else {
    $predictionLogPath = Assert-FileNonEmpty "prediction log" $PredictionLog
}
$manualGateSummaryPath = Assert-FileNonEmpty "manual gate summary" $ManualGateSummary

$plan = Get-Content -Raw -Path $planPath
$goalMarker = Get-YoloGoalAuditMarker $plan
$manualGateSummaryText = Get-Content -Raw -Path $manualGateSummaryPath
Assert-Contains "goal audit marker" $goalMarker "yolo-goal-audit-state:"
Assert-Contains "backend integrated" $goalMarker "backend=integrated"
Assert-Contains "faceonnx default" $goalMarker "default=FaceONNX"
Assert-Contains "no recommendation" $goalMarker "recommendation=none"
Assert-Contains "extended gate failed" $goalMarker "extended=fail"
Assert-Contains "extended export failed" $goalMarker "extended-export=fail"
Assert-Contains "track hold state passed" $goalMarker "track-hold-state=pass"
Assert-Contains "ten minute full deferred" $goalMarker "ten-minute-full=not-required-after-extended-fail"
Assert-Contains "pseudo-GT test-only boundary" $goalMarker "pseudo-gt-test-only=pass"
Assert-Contains "pseudo-GT review closure gate" $goalMarker "pseudo-gt-review-closure=conditional-gated"
Assert-Contains "manual summary remaining GUI smoke" $manualGateSummaryText "gui-smoke"
Assert-Contains "manual summary track hold GUI step" $manualGateSummaryText "preview-track-hold"
Assert-Contains "manual summary full GT command" $manualGateSummaryText "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "manual summary GUI command" $manualGateSummaryText "verify-yolo-gui-smoke-state.ps1"
Assert-Contains "manual summary completion command" $manualGateSummaryText "verify-yolo-manual-readiness-state.ps1"
Assert-Contains "manual summary app open command" $manualGateSummaryText "open-yolo-manual-gates.ps1 -OpenApp"
Assert-Contains "manual summary final yolo state command" $manualGateSummaryText "verify-yolo-state.ps1"
Assert-Contains "manual summary require complete command" $manualGateSummaryText "RequireComplete"
Assert-Contains "manual summary completion plan action" $manualGateSummaryText "completionPlanAction"

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameCsv)
$guiRows = @(Import-Csv $guiCsv)

if ($reviewRows.Count -eq 0) {
    throw "Full GT review CSV has no rows: $reviewCsv"
}
if ($frameRows.Count -eq 0) {
    throw "Full-frame review CSV has no rows: $frameCsv"
}
if ($guiRows.Count -eq 0) {
    throw "GUI checklist CSV has no rows: $guiCsv"
}

$reviewedRows = Get-FilledCount $reviewRows "label"
$frameReviewedRows = Get-FilledCount $frameRows "missedFaceCount"
$guiStatusRows = Get-FilledCount $guiRows "status"
$fullGtComplete = $reviewedRows -eq $reviewRows.Count -and $frameReviewedRows -eq $frameRows.Count
$guiComplete = $guiStatusRows -eq $guiRows.Count

if ($RequireComplete) {
    Assert-Contains "goal marked complete" $goalMarker "complete=true"
    Assert-Contains "no remaining gates" $goalMarker "remaining=none"

    $fullGtReviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
    $guiSmokeVerifier = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"

    $fullGtReviewedArgs = @(
        "-ReviewCsv", $reviewCsv,
        "-FullFrameReviewCsv", $frameCsv,
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou"
    )
    if (-not [string]::IsNullOrWhiteSpace($predictionCsvPath)) {
        $fullGtReviewedArgs += @("-PredictionCsv", $predictionCsvPath)
    }
    else {
        $fullGtReviewedArgs += @("-PredictionLog", $predictionLogPath)
    }
    if ($AllowQualityGateFailure) {
        $fullGtReviewedArgs += "-AllowQualityGateFailure"
    }

    Invoke-RequiredVerifier "full-gt-reviewed-state" $fullGtReviewedVerifier $fullGtReviewedArgs
    Invoke-RequiredVerifier "gui-smoke-state" $guiSmokeVerifier @(
        "-ChecklistCsv", $guiCsv,
        "-RequireManualPass"
    )
    $pseudoGtClosureState = Assert-PseudoGtReviewClosure -CandidateCsv $PseudoGtCsv -ClosureCsv $PseudoGtReviewClosureCsv -RequireCandidates

    Write-Host "[YoloCompletionAuditVerify] complete=true, fullGtRows=$($reviewRows.Count), guiRows=$($guiRows.Count), pseudoGtReviewClosure=$pseudoGtClosureState"
    Write-Host "[YoloCompletionAuditVerify] all requested checks passed"
    exit 0
}

Assert-Contains "goal remains incomplete" $goalMarker "complete=false"

if ($fullGtComplete -and -not $guiComplete) {
    Assert-Contains "remaining GUI smoke only" $goalMarker "remaining=gui-smoke"
    $fullGtReviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
    $fullGtReviewedArgs = @(
        "-ReviewCsv", $reviewCsv,
        "-FullFrameReviewCsv", $frameCsv,
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou"
    )
    if (-not [string]::IsNullOrWhiteSpace($predictionCsvPath)) {
        $fullGtReviewedArgs += @("-PredictionCsv", $predictionCsvPath)
    }
    else {
        $fullGtReviewedArgs += @("-PredictionLog", $predictionLogPath)
    }
    if ($AllowQualityGateFailure) {
        $fullGtReviewedArgs += "-AllowQualityGateFailure"
    }

    Invoke-RequiredVerifier "full-gt-reviewed-state" $fullGtReviewedVerifier $fullGtReviewedArgs
    Write-Host "[YoloCompletionAuditVerify] incomplete=true, remaining=gui-smoke, fullGtRows=$($reviewRows.Count), fullFrameRows=$($frameRows.Count), guiRows=$($guiRows.Count)"
    Write-Host "[YoloCompletionAuditVerify] all requested checks passed"
    exit 0
}

if (-not $fullGtComplete -and -not $guiComplete) {
    Assert-Contains "remaining full GT and GUI smoke" $goalMarker "remaining=full-gt-label,gui-smoke"
    if ($reviewedRows -ne 0) {
        throw "Default completion audit expects either completed or pending full GT labels; reviewedRows=$reviewedRows, totalRows=$($reviewRows.Count)"
    }
    if ($frameReviewedRows -ne 0) {
        throw "Default completion audit expects either completed or pending full-frame review rows; reviewedFrameRows=$frameReviewedRows, totalRows=$($frameRows.Count)"
    }
    if ($guiStatusRows -ne 0) {
        throw "Default completion audit expects pending GUI smoke checklist rows; guiStatusRows=$guiStatusRows"
    }

    Write-Host "[YoloCompletionAuditVerify] incomplete=true, remaining=full-gt-label,gui-smoke, fullGtRows=$($reviewRows.Count), fullFrameRows=$($frameRows.Count), guiRows=$($guiRows.Count)"
    Write-Host "[YoloCompletionAuditVerify] all requested checks passed"
    exit 0
}

throw "Unsupported incomplete completion state: fullGtComplete=$fullGtComplete, guiComplete=$guiComplete, reviewedRows=$reviewedRows/$($reviewRows.Count), frameReviewedRows=$frameReviewedRows/$($frameRows.Count), guiStatusRows=$guiStatusRows/$($guiRows.Count)"
