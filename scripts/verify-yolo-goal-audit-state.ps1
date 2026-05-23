param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$AutoMosaicDefaultVerify = "scripts/verify-auto-mosaic-default.ps1",
    [string]$YoloStateVerify = "scripts/verify-yolo-state.ps1",
    [string]$YoloManualReadinessVerify = "scripts/verify-yolo-manual-readiness-state.ps1",
    [string]$YoloManualGateHelper = "scripts/open-yolo-manual-gates.ps1",
    [string]$YoloManualGateHelperVerify = "scripts/verify-yolo-manual-gate-helper-state.ps1"
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
$manualReadinessVerify = Read-RepoFile $YoloManualReadinessVerify
$manualGateHelper = Read-RepoFile $YoloManualGateHelper
$manualGateHelperVerify = Read-RepoFile $YoloManualGateHelperVerify

$marker = "yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; complete=false; remaining=full-gt-label,gui-smoke"
Assert-Contains "goal audit marker" $plan $marker

foreach ($token in @(
    "backend=integrated",
    "default=FaceONNX",
    "recommendation=none",
    "representative=pass",
    "extended=fail",
    "extended-export=fail",
    "sample-gt=pass",
    "full-gt-harness=pass",
    "license-source=pass",
    "manual-readiness=pass",
    "ten-minute-full=not-required-after-extended-fail",
    "complete=false",
    "full-gt-label",
    "gui-smoke")) {
    Assert-Contains "goal audit token $token" $plan $token
}

