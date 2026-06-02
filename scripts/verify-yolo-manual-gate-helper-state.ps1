param(
    [string]$ManualGateHelper = "scripts\open-yolo-manual-gates.ps1",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$CompletedFixtureReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$CompletedFixtureFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$CompletedFixtureGuiChecklistCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\manual-smoke-checklist.csv",
    [string]$CompletedFixtureManualReviewCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\full-gt-review-reviewed.csv",
    [string]$CompletedFixtureManualFullFrameReviewCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\full-frame-review-reviewed.csv",
    [string]$CompletedFixturePseudoGtCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\pseudo-gt-candidates.csv",
    [string]$CompletedFixturePseudoGtReviewClosureCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\pseudo-gt-review-closure.csv",
    [string]$CompletedFixturePseudoGtReviewClosureSummary = ".tmp\yolo-manual-gate-helper\completed-fixture\pseudo-gt-review-closure-summary.md",
    [string]$SummaryPath = ".tmp\yolo-manual-gates\manual-gate-summary.md"
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

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloManualGateHelperVerify] pass $Name"
}

function Invoke-Helper {
    param([string[]]$Arguments)

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helperPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = $text
    }
}

function Get-ColumnFillState {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    if ($Rows.Count -eq 0) {
        return "empty"
    }

    $filled = @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    })

    if ($filled.Count -eq 0) {
        return "pending"
    }

    if ($filled.Count -eq $Rows.Count) {
        return "completed"
    }

    return "partial"
}

function Test-PseudoGtReviewClosureReady {
    param(
        [string]$ClosureCsvPath,
        [string]$ClosureSummaryPath
    )

    $resolvedClosureCsv = Resolve-RepoPath $ClosureCsvPath
    $resolvedClosureSummary = Resolve-RepoPath $ClosureSummaryPath
    if (-not (Test-Path $resolvedClosureCsv) -or -not (Test-Path $resolvedClosureSummary)) {
        return $false
    }

    $rows = @(Import-Csv $resolvedClosureCsv)
    if ($rows.Count -eq 0) {
        return $false
    }

    return @($rows | Where-Object { $_.closureStatus -ne "closed" }).Count -eq 0
}

