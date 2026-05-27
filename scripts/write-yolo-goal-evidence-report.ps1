param(
    [string]$PlanDocument = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$OutputPath = ".tmp\yolo-manual-gates\goal-evidence-report.md",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$PseudoGtReviewClosureCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$RequireComplete,
    [switch]$Verify
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

function Read-RequiredFile {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Required file not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
}

function Read-RequiredCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Required CSV not found: $resolved"
    }

    return @(Import-Csv $resolved)
}

function Count-Filled {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    }).Count
}

function Count-Matching {
    param(
        [object[]]$Rows,
        [string]$Column,
        [string]$Value
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and $_.$Column.Trim().ToLowerInvariant() -eq $Value
    }).Count
}

function Read-OptionalCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        return @()
    }

    return @(Import-Csv $resolved)
}

function Escape-Cell {
    param([string]$Value)

    return ($Value -replace "\|", "\|") -replace "`r?`n", " "
}

function Assert-ReportContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloGoalEvidenceReport] pass $Name"
}

function Get-GoalAuditMarker {
    param([string]$Text)

    $match = [regex]::Match(
        $Text,
        '<!--\s*(?<marker>yolo-goal-audit-state:\s*.*?)\s*-->',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if (-not $match.Success) {
        throw "Plan is missing yolo-goal-audit-state marker."
    }

    return $match.Groups["marker"].Value
}

function Invoke-FullGtQualityGate {
    param(
        [string]$ReviewCsv,
        [string]$FrameReviewCsv
    )

    $verifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
    if (-not (Test-Path $verifier)) {
        throw "Full GT reviewed verifier not found: $verifier"
    }

    $args = @(
        "-ReviewCsv", (Resolve-RepoPath $ReviewCsv),
        "-FullFrameReviewCsv", (Resolve-RepoPath $FrameReviewCsv),
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou"
    )

    if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
        $args += @("-PredictionCsv", (Resolve-RepoPath $PredictionCsv))
    }
    else {
        $args += @("-PredictionLog", (Resolve-RepoPath $PredictionLog))
    }

    if ($AllowQualityGateFailure) {
        $args += "-AllowQualityGateFailure"
    }

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier @args 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    $text = ($output | Out-String)
    $summaryLine = (($text -split "`r?`n") | Where-Object { $_.StartsWith("[YoloFullGtLabelSummary]") } | Select-Object -Last 1)
    $gateLine = (($text -split "`r?`n") | Where-Object { $_.StartsWith("[YoloFullGtLabelQualityGate]") -and $_.Contains("passed=") } | Select-Object -Last 1)
    $failureAllowed = $text.Contains("failureAllowed=True")
    $passed = $gateLine -match "passed=True"

    [pscustomobject]@{
        ExitCode = $exitCode
        Passed = $passed
        FailureAllowed = $failureAllowed
        SummaryLine = if ($summaryLine) { $summaryLine } else { "n/a" }
        GateLine = if ($gateLine) { $gateLine } else { "n/a" }
        Output = $text
    }
}

$plan = Read-RequiredFile $PlanDocument
$goalMarker = Get-GoalAuditMarker $plan
$summary = Read-RequiredFile $ManualGateSummary
$fullGtRows = @(Read-RequiredCsv $FullGtReviewCsv)
$fullFrameRows = @(Read-RequiredCsv $FullFrameReviewCsv)
$guiRows = @(Read-RequiredCsv $GuiChecklistCsv)

$fullGtReviewed = Count-Filled $fullGtRows "reviewStatus"
$fullGtLabels = Count-Filled $fullGtRows "label"
$fullFrameReviewed = Count-Filled $fullFrameRows "reviewStatus"
$fullFrameMissCounts = Count-Filled $fullFrameRows "missedFaceCount"
$guiPassed = Count-Matching $guiRows "status" "pass"
$guiArtifacts = Count-Filled $guiRows "artifactPath"
$previewTrackHoldPassed = @($guiRows | Where-Object {
    $null -ne $_.PSObject.Properties["stepId"] -and
    $null -ne $_.PSObject.Properties["status"] -and
    $_.stepId -eq "preview-track-hold" -and
    $_.status.Trim().ToLowerInvariant() -eq "pass"
}).Count -gt 0

