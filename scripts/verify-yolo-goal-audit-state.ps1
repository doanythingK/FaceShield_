param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$AutoMosaicDefaultVerify = "scripts/verify-auto-mosaic-default.ps1",
    [string]$YoloStateVerify = "scripts/verify-yolo-state.ps1",
    [string]$YoloProfileStateVerify = "scripts/verify-yolo-profile-state.ps1",
    [string]$YoloStartupSmokeStateVerify = "scripts/verify-yolo-startup-smoke-state.ps1",
    [string]$YoloTrackHoldVerify = "scripts/verify-yolo-track-hold-state.ps1",
    [string]$YoloCompletionAuditVerify = "scripts/verify-yolo-completion-audit-state.ps1",
    [string]$YoloTopLevelRequireCompleteVerify = "scripts/verify-yolo-top-level-require-complete-state.ps1",
    [string]$YoloCompletionFinalizerStateVerify = "scripts/verify-yolo-completion-finalizer-state.ps1",
    [string]$YoloFullGtReviewPackageVerify = "scripts/verify-yolo-full-gt-review-package-state.ps1",
    [string]$YoloManualReadinessVerify = "scripts/verify-yolo-manual-readiness-state.ps1",
    [string]$YoloManualGateHelper = "scripts/open-yolo-manual-gates.ps1",
    [string]$YoloManualGateHelperVerify = "scripts/verify-yolo-manual-gate-helper-state.ps1",
    [string]$YoloReadyForHumanGatesVerify = "scripts/verify-yolo-ready-for-human-gates-state.ps1",
    [string]$YoloGuiSmokeEvidenceSetter = "scripts/set-yolo-gui-smoke-evidence.ps1",
    [string]$AppStartupOptions = "Models/AppStartupOptions.cs",
    [string]$MainWindowViewModel = "ViewModels/MainWindowViewModel.cs",
    [string]$AppCodeBehind = "App.axaml.cs",
    [string]$YoloGoalEvidenceReportWriter = "scripts/write-yolo-goal-evidence-report.ps1",
    [string]$YoloManualPendingReportWriter = "scripts/write-yolo-manual-pending-report.ps1",
    [string]$YoloHumanReviewDraftWriter = "scripts/new-yolo-human-review-draft.ps1",
    [string]$YoloPseudoGtReviewDraftWriter = "scripts/new-yolo-pseudo-gt-review-draft.ps1",
    [string]$YoloPseudoGtReviewDraftVerify = "scripts/verify-yolo-pseudo-gt-review-draft-state.ps1",
    [string]$YoloPseudoGtReviewVisualPackage = "scripts/new-yolo-pseudo-gt-review-visual-package.ps1",
    [string]$YoloPseudoGtReviewVisualPackageVerify = "scripts/verify-yolo-pseudo-gt-review-visual-package-state.ps1",
    [string]$YoloPseudoGtDecisionProgressWriter = "scripts/write-yolo-pseudo-gt-decision-progress.ps1",
    [string]$YoloPseudoGtReviewDraftApply = "scripts/apply-yolo-pseudo-gt-review-draft.ps1",
    [string]$YoloPseudoGtReviewDraftApplyVerify = "scripts/verify-yolo-pseudo-gt-review-draft-apply-state.ps1",
    [string]$YoloCompletionFinalizer = "scripts/complete-yolo-goal-after-manual-gates.ps1",
    [string]$YoloPseudoGtReviewClosure = "scripts/close-yolo-pseudo-gt-review.ps1",
    [string]$YoloPseudoGtSeparationVerify = "scripts/verify-yolo-pseudo-gt-separation-state.ps1"
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

function Read-RepoFile {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "File not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
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

    Write-Host "[YoloGoalAuditVerify] pass $Name"
}

function Assert-Match {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloGoalAuditVerify] pass $Name"
}

$plan = Read-RepoFile $PlanPath
$autoVerify = Read-RepoFile $AutoMosaicDefaultVerify
$yoloState = Read-RepoFile $YoloStateVerify
$yoloProfileState = Read-RepoFile $YoloProfileStateVerify
$yoloStartupSmokeState = Read-RepoFile $YoloStartupSmokeStateVerify
$yoloTrackHoldVerify = Read-RepoFile $YoloTrackHoldVerify
$completionAuditVerify = Read-RepoFile $YoloCompletionAuditVerify
$topLevelRequireCompleteVerify = Read-RepoFile $YoloTopLevelRequireCompleteVerify
$completionFinalizerStateVerify = Read-RepoFile $YoloCompletionFinalizerStateVerify
$fullGtReviewPackageVerify = Read-RepoFile $YoloFullGtReviewPackageVerify
$manualReadinessVerify = Read-RepoFile $YoloManualReadinessVerify
$manualGateHelper = Read-RepoFile $YoloManualGateHelper
$manualGateHelperVerify = Read-RepoFile $YoloManualGateHelperVerify
$readyForHumanGatesVerify = Read-RepoFile $YoloReadyForHumanGatesVerify
$guiSmokeEvidenceSetter = Read-RepoFile $YoloGuiSmokeEvidenceSetter
$appStartupOptions = Read-RepoFile $AppStartupOptions
$mainWindowViewModel = Read-RepoFile $MainWindowViewModel
$appCodeBehind = Read-RepoFile $AppCodeBehind
$goalEvidenceReportWriter = Read-RepoFile $YoloGoalEvidenceReportWriter
$manualPendingReportWriter = Read-RepoFile $YoloManualPendingReportWriter
$humanReviewDraftWriter = Read-RepoFile $YoloHumanReviewDraftWriter
$pseudoGtReviewDraftWriter = Read-RepoFile $YoloPseudoGtReviewDraftWriter
$pseudoGtReviewDraftVerify = Read-RepoFile $YoloPseudoGtReviewDraftVerify
$pseudoGtReviewVisualPackage = Read-RepoFile $YoloPseudoGtReviewVisualPackage
$pseudoGtReviewVisualPackageVerify = Read-RepoFile $YoloPseudoGtReviewVisualPackageVerify
$pseudoGtDecisionProgressWriter = Read-RepoFile $YoloPseudoGtDecisionProgressWriter
$pseudoGtReviewDraftApply = Read-RepoFile $YoloPseudoGtReviewDraftApply
$pseudoGtReviewDraftApplyVerify = Read-RepoFile $YoloPseudoGtReviewDraftApplyVerify
$completionFinalizer = Read-RepoFile $YoloCompletionFinalizer
$pseudoGtReviewClosure = Read-RepoFile $YoloPseudoGtReviewClosure
$pseudoGtSeparationVerify = Read-RepoFile $YoloPseudoGtSeparationVerify

$goalAuditMarkerMatch = [regex]::Match($plan, "yolo-goal-audit-state:[^<]+")
if (-not $goalAuditMarkerMatch.Success) {
    throw "goal audit marker missing"
}

$goalAuditMarker = $goalAuditMarkerMatch.Value.Trim()
Write-Host "[YoloGoalAuditVerify] pass goal audit marker"

$isCompleteMarker = $goalAuditMarker.Contains("complete=true")
if ($isCompleteMarker) {
    Assert-Contains "goal audit complete marker" $goalAuditMarker "remaining=none"
    Assert-Contains "goal audit complete audit marker" $goalAuditMarker "completion-audit=pass-complete"
}
else {
    Assert-Contains "goal audit incomplete marker" $goalAuditMarker "complete=false"
    Assert-Contains "goal audit incomplete remaining marker" $goalAuditMarker "remaining=gui-smoke"
    Assert-Contains "goal audit incomplete audit marker" $goalAuditMarker "completion-audit=pass-incomplete"
}

foreach ($token in @(
    "backend=integrated",
    "default=FaceONNX",
    "recommendation=none",
    "representative=pass",
    "anti-flicker-tracking=pass",
    "track-hold-state=pass",
    "extended=fail",
    "extended-export=fail",
    "sample-gt=pass",
    "full-gt-harness=pass",
    "full-gt-reviewed=pass",
    "full-gt-quality-failure-allowed=pass",
    "pseudo-gt-test-only=pass",
    "pseudo-gt-review-closure=conditional-gated",
    "pseudo-gt-decision-progress=pass",
    "pseudo-gt-separation=pass",
    "license-source=pass",
    "manual-readiness=pass",
    "ten-minute-full=not-required-after-extended-fail",
    "short-span-only-goal=pass",
    "obsolete-sweep-harness=removed",
    "obsolete-ten-minute-runner=removed",
    "top-level-require-complete=fast-fail-guarded",
    "top-level-ready-rerun=pass",
    "evidence-report=pass",
    "evidence-report-dynamic=pass",
    "evidence-report-candidate-comparison=pass",
    "completion-audit-marker-only-selftest=pass",
    "completion-audit-pending-negative-selftest=pass",
    "top-level-require-complete-negative=pass",
    "completion-finalizer-state=pass",
    "empty-yolo-model-args=guarded")) {
    Assert-Contains "goal audit token $token" $plan $token
}

