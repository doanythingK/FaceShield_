param(
    [string]$QualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$AutoTuneClip = ".tmp/srcTest-smoke/smoke-0600-5s.mp4",
    [string]$RoiHitClip = ".tmp/srcTest-smoke/smoke-0900-2s.mp4",
    [switch]$RunExportSmoke,
    [string]$ExportClip = ".tmp/srcTest-smoke/smoke-1200-2s.mp4",
    [switch]$RunMediumAuto,
    [string]$MediumAutoClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunMediumExport,
    [string]$MediumExportClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunLongAutoTune,
    [string]$LongAutoTuneClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunYoloState,
    [switch]$RunYoloRepresentativeGate,
    [switch]$RunYoloExtendedGate,
    [switch]$RunYoloExtendedExportGate,
    [switch]$RunYoloTenMinuteState,
    [switch]$RequireYoloTenMinuteClip,
    [switch]$RequireYoloTenMinuteRun,
    [switch]$RequireYoloTenMinuteBaselineOnlyRun,
    [switch]$RequireYoloTenMinuteIncompleteBaselineFullAttempt,
    [switch]$RequireYoloTenMinuteFaceOnnxOptimizedOnlyRun,
    [switch]$RequireYoloTenMinutePartialSpeedCompareRun,
    [switch]$RunYoloGuiSmokeState,
    [switch]$RequireYoloGuiSmokeManualPass,
    [switch]$RunYoloManualReadinessState,
    [switch]$AllowCompletedYoloFullGt,
    [switch]$AllowCompletedYoloGuiSmoke,
    [string]$YoloFullGtPredictionCsv = "",
    [string]$YoloFullGtPredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$YoloFullGtMinIou = 0.50,
    [int]$YoloFullGtMaxMisses = 0,
    [int]$YoloFullGtMaxFalsePositives = 0,
    [int]$YoloFullGtMaxLowIou = 0,
    [switch]$RunYoloProfileState,
    [switch]$RunYoloConclusionState,
    [switch]$RunYoloDistributionState,
    [switch]$RunYoloCropReview,
    [switch]$RunYoloFullGtReviewedCandidateState,
    [string]$YoloCropReviewPassCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$YoloCropReviewFailCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv",
    [string]$YoloRepresentativeQualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$YoloRepresentativeModelPath = "",
    [string]$YoloExtendedQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$YoloExtendedModelPath = "",
    [string]$YoloExtendedExportQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$YoloExtendedExportModelPath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$trackPostprocessVerify = Join-Path $repo "scripts\verify-face-track-postprocess.ps1"
$yoloStateVerify = Join-Path $repo "scripts\verify-yolo-state.ps1"
$yoloProfileStateVerify = Join-Path $repo "scripts\verify-yolo-profile-state.ps1"
$yoloConclusionStateVerify = Join-Path $repo "scripts\verify-yolo-conclusion-state.ps1"
$yoloDistributionStateVerify = Join-Path $repo "scripts\verify-yolo-distribution-state.ps1"
$yoloCropReviewVerify = Join-Path $repo "scripts\verify-yolo-crop-review.ps1"
$yoloFullGtReviewedCandidateStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-candidate-state.ps1"
$yoloRepresentativeGateVerify = Join-Path $repo "scripts\verify-yolo-representative-gate.ps1"
$yoloExtendedGateVerify = Join-Path $repo "scripts\verify-yolo-extended-gate.ps1"
$yoloExtendedExportGateVerify = Join-Path $repo "scripts\verify-yolo-extended-export-gate.ps1"
$yoloTenMinuteStateVerify = Join-Path $repo "scripts\verify-yolo-ten-minute-state.ps1"
$yoloGuiSmokeStateVerify = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$yoloManualReadinessStateVerify = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"

function Invoke-ScriptStep([string]$Name, [string]$ScriptPath, [string[]]$Arguments) {
    Write-Host "[AutoMosaicVerify] start $Name"
    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
        if ($exitCode -ne 0) {
            throw "$Name failed with exit code $exitCode"
        }
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    Write-Host "[AutoMosaicVerify] pass $Name"
    return $text
}

function Invoke-Step([string]$Name, [string[]]$Arguments) {
    return Invoke-ScriptStep $Name $smoke $Arguments
}

function Assert-Contains([string]$Name, [string]$Text, [string]$Pattern) {
    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }
}