Assert-Contains "profile verifier recorded" $plan "verify-yolo-profile-state.ps1"
Assert-Contains "representative verifier recorded" $plan "verify-yolo-representative-gate.ps1"
Assert-Contains "representative pass recorded" $plan "SmokeQualityGate passed=True"
Assert-Contains "extended verifier recorded" $plan "verify-yolo-extended-gate.ps1"
Assert-Contains "extended export verifier recorded" $plan "verify-yolo-extended-export-gate.ps1"
Assert-Contains "ten minute runner recorded" $plan "run-yolo-ten-minute-full.ps1"
Assert-Contains "ten minute state verifier recorded" $plan "verify-yolo-ten-minute-state.ps1"
Assert-Contains "ten minute runner marker recorded" $plan "yolo-ten-minute-runner-state: prepared=true"
Assert-Contains "ten minute optimized full run recorded" $plan "full-run=yolo-optimized-only-pass"
Assert-Contains "ten minute output probe recorded" $plan "output-probe=pass-3840x2160-17980frames-599to601s"
Assert-Contains "ten minute baseline full attempt recorded" $plan "baseline-only-full=attempted-incomplete-slow"
Assert-Contains "ten minute baseline full incomplete progress recorded" $plan "baseline-only-full-progress=240frames-no-complete"
Assert-Contains "ten minute faceonnx optimized-only runner recorded" $plan "faceonnx-optimized-only-runner=short-smoke-pass"
Assert-Contains "ten minute partial speed compare recorded" $plan "partial-speed-compare=short-smoke-pass"
Assert-Contains "ten minute partial speed yolo total recorded" $plan "partial-yolo-totalMs=20720"
Assert-Contains "ten minute partial speed faceonnx total recorded" $plan "partial-faceonnx-totalMs=34039"
Assert-Contains "ten minute partial speed ratio recorded" $plan "partial-faceonnx-yolo-ratio=1.643"
Assert-Contains "ten minute auto total recorded" $plan "autoTotalMs=2536529"
Assert-Contains "ten minute export total recorded" $plan "exportTotalMs=1375350"
Assert-Contains "ten minute direct face frames recorded" $plan "directFaceFrames=8063"
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
Assert-Contains "full gt full-frame review smoke recorded" $plan "full-frame-review-smoke=pass-19-candidate-frames"
Assert-Contains "full gt full-frame overlay recorded" $plan "full-frame-overlay=pass"
Assert-Contains "full gt full-frame candidate summary recorded" $plan "full-frame-candidate-summary=pass"
Assert-Contains "full gt review index recorded" $plan "review-index=pass"
Assert-Contains "full gt reviewed gate recorded" $plan "reviewed-gate=selftest-pass"
Assert-Contains "full gt manual missed consistency recorded" $plan "manual-missed-consistency=pass"
Assert-Contains "full gt review artifact validation recorded" $plan "review-artifact-validation=pass"
Assert-Contains "full gt negative selftests recorded" $plan "negative-selftests=pass"
Assert-Contains "full gt ai reviewed candidate recorded" $plan "ai-reviewed-candidate=tp8-fp12-miss0-strict-fail"
Assert-Contains "full gt real reviewed gate recorded" $plan "real-reviewed-gate=requires-full-frame-review"
Assert-Contains "gui smoke harness marker recorded" $plan "yolo-gui-smoke-harness-state:"
Assert-Contains "gui smoke verifier recorded" $plan "verify-yolo-gui-smoke-state.ps1"
Assert-Contains "gui smoke checklist recorded" $plan "new-yolo-gui-smoke-checklist.ps1"
Assert-Contains "gui smoke download button source invariant recorded" $plan "download-button-source-invariant=pass"
Assert-Contains "gui smoke source invariant recorded" $plan "source-invariant=pass"
Assert-Contains "gui smoke manual evidence schema recorded" $plan "manual-evidence-schema=pass"
Assert-Contains "gui smoke manual evidence type validation recorded" $plan "manual-evidence-type-validation=pass"
Assert-Contains "gui smoke manual verifier selftest recorded" $plan "manual-verifier-selftest=pass"
Assert-Contains "gui smoke manual negative selftests recorded" $plan "manual-negative-selftests=pass"
Assert-Contains "gui smoke manual missing recorded" $plan "manual-checklist=missing"
Assert-Contains "gui smoke open video step recorded" $plan "open-video"
Assert-Contains "gui smoke download model step recorded" $plan "download-yolo-model"
Assert-Contains "gui smoke export step recorded" $plan "export"
Assert-Contains "manual readiness marker recorded" $plan "yolo-manual-readiness-state:"
Assert-Contains "manual readiness verifier recorded" $plan "verify-yolo-manual-readiness-state.ps1"
Assert-Contains "manual readiness full gt ready recorded" $plan "full-gt-review-package=ready-pending-human-labels"
Assert-Contains "manual readiness ai candidate ready recorded" $plan "ai-reviewed-candidate=ready-not-final-gt"
Assert-Contains "manual readiness gui checklist ready recorded" $plan "gui-checklist=ready-pending-human-smoke"
Assert-Contains "manual readiness completed mode recorded" $plan "completed-mode=AllowCompletedFullGt+AllowCompletedGuiSmoke"
Assert-Contains "manual readiness completed full gt gate recorded" $plan "completed-full-gt-reviewed-gate=RequireFullFrameReview+RequireArtifacts+RequireEvidence"
Assert-Contains "manual readiness ten minute ready recorded" $plan "ten-minute-artifacts=ready-yolo-output-and-incomplete-faceonnx-baseline"
Assert-Contains "license source marker recorded" $plan "yolo-license-source-state: checked=2026-05-23"
Assert-Contains "license source gate recorded" $plan "source-gate=pass"
Assert-Contains "license bundle marker recorded" $plan "bundle=blocked"
Assert-Contains "license source pass recorded" $plan "license-source=pass"
Assert-Contains "extended fail recorded" $plan "SmokeQualityGate passed=False"
Assert-Contains "extended baseline frames recorded" $plan "baselineFrames=83"
Assert-Contains "extended optimized frames recorded" $plan "optimizedFrames=74"
Assert-Contains "extended only baseline recorded" $plan "onlyBaseline=14"
Assert-Contains "extended export baseline direct frames recorded" $plan "directFaceFrames=83"
Assert-Contains "extended export yolo direct frames recorded" $plan "directFaceFrames=74"
Assert-Contains "no final recommendation recorded" $plan "recommendation=none"
Assert-Contains "full gt label still missing" $plan "full-gt-label"
Assert-Contains "gui smoke still missing" $plan "gui-smoke"
Assert-Contains "ten minute full not required after extended fail" $plan "ten-minute-full=not-required-after-extended-fail"
Assert-Contains "ten minute full A/B deferred explanation" $plan "다음 추천 후보가 확장 gate를 통과할 때 다시 수행"

