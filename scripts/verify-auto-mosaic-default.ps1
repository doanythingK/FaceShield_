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
    [string]$LongAutoTuneClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$trackPostprocessVerify = Join-Path $repo "scripts\verify-face-track-postprocess.ps1"

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

Write-Host "[AutoMosaicVerify] all requested checks passed"
