param(
    [string]$PassReviewCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$FailReviewCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$reviewScript = Join-Path $repo "scripts\new-yolo-crop-review.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Invoke-Review {
    param(
        [string]$Name,
        [string]$CsvPath,
        [int]$ExpectedExitCode
    )

    $resolved = Resolve-RepoPath $CsvPath
    if (-not (Test-Path $resolved)) {
        throw "$Name review CSV not found: $resolved"
    }

    Write-Host "[YoloCropReviewVerify] start $Name"
    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reviewScript -InputCsv $resolved -Summarize -QualityGate 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit code $ExpectedExitCode but got $exitCode"
    }

    Write-Host "[YoloCropReviewVerify] pass $Name exitCode=$exitCode"
    return $text
}

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }
}

$passOutput = Invoke-Review "yolo5face-0900-review-pass" $PassReviewCsv 0
Assert-Contains "yolo5face-0900-review-pass" $passOutput "passed=True"
Assert-Contains "yolo5face-0900-review-pass" $passOutput "OptimizedActualFace\s*:\s*15"
Assert-Contains "yolo5face-0900-review-pass" $passOutput "OptimizedFalsePositive\s*:\s*0"
Assert-Contains "yolo5face-0900-review-pass" $passOutput "Unreviewed\s*:\s*0"

$failOutput = Invoke-Review "yolo5face-0600-30s-review-fail" $FailReviewCsv 2
Assert-Contains "yolo5face-0600-30s-review-fail" $failOutput "passed=False"
Assert-Contains "yolo5face-0600-30s-review-fail" $failOutput "OptimizedFalsePositive\s*:\s*10"
Assert-Contains "yolo5face-0600-30s-review-fail" $failOutput "Unclear\s*:\s*1"
Assert-Contains "yolo5face-0600-30s-review-fail" $failOutput "Unreviewed\s*:\s*0"

Write-Host "[YoloCropReviewVerify] all requested checks passed"