function New-CompletedGuiChecklistFixture {
    param([string]$OutputCsv)

    $outputPath = Resolve-RepoPath $OutputCsv
    $outputDir = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $relativeDir = ".tmp\yolo-manual-gate-helper\completed-fixture"
    $artifactMap = @{
        "open-video" = "open-video.png"
        "select-yolo-backend" = "select-yolo-backend.png"
        "download-yolo-model" = "download-yolo-model.log"
        "run-yolo-auto-detect" = "run-yolo-auto-detect.log"
        "preview-result" = "preview-result.mp4"
        "preview-track-hold" = "preview-track-hold.mp4"
        "manual-edit" = "manual-edit.png"
        "export" = "export.mp4"
        "reopen-state" = "reopen-state.png"
    }
    $evidenceTypeMap = @{
        "open-video" = "screenshot-or-recording"
        "select-yolo-backend" = "screenshot"
        "download-yolo-model" = "screenshot-or-log"
        "run-yolo-auto-detect" = "screenshot-or-log"
        "preview-result" = "screenshot-or-recording"
        "preview-track-hold" = "recording"
        "manual-edit" = "screenshot-or-recording"
        "export" = "output-file"
        "reopen-state" = "screenshot-or-recording"
    }

    $rows = foreach ($step in @(
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
        $artifactRelative = Join-Path $relativeDir $artifactMap[$step]
        $artifactPath = Resolve-RepoPath $artifactRelative
        Set-Content -Encoding UTF8 -Path $artifactPath -Value "manual gate helper completed fixture artifact for $step"

        [pscustomobject]@{
            stepId = $step
            status = "pass"
            evidenceType = $evidenceTypeMap[$step]
            artifactPath = $artifactRelative
            evidence = "manual gate helper completed fixture evidence for $step"
            notes = "fixture only; not real human GUI smoke evidence"
        }
    }

    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
    return $outputPath
}

function New-ReviewedCsvFixture {
    param(
        [string]$InputCsv,
        [string]$OutputCsv
    )

    $inputPath = Resolve-RepoPath $InputCsv
    $outputPath = Resolve-RepoPath $OutputCsv
    $outputDir = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $rows = @(Import-Csv $inputPath)
    foreach ($row in $rows) {
        if ($null -ne $row.PSObject.Properties["reviewStatus"]) {
            $row.reviewStatus = "reviewed"
        }
        if ($null -ne $row.PSObject.Properties["evidenceNotes"] -and [string]::IsNullOrWhiteSpace([string]$row.evidenceNotes)) {
            $row.evidenceNotes = "manual gate helper reviewed fixture evidence"
        }
    }

    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
    return $outputPath
}

function New-CompletedPseudoGtFixture {
    param(
        [string]$ReviewCsv,
        [string]$OutputCsv
    )

    $reviewPath = Resolve-RepoPath $ReviewCsv
    $outputPath = Resolve-RepoPath $OutputCsv
    $outputDir = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $reviewRow = @(Import-Csv $reviewPath | Select-Object -First 1)
    if ($reviewRow.Count -eq 0) {
        throw "Cannot create pseudo-GT fixture because review CSV has no rows: $reviewPath"
    }

    $row = $reviewRow[0]
    $candidate = [pscustomobject]@{
        candidateId = "fixture-supported-face-0"
        frame = $row.frame
        candidateType = "supportedFaceCandidate"
        x = $row.x
        y = $row.y
        w = $row.w
        h = $row.h
        basePredictionId = $row.sourcePredictionId
        baseFaceConfidence = $row.sourceConfidence
        tileFaceConfidence = "0.93"
        tileSupportCount = "1"
        faceVerificationConfidence = "0.91"
        faceVerificationDistance = "0.11"
        personConfidence = ""
        personUpperOverlap = ""
        personObjectClass = ""
        supportFrameCount = "1"
        supportRowCount = "2"
        supportSources = "tile-face;face-verification"
        bestIou = "1"
        centerDistanceRatio = "0"
        areaChangeRatio = "0"
        fpProbability = "0.01"
        missProbability = "0.01"
        pseudoGtReason = "fixture high-precision support closes through human reviewed face label"
    }

    @($candidate) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
    return $outputPath
}

$helperPath = Assert-FileNonEmpty "manual gate helper" $ManualGateHelper
$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$completedReviewCsv = Assert-FileNonEmpty "completed fixture full GT review CSV" $CompletedFixtureReviewCsv
$completedFrameCsv = Assert-FileNonEmpty "completed fixture full-frame review CSV" $CompletedFixtureFullFrameReviewCsv

$helperText = Get-Content -Raw -Path $helperPath
Assert-Contains "helper prints completed manual readiness command" $helperText "completedManualReadinessCommand"
Assert-Contains "helper prints pseudo GT review closure command" $helperText "completedPseudoGtReviewClosureCommand"
Assert-Contains "helper uses pseudo GT review closure script" $helperText "close-yolo-pseudo-gt-review.ps1"
Assert-Contains "helper prints pseudo GT status" $helperText "pseudoGtStatus"
Assert-Contains "helper prints pseudo GT action" $helperText "pseudoGtAction"
Assert-Contains "helper prints pseudo GT review draft command" $helperText "pseudoGtReviewDraftCommand"
Assert-Contains "helper prints pseudo GT visual command" $helperText "pseudoGtReviewVisualCommand"
Assert-Contains "helper prints pseudo GT decision progress command" $helperText "pseudoGtDecisionProgressCommand"
Assert-Contains "helper prints pseudo GT review draft apply command" $helperText "pseudoGtReviewDraftApplyCommand"
Assert-Contains "helper uses pseudo GT decision progress writer" $helperText "write-yolo-pseudo-gt-decision-progress.ps1"
Assert-Contains "helper applies pseudo GT review draft in place" $helperText "-InPlace -Verify"
Assert-Contains "helper applies pseudo GT compact decision sheet" $helperText "-DecisionCsv"
Assert-Contains "helper records pseudo GT review queue csv" $helperText "PseudoGtReviewQueueCsv"
Assert-Contains "helper records pseudo GT review draft dir" $helperText "PseudoGtReviewDraftDir"
Assert-Contains "helper records pseudo GT visual dir" $helperText "PseudoGtReviewVisualDir"
Assert-Contains "helper uses pseudo GT review draft writer" $helperText "new-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "helper uses pseudo GT visual package writer" $helperText "new-yolo-pseudo-gt-review-visual-package.ps1"
Assert-Contains "helper uses pseudo GT review draft apply script" $helperText "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "helper documents goal evidence publish switch" $helperText "PublishPseudoGtToGoalEvidence"
Assert-Contains "helper requires pseudo GT before completion" $helperText "Pseudo-GT candidate CSV is required before completed manual gates"
Assert-Contains "helper supports ready verification" $helperText "VerifyReady"
Assert-Contains "helper supports completed verification" $helperText "VerifyCompleted"
Assert-Contains "helper completed path allows full GT" $helperText "AllowCompletedFullGt"
Assert-Contains "helper completed path allows GUI smoke" $helperText "AllowCompletedGuiSmoke"
Assert-Contains "helper completed path allows documented quality failure" $helperText "AllowQualityGateFailure"
Assert-Contains "helper prints final yolo state command" $helperText "completedYoloStateCommand"
Assert-Contains "helper final yolo state requires complete" $helperText "RequireComplete"
Assert-Contains "helper final yolo state allows full GT quality failure" $helperText "AllowFullGtQualityGateFailure"
Assert-Contains "helper prints completion finalizer command" $helperText "completionFinalizerCommand"
Assert-Contains "helper finalizer updates plan" $helperText "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "helper prints pending report command" $helperText "pendingReportCommand"
Assert-Contains "helper writes pending report" $helperText "manual-pending-report"
Assert-Contains "helper prints human review draft command" $helperText "humanReviewDraftCommand"
Assert-Contains "helper writes human review draft" $helperText "human-review-draft"
Assert-Contains "helper prints completion plan action" $helperText "completionPlanAction"
Assert-Contains "helper completed path runs full GT verifier" $helperText "full-gt-reviewed-state"
Assert-Contains "helper completed path runs GUI verifier" $helperText "gui-smoke-state"
Assert-Contains "helper computes remaining gates" $helperText "Get-RemainingManualGates"
Assert-Contains "helper prints remaining gates" $helperText "remaining="
Assert-Contains "helper prints open command" $helperText "openCommand"
Assert-Contains "helper prints dashboard open command" $helperText "openDashboardCommand"
Assert-Contains "helper supports dashboard open" $helperText "OpenDashboard"
Assert-Contains "helper prints app open command" $helperText "openAppCommand"
Assert-Contains "helper supports app open" $helperText "OpenApp"
Assert-Contains "helper app open starts dotnet" $helperText "Start-Process"
Assert-Contains "helper prints smoke manual startup command" $helperText "openSmokeManualCommand"
Assert-Contains "helper prints smoke auto startup command" $helperText "openSmokeAutoCommand"
Assert-Contains "helper prints full GT action" $helperText "fullGtAction"
Assert-Contains "helper prints GUI smoke action" $helperText "guiSmokeAction"
Assert-Contains "helper prints track hold action" $helperText "trackHoldAction"
Assert-Contains "helper supports summary writing" $helperText "WriteSummary"
Assert-Contains "helper exposes summary path" $helperText "SummaryPath"
Assert-Contains "helper exposes dashboard path" $helperText "DashboardPath"
Assert-Contains "helper writes dashboard" $helperText "Write-ManualGateDashboard"
Assert-Contains "helper computes dashboard progress" $helperText "Get-ManualGateProgress"
Assert-Contains "helper supports GUI checklist preparation" $helperText "PrepareGuiChecklist"
Assert-Contains "helper uses GUI checklist generator" $helperText "new-yolo-gui-smoke-checklist.ps1"
Assert-Contains "helper supports GUI evidence preparation" $helperText "PrepareGuiEvidence"
Assert-Contains "helper uses GUI evidence prep script" $helperText "prepare-yolo-gui-smoke-evidence.ps1"
Assert-Contains "helper supports pseudo GT visual preparation" $helperText "PreparePseudoGtReviewVisual"
Assert-Contains "helper prepares pseudo GT visual package" $helperText "Prepare-PseudoGtReviewVisual"
Assert-Contains "helper supports pseudo GT visual video path" $helperText "PseudoGtReviewVisualVideoPath"
Assert-Contains "helper computes pending GUI rows" $helperText "Get-PendingGuiSmokeRows"
Assert-Contains "helper prints next GUI step" $helperText "nextGuiStep="
Assert-Contains "helper prints next GUI evidence setter" $helperText "nextGuiEvidenceSetterCommand"

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameCsv)
$guiRows = @(Import-Csv $guiCsv)

