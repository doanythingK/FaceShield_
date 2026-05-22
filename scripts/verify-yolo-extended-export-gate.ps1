param(
    [string]$QualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$YoloModelPath = ".tmp/models/YoloV5Face.onnx",
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
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$resolvedQualityClip = if ([IO.Path]::IsPathRooted($QualityClip)) { $QualityClip } else { Join-Path $repo $QualityClip }
$resolvedYoloModelPath = if ([IO.Path]::IsPathRooted($YoloModelPath)) { $YoloModelPath } else { Join-Path $repo $YoloModelPath }

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
    "-YoloModelPath", $YoloModelPath,
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
Assert-Match "yolo export summary" $text "\[ExportRunSummary\].*directFaceFrames=74"
Assert-Match "export output logged" $text "\[Smoke\].*output="
Assert-Match "smoke quality gate failed" $text "\[SmokeQualityGate\]\s+passed=False"
Assert-Match "extended frame counts" $text "\[SmokeCompare\].*baselineFrames=83, optimizedFrames=74, common=69, onlyBaseline=14, onlyOptimized=5"
Assert-Match "extended box count mismatch" $text "\[SmokeCompare\].*boxCountDiffFrames=14"
Assert-Match "yolo detector used" $text "detector=YoloFaceOnnxDetector"

Write-Host "[YoloExtendedExportGateVerify] all requested checks passed"
