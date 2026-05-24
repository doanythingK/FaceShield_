param(
    [string]$QualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
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

    Write-Host "[YoloExtendedExportGateVerify] pass $Name"
}

$arguments = @(
    "-SkipTrim",
    "-Source", $QualityClip,
    "-OptimizedCpuOnly",
    "-ParallelDetectorCount", "$ParallelDetectorCount",
    "-YoloModelPath", $resolvedYoloModelPath,
    "-YoloModelType", $YoloModelType,
    "-YoloInputSize", "$YoloInputSize",
    "-YoloObjectnessThreshold", "$YoloObjectnessThreshold",
    "-YoloConfidenceThreshold", "$YoloConfidenceThreshold",
    "-YoloNmsThreshold", "$YoloNmsThreshold",
    "-MinAvgIou", "$MinAvgIou",
    "-MinBestIou", "$MinBestIou"
)

Write-Host "[YoloExtendedExportGateVerify] start extended-export-gate"
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

if ($exitCode -ne 2) {
    throw "extended-export-gate expected exit code 2 but got $exitCode"
}

Assert-Match "baseline export summary" $text "\[ExportRunSummary\].*directFaceFrames=83"
Assert-Match "yolo export summary" $text "\[ExportRunSummary\].*directFaceFrames=81"
Assert-Match "export output logged" $text "\[Smoke\].*output="
Assert-Match "smoke quality gate failed" $text "\[SmokeQualityGate\]\s+passed=False"
Assert-Match "extended frame counts" $text "\[SmokeCompare\].*baselineFrames=83, optimizedFrames=81, common=70, onlyBaseline=13, onlyOptimized=11"
Assert-Match "extended box count mismatch" $text "\[SmokeCompare\].*boxCountDiffFrames=15"
Assert-Match "tracking lost fill extended" $text "\[SmokeFaceTrackPost\].*label=optimized-track-1-scale-1-cpu-yolo.*lostFilled=24.*lostFrames=6,7,8,9,10,11,671,672,673,674,675,676,674,675,676,677,678,679,706,707,708,709,710,711"
Assert-Match "yolo detector used" $text "detector=YoloFaceOnnxDetector"

Write-Host "[YoloExtendedExportGateVerify] all requested checks passed"