$reviewState = Get-ColumnFillState $reviewRows "label"
$frameState = Get-ColumnFillState $frameRows "missedFaceCount"
$guiState = Get-ColumnFillState $guiRows "status"
$defaultPseudoGtExists = Test-Path (Resolve-RepoPath ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv")
$defaultPseudoGtClosureReady = Test-PseudoGtReviewClosureReady `
    -ClosureCsvPath ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv" `
    -ClosureSummaryPath ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure-summary.md"
$allPending = $reviewState -eq "pending" -and $frameState -eq "pending" -and $guiState -eq "pending"
$allCompleted = $reviewState -eq "completed" -and $frameState -eq "completed" -and $guiState -eq "completed"
$expectedRemainingParts = @()
if (-not ($reviewState -eq "completed" -and $frameState -eq "completed")) {
    $expectedRemainingParts += "full-gt-label"
}
if ($guiState -ne "completed") {
    $expectedRemainingParts += "gui-smoke"
}
if (-not $defaultPseudoGtExists) {
    $expectedRemainingParts += "pseudo-gt-evidence"
}
elseif (-not $defaultPseudoGtClosureReady) {
    $expectedRemainingParts += "pseudo-gt-review-closure"
}
if ($expectedRemainingParts.Count -eq 0) {
    $expectedRemainingParts += "none"
}
$expectedRemaining = "remaining=$($expectedRemainingParts -join ',')"
$expectedRemainingSummaryText = $expectedRemainingParts -join ", "

Write-Host "[YoloManualGateHelperVerify] state review=$reviewState, fullFrame=$frameState, gui=$guiState"

$ready = Invoke-Helper @("-VerifyReady")
if ($ready.ExitCode -ne 0) {
    throw "Manual gate helper -VerifyReady failed with exit code $($ready.ExitCode)"
}
Assert-Contains "ready output includes completed readiness command" $ready.Text "completedManualReadinessCommand"
Assert-Contains "ready output includes pseudo GT closure command" $ready.Text "completedPseudoGtReviewClosureCommand"
Assert-Contains "ready output includes pseudo GT status" $ready.Text "pseudoGtStatus="
Assert-Contains "ready output includes pseudo GT action" $ready.Text "pseudoGtAction="
Assert-Contains "ready output includes pseudo GT review queue csv" $ready.Text "pseudoGtReviewQueueCsv="
Assert-Contains "ready output includes pseudo GT review draft dir" $ready.Text "pseudoGtReviewDraftDir="
Assert-Contains "ready output includes pseudo GT review draft report" $ready.Text "pseudoGtReviewDraftReport="
Assert-Contains "ready output includes pseudo GT review draft command" $ready.Text "pseudoGtReviewDraftCommand="
Assert-Contains "ready output includes pseudo GT visual dir" $ready.Text "pseudoGtReviewVisualDir="
Assert-Contains "ready output includes pseudo GT visual command" $ready.Text "pseudoGtReviewVisualCommand="
Assert-Contains "ready output includes pseudo GT decision progress command" $ready.Text "pseudoGtDecisionProgressCommand="
Assert-Contains "ready output includes pseudo GT review draft apply command" $ready.Text "pseudoGtReviewDraftApplyCommand="
Assert-Contains "ready output points to goal evidence publish switch" $ready.Text "PublishPseudoGtToGoalEvidence"
Assert-Contains "ready output includes final yolo state command" $ready.Text "completedYoloStateCommand"
Assert-Contains "ready output includes completion finalizer command" $ready.Text "completionFinalizerCommand"
Assert-Contains "ready output includes pending report command" $ready.Text "pendingReportCommand"
Assert-Contains "ready output includes GUI evidence prep command" $ready.Text "prepareGuiEvidenceCommand"
Assert-Contains "ready output includes human review draft command" $ready.Text "humanReviewDraftCommand"
Assert-Contains "ready output includes completion plan action" $ready.Text "completionPlanAction"
Assert-Contains "ready output includes documented quality failure flag" $ready.Text "AllowQualityGateFailure"
Assert-Contains "ready output includes remaining gates" $ready.Text $expectedRemaining
Assert-Contains "ready output includes open command" $ready.Text "openCommand"
Assert-Contains "ready output includes dashboard open command" $ready.Text "openDashboardCommand"
Assert-Contains "ready output includes app open command" $ready.Text "openAppCommand"
Assert-Contains "ready output includes smoke manual startup command" $ready.Text "openSmokeManualCommand"
Assert-Contains "ready output includes smoke auto startup command" $ready.Text "openSmokeAutoCommand"
Assert-Contains "ready output includes dashboard command" $ready.Text "dashboardCommand"
Assert-Contains "ready output includes progress counts" $ready.Text "progress=fullGtPendingRows="
Assert-Contains "ready output includes next GUI step" $ready.Text "nextGuiStep="
Assert-Contains "ready output includes next GUI evidence setter" $ready.Text "nextGuiEvidenceSetterCommand="
Assert-Contains "ready output includes full GT action" $ready.Text "fullGtAction"
Assert-Contains "ready output includes GUI smoke action" $ready.Text "guiSmokeAction"
Assert-Contains "ready output includes track hold action" $ready.Text "trackHoldAction"
Assert-Contains "ready output passes" $ready.Text "[YoloManualGate] all requested checks passed"

$summary = Invoke-Helper @("-WriteSummary", "-SummaryPath", $SummaryPath)
if ($summary.ExitCode -ne 0) {
    throw "Manual gate helper -WriteSummary failed with exit code $($summary.ExitCode)"
}
$summaryFile = Assert-FileNonEmpty "manual gate summary" $SummaryPath
$summaryText = Get-Content -Raw -Path $summaryFile
Assert-Contains "summary output includes path" $summary.Text "summaryPath="
Assert-Contains "summary output includes dashboard path" $summary.Text "dashboardPath="
foreach ($expectedRemainingPart in $expectedRemainingParts) {
    Assert-Contains "summary records remaining gate $expectedRemainingPart" $summaryText $expectedRemainingPart
}
Assert-Contains "summary records gui smoke gate" $summaryText "gui-smoke"
Assert-Contains "summary records completed full GT command" $summaryText "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "summary records completed GUI command" $summaryText "verify-yolo-gui-smoke-state.ps1"
Assert-Contains "summary records completed readiness command" $summaryText "verify-yolo-manual-readiness-state.ps1"
Assert-Contains "summary records pseudo GT closure command" $summaryText "close-yolo-pseudo-gt-review.ps1"
Assert-Contains "summary records final yolo state command" $summaryText "verify-yolo-state.ps1"
Assert-Contains "summary records pseudo GT status" $summaryText "pseudoGtStatus"
Assert-Contains "summary records pseudo GT action" $summaryText "pseudoGtAction"
Assert-Contains "summary records pseudo GT review queue" $summaryText "pseudoGtReviewQueueCsv"
Assert-Contains "summary records pseudo GT review draft dir" $summaryText "pseudoGtReviewDraftDir"
Assert-Contains "summary records pseudo GT review draft report" $summaryText "pseudo-gt-review-draft-report.md"
Assert-Contains "summary records pseudo GT review draft command" $summaryText "new-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "summary records pseudo GT visual dir" $summaryText "pseudoGtReviewVisualDir"
Assert-Contains "summary records pseudo GT visual report" $summaryText "pseudo-gt-review-visual-report.md"
Assert-Contains "summary records pseudo GT visual command" $summaryText "new-yolo-pseudo-gt-review-visual-package.ps1"
Assert-Contains "summary records pseudo GT decision progress command" $summaryText "write-yolo-pseudo-gt-decision-progress.ps1"
Assert-Contains "summary records pseudo GT decision progress summary" $summaryText "pseudo-gt-decision-progress.md"
Assert-Contains "summary records pseudo GT review draft apply command" $summaryText "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "summary records goal evidence publish switch" $summaryText "PublishPseudoGtToGoalEvidence"
Assert-Contains "summary records completion finalizer command" $summaryText "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "summary records pending report command" $summaryText "write-yolo-manual-pending-report.ps1"
Assert-Contains "summary records GUI evidence prep command" $summaryText "prepare-yolo-gui-smoke-evidence.ps1"
Assert-Contains "summary records GUI evidence guide path" $summaryText "gui-smoke-evidence-guide.md"
Assert-Contains "summary records pending report path" $summaryText "manual-pending-report.md"
Assert-Contains "summary records dashboard path" $summaryText "manual-gate-dashboard.html"
Assert-Contains "summary records AI candidate reference" $summaryText "aiCandidateFullGtReviewCsv"
Assert-Contains "summary records AI candidate rule" $summaryText "reference-only-not-final-gt"
Assert-Contains "summary records pseudo GT closure rule" $summaryText "pseudoGtReviewClosureRule"
if ($expectedRemaining -eq "remaining=full-gt-label,gui-smoke") {
    Assert-Contains "summary records human review draft command" $summaryText "new-yolo-human-review-draft.ps1"
    Assert-Contains "summary records human review draft report" $summaryText "human-review-draft-report.md"
}
Assert-Contains "summary records progress section" $summaryText "## Progress"
Assert-Contains "summary records full GT pending count" $summaryText "fullGtPendingRows="
Assert-Contains "summary records full-frame pending count" $summaryText "fullFramePendingRows="
Assert-Contains "summary records GUI pending count" $summaryText "guiPendingRows="
Assert-Contains "summary records next GUI section" $summaryText "## Next GUI Step"
Assert-Contains "summary records next GUI step" $summaryText "nextGuiStep="
Assert-Contains "summary records next GUI evidence type" $summaryText "nextGuiEvidenceType="
Assert-Contains "summary records next GUI artifact path" $summaryText "nextGuiArtifactPath="
Assert-Contains "summary records next GUI evidence setter" $summaryText "nextGuiEvidenceSetterCommand="
Assert-Contains "summary records finalizer update plan" $summaryText "UpdatePlan"
Assert-Contains "summary records require complete command" $summaryText "RequireComplete"
Assert-Contains "summary records documented quality failure flag" $summaryText "AllowQualityGateFailure"
Assert-Contains "summary records completion plan action" $summaryText "completionPlanAction"
Assert-Contains "summary records pseudo GT visual prep command" $summaryText "PreparePseudoGtReviewVisual"
Assert-Contains "summary records pseudo GT visual video recovery command" $summaryText "PseudoGtReviewVisualVideoPath"
Assert-Contains "summary records short problem clip placeholder" $summaryText "<short problem clip path>"
Assert-Contains "summary records open command" $summaryText "open-yolo-manual-gates.ps1 -Open"
Assert-Contains "summary records dashboard open command" $summaryText "open-yolo-manual-gates.ps1 -WriteSummary -OpenDashboard"
Assert-Contains "summary records app open command" $summaryText "open-yolo-manual-gates.ps1 -OpenApp"
Assert-Contains "summary records smoke manual startup command" $summaryText "--yolo-smoke --open-manual"
Assert-Contains "summary records smoke auto startup command" $summaryText "--yolo-smoke --open-auto"
Assert-Contains "summary records dashboard command" $summaryText "open-yolo-manual-gates.ps1 -WriteSummary"
Assert-Contains "summary records track hold action" $summaryText "trackHoldAction"
Assert-Contains "summary records preview track hold step" $summaryText "preview-track-hold"

$dashboardPath = Resolve-RepoPath ".tmp\yolo-manual-gates\manual-gate-dashboard.html"
Assert-FileNonEmpty "manual gate dashboard" $dashboardPath | Out-Null
$dashboardText = Get-Content -Raw -Path $dashboardPath
Assert-Contains "dashboard records remaining gates" $dashboardText $expectedRemainingSummaryText
Assert-Contains "dashboard links review index" $dashboardText "Review index"
Assert-Contains "dashboard links pending report" $dashboardText "Manual pending report"
Assert-Contains "dashboard links GUI checklist" $dashboardText "GUI smoke checklist CSV"
Assert-Contains "dashboard records progress section" $dashboardText "Progress"
Assert-Contains "dashboard records pending preview" $dashboardText "Pending Preview"
Assert-Contains "dashboard records pending crop rows" $dashboardText "Full-GT crop rows"
Assert-Contains "dashboard records pending full-frame rows" $dashboardText "Full-frame rows"
Assert-Contains "dashboard records pending GUI rows" $dashboardText "GUI smoke rows"
Assert-Contains "dashboard records AI candidate reference" $dashboardText "AI-Assisted Candidate Reference"
Assert-Contains "dashboard records AI candidate rule" $dashboardText "reference-only-not-final-gt"
if ($expectedRemaining -eq "remaining=full-gt-label,gui-smoke") {
    Assert-Contains "dashboard links human review draft report" $dashboardText "Human review draft report"
}
Assert-Contains "dashboard records full GT progress" $dashboardText "Full-GT crop review"
Assert-Contains "dashboard records full-frame progress" $dashboardText "Full-frame review"
Assert-Contains "dashboard records GUI progress" $dashboardText "GUI smoke checklist"
Assert-Contains "dashboard records pending wording" $dashboardText "pending of"
Assert-Contains "dashboard records preview track hold" $dashboardText "preview-track-hold"
Assert-Contains "dashboard records completion finalizer" $dashboardText "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "dashboard records pseudo GT closure command" $dashboardText "close-yolo-pseudo-gt-review.ps1"
Assert-Contains "dashboard records pseudo GT evidence section" $dashboardText "Pseudo-GT Completion Evidence"
Assert-Contains "dashboard records pseudo GT review draft command" $dashboardText "Prepare review draft"
Assert-Contains "dashboard records pseudo GT review draft writer" $dashboardText "new-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "dashboard records pseudo GT visual package command" $dashboardText "Create visual crop/overlay evidence"
Assert-Contains "dashboard records pseudo GT visual package writer" $dashboardText "new-yolo-pseudo-gt-review-visual-package.ps1"
Assert-Contains "dashboard records pseudo GT decision progress command" $dashboardText "Check decision sheet progress"
Assert-Contains "dashboard records pseudo GT decision progress writer" $dashboardText "write-yolo-pseudo-gt-decision-progress.ps1"
Assert-Contains "dashboard records pseudo GT review draft apply command" $dashboardText "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "dashboard records goal evidence publish switch" $dashboardText "PublishPseudoGtToGoalEvidence"

$preparedGuiCsv = ".tmp\yolo-manual-gate-helper\prepared-fixture\manual-smoke-checklist.csv"
$preparedGuiPath = Resolve-RepoPath $preparedGuiCsv
if (Test-Path $preparedGuiPath) {
    Remove-Item -Force -Path $preparedGuiPath
}

$prepared = Invoke-Helper @(
    "-GuiChecklistCsv", $preparedGuiCsv,
    "-PrepareGuiChecklist",
    "-VerifyReady",
    "-WriteSummary",
    "-SummaryPath", ".tmp\yolo-manual-gate-helper\prepared-fixture\manual-gate-summary.md",
    "-PendingReportPath", ".tmp\yolo-manual-gate-helper\prepared-fixture\manual-pending-report.md"
)
if ($prepared.ExitCode -ne 0) {
    throw "Manual gate helper -PrepareGuiChecklist failed with exit code $($prepared.ExitCode)"
}
$preparedFile = Assert-FileNonEmpty "prepared GUI checklist" $preparedGuiCsv
$preparedRows = @(Import-Csv $preparedFile)
if ($preparedRows.Count -ne 9) {
    throw "Prepared GUI checklist should have 9 rows, actual=$($preparedRows.Count)"
}
Assert-Contains "prepared output records GUI checklist creation" $prepared.Text "preparedGuiChecklist="
Assert-Contains "prepared output records anti-flicker step" ($preparedRows.stepId -join ",") "preview-track-hold"
Assert-Contains "prepared output verifies custom GUI checklist path" $prepared.Text "pass manual-readiness-state"

$preparedEvidenceCsv = ".tmp\yolo-manual-gate-helper\evidence-fixture\manual-smoke-checklist.csv"
$preparedEvidencePath = Resolve-RepoPath $preparedEvidenceCsv
$preparedEvidenceDir = Resolve-RepoPath ".tmp\yolo-manual-gate-helper\evidence-fixture\evidence"
$preparedEvidenceGuide = Resolve-RepoPath ".tmp\yolo-manual-gate-helper\evidence-fixture\gui-smoke-evidence-guide.md"
if (Test-Path (Split-Path -Parent $preparedEvidencePath)) {
    Remove-Item -Recurse -Force -Path (Split-Path -Parent $preparedEvidencePath)
}

$preparedEvidence = Invoke-Helper @(
    "-GuiChecklistCsv", $preparedEvidenceCsv,
    "-PrepareGuiChecklist",
    "-PrepareGuiEvidence",
    "-GuiEvidenceDir", $preparedEvidenceDir,
    "-GuiEvidenceGuidePath", $preparedEvidenceGuide,
    "-VerifyReady",
    "-WriteSummary",
    "-SummaryPath", ".tmp\yolo-manual-gate-helper\evidence-fixture\manual-gate-summary.md",
    "-PendingReportPath", ".tmp\yolo-manual-gate-helper\evidence-fixture\manual-pending-report.md"
)
if ($preparedEvidence.ExitCode -ne 0) {
    throw "Manual gate helper -PrepareGuiEvidence failed with exit code $($preparedEvidence.ExitCode)"
}
$preparedEvidenceRows = @(Import-Csv (Assert-FileNonEmpty "prepared GUI evidence checklist" $preparedEvidenceCsv))
$trackHoldRow = $preparedEvidenceRows | Where-Object { $_.stepId -eq "preview-track-hold" } | Select-Object -First 1
if ($null -eq $trackHoldRow -or $trackHoldRow.artifactPath -notlike "*preview-track-hold.mp4") {
    throw "Prepared GUI evidence checklist missing preview-track-hold artifact path"
}
if ($preparedEvidenceRows | Where-Object { $_.status -eq "pass" -or -not [string]::IsNullOrWhiteSpace($_.evidence) }) {
    throw "Prepared GUI evidence unexpectedly filled final GUI smoke fields"
}
Assert-FileNonEmpty "prepared GUI evidence guide" $preparedEvidenceGuide | Out-Null
Assert-Contains "prepared output runs GUI evidence prep" $preparedEvidence.Text "pass gui-smoke-evidence-prep"
Assert-Contains "prepared output verifies evidence custom GUI checklist" $preparedEvidence.Text "pass manual-readiness-state"

$preparedPseudoGtVisual = Invoke-Helper @(
    "-PreparePseudoGtReviewVisual",
    "-WriteSummary",
    "-SummaryPath", ".tmp\yolo-manual-gate-helper\pseudo-gt-visual-fixture\manual-gate-summary.md",
    "-PendingReportPath", ".tmp\yolo-manual-gate-helper\pseudo-gt-visual-fixture\manual-pending-report.md"
)
if ($preparedPseudoGtVisual.ExitCode -ne 0) {
    throw "Manual gate helper -PreparePseudoGtReviewVisual failed with exit code $($preparedPseudoGtVisual.ExitCode)"
}
$visualDraftCsv = Assert-FileNonEmpty "prepared pseudo GT visual draft" ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-full-gt-review-visual-draft.csv"
$visualIndex = Assert-FileNonEmpty "prepared pseudo GT visual index" ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-review-visual-index.html"
$visualReport = Assert-FileNonEmpty "prepared pseudo GT visual report" ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-review-visual-report.md"
$visualRows = @(Import-Csv $visualDraftCsv)
if ($visualRows.Count -eq 0) {
    throw "Prepared pseudo GT visual draft has no rows: $visualDraftCsv"
}
if ($visualRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.label) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
}) {
    throw "Prepared pseudo GT visual draft unexpectedly filled final review fields"
}
Assert-Contains "prepared pseudo GT visual output runs draft writer" $preparedPseudoGtVisual.Text "pass pseudo-gt-review-draft"
Assert-Contains "prepared pseudo GT visual output runs visual package" $preparedPseudoGtVisual.Text "pass pseudo-gt-review-visual-package"
Assert-Contains "prepared pseudo GT visual output prints visual index" $preparedPseudoGtVisual.Text "pseudoGtReviewVisualIndex="
Assert-Contains "prepared pseudo GT visual index records test-only boundary" (Get-Content -Raw -Path $visualIndex) "test-only visual review evidence"
Assert-Contains "prepared pseudo GT visual report records final fields blank" (Get-Content -Raw -Path $visualReport) "finalFilledRows: 0"