$planIncomplete = $goalMarker.Contains("complete=false") -and (
    $goalMarker.Contains("remaining=full-gt-label,gui-smoke") -or
    $goalMarker.Contains("remaining=gui-smoke"))
$planComplete = $goalMarker.Contains("complete=true") -and $goalMarker.Contains("remaining=none")
if (-not $planIncomplete -and -not $planComplete) {
    throw "Goal audit marker must contain complete=false with a supported remaining gate or complete=true/remaining=none."
}

$fullGtFilled = $fullGtRows.Count -gt 0 -and
    $fullGtReviewed -eq $fullGtRows.Count -and
    $fullGtLabels -eq $fullGtRows.Count -and
    $fullFrameRows.Count -gt 0 -and
    $fullFrameReviewed -eq $fullFrameRows.Count -and
    $fullFrameMissCounts -eq $fullFrameRows.Count
$guiFilled = $guiRows.Count -gt 0 -and $guiPassed -eq $guiRows.Count -and $guiArtifacts -eq $guiRows.Count

$pseudoGtRows = @(Read-OptionalCsv $PseudoGtCsv)
$pseudoGtClosureRows = @(Read-OptionalCsv $PseudoGtReviewClosureCsv)
$pseudoGtClosedRows = @($pseudoGtClosureRows | Where-Object {
    $null -ne $_.PSObject.Properties["closureStatus"] -and $_.closureStatus.Trim().ToLowerInvariant() -eq "closed"
}).Count
$pseudoGtOpenRows = if ($pseudoGtRows.Count -eq 0) {
    0
}
else {
    [Math]::Max(0, $pseudoGtRows.Count - $pseudoGtClosedRows)
}
$pseudoGtStatus = if ($pseudoGtRows.Count -eq 0) {
    "skipped-no-candidates"
}
elseif ($pseudoGtClosureRows.Count -eq $pseudoGtRows.Count -and $pseudoGtOpenRows -eq 0) {
    "closed"
}
else {
    "pending-human"
}

$qualityGate = $null
if ($fullGtFilled) {
    $qualityGate = Invoke-FullGtQualityGate -ReviewCsv $FullGtReviewCsv -FrameReviewCsv $FullFrameReviewCsv
}

$qualityGateStatus = "pending-human"
$qualityGateEvidence = "full GT labels and full-frame review must be filled before quality gate can run"
if ($qualityGate -ne $null) {
    if ($qualityGate.Passed) {
        $qualityGateStatus = "pass"
    }
    elseif ($qualityGate.FailureAllowed) {
        $qualityGateStatus = "fail-documented"
    }
    else {
        $qualityGateStatus = "fail-blocking"
    }

    $qualityGateEvidence = "exitCode=$($qualityGate.ExitCode), $($qualityGate.GateLine), $($qualityGate.SummaryLine)"
}

$fullGtStatus = if ($fullGtFilled) { "filled-pending-strict-gate" } else { "pending-human" }
$guiStatus = if ($guiFilled) { "filled-pending-strict-gate" } else { "pending-human" }
$previewTrackHoldStatus = if ($previewTrackHoldPassed) { "pass" } else { "pending-human" }
$qualityGateReady = $qualityGateStatus -eq "pass" -or $qualityGateStatus -eq "fail-documented"
$pseudoGtReady = $pseudoGtStatus -eq "closed" -or $pseudoGtStatus -eq "skipped-no-candidates"
$goalStatus = if ($planComplete -and $fullGtFilled -and $guiFilled -and $qualityGateReady -and $pseudoGtReady) { "ready-for-strict-completion-audit" } else { "incomplete" }
$planCompletionText = if ($planComplete) { "complete=true" } else { "complete=false" }
$planRemainingText = if ($planComplete) { "remaining=none" } elseif ($goalMarker.Contains("remaining=gui-smoke")) { "remaining=gui-smoke" } else { "remaining=full-gt-label,gui-smoke" }

