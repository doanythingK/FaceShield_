param(
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$EvidenceReport = ".tmp\yolo-manual-gates\goal-evidence-report.md"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$yoloStateVerify = Join-Path $repo "scripts\verify-yolo-state.ps1"
$manualGateHelper = Join-Path $repo "scripts\open-yolo-manual-gates.ps1"
$completionAuditVerify = Join-Path $repo "scripts\verify-yolo-completion-audit-state.ps1"
$evidenceReportWriter = Join-Path $repo "scripts\write-yolo-goal-evidence-report.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Invoke-RequiredStep {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name not found: $ScriptPath"
    }

    Write-Host "[YoloReadyForHumanGatesVerify] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }

    Write-Host "[YoloReadyForHumanGatesVerify] pass $Name"
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

    Write-Host "[YoloReadyForHumanGatesVerify] pass $Name"
}

Invoke-RequiredStep "yolo-state" $yoloStateVerify @(
    "-AllowCompletedFullGt",
    "-AllowFullGtQualityGateFailure"
)
Invoke-RequiredStep "manual-gate-summary" $manualGateHelper @(
    "-WriteSummary",
    "-VerifyReady",
    "-SummaryPath", $ManualGateSummary
)
Invoke-RequiredStep "goal-evidence-report" $evidenceReportWriter @(
    "-ManualGateSummary", $ManualGateSummary,
    "-OutputPath", $EvidenceReport,
    "-AllowQualityGateFailure",
    "-Verify"
)

$summaryPath = Resolve-RepoPath $ManualGateSummary
if (-not (Test-Path $summaryPath)) {
    throw "Manual gate summary not found: $summaryPath"
}

$summary = Get-Content -Raw -Path $summaryPath
$completionAuditArgs = @(
    "-ManualGateSummary", $ManualGateSummary,
    "-AllowQualityGateFailure"
)
if ($summary.Contains("remaining=none")) {
    $completionAuditArgs += "-RequireComplete"
}

Invoke-RequiredStep "completion-audit" $completionAuditVerify $completionAuditArgs
Assert-Contains "summary keeps completed full GT progress" $summary "fullGtPendingRows=0"
Assert-Contains "summary keeps completed full-frame progress" $summary "fullFramePendingRows=0"
if ($summary.Contains("remaining=none")) {
    Assert-Contains "summary records no remaining gates" $summary "remaining=none"
    Assert-Contains "summary keeps completed GUI progress" $summary "guiPendingRows=0"
}
else {
    Assert-Contains "summary records GUI-only remaining gates" $summary "- gui-smoke"
    Assert-Contains "summary keeps pending GUI progress" $summary "guiPendingRows=9"
}
Assert-Contains "summary records app open command" $summary "open-yolo-manual-gates.ps1 -OpenApp"
Assert-Contains "summary records smoke manual startup command" $summary "--yolo-smoke --open-manual"
Assert-Contains "summary records smoke auto startup command" $summary "--yolo-smoke --open-auto"
Assert-Contains "summary records dashboard open command" $summary "open-yolo-manual-gates.ps1 -WriteSummary -OpenDashboard"
Assert-Contains "summary records dashboard path" $summary "manual-gate-dashboard.html"
Assert-Contains "summary records manual progress counts" $summary "fullGtPendingRows="
Assert-Contains "summary records full-frame progress counts" $summary "fullFramePendingRows="
Assert-Contains "summary records GUI progress counts" $summary "guiPendingRows="
Assert-Contains "summary records next GUI step" $summary "nextGuiStep="
Assert-Contains "summary records next GUI evidence setter" $summary "nextGuiEvidenceSetterCommand="
Assert-Contains "summary records final completion command" $summary "verify-yolo-state.ps1"
Assert-Contains "summary records completion finalizer command" $summary "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "summary records finalizer update plan" $summary "UpdatePlan"
Assert-Contains "summary records require complete" $summary "RequireComplete"
Assert-Contains "summary records completion plan action" $summary "completionPlanAction"
Assert-Contains "summary records anti-flicker GUI step" $summary "preview-track-hold"
Assert-Contains "summary records human review draft report" $summary "human-review-draft-report.md"
if ($summary.Contains("skippedHumanReviewDraft=full-gt-complete")) {
    Write-Host "[YoloReadyForHumanGatesVerify] pass summary records human review draft skipped after full GT completion"
} else {
    Assert-Contains "summary records human review draft command" $summary "new-yolo-human-review-draft.ps1"
}

$evidenceReportPath = Resolve-RepoPath $EvidenceReport
if (-not (Test-Path $evidenceReportPath)) {
    throw "Evidence report not found: $evidenceReportPath"
}

$report = Get-Content -Raw -Path $evidenceReportPath
Assert-Contains "report records filled full GT" $report "Full-GT label review | filled-pending-strict-gate"
Assert-Contains "report records documented full GT quality failure" $report "Full-GT quality gate | fail-documented"
Assert-Contains "report records documented full GT quality state" $report "fullGtQualityGate=fail-documented"
if ($summary.Contains("remaining=none")) {
    Assert-Contains "report records filled GUI smoke" $report "Avalonia GUI smoke | filled-pending-strict-gate"
    Assert-Contains "report records preview track hold pass" $report "Preview track-hold GUI evidence | pass"
    Assert-Contains "report records completion ready" $report "Goal completion | ready-for-strict-completion-audit"
}
else {
    Assert-Contains "report keeps GUI pending" $report "Avalonia GUI smoke | pending-human"
    Assert-Contains "report keeps preview track hold pending" $report "Preview track-hold GUI evidence | pending-human"
    Assert-Contains "report keeps goal incomplete" $report "Goal completion | incomplete"
}
Assert-Contains "report records YOLOv8 candidate comparison" $report "YOLOv8 candidate A/B comparison | pass"
Assert-Contains "report records YOLO5Face candidate comparison" $report "YOLO5Face candidate A/B comparison | pass"
Assert-Contains "report records failure axis classification" $report "Failure-axis classification | pass"
Assert-Contains "report records pseudo-GT review queue" $report "Test-only pseudo-GT review queue |"
Assert-Contains "report records pseudo-GT review queue status" $report "pseudoGtReviewQueueStatus="
Assert-Contains "report keeps recommendation none" $report "Final YOLO recommendation | none"

$remaining = if ($summary.Contains("remaining=none")) { "none" } else { "gui-smoke" }
Write-Host "[YoloReadyForHumanGatesVerify] ready=true, remaining=$remaining"
Write-Host "[YoloReadyForHumanGatesVerify] all requested checks passed"
