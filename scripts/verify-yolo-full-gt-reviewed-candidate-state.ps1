param(
    [string]$ReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [int]$ExpectedRows = 21,
    [int]$ExpectedFrameRows = 19,
    [int]$ExpectedTruePositives = 7,
    [int]$ExpectedFalsePositives = 13,
    [int]$ExpectedMisses = 1,
    [int]$ExpectedLowIou = 1
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$reviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
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

    Write-Host "[YoloFullGtReviewedCandidateVerify] pass $Name"
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "$Name is not a file: $resolved"
    }

    if ($item.Length -le 0) {
        throw "$Name is empty: $resolved"
    }

    Write-Host "[YoloFullGtReviewedCandidateVerify] pass $Name"
    return $resolved
}

function Invoke-ReviewedVerifier {
    param(
        [int]$MaxFalsePositives,
        [int]$ExpectedExitCode,
        [switch]$AllowQualityGateFailure
    )

    $qualityGateArgs = @()
    if ($AllowQualityGateFailure) {
        $qualityGateArgs += "-AllowQualityGateFailure"
    }

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reviewedVerifier `
            -ReviewCsv $reviewCsv `
            -FullFrameReviewCsv $frameReviewCsv `
            -PredictionLog $predictionLog `
            -RequireFullFrameReview `
            -RequireEvidence `
            -RequireArtifacts `
            -MaxMisses $ExpectedMisses `
            -MaxFalsePositives $MaxFalsePositives `
            -MaxLowIou $ExpectedLowIou `
            @qualityGateArgs 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($exitCode -ne $ExpectedExitCode) {
        throw "Reviewed candidate verifier expected exit code $ExpectedExitCode but got $exitCode"
    }

    return $text
}

if (-not (Test-Path $reviewedVerifier)) {
    throw "Reviewed verifier not found: $reviewedVerifier"
}

$reviewCsv = Assert-FileNonEmpty "AI candidate full GT review CSV" $ReviewCsv
$frameReviewCsv = Assert-FileNonEmpty "AI candidate full-frame review CSV" $FullFrameReviewCsv
$predictionLog = Assert-FileNonEmpty "AI candidate prediction log" $PredictionLog

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameReviewCsv)
if ($reviewRows.Count -ne $ExpectedRows) {
    throw "AI candidate review row count mismatch: expected=$ExpectedRows, actual=$($reviewRows.Count)"
}
if ($frameRows.Count -ne $ExpectedFrameRows) {
    throw "AI candidate full-frame row count mismatch: expected=$ExpectedFrameRows, actual=$($frameRows.Count)"
}

foreach ($row in $reviewRows) {
    if ($row.reviewStatus -ne "ai-candidate") {
        throw "AI candidate reviewStatus should be ai-candidate at frame=$($row.frame), actual=$($row.reviewStatus)"
    }
}
foreach ($row in $frameRows) {
    if ($row.reviewStatus -ne "ai-candidate") {
        throw "AI candidate full-frame reviewStatus should be ai-candidate at frame=$($row.frame), actual=$($row.reviewStatus)"
    }
}
Write-Host "[YoloFullGtReviewedCandidateVerify] pass candidate rows review=$($reviewRows.Count), fullFrame=$($frameRows.Count)"

$tolerantOutput = Invoke-ReviewedVerifier -MaxFalsePositives $ExpectedFalsePositives -ExpectedExitCode 0
Assert-Contains "candidate tolerant true positive" $tolerantOutput "truePositive=$ExpectedTruePositives"
Assert-Contains "candidate tolerant miss" $tolerantOutput "miss=$ExpectedMisses"
Assert-Contains "candidate tolerant false positive" $tolerantOutput "falsePositive=$ExpectedFalsePositives"
Assert-Contains "candidate tolerant low iou" $tolerantOutput "lowIou=$ExpectedLowIou"
Assert-Contains "candidate tolerant gate passes" $tolerantOutput "passed=True"

$strictOutput = Invoke-ReviewedVerifier -MaxFalsePositives 0 -ExpectedExitCode 2
Assert-Contains "candidate strict false positive" $strictOutput "falsePositive=$ExpectedFalsePositives"
Assert-Contains "candidate strict gate fails" $strictOutput "passed=False"

$allowedFailureOutput = Invoke-ReviewedVerifier -MaxFalsePositives 0 -ExpectedExitCode 0 -AllowQualityGateFailure
Assert-Contains "candidate allowed strict gate still fails" $allowedFailureOutput "passed=False"
Assert-Contains "candidate allowed quality failure" $allowedFailureOutput "failureAllowed=True"

Write-Host "[YoloFullGtReviewedCandidateVerify] all requested checks passed"
