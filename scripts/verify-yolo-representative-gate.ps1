param(
    [string]$QualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$YoloModelPath = "",
    [string]$YoloModelType = "Yolo5Face",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [double]$MinAvgIou = 0.90,
    [double]$MinBestIou = 0.75,
    [int]$ParallelDetectorCount = 2
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "resolve-yolo-model-path.ps1")
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$resolvedQualityClip = if ([IO.Path]::IsPathRooted($QualityClip)) { $QualityClip } else { Join-Path $repo $QualityClip }
$resolvedYoloModelPath = Resolve-YoloModelPath -Repo $repo -YoloModelPath $YoloModelPath -YoloModelType $YoloModelType -Require

if (-not (Test-Path $smoke)) {
    throw "Smoke script not found: $smoke"
}

if (-not (Test-Path $resolvedQualityClip)) {
    throw "Quality clip not found: $resolvedQualityClip"
}

if (-not (Test-Path $resolvedYoloModelPath)) {
    throw "YOLO model not found: $resolvedYoloModelPath"
}

function Assert-Match {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }

    Write-Host "[YoloRepresentativeGateVerify] pass $Name"
}

$arguments = @(
    "-SkipTrim",
    "-Source", $QualityClip,
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-ProcessingMode", "Legacy",
    "-ParallelDetectorCount", "$ParallelDetectorCount",
    "-YoloModelPath", $resolvedYoloModelPath,
    "-YoloModelType", $YoloModelType,
    "-YoloInputSize", "$YoloInputSize",
    "-YoloObjectnessThreshold", "$YoloObjectnessThreshold",
    "-YoloConfidenceThreshold", "$YoloConfidenceThreshold",
    "-YoloNmsThreshold", "$YoloNmsThreshold",
    "-MinAvgIou", "$MinAvgIou",
    "-MinBestIou", "$MinBestIou",
    "-AllowFrameMismatch"
)

Write-Host "[YoloRepresentativeGateVerify] start representative-gate"
$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smoke @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

if ($exitCode -ne 0) {
    throw "representative-gate failed with exit code $exitCode"
}

Assert-Match "smoke quality gate passed" $text "\[SmokeQualityGate\]\s+passed=True"
Assert-Match "tracking frame counts" $text "\[SmokeCompare\].*baselineFrames=19, optimizedFrames=20, common=19, onlyBaseline=0, onlyOptimized=1"
Assert-Match "tracking extra frame" $text "\[SmokeCompareFrames\].*onlyBaseline=none, onlyOptimized=9"
Assert-Match "tracking iou preserved" $text "\[SmokeCompare\].*avgBestIou=0\.971, minBestIou=0\.944"
Assert-Match "tracking lost fill extended" $text "\[SmokeFaceTrackPost\].*label=optimized-track-1-scale-1-cpu-yolo.*lostFilled=6.*lostFrames=6,7,8,9,10,11"
Assert-Match "yolo detector used" $text "detector=YoloFaceOnnxDetector"
Assert-Match "parallel optimized path used" $text "mode=pipe-parallel"

Write-Host "[YoloRepresentativeGateVerify] all requested checks passed"
