param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$TrimStart,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 30)]
    [int]$TrimSeconds,
    [string]$OutputDir = "",
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string]$YoloModelType = "Yolo5Face",
    [string]$YoloModelPath = "",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [int]$ParallelDetectorCount = 2,
    [int]$MaxFullFrameRows = 24,
    [switch]$AllowNoDetections,
    [switch]$WithReviewPackage,
    [switch]$WithDetectionOverlayVideo,
    [switch]$WithReviewContactSheet,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$evidenceScript = Join-Path $PSScriptRoot "write-yolo-followup-quality-evidence.ps1"
if (-not (Test-Path $evidenceScript)) {
    throw "Evidence wrapper not found: $evidenceScript"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $safeStart = $TrimStart -replace '[^0-9A-Za-z]+', '-'
    $OutputDir = ".tmp/yolo-problem-span-$safeStart-${TrimSeconds}s"
}

$argsList = [System.Collections.Generic.List[string]]::new()
$argsList.Add("-RunSmoke") | Out-Null
$argsList.Add("-Source") | Out-Null
$argsList.Add($Source) | Out-Null
$argsList.Add("-TrimStart") | Out-Null
$argsList.Add($TrimStart) | Out-Null
$argsList.Add("-TrimSeconds") | Out-Null
$argsList.Add($TrimSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-OutputDir") | Out-Null
$argsList.Add($OutputDir) | Out-Null
$argsList.Add("-YoloModelType") | Out-Null
$argsList.Add($YoloModelType) | Out-Null
$argsList.Add("-YoloInputSize") | Out-Null
$argsList.Add($YoloInputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-YoloObjectnessThreshold") | Out-Null
$argsList.Add($YoloObjectnessThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-YoloConfidenceThreshold") | Out-Null
$argsList.Add($YoloConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-YoloNmsThreshold") | Out-Null
$argsList.Add($YoloNmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-ParallelDetectorCount") | Out-Null
$argsList.Add($ParallelDetectorCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-MaxFullFrameRows") | Out-Null
$argsList.Add($MaxFullFrameRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null

if ($AllowNoDetections.IsPresent) {
    $argsList.Add("-AllowNoDetections") | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($YoloModelPath)) {
    $argsList.Add("-YoloModelPath") | Out-Null
    $argsList.Add($YoloModelPath) | Out-Null
}

if (-not $WithReviewPackage.IsPresent) {
    $argsList.Add("-SkipReviewPackage") | Out-Null
}

if ($WithDetectionOverlayVideo.IsPresent) {
    $argsList.Add("-WithDetectionOverlayVideo") | Out-Null
}

if ($WithReviewContactSheet.IsPresent) {
    $argsList.Add("-WithReviewContactSheet") | Out-Null
}

if ($Force.IsPresent) {
    $argsList.Add("-ForceTrim") | Out-Null
    $argsList.Add("-ForceRunSmoke") | Out-Null
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $evidenceScript @argsList
if ($LASTEXITCODE -ne 0) {
    throw "YOLO problem-span verification failed."
}

$summaryPath = Join-Path $repo (Join-Path $OutputDir "yolo-followup-quality-evidence.md")
Write-Host "[YoloProblemSpanVerification] output=$OutputDir"
Write-Host "[YoloProblemSpanVerification] summary=$summaryPath"