if (-not (Test-Path (Join-Path $repo $QualityClip))) {
    throw "Quality clip not found: $QualityClip"
}

if (-not (Test-Path (Join-Path $repo $AutoTuneClip))) {
    throw "Auto-tune clip not found: $AutoTuneClip"
}

if (-not (Test-Path (Join-Path $repo $RoiHitClip))) {
    throw "ROI-hit clip not found: $RoiHitClip"
}

if ($RunExportSmoke -and -not (Test-Path (Join-Path $repo $ExportClip))) {
    throw "Export clip not found: $ExportClip"
}

if ($RunMediumAuto -and -not (Test-Path (Join-Path $repo $MediumAutoClip))) {
    throw "Medium auto clip not found: $MediumAutoClip"
}

if ($RunMediumExport -and -not (Test-Path (Join-Path $repo $MediumExportClip))) {
    throw "Medium export clip not found: $MediumExportClip"
}

if ($RunYoloCropReview -and -not (Test-Path $yoloCropReviewVerify)) {
    throw "YOLO crop review verifier not found: $yoloCropReviewVerify"
}

if ($RunYoloState -and -not (Test-Path $yoloStateVerify)) {
    throw "YOLO state verifier not found: $yoloStateVerify"
}

if ($RunYoloProfileState -and -not (Test-Path $yoloProfileStateVerify)) {
    throw "YOLO profile state verifier not found: $yoloProfileStateVerify"
}

if ($RunYoloConclusionState -and -not (Test-Path $yoloConclusionStateVerify)) {
    throw "YOLO conclusion state verifier not found: $yoloConclusionStateVerify"
}

if ($RunYoloDistributionState -and -not (Test-Path $yoloDistributionStateVerify)) {
    throw "YOLO distribution state verifier not found: $yoloDistributionStateVerify"
}

if ($RunYoloRepresentativeGate -and -not (Test-Path $yoloRepresentativeGateVerify)) {
    throw "YOLO representative gate verifier not found: $yoloRepresentativeGateVerify"
}

if ($RunYoloExtendedGate -and -not (Test-Path $yoloExtendedGateVerify)) {
    throw "YOLO extended gate verifier not found: $yoloExtendedGateVerify"
}

if ($RunYoloExtendedExportGate -and -not (Test-Path $yoloExtendedExportGateVerify)) {
    throw "YOLO extended export gate verifier not found: $yoloExtendedExportGateVerify"
}

if ($RunYoloTenMinuteState -and -not (Test-Path $yoloTenMinuteStateVerify)) {
    throw "YOLO ten-minute state verifier not found: $yoloTenMinuteStateVerify"
}

if ($RunYoloGuiSmokeState -and -not (Test-Path $yoloGuiSmokeStateVerify)) {
    throw "YOLO GUI smoke state verifier not found: $yoloGuiSmokeStateVerify"
}

if ($RunYoloManualReadinessState -and -not (Test-Path $yoloManualReadinessStateVerify)) {
    throw "YOLO manual readiness state verifier not found: $yoloManualReadinessStateVerify"
}

if ($RunYoloFullGtReviewedCandidateState -and -not (Test-Path $yoloFullGtReviewedCandidateStateVerify)) {
    throw "YOLO full GT reviewed candidate state verifier not found: $yoloFullGtReviewedCandidateStateVerify"
}

$trackOutput = Invoke-ScriptStep "track-postprocess-policy" $trackPostprocessVerify @()
Assert-Contains "track-postprocess-policy" $trackOutput "\[FaceTrackPostVerify\]"
Assert-Contains "track-postprocess-policy" $trackOutput "gapFrames=11"
Assert-Contains "track-postprocess-policy" $trackOutput "lostFrames=33,34,35"
Assert-Contains "track-postprocess-policy" $trackOutput "filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52"

$qualityOutput = Invoke-Step "quality-gate-all-frame-parallel" @(
    "-SkipTrim",
    "-Source", $QualityClip,
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-OptimizedNoTracking",
    "-ParallelDetectorCount", "2",
    "-MinAvgIou", "0.99",
    "-MinBestIou", "0.99"
)
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "\[SmokeQualityGate\] passed=True"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "avgBestIou=1\.000"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "minBestIou=1\.000"