Assert-Contains "profile verifier recorded" $plan "verify-yolo-profile-state.ps1"
Assert-Contains "profile marker recorded" $plan "yolo-profile-state:"
Assert-Contains "top small yolo filter marker recorded" $plan "yolo-top-small-filter-state:"
Assert-Contains "top small yolo filter app-only recorded" $plan "app-yolo-only=pass"
Assert-Contains "top small yolo filter threshold recorded" $plan "maxCenterY=0.07; maxArea=0.0045; maxConfidence=0.55"
Assert-Contains "profile settings version recorded" $plan "settings-version=6"
Assert-Contains "profile auto pipeline recorded" $plan "auto-pipeline-profile=downscale,quality,tracking,detectEvery,parallel"
Assert-Contains "obsolete sweep harness cleanup recorded" $plan "obsolete-sweep-harness=removed"
Assert-Contains "smoke harness faceonnx default recorded" $plan "smoke-harness-faceonnx-default=pass"
Assert-Contains "representative verifier recorded" $plan "verify-yolo-representative-gate.ps1"
Assert-Contains "representative pass recorded" $plan "SmokeQualityGate passed=True"
Assert-Contains "representative baseline frames recorded" $plan "baselineFrames=19"
Assert-Contains "representative optimized frames recorded" $plan "optimizedFrames=20"
Assert-Contains "representative only optimized tracking frame recorded" $plan "onlyOptimized=1(frame 9)"
Assert-Contains "representative tracking lost fill recorded" $plan "lostFilled=6"
Assert-Contains "representative tracking lost frames recorded" $plan "lostFrames=6,7,8,9,10,11"
Assert-Contains "anti flicker goal recorded" $plan "한번 모자이크 된 대상 트래킹 유지"
Assert-Contains "anti flicker yolo lost fill cap recorded" $plan "MaxLostFillFrames=6"
Assert-Contains "anti flicker yolo confirmed hold cap recorded" $plan "MaxConfirmedTrackHoldFrames=5"
Assert-Contains "anti flicker yolo small lost-fill policy recorded" $plan "AllowSmallTrackLostFill=true"
Assert-Contains "anti flicker gui step recorded" $plan "preview-track-hold"
Assert-Contains "track hold state verifier recorded" $plan "verify-yolo-track-hold-state.ps1"
Assert-Contains "track hold state marker recorded" $plan "yolo-track-hold-state:"
Assert-Contains "track hold internal gap frames recorded" $plan "appGapFrames=13,14,15,16,17"
Assert-Contains "track hold held frames recorded" $plan "heldFrames=21,22,23,24,25,26"
Assert-Contains "track hold weak candidate recorded" $plan "weak-single-frame-candidate=removed"
Assert-Contains "track hold preview refresh recorded" $plan "preview-refresh-after-postprocess=pass"
Assert-Contains "track hold toggle gate recorded" $plan "tracking-toggle-gated=pass"
Assert-Contains "track hold smoothing toggle gate recorded" $plan "temporal-smoothing-toggle-gated=pass"
Assert-Contains "representative iou recorded" $plan "avgBestIou=0.971"
Assert-Contains "extended verifier recorded" $plan "verify-yolo-extended-gate.ps1"
Assert-Contains "extended export verifier recorded" $plan "verify-yolo-extended-export-gate.ps1"
Assert-Contains "obsolete ten minute runner cleanup recorded" $plan "obsolete-ten-minute-runner=removed"
Assert-Contains "gt label sample marker recorded" $plan "yolo-gt-label-sample-state:"
Assert-Contains "gt label verifier recorded" $plan "verify-yolo-gt-label-state.ps1"
Assert-Contains "gt label pass sample rows recorded" $plan "passRows=15"
Assert-Contains "gt label pass yolo tp recorded" $plan "passYoloTP=15"
Assert-Contains "gt label pass yolo fp recorded" $plan "passYoloFP=0"
Assert-Contains "gt label fail sample rows recorded" $plan "failRows=26"
Assert-Contains "gt label fail unclear recorded" $plan "failUnclear=1"
Assert-Contains "gt label fail yolo fp recorded" $plan "failYoloFP=10"
Assert-Contains "gt label fail faceonnx fp recorded" $plan "failFaceOnnxFP=14"
Assert-Contains "gt label scope recorded" $plan "sample-crops-not-full-video-gt"
Assert-Contains "full gt harness marker recorded" $plan "yolo-full-gt-label-harness-state:"
Assert-Contains "full gt harness verifier recorded" $plan "verify-yolo-full-gt-label-state.ps1"
Assert-Contains "full gt template recorded" $plan "new-yolo-full-gt-template.ps1"
Assert-Contains "full gt template verifier recorded" $plan "verify-yolo-full-gt-template-state.ps1"
Assert-Contains "full gt review package recorded" $plan "new-yolo-full-gt-review-package.ps1"
Assert-Contains "full gt review package verifier recorded" $plan "verify-yolo-full-gt-review-package-state.ps1"
Assert-Contains "full gt reviewed verifier recorded" $plan "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "full gt candidate verifier recorded" $plan "verify-yolo-full-gt-reviewed-candidate-state.ps1"
Assert-Contains "full gt manual gate helper recorded" $plan "open-yolo-manual-gates.ps1"
Assert-Contains "full gt manual gate helper verifier recorded" $plan "verify-yolo-manual-gate-helper-state.ps1"
Assert-Contains "full gt manual gate helper completed mode recorded" $plan "manual-gate-helper-completed-mode=pass"
Assert-Contains "full gt manual gate helper completed fixture recorded" $plan "manual-gate-helper-completed-fixture=pass-not-final-gt"
Assert-Contains "full gt harness selftest recorded" $plan "mode=selftest-pass-and-synthetic-data-pass"
Assert-Contains "full gt data missing recorded" $plan "gt-data=missing"
Assert-Contains "full gt harness metrics recorded" $plan "metrics=tp,miss,false-positive,low-iou"
Assert-Contains "full gt runner dump recorded" $plan "runner-dump=detections-and-compare-artifacts"
Assert-Contains "full gt real log template recorded" $plan "real-log-template=pass-20-rows"
Assert-Contains "full gt review package smoke recorded" $plan "review-package-smoke=pass-20-crops"
Assert-Contains "full gt review package no-clobber recorded" $plan "review-package-no-clobber=pass"
Assert-Contains "full gt review package force regenerate recorded" $plan "review-package-force-regenerate=explicit"
Assert-Contains "full gt review package refresh index recorded" $plan "review-index-refresh=pass"
Assert-Contains "full gt full-frame review smoke recorded" $plan "full-frame-review-smoke=pass-19-candidate-frames"
Assert-Contains "full gt full-frame overlay recorded" $plan "full-frame-overlay=pass"
Assert-Contains "full gt full-frame candidate summary recorded" $plan "full-frame-candidate-summary=pass"
Assert-Contains "full gt review index recorded" $plan "review-index=pass"
Assert-Contains "full gt review index input rules recorded" $plan "review-index-input-rules=pass"
Assert-Contains "full gt review index csv key recorded" $plan "review-index-csv-key=pass"
Assert-Contains "full gt review index pending fields recorded" $plan "review-index-pending-fields=pass"
Assert-Contains "full gt reviewed gate recorded" $plan "reviewed-gate=selftest-pass"
Assert-Contains "full gt manual missed consistency recorded" $plan "manual-missed-consistency=pass"
Assert-Contains "full gt review artifact validation recorded" $plan "review-artifact-validation=pass"
Assert-Contains "full gt negative selftests recorded" $plan "negative-selftests=pass"
Assert-Contains "full gt ai reviewed candidate recorded" $plan "ai-reviewed-candidate=tp7-fp13-miss1-strict-fail"
Assert-Contains "full gt quality gate failure allowed recorded" $plan "quality-gate-failure-allowed=pass"
Assert-Contains "full gt real reviewed gate recorded" $plan "real-reviewed-gate=requires-full-frame-review"
Assert-Contains "gui smoke harness marker recorded" $plan "yolo-gui-smoke-harness-state:"
Assert-Contains "gui smoke verifier recorded" $plan "verify-yolo-gui-smoke-state.ps1"
Assert-Contains "gui smoke checklist recorded" $plan "new-yolo-gui-smoke-checklist.ps1"
Assert-Contains "gui smoke evidence prep recorded" $plan "prepare-yolo-gui-smoke-evidence.ps1"
Assert-Contains "gui smoke evidence guide recorded" $plan "evidence-guide=.tmp/yolo-gui-smoke/gui-smoke-evidence-guide.md"
Assert-Contains "gui smoke detector-row download button recorded" $plan 'detector 선택 줄의 `YOLO 다운로드` 버튼'
Assert-Contains "gui smoke download button source invariant recorded" $plan "download-button-source-invariant=pass"
Assert-Contains "gui smoke source invariant recorded" $plan "source-invariant=pass"
Assert-Contains "gui smoke yolo numeric width invariant recorded" $plan "yolo-numeric-width-source-invariant=pass"
Assert-Contains "gui smoke preview playback queue invariant recorded" $plan "preview-playback-queue-source-invariant=pass"
Assert-Contains "gui smoke preview cancellation suppression recorded" $plan "preview-cancel-exception-suppressed=pass"
Assert-Contains "gui smoke lazy thumbnail open invariant recorded" $plan "lazy-thumbnail-open-source-invariant=pass"
Assert-Contains "gui smoke open video evidence recorded" $plan "open-video-evidence=pass-local-screenshot"
Assert-Contains "gui smoke detector selection evidence recorded" $plan "select-yolo-backend-evidence=pass-local-screenshot"
Assert-Contains "gui smoke existing model evidence recorded" $plan "download-yolo-model-evidence=pass-existing-model-path"
Assert-Contains "gui smoke run auto detect evidence recorded" $plan "run-yolo-auto-detect-evidence=pass-debug-output"
Assert-Contains "gui smoke manual evidence schema recorded" $plan "manual-evidence-schema=pass"
Assert-Contains "gui smoke manual evidence type validation recorded" $plan "manual-evidence-type-validation=pass"
Assert-Contains "gui smoke anti flicker tracking recorded" $plan "anti-flicker-tracking=pass"
Assert-Contains "gui smoke startup smoke command recorded" $plan "startup-smoke-command=pass"
Assert-Contains "gui smoke startup smoke state recorded" $plan "startup-smoke-state=pass"
Assert-Contains "gui smoke startup smoke verifier recorded" $plan "verify-yolo-startup-smoke-state.ps1"
Assert-Contains "gui smoke manual verifier selftest recorded" $plan "manual-verifier-selftest=pass"
Assert-Contains "gui smoke manual negative selftests recorded" $plan "manual-negative-selftests=pass"
Assert-Contains "gui smoke checklist no-clobber recorded" $plan "gui-checklist-no-clobber=pass"
Assert-Match "gui smoke manual checklist recorded" $plan "manual-checklist=(partial-human-smoke|complete-human-smoke)"
Assert-Contains "gui smoke open video step recorded" $plan "open-video"
Assert-Contains "gui smoke download model step recorded" $plan "download-yolo-model"
Assert-Contains "gui smoke preview track hold step recorded" $plan "preview-track-hold"
Assert-Contains "gui smoke export step recorded" $plan "export"
Assert-Contains "gui smoke evidence setter recorded" $plan "set-yolo-gui-smoke-evidence.ps1"
Assert-Contains "gui smoke evidence setter validates artifacts" $guiSmokeEvidenceSetter "Artifact does not exist"
Assert-Contains "gui smoke evidence setter rejects wrong artifact type" $guiSmokeEvidenceSetter "extension does not match evidenceType"
Assert-Contains "gui smoke verifier checks evidence setter" (Read-RepoFile "scripts/verify-yolo-gui-smoke-state.ps1") "checklist evidence setter selftest"
Assert-Contains "startup options parse yolo smoke preset" $appStartupOptions "--yolo-smoke"
Assert-Contains "startup options default srcTest video" $appStartupOptions "srcTest/260102_jp_10.mp4"
Assert-Contains "startup options default yolo model" $appStartupOptions ".tmp/models/YoloV5Face.onnx"
Assert-Contains "startup options parse auto open" $appStartupOptions "--open-auto"
Assert-Contains "startup options parse no auto export" $appStartupOptions "--no-auto-export"
Assert-Contains "startup options parse frame selection" $appStartupOptions "--frame"
Assert-Contains "app forwards startup args" $appCodeBehind "new MainWindowViewModel(desktop.Args)"
Assert-Contains "main window runs startup open" $mainWindowViewModel "OpenStartupWorkspaceAsync"
Assert-Contains "main window applies startup frame" $mainWindowViewModel "_startupFrameIndex"
Assert-Contains "startup smoke verifier creates runtime harness" $yoloStartupSmokeState "YoloStartupSmokeHarness.csproj"
Assert-Contains "startup smoke verifier checks VM can start" $yoloStartupSmokeState "CanStartWorkspace"
Assert-Contains "manual readiness marker recorded" $plan "yolo-manual-readiness-state:"
Assert-Contains "manual readiness verifier recorded" $plan "verify-yolo-manual-readiness-state.ps1"
Assert-Contains "manual readiness finalizer recorded" $plan "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "manual readiness finalizer selftest recorded" $plan "finalizer-selftest=pass"
Assert-Contains "manual readiness full gt ready recorded" $plan "full-gt-review-package=ready-pending-human-labels"
Assert-Contains "manual readiness ai candidate ready recorded" $plan "ai-reviewed-candidate=ready-not-final-gt"
Assert-Contains "manual readiness human review draft recorded" $plan "human-review-draft=pass"
Assert-Contains "manual readiness gui checklist ready recorded" $plan "gui-checklist=ready-pending-human-smoke"
Assert-Contains "manual readiness gui checklist preparation recorded" $plan "prepare-gui-checklist=pass"
Assert-Contains "manual readiness gui evidence prep recorded" $plan "evidence-prep=scripts/prepare-yolo-gui-smoke-evidence.ps1"
Assert-Contains "manual readiness next actions recorded" $plan "manual-gate-next-actions=pass"
Assert-Contains "manual readiness summary recorded" $plan "manual-gate-summary=pass"
Assert-Contains "manual readiness dashboard recorded" $plan "manual-gate-dashboard=pass"
Assert-Contains "manual readiness dashboard progress recorded" $plan "manual-gate-dashboard-progress=pass"
Assert-Contains "manual readiness dashboard open recorded" $plan "manual-gate-open-dashboard=pass"
Assert-Contains "manual readiness track hold summary recorded" $plan "manual-gate-summary-track-hold=pass"
Assert-Contains "manual readiness pending report recorded" $plan "manual-pending-report=pass"
Assert-Contains "manual readiness open app recorded" $plan "manual-gate-open-app=pass"
Assert-Contains "manual readiness final completion command recorded" $plan "manual-gate-final-completion-command=pass"
Assert-Contains "manual readiness completed mode recorded" $plan "completed-mode=AllowCompletedFullGt+AllowCompletedGuiSmoke"
Assert-Contains "manual readiness completed full gt gate recorded" $plan "completed-full-gt-reviewed-gate=RequireFullFrameReview+RequireArtifacts+RequireEvidence"
Assert-Contains "manual readiness ten minute ready recorded" $plan "ten-minute-artifacts=ready-yolo-output-and-incomplete-faceonnx-baseline"
Assert-Contains "ready for human gates marker recorded" $plan "yolo-ready-for-human-gates-state:"
Assert-Contains "ready for human gates verifier recorded" $plan "verify-yolo-ready-for-human-gates-state.ps1"
Assert-Contains "ready for human gates top-level command recorded" $plan "RunYoloReadyForHumanGatesState"
Assert-Contains "ready for human gates top-level rerun recorded" $plan "top-level-ready-rerun=pass"
Assert-Contains "ready for human gates evidence report recorded" $plan "evidence-report=pass"
Assert-Contains "ready for human gates dynamic evidence report recorded" $plan "evidence-report-dynamic=pass"
Assert-Contains "ready for human gates candidate comparison report recorded" $plan "evidence-report-candidate-comparison=pass"
Assert-Contains "ready for human gates full gt quality report recorded" $plan "evidence-report-full-gt-quality=fail-documented"
Assert-Contains "ready for human gates pending report recorded" $plan "manual-pending-report=pass"
Assert-Contains "ready for human gates completion finalizer recorded" $plan "completion-finalizer=pass"
Assert-Contains "ready for human gates completion finalizer state recorded" $plan "completion-finalizer-state=pass"
Assert-Contains "ready for human gates yolo-state pass recorded" $plan "yolo-state=pass"
Assert-Contains "ready for human gates manual summary pass recorded" $plan "manual-gate-summary=pass"
Assert-Contains "ready for human gates completion audit pass recorded" $plan "completion-audit=pass-incomplete"
Assert-Contains "ready for human gates remaining recorded" $plan "remaining=gui-smoke"
Assert-Contains "ready for human gates manual progress recorded" $plan "manual-gate-progress=pass"
Assert-Contains "ready for human gates dashboard open recorded" $plan "manual-gate-open-dashboard=pass"
Assert-Contains "completion audit verifier recorded" $plan "verify-yolo-completion-audit-state.ps1"
Assert-Contains "completion audit pending recorded" $plan "completion-audit=pass-incomplete"
Assert-Contains "completion audit prediction csv selftest recorded" $plan "completion-audit-prediction-csv-selftest=pass"
Assert-Contains "completion audit marker only selftest recorded" $plan "completion-audit-marker-only-selftest=pass"
Assert-Contains "completion audit pending negative selftest recorded" $plan "completion-audit-pending-negative-selftest=pass"
Assert-Contains "top-level require complete negative recorded" $plan "top-level-require-complete-negative=pass"
Assert-Contains "completion finalizer state verifier recorded" $plan "verify-yolo-completion-finalizer-state.ps1"
Assert-Contains "top-level require complete recorded" $plan "top-level-require-complete=fast-fail-guarded"
Assert-Contains "license source marker recorded" $plan "yolo-license-source-state: checked=2026-05-23"
Assert-Contains "license source gate recorded" $plan "source-gate=pass"
Assert-Contains "license bundle marker recorded" $plan "bundle=blocked"
Assert-Contains "license source pass recorded" $plan "license-source=pass"
Assert-Contains "extended fail recorded" $plan "SmokeQualityGate passed=False"
Assert-Contains "extended baseline frames recorded" $plan "baselineFrames=83"
Assert-Contains "extended optimized frames recorded" $plan "optimizedFrames=81"
Assert-Contains "extended only baseline recorded" $plan "onlyBaseline=13"
Assert-Contains "extended export baseline direct frames recorded" $plan "directFaceFrames=83"
Assert-Contains "extended export yolo direct frames recorded" $plan "directFaceFrames=81"
Assert-Contains "no final recommendation recorded" $plan "recommendation=none"
Assert-Contains "full gt reviewed recorded" $plan "full-gt-reviewed=pass"
Assert-Contains "gui smoke still missing" $plan "gui-smoke"
Assert-Contains "ten minute full not required after extended fail" $plan "ten-minute-full=not-required-after-extended-fail"
Assert-Contains "ten minute full A/B deferred explanation" $plan "다음 추천 후보가 확장 gate를 통과할 때 다시 수행"
Assert-Contains "yolo fixed input metadata policy recorded" $plan "고정 dimension을 제공하면 모델 metadata 값을 우선 사용"
Assert-Contains "yolo 736 fixed input smoke recorded" $plan "-YoloInputSize 736"
Assert-Contains "yolo directml provider recorded" $plan "YoloFaceOnnxDetector/GPU:DirectML"
Assert-Contains "yolo provider status api recorded" $plan "GetLastExecutionProviderLabel()"

