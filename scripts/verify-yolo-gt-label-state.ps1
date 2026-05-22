param(
    [string]$PassReviewCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$FailReviewCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Normalize-Verdict {
    param([string]$Value)

    $text = ""
    if ($null -ne $Value) {
        $text = $Value.Trim().ToLowerInvariant()
    }

    switch ($text) {
        { $_ -in @("face", "actualface", "true", "yes", "y", "1") } { return "Face" }
        { $_ -in @("nonface", "notface", "false", "no", "n", "0", "object", "background") } { return "NonFace" }
        { $_ -in @("unclear", "unknown", "?") } { return "Unclear" }
        default { return "Unreviewed" }
    }
}

function Get-GtSummary {
    param([string]$CsvPath)

    $resolved = Resolve-RepoPath $CsvPath
    if (-not (Test-Path $resolved)) {
        throw "GT review CSV not found: $resolved"
    }

    $rows = @(Import-Csv $resolved)
    if ($rows.Count -eq 0) {
        throw "GT review CSV has no rows: $resolved"
    }

    $summary = [ordered]@{
        Path = $CsvPath
        Rows = $rows.Count
        Reviewed = 0
        Unreviewed = 0
        Unclear = 0
        YoloTruePositive = 0
        YoloFalsePositive = 0
        YoloMiss = 0
        FaceOnnxFalsePositive = 0
        UnknownLabel = 0
    }

    foreach ($row in $rows) {
        $verdict = Normalize-Verdict $row.verdict
        if ($verdict -eq "Unreviewed") {
            $summary.Unreviewed++
            continue
        }

        $summary.Reviewed++
        if ($verdict -eq "Unclear") {
            $summary.Unclear++
            continue
        }

        if ($row.label -eq "optimized") {
            if ($verdict -eq "Face") {
                $summary.YoloTruePositive++
            }
            elseif ($verdict -eq "NonFace") {
                $summary.YoloFalsePositive++
            }
        }
        elseif ($row.label -eq "baseline") {
            if ($verdict -eq "Face") {
                $summary.YoloMiss++
            }
            elseif ($verdict -eq "NonFace") {
                $summary.FaceOnnxFalsePositive++
            }
        }
        else {
            $summary.UnknownLabel++
        }
    }

    return [pscustomobject]$summary
}

function Assert-Equal {
    param(
        [string]$Name,
        [object]$Actual,
        [object]$Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Name expected $Expected but got $Actual"
    }

    Write-Host "[YoloGtLabelVerify] pass $Name=$Actual"
}

$pass = Get-GtSummary $PassReviewCsv
$fail = Get-GtSummary $FailReviewCsv

Write-Host "[YoloGtLabelSummary] name=pass, path=$($pass.Path), rows=$($pass.Rows), reviewed=$($pass.Reviewed), unreviewed=$($pass.Unreviewed), unclear=$($pass.Unclear), yoloTruePositive=$($pass.YoloTruePositive), yoloFalsePositive=$($pass.YoloFalsePositive), yoloMiss=$($pass.YoloMiss), faceOnnxFalsePositive=$($pass.FaceOnnxFalsePositive)"
Write-Host "[YoloGtLabelSummary] name=fail, path=$($fail.Path), rows=$($fail.Rows), reviewed=$($fail.Reviewed), unreviewed=$($fail.Unreviewed), unclear=$($fail.Unclear), yoloTruePositive=$($fail.YoloTruePositive), yoloFalsePositive=$($fail.YoloFalsePositive), yoloMiss=$($fail.YoloMiss), faceOnnxFalsePositive=$($fail.FaceOnnxFalsePositive)"

Assert-Equal "pass rows" $pass.Rows 15
Assert-Equal "pass reviewed" $pass.Reviewed 15
Assert-Equal "pass unreviewed" $pass.Unreviewed 0
Assert-Equal "pass unclear" $pass.Unclear 0
Assert-Equal "pass yolo true positive" $pass.YoloTruePositive 15
Assert-Equal "pass yolo false positive" $pass.YoloFalsePositive 0
Assert-Equal "pass yolo miss" $pass.YoloMiss 0
Assert-Equal "pass faceonnx false positive" $pass.FaceOnnxFalsePositive 0

Assert-Equal "fail rows" $fail.Rows 26
Assert-Equal "fail reviewed" $fail.Reviewed 26
Assert-Equal "fail unreviewed" $fail.Unreviewed 0
Assert-Equal "fail unclear" $fail.Unclear 1
Assert-Equal "fail yolo true positive" $fail.YoloTruePositive 1
Assert-Equal "fail yolo false positive" $fail.YoloFalsePositive 10
Assert-Equal "fail yolo miss" $fail.YoloMiss 0
Assert-Equal "fail faceonnx false positive" $fail.FaceOnnxFalsePositive 14

Write-Host "[YoloGtLabelVerify] all requested checks passed"