$roiHitOutput = Invoke-Step "roi-refiner-hit-representative" @(
    "-SkipTrim",
    "-Source", $RoiHitClip,
    "-SkipBaseline",
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-OptimizedNoTracking",
    "-ParallelDetectorCount", "2"
)
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*attempts=1[0-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*seeks=[1-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*decoded=[1-9][0-9]*"

if ($RunExportSmoke) {
    $exportOutput = Invoke-Step "direct-face-export-smoke" @(
        "-SkipTrim",
        "-Source", $ExportClip,
        "-SkipBaseline",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[ExportRunSummary\].*bitmapMaskFrames=0"
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[ExportRunSummary\].*directFaceFrames=[1-9][0-9]*"
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[Smoke\].*output="
}

if ($RunMediumAuto) {
    $mediumOutput = Invoke-Step "medium-auto-track-roi" @(
        "-SkipTrim",
        "-Source", $MediumAutoClip,
        "-SkipBaseline",
        "-SkipExport",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "medium-auto-track-roi" $mediumOutput "processed=899"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "detects=899"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "interpolated=0"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*filled=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*lostFilled=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*removedShort=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*attempts=32"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*seeks=[1-9]"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*decoded=[1-9][0-9]*"
}

if ($RunMediumExport) {
    $mediumExportOutput = Invoke-Step "medium-auto-export" @(
        "-SkipTrim",
        "-Source", $MediumExportClip,
        "-SkipBaseline",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "medium-auto-export" $mediumExportOutput "processed=899"
    Assert-Contains "medium-auto-export" $mediumExportOutput "detects=899"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackPost\].*filled=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackPost\].*lostFilled=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackRoiRefine\].*attempts=32"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*frames=902"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*bitmapMaskFrames=0"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*directFaceFrames=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[Smoke\].*output="
}

$shortTuneOutput = Invoke-Step "default-autotune-provider-short" @(
    "-SkipTrim",
    "-Source", $AutoTuneClip,
    "-SkipBaseline",
    "-SkipExport",
    "-UseAutoTune"
)
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "\[SmokeTune\].*tuned="
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "detector=FaceOnnxDetector/(CPU|GPU:DirectML)"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "mode=pipe-parallel"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "detects=150"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "interpolated=0"

if ($RunLongAutoTune) {
    if (-not (Test-Path (Join-Path $repo $LongAutoTuneClip))) {
        throw "Long auto-tune clip not found: $LongAutoTuneClip"
    }

    $longTuneOutput = Invoke-Step "default-autotune-provider-long" @(
        "-SkipTrim",
        "-Source", $LongAutoTuneClip,
        "-SkipBaseline",
        "-SkipExport",
        "-UseAutoTune"
    )
    Assert-Contains "default-autotune-provider-long" $longTuneOutput "\[SmokeTune\].*tuned="
    Assert-Contains "default-autotune-provider-long" $longTuneOutput "detector=FaceOnnxDetector/(CPU|GPU:DirectML)"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "mode=pipe-parallel"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "detects=899"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "interpolated=0"
}

if ($RunYoloCropReview) {
    $yoloReviewOutput = Invoke-ScriptStep "yolo-crop-review" $yoloCropReviewVerify @(
        "-PassReviewCsv", $YoloCropReviewPassCsv,
        "-FailReviewCsv", $YoloCropReviewFailCsv
    )
    Assert-Contains "yolo-crop-review" $yoloReviewOutput "\[YoloCropReviewVerify\] all requested checks passed"
}

if ($RunYoloFullGtReviewedCandidateState -and -not $RunYoloState) {
    $candidateOutput = Invoke-ScriptStep "yolo-full-gt-reviewed-candidate-state" $yoloFullGtReviewedCandidateStateVerify @()
    Assert-Contains "yolo-full-gt-reviewed-candidate-state" $candidateOutput "\[YoloFullGtReviewedCandidateVerify\] all requested checks passed"
}

if ($RunYoloState) {
    $yoloStateArgs = @(
        "-YoloCropReviewPassCsv", $YoloCropReviewPassCsv,
        "-YoloCropReviewFailCsv", $YoloCropReviewFailCsv,
        "-RepresentativeQualityClip", $YoloRepresentativeQualityClip,
        "-RepresentativeYoloModelPath", $YoloRepresentativeModelPath,
        "-ExtendedQualityClip", $YoloExtendedQualityClip,
        "-ExtendedYoloModelPath", $YoloExtendedModelPath,
        "-ExtendedExportQualityClip", $YoloExtendedExportQualityClip,
        "-ExtendedExportYoloModelPath", $YoloExtendedExportModelPath
    )
    if ($RunYoloRepresentativeGate) {
        $yoloStateArgs += "-RunRepresentativeGate"
    }
    if ($RunYoloExtendedGate) {
        $yoloStateArgs += "-RunExtendedGate"
    }
    if ($RunYoloExtendedExportGate) {
        $yoloStateArgs += "-RunExtendedExportGate"
    }
    if ($RunYoloTenMinuteState) {
        $yoloStateArgs += "-RunTenMinuteState"
    }
    if ($RequireYoloTenMinuteClip) {
        $yoloStateArgs += "-RequireTenMinuteClip"
    }
    if ($RequireYoloTenMinuteRun) {
        $yoloStateArgs += "-RequireTenMinuteRun"
    }
    if ($RequireYoloTenMinuteBaselineOnlyRun) {
        $yoloStateArgs += "-RequireTenMinuteBaselineOnlyRun"
    }
    if ($RequireYoloTenMinuteIncompleteBaselineFullAttempt) {
        $yoloStateArgs += "-RequireTenMinuteIncompleteBaselineFullAttempt"
    }
    if ($RequireYoloTenMinuteFaceOnnxOptimizedOnlyRun) {
        $yoloStateArgs += "-RequireTenMinuteFaceOnnxOptimizedOnlyRun"
    }
    if ($RequireYoloTenMinutePartialSpeedCompareRun) {
        $yoloStateArgs += "-RequireTenMinutePartialSpeedCompareRun"
    }
    if ($RunYoloGuiSmokeState) {
        $yoloStateArgs += "-RunGuiSmokeState"
    }
    if ($RequireYoloGuiSmokeManualPass) {
        $yoloStateArgs += "-RequireGuiSmokeManualPass"
    }
    if ($AllowCompletedYoloFullGt) {
        $yoloStateArgs += "-AllowCompletedFullGt"
    }
    if ($AllowCompletedYoloGuiSmoke) {
        $yoloStateArgs += "-AllowCompletedGuiSmoke"
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloFullGtPredictionCsv)) {
        $yoloStateArgs += @("-FullGtPredictionCsv", $YoloFullGtPredictionCsv)
    }
    else {
        $yoloStateArgs += @("-FullGtPredictionLog", $YoloFullGtPredictionLog)
    }
    $yoloStateArgs += @(
        "-FullGtMinIou", "$YoloFullGtMinIou",
        "-FullGtMaxMisses", "$YoloFullGtMaxMisses",
        "-FullGtMaxFalsePositives", "$YoloFullGtMaxFalsePositives",
        "-FullGtMaxLowIou", "$YoloFullGtMaxLowIou"
    )

    $yoloStateOutput = Invoke-ScriptStep "yolo-state" $yoloStateVerify $yoloStateArgs
    Assert-Contains "yolo-state" $yoloStateOutput "\[YoloStateVerify\] all requested checks passed"
}

if ($RunYoloProfileState) {
    $yoloProfileOutput = Invoke-ScriptStep "yolo-profile-state" $yoloProfileStateVerify @()
    Assert-Contains "yolo-profile-state" $yoloProfileOutput "\[YoloProfileVerify\] all requested checks passed"
}

if ($RunYoloConclusionState) {
    $yoloConclusionOutput = Invoke-ScriptStep "yolo-conclusion-state" $yoloConclusionStateVerify @()
    Assert-Contains "yolo-conclusion-state" $yoloConclusionOutput "\[YoloConclusionVerify\] all requested checks passed"
}

if ($RunYoloDistributionState) {
    $yoloDistributionOutput = Invoke-ScriptStep "yolo-distribution-state" $yoloDistributionStateVerify @()
    Assert-Contains "yolo-distribution-state" $yoloDistributionOutput "\[YoloDistributionVerify\] all requested checks passed"
}

if ($RunYoloRepresentativeGate -and -not $RunYoloState) {
    $yoloRepresentativeOutput = Invoke-ScriptStep "yolo-representative-gate" $yoloRepresentativeGateVerify @(
        "-QualityClip", $YoloRepresentativeQualityClip,
        "-YoloModelPath", $YoloRepresentativeModelPath
    )
    Assert-Contains "yolo-representative-gate" $yoloRepresentativeOutput "\[YoloRepresentativeGateVerify\] all requested checks passed"
}

if ($RunYoloExtendedGate -and -not $RunYoloState) {
    $yoloExtendedOutput = Invoke-ScriptStep "yolo-extended-gate" $yoloExtendedGateVerify @(
        "-QualityClip", $YoloExtendedQualityClip,
        "-YoloModelPath", $YoloExtendedModelPath
    )
    Assert-Contains "yolo-extended-gate" $yoloExtendedOutput "\[YoloExtendedGateVerify\] all requested checks passed"
}

if ($RunYoloExtendedExportGate -and -not $RunYoloState) {
    $yoloExtendedExportOutput = Invoke-ScriptStep "yolo-extended-export-gate" $yoloExtendedExportGateVerify @(
        "-QualityClip", $YoloExtendedExportQualityClip,
        "-YoloModelPath", $YoloExtendedExportModelPath
    )
    Assert-Contains "yolo-extended-export-gate" $yoloExtendedExportOutput "\[YoloExtendedExportGateVerify\] all requested checks passed"
}

if ($RunYoloTenMinuteState -and -not $RunYoloState) {
    $tenMinuteArgs = @()
    if ($RequireYoloTenMinuteClip) {
        $tenMinuteArgs += "-RequireClip"
    }
    if ($RequireYoloTenMinuteRun) {
        $tenMinuteArgs += "-RequireRun"
    }
    if ($RequireYoloTenMinuteBaselineOnlyRun) {
        $tenMinuteArgs += "-RequireBaselineOnlyRun"
    }
    if ($RequireYoloTenMinuteIncompleteBaselineFullAttempt) {
        $tenMinuteArgs += "-RequireIncompleteBaselineFullAttempt"
    }
    if ($RequireYoloTenMinuteFaceOnnxOptimizedOnlyRun) {
        $tenMinuteArgs += "-RequireFaceOnnxOptimizedOnlyRun"
    }
    if ($RequireYoloTenMinutePartialSpeedCompareRun) {
        $tenMinuteArgs += "-RequirePartialSpeedCompareRun"
    }

    $yoloTenMinuteOutput = Invoke-ScriptStep "yolo-ten-minute-state" $yoloTenMinuteStateVerify $tenMinuteArgs
    Assert-Contains "yolo-ten-minute-state" $yoloTenMinuteOutput "\[YoloTenMinuteStateVerify\] all requested checks passed"
}

if ($RunYoloGuiSmokeState -and -not $RunYoloState) {
    $guiSmokeArgs = @()
    if ($RequireYoloGuiSmokeManualPass) {
        $guiSmokeArgs += "-RequireManualPass"
    }

    $yoloGuiSmokeOutput = Invoke-ScriptStep "yolo-gui-smoke-state" $yoloGuiSmokeStateVerify $guiSmokeArgs
    Assert-Contains "yolo-gui-smoke-state" $yoloGuiSmokeOutput "\[YoloGuiSmokeVerify\] all requested checks passed"
}

if ($RunYoloManualReadinessState -and -not $RunYoloState) {
    $manualReadinessArgs = @()
    if ($AllowCompletedYoloFullGt) {
        $manualReadinessArgs += "-AllowCompletedFullGt"
    }
    if ($AllowCompletedYoloGuiSmoke) {
        $manualReadinessArgs += "-AllowCompletedGuiSmoke"
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloFullGtPredictionCsv)) {
        $manualReadinessArgs += @("-FullGtPredictionCsv", $YoloFullGtPredictionCsv)
    }
    else {
        $manualReadinessArgs += @("-FullGtPredictionLog", $YoloFullGtPredictionLog)
    }
    $manualReadinessArgs += @(
        "-FullGtMinIou", "$YoloFullGtMinIou",
        "-FullGtMaxMisses", "$YoloFullGtMaxMisses",
        "-FullGtMaxFalsePositives", "$YoloFullGtMaxFalsePositives",
        "-FullGtMaxLowIou", "$YoloFullGtMaxLowIou"
    )

    $yoloManualReadinessOutput = Invoke-ScriptStep "yolo-manual-readiness-state" $yoloManualReadinessStateVerify $manualReadinessArgs
    Assert-Contains "yolo-manual-readiness-state" $yoloManualReadinessOutput "\[YoloManualReadinessVerify\] all requested checks passed"
}

Write-Host "[AutoMosaicVerify] all requested checks passed"
