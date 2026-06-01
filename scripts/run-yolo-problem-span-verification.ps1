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
    [string]$PseudoGtOutputCsv = "",
    [string]$PseudoGtSummaryPath = "",
    [string]$PseudoGtReviewQueueCsv = "",
    [switch]$PublishPseudoGtToGoalEvidence,
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
    [double]$PseudoGtTileScale = 2.0,
    [double]$PseudoGtMinTileFaceConfidence = 0.55,
    [int]$PseudoGtMinTileSupportCount = 2,
    [string]$PseudoGtTileExternalCommand = "",
    [string]$PseudoGtTileExternalArgumentsTemplate = "",
    [string]$PseudoGtTileExternalOutputCsv = "",
    [ValidateSet("Frame", "TileImage", "TileOriginal")]
    [string]$PseudoGtTileExternalOutputCoordinateSpace = "Frame",
    [int]$PseudoGtTileExternalTimeoutSeconds = 0,
    [switch]$WithPseudoGtFaceVerificationInput,
    [switch]$PseudoGtFaceVerificationSkipImageExtraction,
    [double]$PseudoGtFaceVerificationCropPaddingRatio = 0.35,
    [string]$PseudoGtFaceVerificationExternalCommand = "",
    [string]$PseudoGtFaceVerificationExternalArgumentsTemplate = "",
    [string]$PseudoGtFaceVerificationExternalOutputCsv = "",
    [ValidateSet("Frame", "CropImage", "CropOriginal")]
    [string]$PseudoGtFaceVerificationExternalOutputCoordinateSpace = "Frame",
    [int]$PseudoGtFaceVerificationExternalTimeoutSeconds = 0,
    [switch]$WithPseudoGtPersonObjectInput,
    [switch]$PseudoGtPersonObjectSkipImageExtraction,
    [int]$PseudoGtPersonObjectScaleWidth = 0,
    [string]$PseudoGtPersonObjectExternalCommand = "",
    [string]$PseudoGtPersonObjectExternalArgumentsTemplate = "",
    [string]$PseudoGtPersonObjectExternalOutputCsv = "",
    [ValidateSet("Frame", "ScaledFrame")]
    [string]$PseudoGtPersonObjectExternalOutputCoordinateSpace = "Frame",
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

if ($PublishPseudoGtToGoalEvidence.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($PseudoGtTileFaceCsv) -and
        [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationCsv) -and
        [string]::IsNullOrWhiteSpace($PseudoGtTileExternalCommand) -and
        [string]::IsNullOrWhiteSpace($PseudoGtFaceVerificationExternalCommand)) {
        throw "PublishPseudoGtToGoalEvidence requires tile-face or face-verification evidence, either as CSV input or an external runner."
    }

    if ([string]::IsNullOrWhiteSpace($PseudoGtOutputCsv)) {
        $PseudoGtOutputCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv"
    }
    if ([string]::IsNullOrWhiteSpace($PseudoGtSummaryPath)) {
        $PseudoGtSummaryPath = ".tmp\yolo-pseudo-gt\pseudo-gt-summary.md"
    }
    if ([string]::IsNullOrWhiteSpace($PseudoGtReviewQueueCsv)) {
        $PseudoGtReviewQueueCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-queue.csv"
    }
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

if (-not [string]::IsNullOrWhiteSpace($PseudoGtOutputCsv)) {
    $argsList.Add("-PseudoGtOutputCsv") | Out-Null
    $argsList.Add($PseudoGtOutputCsv) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtSummaryPath)) {
    $argsList.Add("-PseudoGtSummaryPath") | Out-Null
    $argsList.Add($PseudoGtSummaryPath) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtReviewQueueCsv)) {
    $argsList.Add("-PseudoGtReviewQueueCsv") | Out-Null
    $argsList.Add($PseudoGtReviewQueueCsv) | Out-Null
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
    $argsList.Add("-PseudoGtTileScale") | Out-Null
    $argsList.Add($PseudoGtTileScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

$argsList.Add("-PseudoGtMinTileFaceConfidence") | Out-Null
$argsList.Add($PseudoGtMinTileFaceConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
$argsList.Add("-PseudoGtMinTileSupportCount") | Out-Null
$argsList.Add($PseudoGtMinTileSupportCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null

if ($PseudoGtTileSkipImageExtraction.IsPresent) {
    $argsList.Add("-PseudoGtTileSkipImageExtraction") | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtTileExternalCommand)) {
    $argsList.Add("-PseudoGtTileExternalCommand") | Out-Null
    $argsList.Add($PseudoGtTileExternalCommand) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtTileExternalArgumentsTemplate)) {
    $argsList.Add("-PseudoGtTileExternalArgumentsTemplate") | Out-Null
    $argsList.Add($PseudoGtTileExternalArgumentsTemplate) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($PseudoGtTileExternalOutputCsv)) {
    $argsList.Add("-PseudoGtTileExternalOutputCsv") | Out-Null
    $argsList.Add($PseudoGtTileExternalOutputCsv) | Out-Null
}

if ($PseudoGtTileExternalOutputCoordinateSpace -ne "Frame") {
    $argsList.Add("-PseudoGtTileExternalOutputCoordinateSpace") | Out-Null
    $argsList.Add($PseudoGtTileExternalOutputCoordinateSpace) | Out-Null
}

if ($PseudoGtTileExternalTimeoutSeconds -gt 0) {
    $argsList.Add("-PseudoGtTileExternalTimeoutSeconds") | Out-Null
    $argsList.Add($PseudoGtTileExternalTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
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

if ($PseudoGtFaceVerificationExternalOutputCoordinateSpace -ne "Frame") {
    $argsList.Add("-PseudoGtFaceVerificationExternalOutputCoordinateSpace") | Out-Null
    $argsList.Add($PseudoGtFaceVerificationExternalOutputCoordinateSpace) | Out-Null
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

if ($PseudoGtPersonObjectExternalOutputCoordinateSpace -ne "Frame") {
    $argsList.Add("-PseudoGtPersonObjectExternalOutputCoordinateSpace") | Out-Null
    $argsList.Add($PseudoGtPersonObjectExternalOutputCoordinateSpace) | Out-Null
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