Assert-Contains "auto verifier exposes yolo state" $autoVerify "RunYoloState"
Assert-Contains "auto verifier exposes representative gate" $autoVerify "RunYoloRepresentativeGate"
Assert-Contains "auto verifier exposes extended gate" $autoVerify "RunYoloExtendedGate"
Assert-Contains "auto verifier exposes extended export gate" $autoVerify "RunYoloExtendedExportGate"
Assert-Contains "auto verifier exposes ten minute state" $autoVerify "RunYoloTenMinuteState"
Assert-Contains "auto verifier exposes ten minute clip requirement" $autoVerify "RequireYoloTenMinuteClip"
Assert-Contains "auto verifier exposes ten minute run requirement" $autoVerify "RequireYoloTenMinuteRun"
Assert-Contains "auto verifier exposes ten minute baseline-only requirement" $autoVerify "RequireYoloTenMinuteBaselineOnlyRun"
Assert-Contains "auto verifier exposes ten minute incomplete baseline full requirement" $autoVerify "RequireYoloTenMinuteIncompleteBaselineFullAttempt"
Assert-Contains "auto verifier exposes ten minute faceonnx optimized-only requirement" $autoVerify "RequireYoloTenMinuteFaceOnnxOptimizedOnlyRun"
Assert-Contains "auto verifier exposes ten minute partial speed compare requirement" $autoVerify "RequireYoloTenMinutePartialSpeedCompareRun"
Assert-Contains "auto verifier exposes gui smoke state" $autoVerify "RunYoloGuiSmokeState"
Assert-Contains "auto verifier exposes gui smoke manual pass" $autoVerify "RequireYoloGuiSmokeManualPass"
Assert-Contains "auto verifier exposes manual readiness state" $autoVerify "RunYoloManualReadinessState"
Assert-Contains "auto verifier allows completed full gt" $autoVerify "AllowCompletedYoloFullGt"
Assert-Contains "auto verifier allows completed gui smoke" $autoVerify "AllowCompletedYoloGuiSmoke"
Assert-Contains "auto verifier exposes full gt prediction log" $autoVerify "YoloFullGtPredictionLog"
Assert-Contains "auto verifier exposes full gt quality limits" $autoVerify "YoloFullGtMaxFalsePositives"
Assert-Contains "auto verifier exposes full gt candidate state" $autoVerify "RunYoloFullGtReviewedCandidateState"
Assert-Contains "yolo state exposes representative gate" $yoloState "RunRepresentativeGate"
Assert-Contains "yolo state exposes extended gate" $yoloState "RunExtendedGate"
Assert-Contains "yolo state exposes extended export gate" $yoloState "RunExtendedExportGate"
Assert-Contains "yolo state exposes ten minute state" $yoloState "RunTenMinuteState"
Assert-Contains "yolo state exposes ten minute clip requirement" $yoloState "RequireTenMinuteClip"
Assert-Contains "yolo state exposes ten minute run requirement" $yoloState "RequireTenMinuteRun"
Assert-Contains "yolo state exposes ten minute baseline-only requirement" $yoloState "RequireTenMinuteBaselineOnlyRun"
Assert-Contains "yolo state exposes ten minute incomplete baseline full requirement" $yoloState "RequireTenMinuteIncompleteBaselineFullAttempt"
Assert-Contains "yolo state exposes ten minute faceonnx optimized-only requirement" $yoloState "RequireTenMinuteFaceOnnxOptimizedOnlyRun"
Assert-Contains "yolo state exposes ten minute partial speed compare requirement" $yoloState "RequireTenMinutePartialSpeedCompareRun"
Assert-Contains "yolo state exposes gui smoke state" $yoloState "RunGuiSmokeState"
Assert-Contains "yolo state exposes gui smoke manual pass" $yoloState "RequireGuiSmokeManualPass"
Assert-Contains "yolo state allows completed full gt" $yoloState "AllowCompletedFullGt"
Assert-Contains "yolo state allows completed gui smoke" $yoloState "AllowCompletedGuiSmoke"
Assert-Contains "yolo state exposes full gt prediction log" $yoloState "FullGtPredictionLog"
Assert-Contains "yolo state exposes full gt quality limits" $yoloState "FullGtMaxFalsePositives"
Assert-Contains "yolo state runs manual readiness state" $yoloState "manual-readiness-state"
Assert-Contains "yolo state runs manual gate helper state" $yoloState "manual-gate-helper-state"
Assert-Contains "yolo state runs full gt candidate state" $yoloState "full-gt-reviewed-candidate-state"
Assert-Contains "manual readiness invokes reviewed full gt verifier" $manualReadinessVerify "verify-yolo-full-gt-reviewed-state.ps1"
Assert-Contains "manual readiness requires full frame review" $manualReadinessVerify "RequireFullFrameReview"
Assert-Contains "manual readiness requires full gt artifacts" $manualReadinessVerify "RequireArtifacts"
Assert-Contains "manual gate helper opens review index" $manualGateHelper "ReviewIndex"
Assert-Contains "manual gate helper verifies ready state" $manualGateHelper "VerifyReady"
Assert-Contains "manual gate helper verifies completed state" $manualGateHelper "VerifyCompleted"
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

Write-Host "[YoloGoalAuditVerify] all requested checks passed"