if ($RequireComplete -and -not ($planComplete -and $fullGtFilled -and $guiFilled -and $qualityGateReady -and $pseudoGtReady)) {
    throw "Evidence report is not ready for strict completion audit: planComplete=$planComplete, fullGtFilled=$fullGtFilled, guiFilled=$guiFilled, qualityGateStatus=$qualityGateStatus, pseudoGtStatus=$pseudoGtStatus, pseudoGtRows=$($pseudoGtRows.Count), pseudoGtClosedRows=$pseudoGtClosedRows, fullGtRows=$($fullGtRows.Count), fullGtReviewed=$fullGtReviewed, fullGtLabels=$fullGtLabels, fullFrameRows=$($fullFrameRows.Count), fullFrameReviewed=$fullFrameReviewed, fullFrameMissCounts=$fullFrameMissCounts, guiRows=$($guiRows.Count), guiPassed=$guiPassed, guiArtifacts=$guiArtifacts"
}

$rows = @(
    [pscustomobject]@{ Requirement = "YOLO backend/profile split"; Status = "pass"; Evidence = "verify-yolo-profile-state.ps1, yolo-state=pass" },
    [pscustomobject]@{ Requirement = "FaceONNX default preservation"; Status = "pass"; Evidence = "verify-auto-mosaic-default=pass, integrated scene/sparse guards pass, SmokeQualityGate passed=True, baselineFrames=19, optimizedFrames=19, avgBestIou=1.000, minBestIou=1.000" },
    [pscustomobject]@{ Requirement = "YOLO representative 3s gate"; Status = "pass"; Evidence = "representative=pass, SmokeQualityGate passed=True" },
    [pscustomobject]@{ Requirement = "YOLOv8 candidate A/B comparison"; Status = "pass"; Evidence = "YOLOv8n/m/s/l classified as no recommendation, documented strict gate failures" },
    [pscustomobject]@{ Requirement = "YOLO5Face candidate A/B comparison"; Status = "pass"; Evidence = "representative pass, extended/export fail, speed gain not promoted without quality gate" },
    [pscustomobject]@{ Requirement = "Failure-axis classification"; Status = "pass"; Evidence = "axes=model/decode/preprocess/post-filter/track/roi/tiling/small-face/box-refine/speed documented" },
    [pscustomobject]@{ Requirement = "Anti-flicker track hold algorithm"; Status = "pass"; Evidence = "track-hold-state=pass, app YOLO MaxLostFillFrames=0, app YOLO MaxInitialFillFrames=3, app keeps internal gap-fill but does not carry final tracks into the next scene, YOLO synthetic track-fill confidence capped at 0.78 for opt-in lost-fill scene-cut cleanup, sceneCutLostRemoved=6 verifies capped high-confidence lost-fill tails are cleared after a hard cut, stable final-mask gap fill up to 5 frames, synthetic cap verifier still covers MaxLostFillFrames=6" },
    [pscustomobject]@{ Requirement = "Scene-cut track reset guard"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes scene-cut-guard=pass, final YOLO scene-cut cleanup runs after ROI/smoothing, post-scene weak cleanup runs without gap-fill, gap/lost track fill plus weak and medium-confidence direct carry-tail transitions checked, hardCutRemoved=True, sameSceneKept=True, directRemoved=3, mediumDirectRemoved=3, checkedPairs and removedFrames logged" },
    [pscustomobject]@{ Requirement = "Sparse auto-detect scene-cut guard"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes automask-sparse-scene-cut-guard=pass and automask-sparse-materialize-scene-cut=pass, YOLO hard cut interpolated=0 sceneCutStops=1, yoloSceneCutTransitions=0->5, cutBeforePositiveInterpolated=0, sparseSceneCutPairs logged" },
    [pscustomobject]@{ Requirement = "Sparse/unstable-tail/edge-tail temporal false-positive filter"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes face-track-postprocess=pass, removedSparse=3, removedUnstableTail=1, removedEdgeTail=1, YOLO sparse weak track profile active, low-confidence edge lost-fill blocked" },
    [pscustomobject]@{ Requirement = "YOLO aspect-ratio candidate filter"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes yolo-aspect-ratio-filter=pass; YoloFaceOnnxDetectorOptions exposes UseAspectRatioFilter/MinAspectRatio/MaxAspectRatio, app YOLO path sets 0.35-1.65, smoke harness can exercise the filter" },
    [pscustomobject]@{ Requirement = "Frame-level false-positive review evidence"; Status = "pass"; Evidence = "run-srcTest-smoke -DumpDetections emits parseable SmokeDetection lines with cx/cy/areaRatio/aspectRatio plus SmokeDetectionSummary; FinalMaskSummary/SmokeFinalMaskSummary now separates weakNonEdge, edgeWeak, topEdgeWeak, upperWeak, lowerWeak, aspectBad, tinyWeak, and tinyShort residuals; write-yolo-mask-continuity-report lists weak edge/top-edge/upper/lower/aspect-outlier review targets; new-yolo-full-gt-template/new-yolo-full-gt-review-package create crop and full-frame review artifacts for face/nonface/miss labeling; write-yolo-followup-quality-evidence wraps the follow-up evidence bundle" },
    [pscustomobject]@{ Requirement = "Preview track-hold GUI evidence"; Status = $previewTrackHoldStatus; Evidence = "preview-track-hold status=$previewTrackHoldPassed, required recording artifact before completion" },
    [pscustomobject]@{ Requirement = "30s extended quality gate"; Status = "fail"; Evidence = "extended=fail, SmokeQualityGate passed=False" },
    [pscustomobject]@{ Requirement = "30s export smoke quality"; Status = "fail"; Evidence = "extended-export=fail, directFaceFrames mismatch remains" },
    [pscustomobject]@{ Requirement = "Final YOLO recommendation"; Status = "none"; Evidence = "recommendation=none, no-final-yolo-recommendation" },
    [pscustomobject]@{ Requirement = "Full-GT label review"; Status = $fullGtStatus; Evidence = "rows=$($fullGtRows.Count), labels=$fullGtLabels, reviewed=$fullGtReviewed, fullFrameRows=$($fullFrameRows.Count), fullFrameReviewed=$fullFrameReviewed, missedFaceCountFilled=$fullFrameMissCounts" },
    [pscustomobject]@{ Requirement = "Full-GT quality gate"; Status = $qualityGateStatus; Evidence = $qualityGateEvidence },
    [pscustomobject]@{ Requirement = "Test-only pseudo-GT review closure"; Status = $pseudoGtStatus; Evidence = "candidates=$($pseudoGtRows.Count), closureRows=$($pseudoGtClosureRows.Count), closed=$pseudoGtClosedRows, open=$pseudoGtOpenRows; runtimePath=not-used-by-app" },
    [pscustomobject]@{ Requirement = "Avalonia GUI smoke"; Status = $guiStatus; Evidence = "steps=$($guiRows.Count), pass=$guiPassed, artifactPathFilled=$guiArtifacts, required=preview-track-hold/manual-edit/export/reopen-state" },
    [pscustomobject]@{ Requirement = "Model license/distribution policy"; Status = "pass"; Evidence = "license-source=pass, bundle=blocked" },
    [pscustomobject]@{ Requirement = "10-minute/whole-video decision"; Status = "deferred"; Evidence = "ten-minute-full=not-required-after-extended-fail" },
    [pscustomobject]@{ Requirement = "Goal completion"; Status = $goalStatus; Evidence = "$planCompletionText, $planRemainingText, fullGtFilled=$fullGtFilled, guiFilled=$guiFilled" }
)

$requiredGoalMarkerTokens = @("yolo-goal-audit-state:")
if ($planIncomplete) {
    $requiredGoalMarkerTokens += "top-level-ready-rerun=pass"
}

$missingPlanTokens = $requiredGoalMarkerTokens | Where-Object { -not $goalMarker.Contains($_) }

if ($missingPlanTokens.Count -gt 0) {
    throw "Plan is missing required goal audit tokens: $($missingPlanTokens -join ', ')"
}

$requiredSummaryTokens = @("gui-smoke", "preview-track-hold", "RequireComplete")
if ($planRemainingText -eq "remaining=full-gt-label,gui-smoke") {
    $requiredSummaryTokens += "full-gt-label"
}
elseif ($planIncomplete) {
    $requiredSummaryTokens += "fullGtPendingRows=0"
}

foreach ($requiredSummaryToken in $requiredSummaryTokens) {
    if (-not $summary.Contains($requiredSummaryToken)) {
        throw "Manual gate summary is missing required token: $requiredSummaryToken"
    }
}

$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$lines = @(
    "# YOLO Goal Evidence Report",
    "",
    "This report is generated from the current plan markers and review artifacts. It is not a completion approval.",
    "",
    "- $planCompletionText",
    "- $planRemainingText",
    "- fullGtRows=$($fullGtRows.Count)",
    "- fullGtReviewed=$fullGtReviewed",
    "- fullGtFilled=$fullGtFilled",
    "- fullGtQualityGate=$qualityGateStatus",
    "- pseudoGtStatus=$pseudoGtStatus",
    "- pseudoGtRows=$($pseudoGtRows.Count)",
    "- pseudoGtClosedRows=$pseudoGtClosedRows",
    "- fullFrameRows=$($fullFrameRows.Count)",
    "- fullFrameReviewed=$fullFrameReviewed",
    "- guiSteps=$($guiRows.Count)",
    "- guiPassed=$guiPassed",
    "- guiFilled=$guiFilled",
    "- goalStatus=$goalStatus",
    "",
    "| Requirement | Status | Evidence |",
    "| --- | --- | --- |"
)

foreach ($row in $rows) {
    $lines += "| $(Escape-Cell $row.Requirement) | $(Escape-Cell $row.Status) | $(Escape-Cell $row.Evidence) |"
}

$lines | Set-Content -Encoding UTF8 -Path $resolvedOutput

$report = Get-Content -Raw -Path $resolvedOutput
foreach ($requiredReportToken in @(
    "Full-GT label review",
    "Full-GT quality gate",
    "Test-only pseudo-GT review closure",
    "Avalonia GUI smoke",
    "Preview track-hold GUI evidence",
    "YOLOv8 candidate A/B comparison",
    "YOLO5Face candidate A/B comparison",
    "Failure-axis classification",
    "Goal completion",
    $planCompletionText,
    $planRemainingText,
    "track-hold-state=pass",
    "recommendation=none")) {
    Assert-ReportContains "report token $requiredReportToken" $report $requiredReportToken
}

if ($Verify) {
    if ($RequireComplete) {
        Assert-ReportContains "report keeps full GT filled" $report "Full-GT label review | filled-pending-strict-gate"
        Assert-ReportContains "report keeps full GT quality checked" $report "Full-GT quality gate | $qualityGateStatus"
        Assert-ReportContains "report keeps pseudo-GT closure checked" $report "Test-only pseudo-GT review closure | $pseudoGtStatus"
        Assert-ReportContains "report keeps GUI filled" $report "Avalonia GUI smoke | filled-pending-strict-gate"
        Assert-ReportContains "report keeps preview track-hold passed" $report "Preview track-hold GUI evidence | pass"
        Assert-ReportContains "report keeps completion ready for strict audit" $report "Goal completion | ready-for-strict-completion-audit"
    }
    else {
        if ($fullGtFilled) {
            Assert-ReportContains "report keeps full GT filled" $report "Full-GT label review | filled-pending-strict-gate"
            Assert-ReportContains "report keeps full GT quality checked" $report "Full-GT quality gate | $qualityGateStatus"
            Assert-ReportContains "report records full GT quality state" $report "fullGtQualityGate=$qualityGateStatus"
        }
        else {
            Assert-ReportContains "report keeps full GT pending" $report "Full-GT label review | pending-human"
            Assert-ReportContains "report keeps full GT quality pending" $report "Full-GT quality gate | pending-human"
            Assert-ReportContains "report records pending full GT quality state" $report "fullGtQualityGate=pending-human"
        }
        if ($guiFilled) {
            Assert-ReportContains "report keeps GUI filled" $report "Avalonia GUI smoke | filled-pending-strict-gate"
        }
        else {
            Assert-ReportContains "report keeps GUI pending" $report "Avalonia GUI smoke | pending-human"
        }

        Assert-ReportContains "report records pseudo-GT closure state" $report "pseudoGtStatus=$pseudoGtStatus"

        if ($previewTrackHoldPassed) {
            Assert-ReportContains "report keeps preview track-hold passed" $report "Preview track-hold GUI evidence | pass"
        }
        else {
            Assert-ReportContains "report keeps preview track-hold pending" $report "Preview track-hold GUI evidence | pending-human"
        }

        if ($goalStatus -eq "ready-for-strict-completion-audit") {
            Assert-ReportContains "report keeps completion ready for strict audit" $report "Goal completion | ready-for-strict-completion-audit"
        }
        else {
            Assert-ReportContains "report keeps completion incomplete" $report "Goal completion | incomplete"
        }
    }
}

Write-Host "[YoloGoalEvidenceReport] wrote output=$resolvedOutput"
Write-Host "[YoloGoalEvidenceReport] all requested checks passed"
