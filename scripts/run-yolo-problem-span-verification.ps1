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
    [string]$PseudoGtTileFaceCsv = "",
    [string]$PseudoGtFaceVerificationCsv = "",
    [string]$PseudoGtPersonObjectCsv = "",
    [switch]$AllowNoDetections,
    [switch]$WithReviewPackage,
    [switch]$WithDetectionOverlayVideo,
    [switch]$WithReviewContactSheet,
    [switch]$WithPseudoGtTileInput,
    [switch]$PseudoGtTileSkipImageExtraction,
    [int]$PseudoGtMaxFrames = 900,
    [int]$PseudoGtTileColumns = 3,
    [int]$PseudoGtTileRows = 3,
    [double]$PseudoGtTileOverlapRatio = 0.25,
    [switch]$WithPseudoGtFaceVerificationInput,
    [switch]$PseudoGtFaceVerificationSkipImageExtraction,
    [double]$PseudoGtFaceVerificationCropPaddingRatio = 0.35,
    [string]$PseudoGtFaceVerificationExternalCommand = "",
    [string]$PseudoGtFaceVerificationExternalArgumentsTemplate = "",
    [string]$PseudoGtFaceVerificationExternalOutputCsv = "",
    [int]$PseudoGtFaceVerificationExternalTimeoutSeconds = 0,
    [switch]$WithPseudoGtPersonObjectInput,
    [switch]$PseudoGtPersonObjectSkipImageExtraction,
    [int]$PseudoGtPersonObjectScaleWidth = 0,
    [string]$PseudoGtPersonObjectExternalCommand = "",
    [string]$PseudoGtPersonObjectExternalArgumentsTemplate = "",
    [string]$PseudoGtPersonObjectExternalOutputCsv = "",
    [int]$PseudoGtPersonObjectExternalTimeoutSeconds = 0,
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

if ($PseudoGtMaxFrames -lt 1) {
    throw "PseudoGtMaxFrames must be at least 1."
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
$argsList.Add("-PseudoGtMaxFrames") | Out-Null
$argsList.Add($PseudoGtMaxFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null

if ($AllowNoDetections.IsPresent) {
    $argsList.Add("-AllowNoDetections") | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($YoloModelPath)) {
    $argsList.Add("-YoloModelPath") | Out-Null
    $argsList.Add($YoloModelPath) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtTileFaceCsv)) {
    $argsList.Add("-PseudoGtTileFaceCsv") | Out-Null
    $argsList.Add($PseudoGtTileFaceCsv) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationCsv)) {
    $argsList.Add("-PseudoGtFaceVerificationCsv") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationCsv) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtPersonObjectCsv)) {
    $argsList.Add("-PseudoGtPersonObjectCsv") | Out-Null
    $argsList.Add($PseudoGtPersonObjectCsv) | Out-Null
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

if ($WithPseudoGtTileInput.IsPresent) {
    $argsList.Add("-WithPseudoGtTileInput") | Out-Null
    $argsList.Add("-PseudoGtTileColumns") | Out-Null
    $argsList.Add($PseudoGtTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
    $argsList.Add("-PseudoGtTileRows") | Out-Null
    $argsList.Add($PseudoGtTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
    $argsList.Add("-PseudoGtTileOverlapRatio") | Out-Null
    $argsList.Add($PseudoGtTileOverlapRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

if ($PseudoGtTileSkipImageExtraction.IsPresent) {
    $argsList.Add("-PseudoGtTileSkipImageExtraction") | Out-Null
}

if ($WithPseudoGtFaceVerificationInput.IsPresent) {
    $argsList.Add("-WithPseudoGtFaceVerificationInput") | Out-Null
    $argsList.Add("-PseudoGtFaceVerificationCropPaddingRatio") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationCropPaddingRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

if ($PseudoGtFaceVerificationSkipImageExtraction.IsPresent) {
    $argsList.Add("-PseudoGtFaceVerificationSkipImageExtraction") | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationExternalCommand)) {
    $argsList.Add("-PseudoGtFaceVerificationExternalCommand") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationExternalCommand) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationExternalArgumentsTemplate)) {
    $argsList.Add("-PseudoGtFaceVerificationExternalArgumentsTemplate") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationExternalArgumentsTemplate) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationExternalOutputCsv)) {
    $argsList.Add("-PseudoGtFaceVerificationExternalOutputCsv") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationExternalOutputCsv) | Out-Null
}

if ($PseudoGtFaceVerificationExternalTimeoutSeconds -gt 0) {
    $argsList.Add("-PseudoGtFaceVerificationExternalTimeoutSeconds") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationExternalTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

if ($WithPseudoGtPersonObjectInput.IsPresent) {
    $argsList.Add("-WithPseudoGtPersonObjectInput") | Out-Null
    $argsList.Add("-PseudoGtPersonObjectScaleWidth") | Out-Null
    $argsList.Add($PseudoGtPersonObjectScaleWidth.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

if ($PseudoGtPersonObjectSkipImageExtraction.IsPresent) {
    $argsList.Add("-PseudoGtPersonObjectSkipImageExtraction") | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtPersonObjectExternalCommand)) {
    $argsList.Add("-PseudoGtPersonObjectExternalCommand") | Out-Null
    $argsList.Add($PseudoGtPersonObjectExternalCommand) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtPersonObjectExternalArgumentsTemplate)) {
    $argsList.Add("-PseudoGtPersonObjectExternalArgumentsTemplate") | Out-Null
    $argsList.Add($PseudoGtPersonObjectExternalArgumentsTemplate) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtPersonObjectExternalOutputCsv)) {
    $argsList.Add("-PseudoGtPersonObjectExternalOutputCsv") | Out-Null
    $argsList.Add($PseudoGtPersonObjectExternalOutputCsv) | Out-Null
}

if ($PseudoGtPersonObjectExternalTimeoutSeconds -gt 0) {
    $argsList.Add("-PseudoGtPersonObjectExternalTimeoutSeconds") | Out-Null
    $argsList.Add($PseudoGtPersonObjectExternalTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
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