$completed = Invoke-Helper @("-VerifyCompleted")
if ($allCompleted) {
    if ($defaultPseudoGtExists) {
        if ($defaultPseudoGtClosureReady) {
            if ($completed.ExitCode -ne 0) {
                throw "Manual gate helper -VerifyCompleted failed on completed manual files with exit code $($completed.ExitCode)"
            }

            Assert-Contains "completed output runs manual readiness" $completed.Text "manual-readiness-completed-state"
            Assert-Contains "completed output runs full GT reviewed verifier" $completed.Text "full-gt-reviewed-state"
            Assert-Contains "completed output runs GUI smoke verifier" $completed.Text "gui-smoke-state"
            Assert-Contains "completed output passes" $completed.Text "[YoloManualGate] all requested checks passed"
        }
        else {
            if ($completed.ExitCode -eq 0) {
                throw "Manual gate helper -VerifyCompleted unexpectedly passed with incomplete pseudo-GT closure"
            }

            Assert-Contains "completed output runs pseudo GT closure" $completed.Text "pseudo-gt-review-closure"
            Assert-Contains "completed output blocks incomplete pseudo GT closure" $completed.Text "Pseudo-GT review closure is incomplete"
        }
    }
    else {
        if ($completed.ExitCode -eq 0) {
            throw "Manual gate helper -VerifyCompleted unexpectedly passed without default pseudo-GT candidates"
        }

        Assert-Contains "completed output requires pseudo GT candidates" $completed.Text "Pseudo-GT candidate CSV is required before completed manual gates"
        Assert-Contains "completed output points to goal evidence publish switch" $completed.Text "PublishPseudoGtToGoalEvidence"
    }
}
elseif ($allPending) {
    if ($completed.ExitCode -eq 0) {
        throw "Manual gate helper -VerifyCompleted unexpectedly passed on pending manual files"
    }

    Assert-Contains "pending completed check runs manual readiness" $completed.Text "manual-readiness-completed-state"
    Assert-Contains "pending completed check runs full GT reviewed verifier" $completed.Text "full-gt-reviewed-state"
    Assert-Contains "pending completed check fails on unreviewed full GT rows" $completed.Text "Review CSV has unreviewed rows"
}
else {
    if ($completed.ExitCode -eq 0) {
        throw "Manual gate helper -VerifyCompleted unexpectedly passed on partial manual files"
    }

    Assert-Contains "partial completed check runs manual readiness" $completed.Text "manual-readiness-completed-state"
}