Assert-Contains "auto verifier exposes yolo state" $autoVerify "RunYoloState"
Assert-Contains "auto verifier exposes representative gate" $autoVerify "RunYoloRepresentativeGate"
Assert-Contains "auto verifier exposes extended gate" $autoVerify "RunYoloExtendedGate"
Assert-Contains "auto verifier exposes extended export gate" $autoVerify "RunYoloExtendedExportGate"
Assert-Contains "auto verifier exposes gui smoke state" $autoVerify "RunYoloGuiSmokeState"
Assert-Contains "auto verifier exposes gui smoke manual pass" $autoVerify "RequireYoloGuiSmokeManualPass"
Assert-Contains "auto verifier exposes manual readiness state" $autoVerify "RunYoloManualReadinessState"
Assert-Contains "auto verifier exposes manual gate summary" $autoVerify "RunYoloManualGateSummary"
Assert-Contains "auto verifier exposes ready for human gates state" $autoVerify "RunYoloReadyForHumanGatesState"
Assert-Contains "auto verifier exposes manual gate summary path" $autoVerify "YoloManualGateSummaryPath"
Assert-Contains "auto verifier allows completed full gt" $autoVerify "AllowCompletedYoloFullGt"
Assert-Contains "auto verifier allows completed gui smoke" $autoVerify "AllowCompletedYoloGuiSmoke"
Assert-Contains "auto verifier exposes require complete" $autoVerify "RequireYoloComplete"
Assert-Contains "auto verifier forwards require complete" $autoVerify "RequireComplete"
Assert-Match "auto verifier skips empty representative yolo model path" $autoVerify 'IsNullOrWhiteSpace\(\$YoloRepresentativeModelPath\)[\s\S]*RepresentativeYoloModelPath'
Assert-Match "auto verifier skips empty extended yolo model path" $autoVerify 'IsNullOrWhiteSpace\(\$YoloExtendedModelPath\)[\s\S]*ExtendedYoloModelPath'
Assert-Match "auto verifier skips empty extended export yolo model path" $autoVerify 'IsNullOrWhiteSpace\(\$YoloExtendedExportModelPath\)[\s\S]*ExtendedExportYoloModelPath'
Assert-Contains "auto verifier exposes full gt prediction log" $autoVerify "YoloFullGtPredictionLog"
Assert-Contains "auto verifier exposes full gt quality limits" $autoVerify "YoloFullGtMaxFalsePositives"
Assert-Contains "auto verifier exposes full gt quality failure allowance" $autoVerify "AllowFullGtQualityGateFailure"
Assert-Contains "auto verifier exposes full gt candidate state" $autoVerify "RunYoloFullGtReviewedCandidateState"
Assert-Contains "auto verifier runs pseudo gt separation state" $autoVerify "verify-yolo-pseudo-gt-separation-state.ps1"
Assert-Contains "auto verifier runs pseudo gt face verification input state" $autoVerify "verify-yolo-pseudo-gt-face-verification-input-state.ps1"
Assert-Contains "auto verifier runs pseudo gt person object input state" $autoVerify "verify-yolo-pseudo-gt-person-object-input-state.ps1"
Assert-Contains "auto verifier runs pseudo gt review draft state" $autoVerify "verify-yolo-pseudo-gt-review-draft-state.ps1"
Assert-Contains "auto verifier runs pseudo gt review visual package state" $autoVerify "verify-yolo-pseudo-gt-review-visual-package-state.ps1"
Assert-Contains "auto verifier runs pseudo gt review draft apply state" $autoVerify "verify-yolo-pseudo-gt-review-draft-apply-state.ps1"
Assert-Contains "top-level verifier has completion audit path" $autoVerify "verify-yolo-completion-audit-state.ps1"
Assert-Contains "top-level verifier has require complete guard" $autoVerify "yolo-require-complete-guard"
Assert-Contains "top-level verifier promotes require complete to yolo state" $autoVerify '$RunYoloState = $true'
Assert-Contains "top-level require complete verifier recorded" $plan "verify-yolo-top-level-require-complete-state.ps1"
Assert-Contains "top-level require complete verifier runs auto verifier" $topLevelRequireCompleteVerify "verify-auto-mosaic-default.ps1"
Assert-Contains "top-level require complete verifier checks guard failure" $topLevelRequireCompleteVerify "goal marked complete missing text: complete=true"
Assert-Contains "top-level require complete verifier checks no faceonnx gate" $topLevelRequireCompleteVerify "quality-gate-all-frame-parallel"
Assert-Contains "yolo state runs top-level require complete state" $yoloState "top-level-require-complete-state"
Assert-Contains "yolo state exposes representative gate" $yoloState "RunRepresentativeGate"
Assert-Contains "yolo state exposes extended gate" $yoloState "RunExtendedGate"
Assert-Contains "yolo state exposes extended export gate" $yoloState "RunExtendedExportGate"
Assert-Contains "yolo state exposes gui smoke state" $yoloState "RunGuiSmokeState"
Assert-Contains "yolo state exposes gui smoke manual pass" $yoloState "RequireGuiSmokeManualPass"
Assert-Contains "yolo state allows completed full gt" $yoloState "AllowCompletedFullGt"
Assert-Contains "yolo state allows completed gui smoke" $yoloState "AllowCompletedGuiSmoke"
Assert-Contains "yolo state exposes full gt prediction log" $yoloState "FullGtPredictionLog"
Assert-Contains "yolo state forwards full gt prediction csv to completion audit" $yoloState "PredictionCsv"
Assert-Contains "yolo state exposes full gt quality limits" $yoloState "FullGtMaxFalsePositives"
Assert-Contains "yolo state has require complete guard" $yoloState "completion-audit-require-complete-guard"
Assert-Contains "yolo state builds completion audit args once" $yoloState "New-CompletionAuditArgs"
Assert-Contains "yolo state runs completion audit state" $yoloState "completion-audit-state"
Assert-Contains "yolo state runs completion audit selftest" $yoloState "completion-audit-selftest"
Assert-Contains "yolo state runs completion finalizer state" $yoloState "completion-finalizer-state"
Assert-Contains "yolo state runs gui evidence prep" $yoloState "gui-smoke-evidence-prep"
Assert-Contains "yolo state runs manual pending report" $yoloState "manual-pending-report"
Assert-Contains "yolo state runs startup smoke state" $yoloState "startup-smoke-state"
Assert-Contains "yolo state runs track hold state" $yoloState "track-hold-state"
Assert-Contains "yolo state exposes require complete" $yoloState "RequireComplete"
Assert-Contains "yolo state can ignore completed goal audit marker" $yoloState "IgnoreCompletedGoalAuditMarker"
Assert-Contains "yolo state can skip current completion audit state" $yoloState "SkipCompletionAuditState"
Assert-Contains "yolo state runs manual readiness state" $yoloState "manual-readiness-state"
Assert-Contains "yolo state runs manual gate helper state" $yoloState "manual-gate-helper-state"
Assert-Contains "yolo state runs full gt candidate state" $yoloState "full-gt-reviewed-candidate-state"
Assert-Contains "full gt label verifier can allow documented failure" $plan "AllowQualityGateFailure"
Assert-Contains "full gt label script supports allowed failure" (Read-RepoFile "scripts/verify-yolo-full-gt-label-state.ps1") "failureAllowed=True"
Assert-Contains "full gt reviewed script forwards allowed failure" (Read-RepoFile "scripts/verify-yolo-full-gt-reviewed-state.ps1") "AllowQualityGateFailure"
Assert-Contains "full gt candidate verifier checks allowed failure" (Read-RepoFile "scripts/verify-yolo-full-gt-reviewed-candidate-state.ps1") "candidate allowed quality failure"
Assert-Contains "profile verifier checks fixed input metadata" $yoloProfileState "yolo fixed input size prefers model metadata"
Assert-Contains "profile verifier checks yolo directml provider" $yoloProfileState "yolo directml provider is loaded like faceonnx"
Assert-Contains "profile verifier checks yolo gpu fallback" $yoloProfileState "yolo gpu failure falls back to cpu session"
Assert-Contains "profile verifier checks yolo home provider status" $yoloProfileState "home yolo status reads yolo provider"
Assert-Contains "profile verifier checks yolo automask provider summary" $yoloProfileState "automask yolo detector summary includes provider"
Assert-Contains "profile verifier checks yolo lost fill" $yoloProfileState "MaxLostFillFrames"
Assert-Contains "profile verifier checks top small yolo filter" $yoloProfileState "home enables conservative top small low-confidence yolo filter"
Assert-Contains "profile verifier checks settings version 6" $yoloProfileState "settings version is v6"
Assert-Contains "profile verifier checks yolo pipeline capture" $yoloProfileState "home captures yolo auto pipeline profile"
Assert-Contains "profile verifier checks yolo pipeline apply" $yoloProfileState "home applies yolo auto pipeline profile"
Assert-Contains "profile verifier checks yolo5 pipeline restore" $yoloProfileState "home reads saved yolo5 auto pipeline profile"
Assert-Contains "profile verifier checks yolov8 pipeline restore" $yoloProfileState "home reads saved yolov8 auto pipeline profile"
Assert-Contains "profile verifier checks yolo profile restart suppression" $yoloProfileState "home suppresses downscale restart while applying yolo profile"
Assert-Contains "profile verifier checks tracking toggle gate" $yoloProfileState "workspace tracking toggle gates temporal fixes"
Assert-Contains "profile verifier checks tracking smoothing gate" $yoloProfileState "workspace tracking toggle gates temporal smoothing"
Assert-Contains "profile verifier checks preview refresh after track postprocess" $yoloProfileState "workspace refreshes preview after track postprocess"
Assert-Contains "track hold verifier checks internal confirmed gap hold" $yoloTrackHoldVerify "MaxConfirmedTrackHoldFrames = 5"
Assert-Contains "track hold verifier checks six lost frames" $yoloTrackHoldVerify "Enumerable.Range(21, 6)"
Assert-Contains "track hold verifier checks stop after cap" $yoloTrackHoldVerify "stop after MaxLostFillFrames=6"
Assert-Contains "track hold verifier checks weak candidate removal" $yoloTrackHoldVerify "one-frame weak YOLO candidate"
Assert-Contains "track hold verifier checks high confidence source" $yoloTrackHoldVerify "0.96f"
Assert-Contains "track hold verifier checks synthetic confidence cap" $yoloTrackHoldVerify "SyntheticFillConfidenceMax = 0.78f"
Assert-Contains "track hold verifier checks scene cut lost tail removal" $yoloTrackHoldVerify "sceneCutLostRemoved"
Assert-Contains "track hold verifier blocks long shifted gap hold" $yoloTrackHoldVerify "long high-shift gap"
Assert-Contains "manual readiness invokes reviewed full gt verifier" $manualReadinessVerify "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "manual readiness requires full frame review" $manualReadinessVerify "RequireFullFrameReview"
Assert-Contains "manual readiness requires full gt artifacts" $manualReadinessVerify "RequireArtifacts"
Assert-Contains "review package verifier checks csv key" $fullGtReviewPackageVerify "CSV key:"
Assert-Contains "review package verifier checks pending fields" $fullGtReviewPackageVerify "pending:"
Assert-Contains "completion audit checks complete false" $completionAuditVerify "complete=false"
Assert-Contains "completion audit checks remaining gates" $completionAuditVerify "remaining=full-gt-label,gui-smoke"
Assert-Contains "completion audit checks gui-only remaining gate" $completionAuditVerify "remaining=gui-smoke"
Assert-Contains "completion audit checks track hold state" $completionAuditVerify "track-hold-state=pass"
Assert-Contains "completion audit checks manual gate summary" $completionAuditVerify "ManualGateSummary"
Assert-Contains "completion audit checks preview track hold summary" $completionAuditVerify "preview-track-hold"
Assert-Contains "completion audit checks manual summary commands" $completionAuditVerify "verify-yolo-manual-readiness-state.ps1"
Assert-Contains "completion audit checks app open summary" $completionAuditVerify "open-yolo-manual-gates.ps1 -OpenApp"
Assert-Contains "completion audit checks final yolo state summary" $completionAuditVerify "verify-yolo-state.ps1"
Assert-Contains "completion audit checks completion plan summary" $completionAuditVerify "completionPlanAction"
Assert-Contains "completion audit parses goal marker" $completionAuditVerify "Get-YoloGoalAuditMarker"
Assert-Contains "completion audit uses parsed marker" $completionAuditVerify '$goalMarker'
Assert-Contains "completion audit supports require complete" $completionAuditVerify "RequireComplete"
Assert-Contains "completion audit selftests marker-only failure" $completionAuditVerify "marker-only complete state"
Assert-Contains "completion audit selftests misleading body prose" $completionAuditVerify "complete=true and remaining=none outside the goal marker"
Assert-Contains "completion audit selftests pending evidence failure" $completionAuditVerify "pending evidence cannot RequireComplete"
Assert-Contains "completion audit checks complete true" $completionAuditVerify "complete=true"
Assert-Contains "completion audit checks no remaining gates" $completionAuditVerify "remaining=none"
Assert-Contains "completion audit invokes strict full gt" $completionAuditVerify "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "completion audit allows documented quality failure" $completionAuditVerify "AllowQualityGateFailure"
Assert-Contains "completion audit accepts prediction csv" $completionAuditVerify "PredictionCsv"
Assert-Contains "completion audit selftests prediction csv complete path" $completionAuditVerify "pass selftest complete PredictionCsv path"
Assert-Contains "completion audit checks pseudo-GT test-only marker" $completionAuditVerify "pseudo-gt-test-only=pass"
Assert-Contains "completion audit checks pseudo-GT review closure marker" $completionAuditVerify "pseudo-gt-review-closure=conditional-gated"
Assert-Contains "completion audit requires pseudo-GT closure when candidates exist" $completionAuditVerify "Assert-PseudoGtReviewClosure"
Assert-Contains "completion audit requires pseudo-GT candidates for complete" $completionAuditVerify "Pseudo-GT candidate CSV is required for complete audit"
Assert-Contains "completion audit requires pseudo-GT review evidence notes" $completionAuditVerify "reviewEvidenceNotes"
Assert-Contains "completion audit requires pseudo-GT source-id geometry" $completionAuditVerify "sourcePredictionId+iou"
Assert-Contains "completion audit requires pseudo-GT full-frame evidence notes" $completionAuditVerify "fullFrameEvidenceNotes"
Assert-Contains "completion audit blocks loose pseudo-GT closure" $completionAuditVerify "loose pseudo-GT closure cannot RequireComplete"
Assert-Contains "completion audit invokes strict gui smoke" $completionAuditVerify "verify-yolo-gui-smoke-state.ps1"
Assert-Contains "completion finalizer state checks selftest" $completionFinalizerStateVerify "finalizer selftest"
Assert-Contains "completion finalizer state checks pseudo gt closure selftest" $completionFinalizerStateVerify "pseudo-GT closure"
Assert-Contains "completion finalizer state checks pending evidence blocks" $completionFinalizerStateVerify "current pending evidence blocks finalizer"
Assert-Contains "completion finalizer state checks strict full gt" $completionFinalizerStateVerify "full-gt-reviewed-state"
Assert-Contains "completion finalizer state checks gui smoke" $completionFinalizerStateVerify "gui-smoke-state"
Assert-Contains "completion finalizer state checks missing pseudo gt blocks" $completionFinalizerStateVerify "Pseudo-GT candidate CSV is required before completing the YOLO goal"
Assert-Contains "completion finalizer state treats missing pseudo gt as not finalizable" $completionFinalizerStateVerify "current missing pseudo-GT candidates block finalizer"
Assert-Contains "completion finalizer state treats unclosed pseudo gt as not finalizable" $completionFinalizerStateVerify "current unclosed pseudo-GT review closure blocks finalizer"
Assert-Contains "manual gate helper opens review index" $manualGateHelper "ReviewIndex"
Assert-Contains "manual gate helper verifies ready state" $manualGateHelper "VerifyReady"
Assert-Contains "manual gate helper verifies completed state" $manualGateHelper "VerifyCompleted"
Assert-Contains "manual gate helper computes remaining gates" $manualGateHelper "Get-RemainingManualGates"
Assert-Contains "manual gate helper checks strict pseudo GT closure readiness" $manualGateHelper "Test-PseudoGtReviewClosureReady"
Assert-Contains "manual gate helper prints remaining gates" $manualGateHelper "remaining="
Assert-Contains "manual gate helper includes missing pseudo GT in remaining gates" $manualGateHelper "pseudo-gt-evidence"
Assert-Contains "manual gate helper includes pseudo GT closure in remaining gates" $manualGateHelper "pseudo-gt-review-closure"
Assert-Contains "manual gate helper prints open command" $manualGateHelper "openCommand"
Assert-Contains "manual gate helper prints dashboard open command" $manualGateHelper "openDashboardCommand"
Assert-Contains "manual gate helper supports dashboard open" $manualGateHelper "OpenDashboard"
Assert-Contains "manual gate helper prints app open command" $manualGateHelper "openAppCommand"
Assert-Contains "manual gate helper can open app" $manualGateHelper "OpenApp"
Assert-Contains "manual gate helper prints final yolo state command" $manualGateHelper "completedYoloStateCommand"
Assert-Contains "manual gate helper prints completion finalizer command" $manualGateHelper "completionFinalizerCommand"
Assert-Contains "manual gate helper prints pending report command" $manualGateHelper "pendingReportCommand"
Assert-Contains "manual gate helper prints gui evidence command" $manualGateHelper "prepareGuiEvidenceCommand"
Assert-Contains "manual gate helper supports gui evidence prep" $manualGateHelper "PrepareGuiEvidence"
Assert-Contains "manual gate helper runs gui evidence prep" $manualGateHelper "prepare-yolo-gui-smoke-evidence.ps1"
Assert-Contains "manual gate helper supports pseudo GT visual prep" $manualGateHelper "PreparePseudoGtReviewVisual"
Assert-Contains "manual gate helper runs pseudo GT visual prep" $manualGateHelper "Prepare-PseudoGtReviewVisual"
Assert-Contains "manual gate helper prints human review draft command" $manualGateHelper "humanReviewDraftCommand"
Assert-Contains "manual gate helper runs human review draft writer" $manualGateHelper "new-yolo-human-review-draft.ps1"
Assert-Contains "manual gate helper prints pseudo GT review draft command" $manualGateHelper "pseudoGtReviewDraftCommand"
Assert-Contains "manual gate helper prints pseudo GT visual command" $manualGateHelper "pseudoGtReviewVisualCommand"
Assert-Contains "manual gate helper prints pseudo GT decision progress command" $manualGateHelper "pseudoGtDecisionProgressCommand"
Assert-Contains "manual gate helper prints pseudo GT review draft apply command" $manualGateHelper "pseudoGtReviewDraftApplyCommand"
Assert-Contains "manual gate helper applies pseudo GT review draft in place" $manualGateHelper "-InPlace -Verify"
Assert-Contains "manual gate helper records pseudo GT review queue csv" $manualGateHelper "PseudoGtReviewQueueCsv"
Assert-Contains "manual gate helper records pseudo GT review draft dir" $manualGateHelper "PseudoGtReviewDraftDir"
Assert-Contains "manual gate helper records pseudo GT visual dir" $manualGateHelper "PseudoGtReviewVisualDir"
Assert-Contains "manual gate helper runs pseudo GT review draft writer" $manualGateHelper "new-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "manual gate helper runs pseudo GT visual package writer" $manualGateHelper "new-yolo-pseudo-gt-review-visual-package.ps1"
Assert-Contains "manual gate helper runs pseudo GT decision progress writer" $manualGateHelper "write-yolo-pseudo-gt-decision-progress.ps1"
Assert-Contains "manual gate helper runs pseudo GT review draft apply script" $manualGateHelper "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "manual gate helper prints completion plan action" $manualGateHelper "completionPlanAction"
Assert-Contains "manual gate helper prints full GT action" $manualGateHelper "fullGtAction"
Assert-Contains "manual gate helper prints GUI smoke action" $manualGateHelper "guiSmokeAction"
Assert-Contains "manual gate helper prints track hold action" $manualGateHelper "trackHoldAction"
Assert-Contains "manual gate helper prints smoke manual startup command" $manualGateHelper "openSmokeManualCommand"
Assert-Contains "manual gate helper prints smoke auto startup command" $manualGateHelper "openSmokeAutoCommand"
Assert-Contains "manual gate helper writes summary" $manualGateHelper "WriteSummary"
Assert-Contains "manual gate helper summary path" $manualGateHelper "SummaryPath"
Assert-Contains "manual gate helper writes dashboard" $manualGateHelper "Write-ManualGateDashboard"
Assert-Contains "manual gate helper dashboard path" $manualGateHelper "DashboardPath"
Assert-Contains "manual gate helper computes progress" $manualGateHelper "Get-ManualGateProgress"
Assert-Contains "manual gate helper prepares gui checklist" $manualGateHelper "PrepareGuiChecklist"
Assert-Contains "manual gate helper generator recorded" $manualGateHelper "new-yolo-gui-smoke-checklist.ps1"
Assert-Contains "manual gate helper forwards custom ready paths" $manualGateHelper "FullGtPredictionLog"
Assert-Contains "manual gate helper requires gui manual pass" $manualGateHelper "RequireManualPass"
Assert-Contains "manual gate helper allows completed full gt" $manualGateHelper "AllowCompletedFullGt"
Assert-Contains "manual gate helper allows completed gui smoke" $manualGateHelper "AllowCompletedGuiSmoke"
Assert-Contains "manual gate helper passes completed quality limits" $manualGateHelper "FullGtMaxFalsePositives"
Assert-Contains "manual gate helper verifier checks ready path" $manualGateHelperVerify "VerifyReady"
Assert-Contains "manual gate helper verifier checks completed path" $manualGateHelperVerify "VerifyCompleted"
Assert-Contains "manual gate helper verifier checks pending failure" $manualGateHelperVerify "unexpectedly passed on pending manual files"
Assert-Contains "manual gate helper verifier checks completed pass" $manualGateHelperVerify "failed on completed manual files"
Assert-Contains "manual gate helper verifier builds completed fixture" $manualGateHelperVerify "New-CompletedGuiChecklistFixture"
Assert-Contains "manual gate helper verifier uses AI candidate fixture" $manualGateHelperVerify "full-gt-review-reviewed-candidate.csv"
Assert-Contains "manual gate helper verifier checks completed fixture pass" $manualGateHelperVerify "completed fixture passes helper"
Assert-Contains "manual gate helper verifier blocks incomplete pseudo GT closure" $manualGateHelperVerify "incomplete pseudo-GT closure"
Assert-Contains "manual gate helper verifier checks summary writing" $manualGateHelperVerify "summary records completed readiness command"
Assert-Contains "manual gate helper verifier checks dashboard" $manualGateHelperVerify "manual gate dashboard"
Assert-Contains "manual gate helper verifier checks progress counts" $manualGateHelperVerify "summary records full GT pending count"
Assert-Contains "manual gate helper verifier checks dashboard progress" $manualGateHelperVerify "dashboard records full GT progress"
Assert-Contains "manual gate helper verifier checks final yolo state summary" $manualGateHelperVerify "summary records final yolo state command"
Assert-Contains "manual gate helper verifier checks finalizer summary" $manualGateHelperVerify "summary records completion finalizer command"
Assert-Contains "manual gate helper verifier checks pending report summary" $manualGateHelperVerify "summary records pending report command"
Assert-Contains "manual gate helper verifier checks gui evidence prep summary" $manualGateHelperVerify "summary records GUI evidence prep command"
Assert-Contains "manual gate helper verifier checks gui evidence prep path" $manualGateHelperVerify "prepared GUI evidence guide"
Assert-Contains "manual gate helper verifier checks pseudo GT visual prep" $manualGateHelperVerify "prepared pseudo GT visual output runs visual package"
Assert-Contains "manual gate helper verifier checks human review draft summary" $manualGateHelperVerify "summary records human review draft command"
Assert-Contains "manual gate helper verifier checks pseudo GT review draft command" $manualGateHelperVerify "summary records pseudo GT review draft command"
Assert-Contains "manual gate helper verifier checks pseudo GT decision progress command" $manualGateHelperVerify "summary records pseudo GT decision progress command"
Assert-Contains "manual gate helper verifier checks pseudo GT decision progress dashboard" $manualGateHelperVerify "dashboard records pseudo GT decision progress command"
Assert-Contains "manual gate helper verifier checks pseudo GT review draft dashboard" $manualGateHelperVerify "dashboard records pseudo GT review draft command"
Assert-Contains "manual gate helper verifier checks completion plan action summary" $manualGateHelperVerify "summary records completion plan action"
Assert-Contains "manual gate helper verifier checks track hold summary" $manualGateHelperVerify "summary records preview track hold step"
Assert-Contains "manual gate helper verifier checks app open summary" $manualGateHelperVerify "summary records app open command"
Assert-Contains "manual gate helper verifier checks dashboard open summary" $manualGateHelperVerify "summary records dashboard open command"
Assert-Contains "manual gate helper verifier checks gui checklist preparation" $manualGateHelperVerify "prepared GUI checklist"
Assert-Contains "manual gate helper verifier checks prepared ready path" $manualGateHelperVerify "prepared output verifies custom GUI checklist path"
Assert-Contains "ready for human gates verifier runs yolo state" $readyForHumanGatesVerify "verify-yolo-state.ps1"
Assert-Contains "ready for human gates verifier writes manual summary" $readyForHumanGatesVerify "open-yolo-manual-gates.ps1"
Assert-Contains "ready for human gates verifier checks dashboard path" $readyForHumanGatesVerify "manual-gate-dashboard.html"
Assert-Contains "ready for human gates verifier checks dashboard open command" $readyForHumanGatesVerify "OpenDashboard"
Assert-Contains "ready for human gates verifier checks progress counts" $readyForHumanGatesVerify "fullGtPendingRows="
Assert-Contains "ready for human gates verifier accepts completed GUI progress" $readyForHumanGatesVerify "guiPendingRows=0"
Assert-Contains "ready for human gates verifier writes evidence report" $readyForHumanGatesVerify "goal-evidence-report"
Assert-Contains "ready for human gates verifier runs completion audit" $readyForHumanGatesVerify "verify-yolo-completion-audit-state.ps1"
Assert-Contains "ready for human gates verifier checks remaining gates" $readyForHumanGatesVerify "gui-smoke"
Assert-Contains "ready for human gates verifier tracks pseudo GT remaining gate" $readyForHumanGatesVerify "pseudo-gt"
Assert-Contains "ready for human gates verifier ignores previous complete marker for readiness" $readyForHumanGatesVerify "IgnoreCompletedGoalAuditMarker"
Assert-Contains "ready for human gates verifier skips current completion audit until pseudo GT is ready" $readyForHumanGatesVerify "SkipCompletionAuditState"
Assert-Contains "ready for human gates verifier accepts no remaining gates" $readyForHumanGatesVerify "remaining=none"
Assert-Contains "ready for human gates verifier checks final command" $readyForHumanGatesVerify "RequireComplete"
Assert-Contains "ready for human gates verifier checks finalizer command" $readyForHumanGatesVerify "complete-yolo-goal-after-manual-gates.ps1"
Assert-Contains "evidence report writer records full GT" $goalEvidenceReportWriter "Full-GT label review"
Assert-Contains "evidence report writer records top small yolo filter" $goalEvidenceReportWriter "YOLO top small false-positive filter"
Assert-Contains "evidence report writer records pending GUI smoke" $goalEvidenceReportWriter "Avalonia GUI smoke"
Assert-Contains "evidence report writer records preview track hold GUI evidence" $goalEvidenceReportWriter "Preview track-hold GUI evidence"
Assert-Contains "evidence report writer records YOLOv8 comparison" $goalEvidenceReportWriter "YOLOv8 candidate A/B comparison"
Assert-Contains "evidence report writer records YOLO5Face comparison" $goalEvidenceReportWriter "YOLO5Face candidate A/B comparison"
Assert-Contains "evidence report writer records failure axes" $goalEvidenceReportWriter "Failure-axis classification"
Assert-Contains "evidence report writer keeps preview track hold human pending separate" $goalEvidenceReportWriter "previewTrackHoldStatus"
Assert-Contains "evidence report writer records full gt quality gate" $goalEvidenceReportWriter "Full-GT quality gate"
Assert-Contains "evidence report writer records high-confidence scene carry cleanup" $goalEvidenceReportWriter "Scene-cut high-confidence carry cleanup"
Assert-Contains "evidence report writer reads final-mask cleanup log" $goalEvidenceReportWriter "FinalMaskCleanupLog"
Assert-Contains "evidence report writer parses final-mask cleanup evidence" $goalEvidenceReportWriter "Get-FinalMaskCleanupEvidence"
Assert-Contains "evidence report writer records same-size carry removal evidence" $goalEvidenceReportWriter "finalMaskCleanupDriftingStrongCarryRemoved="
Assert-Contains "evidence report writer records scale-changed carry protection evidence" $goalEvidenceReportWriter "finalMaskCleanupAreaChangedStrongCarryProtected="
Assert-Contains "evidence report writer records pseudo gt candidate evidence" $goalEvidenceReportWriter "Test-only pseudo-GT candidate evidence"
Assert-Contains "evidence report writer records pseudo gt review queue" $goalEvidenceReportWriter "Test-only pseudo-GT review queue"
Assert-Contains "evidence report writer records pseudo gt review queue status" $goalEvidenceReportWriter "pseudoGtReviewQueueStatus="
Assert-Contains "evidence report writer records pseudo gt auxiliary boost count" $goalEvidenceReportWriter "pseudoGtReviewQueueAuxiliaryBoostedRows="
Assert-Contains "evidence report writer records pseudo gt auxiliary role count" $goalEvidenceReportWriter "pseudoGtReviewQueueAuxiliaryRoleRows="
Assert-Contains "evidence report writer records pseudo gt queue continuity count" $goalEvidenceReportWriter "pseudoGtReviewQueueContinuityRows="
Assert-Contains "evidence report writer records pseudo gt queue continuity boost count" $goalEvidenceReportWriter "pseudoGtReviewQueueContinuityBoostedRows="
Assert-Contains "evidence report writer records pseudo gt candidate auxiliary role evidence" $goalEvidenceReportWriter "pseudoGtAuxiliaryRoleEvidenceRows="
Assert-Contains "evidence report writer records priority-only auxiliary role" $goalEvidenceReportWriter "priority-only-not-face-evidence"
Assert-Contains "evidence report writer records pseudo gt max auxiliary boost" $goalEvidenceReportWriter "pseudoGtReviewQueueMaxAuxiliaryBoost="
Assert-Contains "evidence report writer records pseudo gt support row evidence" $goalEvidenceReportWriter "pseudoGtSupportRowEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt support id evidence" $goalEvidenceReportWriter "pseudoGtSupportIdEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt closure support row evidence" $goalEvidenceReportWriter "pseudoGtClosureSupportRowEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt closure support id evidence" $goalEvidenceReportWriter "pseudoGtClosureSupportIdEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt face verification evidence" $goalEvidenceReportWriter "pseudoGtFaceVerificationEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt closure face verification evidence" $goalEvidenceReportWriter "pseudoGtClosureFaceVerificationEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt source-bound face verification evidence" $goalEvidenceReportWriter "pseudoGtSourceBoundFaceVerificationRows="
Assert-Contains "evidence report writer records pseudo gt closure source-bound face verification evidence" $goalEvidenceReportWriter "pseudoGtClosureSourceBoundFaceVerificationRows="
Assert-Contains "evidence report writer records pseudo gt continuity evidence" $goalEvidenceReportWriter "pseudoGtContinuityEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt closure continuity evidence" $goalEvidenceReportWriter "pseudoGtClosureContinuityEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt model provenance" $goalEvidenceReportWriter "pseudoGtModelProvenanceRows="
Assert-Contains "evidence report writer records pseudo gt runner provenance" $goalEvidenceReportWriter "pseudoGtRunnerProvenanceRows="
Assert-Contains "evidence report writer records pseudo gt closure model provenance" $goalEvidenceReportWriter "pseudoGtClosureModelProvenanceRows="
Assert-Contains "evidence report writer records pseudo gt closure runner provenance" $goalEvidenceReportWriter "pseudoGtClosureRunnerProvenanceRows="
Assert-Contains "evidence report writer records pseudo gt closure auxiliary role evidence" $goalEvidenceReportWriter "pseudoGtClosureAuxiliaryRoleEvidenceRows="
Assert-Contains "evidence report writer requires face verification distance for evidence count" $goalEvidenceReportWriter "faceVerificationDistance"
Assert-Contains "evidence report writer records pseudo gt closure" $goalEvidenceReportWriter "Test-only pseudo-GT review closure"
Assert-Contains "evidence report writer records pseudo gt closure evidence preservation" $goalEvidenceReportWriter "Test-only pseudo-GT closure evidence preservation"
Assert-Contains "evidence report writer independently validates strict pseudo gt closure" $goalEvidenceReportWriter "Test-PseudoGtClosureStrictlyClosed"
Assert-Contains "evidence report writer requires completed pseudo gt review status" $goalEvidenceReportWriter "Test-ReviewedStatus"
Assert-Contains "evidence report writer records loose pseudo gt closure count" $goalEvidenceReportWriter "pseudoGtLooseClosedRows="
Assert-Contains "evidence report writer requires pseudo gt review evidence notes" $goalEvidenceReportWriter "reviewEvidenceNotes"
Assert-Contains "evidence report writer requires pseudo gt source-id geometry" $goalEvidenceReportWriter "sourcePredictionId+iou"
Assert-Contains "evidence report writer requires pseudo gt full-frame evidence notes" $goalEvidenceReportWriter "fullFrameEvidenceNotes"
Assert-Contains "evidence report writer records pseudo gt review evidence notes count" $goalEvidenceReportWriter "pseudoGtClosureReviewEvidenceNotesRows="
Assert-Contains "evidence report writer records pseudo gt review match mode count" $goalEvidenceReportWriter "pseudoGtClosureReviewMatchModeRows="
Assert-Contains "evidence report writer records pseudo gt full-frame continuity evidence count" $goalEvidenceReportWriter "pseudoGtClosureFullFrameContinuityRows="
Assert-Contains "evidence report writer records pseudo gt full-frame continuity reason count" $goalEvidenceReportWriter "pseudoGtClosureFullFrameContinuityReasonRows="
Assert-Contains "evidence report writer records pseudo gt full-frame continuity hint count" $goalEvidenceReportWriter "pseudoGtClosureFullFrameContinuityHintRows="
Assert-Contains "evidence report writer records pseudo gt area ratio evidence" $goalEvidenceReportWriter "areaRatioEvidenceRows="
Assert-Contains "evidence report writer records pseudo gt closure area ratio evidence" $goalEvidenceReportWriter "closureAreaRatioEvidenceRows="
Assert-Contains "evidence report writer runs full gt quality verifier" $goalEvidenceReportWriter "Invoke-FullGtQualityGate"
Assert-Contains "evidence report writer records quality gate status" $goalEvidenceReportWriter "fullGtQualityGate="
Assert-Contains "evidence report writer allows documented quality failure" $goalEvidenceReportWriter "fail-documented"
Assert-Contains "evidence report writer records incomplete goal" $goalEvidenceReportWriter "Goal completion"
Assert-Contains "evidence report writer supports strict completion mode" $goalEvidenceReportWriter "RequireComplete"
Assert-Contains "evidence report writer computes strict completion readiness" $goalEvidenceReportWriter "ready-for-strict-completion-audit"
Assert-Contains "evidence report writer records filled GUI smoke" $goalEvidenceReportWriter "report keeps GUI filled"
Assert-Contains "evidence report writer records passed preview track hold" $goalEvidenceReportWriter "report keeps preview track-hold passed"
Assert-Contains "evidence report writer computes current plan completion" $goalEvidenceReportWriter "planComplete"
Assert-Contains "evidence report writer parses goal audit marker" $goalEvidenceReportWriter "Get-GoalAuditMarker"
Assert-Contains "evidence report writer uses marker status" $goalEvidenceReportWriter '$goalMarker.Contains("complete=false")'
Assert-Contains "manual pending report writer records full GT pending rows" $manualPendingReportWriter "fullGtPendingRows"
Assert-Contains "manual pending report writer records full-frame pending rows" $manualPendingReportWriter "fullFramePendingRows"
Assert-Contains "manual pending report writer records GUI pending rows" $manualPendingReportWriter "guiPendingRows"
Assert-Contains "manual pending report writer records input rules" $manualPendingReportWriter "## Input Rules"
Assert-Contains "manual pending report writer explains label values" $manualPendingReportWriter "full-gt-review.csv label"
Assert-Contains "manual pending report writer explains missed rows" $manualPendingReportWriter "missedFaceRowsAdded"
Assert-Contains "manual pending report writer explains gui status" $manualPendingReportWriter "GUI checklist status"
Assert-Contains "manual pending report writer records preview track hold" $manualPendingReportWriter "preview-track-hold"
Assert-Contains "manual pending report writer preserves documented quality failure flag" $manualPendingReportWriter "AllowQualityGateFailure"
Assert-Contains "human review draft writer records reference rule" $humanReviewDraftWriter "reference-only-not-final-gt"
Assert-Contains "human review draft writer keeps final fields blank" $humanReviewDraftWriter "unexpectedly filled final review fields"
Assert-Contains "human review draft writer records candidate labels" $humanReviewDraftWriter "candidateLabel"
Assert-Contains "human review draft writer records candidate full-frame counts" $humanReviewDraftWriter "candidateMissedFaceCount"
Assert-Contains "human review draft writer records manual missed candidates" $humanReviewDraftWriter "manualMissedCandidateRows"
Assert-Contains "pseudo gt review draft writer records test-only rule" $pseudoGtReviewDraftWriter "test-only-reference-not-final-gt"
Assert-Contains "pseudo gt review draft writer keeps final review fields blank" $pseudoGtReviewDraftWriter 'label = ""'
Assert-Contains "pseudo gt review draft writer preserves base source ids" $pseudoGtReviewDraftWriter "basePredictionId"
Assert-Contains "pseudo gt review draft writer requires miss source id blank" $pseudoGtReviewDraftWriter "sourcePredictionId blank"
Assert-Contains "pseudo gt review draft writer creates full gt draft csv" $pseudoGtReviewDraftWriter "pseudo-gt-full-gt-review-draft.csv"
Assert-Contains "pseudo gt review draft writer creates full frame draft csv" $pseudoGtReviewDraftWriter "pseudo-gt-full-frame-review-draft.csv"
Assert-Contains "pseudo gt review draft verifier selftests draft output" $pseudoGtReviewDraftVerify "YoloPseudoGtReviewDraftVerify"
Assert-Contains "pseudo gt review draft verifier checks source id geometry" $pseudoGtReviewDraftVerify "source id plus IoU geometry"
Assert-Contains "pseudo gt review visual package records test-only boundary" $pseudoGtReviewVisualPackage "test-only visual review evidence"
Assert-Contains "pseudo gt review visual package keeps final labels human owned" $pseudoGtReviewVisualPackage "does not finalize face/nonface/miss labels"
Assert-Contains "pseudo gt review visual package writes crop artifacts" $pseudoGtReviewVisualPackage "cropPath"
Assert-Contains "pseudo gt review visual package writes overlay artifacts" $pseudoGtReviewVisualPackage "visualOverlayPath"
Assert-Contains "pseudo gt review visual package verifier selftests visual artifacts" $pseudoGtReviewVisualPackageVerify "YoloPseudoGtReviewVisualVerify"
Assert-Contains "pseudo gt review visual package verifier checks all candidate types" $pseudoGtReviewVisualPackageVerify "supportedFaceCandidate"
Assert-Contains "pseudo gt decision progress writer records test-only boundary" $pseudoGtDecisionProgressWriter "test-only human review progress report"
Assert-Contains "pseudo gt decision progress writer does not infer labels" $pseudoGtDecisionProgressWriter "does not infer final face/nonface/miss labels"
Assert-Contains "pseudo gt decision progress writer counts pending candidates" $pseudoGtDecisionProgressWriter "pendingCandidateRows"
Assert-Contains "pseudo gt decision progress writer counts pending full-frame rows" $pseudoGtDecisionProgressWriter "pendingFrameDecisionRows"
Assert-Contains "pseudo gt decision progress writer checks invalid labels" $pseudoGtDecisionProgressWriter "invalidCandidateLabelRows"
Assert-Contains "pseudo gt decision progress writer requires evidence notes" $pseudoGtDecisionProgressWriter "evidenceNotes"
Assert-Contains "pseudo gt decision progress writer guards require complete" $pseudoGtDecisionProgressWriter "RequireComplete"
Assert-Contains "pseudo gt decision progress writer prints apply command" $pseudoGtDecisionProgressWriter "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "pseudo gt review draft apply keeps final labels human owned" $pseudoGtReviewDraftApply "does not infer labels from suggestedLabel"
Assert-Contains "pseudo gt review draft apply records review csv ownership" $pseudoGtReviewDraftApply "review CSV-owned"
Assert-Contains "pseudo gt review draft apply requires final review fields" $pseudoGtReviewDraftApply "label/reviewStatus/evidenceNotes"
Assert-Contains "pseudo gt review draft apply verifier selftests closure" $pseudoGtReviewDraftApplyVerify "RequireAllClosed"
Assert-Contains "pseudo gt review draft apply verifier checks suggestedLabel mismatch" $pseudoGtReviewDraftApplyVerify "instead of suggestedLabel"
Assert-Contains "completion finalizer verifies manual readiness" $completionFinalizer "manual-readiness-completed-state"
Assert-Contains "completion finalizer updates plan marker" $completionFinalizer "Update-GoalAuditMarker"
Assert-Contains "completion finalizer gates pseudo gt closure" $completionFinalizer "pseudo-gt-review-closure"
Assert-Contains "completion finalizer requires strict completion audit" $completionFinalizer "completion-audit-complete"
Assert-Contains "completion finalizer allows documented quality failure" $completionFinalizer "AllowQualityGateFailure"
Assert-Contains "completion finalizer forwards state quality failure flag" $completionFinalizer "AllowFullGtQualityGateFailure"
Assert-Contains "completion finalizer writes complete evidence report" $completionFinalizer "goal-evidence-report-complete"
Assert-Contains "completion finalizer has selftest" $completionFinalizer "pass selftest completed fixture"
Assert-Contains "pseudo gt review closure requires completed status" $pseudoGtReviewClosure "Test-ReviewedStatus"
Assert-Contains "pseudo gt review closure requires evidence notes" $pseudoGtReviewClosure "matching row has no evidenceNotes"
Assert-Contains "pseudo gt review closure requires source id geometry" $pseudoGtReviewClosure "sourcePredictionId+iou"
Assert-Contains "pseudo gt review closure requires completed missed scan" $pseudoGtReviewClosure "missedFaceRowsAdded > 0"
Assert-Contains "pseudo gt review closure keeps pending status open" $pseudoGtReviewClosure "is not a completed review state"
Assert-Contains "pseudo gt separation verifier scans runtime source" $pseudoGtSeparationVerify "runtime source has no pseudo-GT references"
Assert-Contains "pseudo gt separation verifier protects postprocess pipeline" $pseudoGtSeparationVerify "postprocess pipeline does not know pseudo-GT"
Assert-Contains "pseudo gt separation verifier checks test-only scripts" $pseudoGtSeparationVerify "test-only evidence"
Assert-Contains "pseudo gt separation verifier checks face verification input script" $pseudoGtSeparationVerify "face verification input script records runtime separation"
Assert-Contains "pseudo gt separation verifier checks person object input script" $pseudoGtSeparationVerify "person/object input script records runtime separation"

Write-Host "[YoloGoalAuditVerify] all requested checks passed"
