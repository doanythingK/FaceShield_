param(
    [string]$YoloCropReviewPassCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$YoloCropReviewFailCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv",
    [switch]$RunRepresentativeGate,
    [string]$RepresentativeQualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$RepresentativeYoloModelPath = ".tmp/models/YoloV5Face.onnx"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Invoke-YoloVerify {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name verifier not found: $ScriptPath"
    }

    Write-Host "[YoloStateVerify] start $Name"
    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode"
    }

    Write-Host "[YoloStateVerify] pass $Name"
}

$profileStateVerify = Join-Path $repo "scripts\verify-yolo-profile-state.ps1"
$cropReviewVerify = Join-Path $repo "scripts\verify-yolo-crop-review.ps1"
$conclusionStateVerify = Join-Path $repo "scripts\verify-yolo-conclusion-state.ps1"
$distributionStateVerify = Join-Path $repo "scripts\verify-yolo-distribution-state.ps1"
$representativeGateVerify = Join-Path $repo "scripts\verify-yolo-representative-gate.ps1"

Invoke-YoloVerify "profile-state" $profileStateVerify @()
Invoke-YoloVerify "crop-review" $cropReviewVerify @(
    "-PassReviewCsv", $YoloCropReviewPassCsv,
    "-FailReviewCsv", $YoloCropReviewFailCsv
)
Invoke-YoloVerify "conclusion-state" $conclusionStateVerify @()
Invoke-YoloVerify "distribution-state" $distributionStateVerify @()
if ($RunRepresentativeGate) {
    Invoke-YoloVerify "representative-gate" $representativeGateVerify @(
        "-QualityClip", $RepresentativeQualityClip,
        "-YoloModelPath", $RepresentativeYoloModelPath
    )
}

Write-Host "[YoloStateVerify] all requested checks passed"