$completedGuiCsv = New-CompletedGuiChecklistFixture -OutputCsv $CompletedFixtureGuiChecklistCsv
$completedManualReviewCsv = New-ReviewedCsvFixture -InputCsv $completedReviewCsv -OutputCsv $CompletedFixtureManualReviewCsv
$completedManualFrameCsv = New-ReviewedCsvFixture -InputCsv $completedFrameCsv -OutputCsv $CompletedFixtureManualFullFrameReviewCsv
$completedPseudoGtCsv = New-CompletedPseudoGtFixture -ReviewCsv $completedManualReviewCsv -OutputCsv $CompletedFixturePseudoGtCsv
$completedFixture = Invoke-Helper @(
    "-FullGtReviewCsv", $completedManualReviewCsv,
    "-FullFrameReviewCsv", $completedManualFrameCsv,
    "-GuiChecklistCsv", $completedGuiCsv,
    "-PseudoGtCsv", $completedPseudoGtCsv,
    "-PseudoGtReviewClosureCsv", $CompletedFixturePseudoGtReviewClosureCsv,
    "-PseudoGtReviewClosureSummary", $CompletedFixturePseudoGtReviewClosureSummary,
    "-MaxMisses", "1",
    "-MaxFalsePositives", "13",
    "-MaxLowIou", "1",
    "-VerifyCompleted"
)
if ($completedFixture.ExitCode -ne 0) {
    throw "Manual gate helper completed fixture failed with exit code $($completedFixture.ExitCode)"
}
Assert-Contains "completed fixture runs manual readiness" $completedFixture.Text "manual-readiness-completed-state"
Assert-Contains "completed fixture passes manual readiness" $completedFixture.Text "pass manual-readiness-completed-state"
Assert-Contains "completed fixture runs full GT reviewed verifier" $completedFixture.Text "full-gt-reviewed-state"
Assert-Contains "completed fixture runs GUI smoke verifier" $completedFixture.Text "gui-smoke-state"
Assert-Contains "completed fixture records pseudo GT closure path" $completedFixture.Text "pseudoGtCsv="
Assert-Contains "completed fixture reports ready pseudo GT" $completedFixture.Text "pseudoGtStatus=ready"
Assert-Contains "completed fixture runs pseudo GT closure" $completedFixture.Text "pseudo-gt-review-closure"
Assert-Contains "completed fixture passes helper" $completedFixture.Text "[YoloManualGate] all requested checks passed"

Write-Host "[YoloManualGateHelperVerify] all requested checks passed"
