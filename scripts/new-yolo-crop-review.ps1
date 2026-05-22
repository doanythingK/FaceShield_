param(
    [Parameter(Mandatory = $true)]
    [string]$InputCsv,
    [string]$ImageRoot = "",
    [string]$OutputCsv = "",
    [switch]$Summarize,
    [switch]$QualityGate,
    [int]$MaxOptimizedMiss = 0,
    [int]$MaxOptimizedFalsePositive = 0,
    [int]$MaxUnclear = 0,
    [int]$MaxUnreviewed = 0
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

function Get-ImpactHint {
    param(
        [string]$Reason,
        [string]$Label
    )

    if ($Label -eq "optimized") {
        return "Face=YOLO recall gain; NonFace=YOLO false-positive"
    }

    return "Face=YOLO miss; NonFace=FaceONNX false-positive"
}

$inputPath = Resolve-RepoPath $InputCsv
if (-not (Test-Path $inputPath)) {
    throw "Input CSV not found: $inputPath"
}

$rows = @(Import-Csv $inputPath)
if ($rows.Count -eq 0) {
    throw "Input CSV has no rows: $inputPath"
}

if ($Summarize) {
    $summary = [ordered]@{
        Reviewed = 0
        Unreviewed = 0
        OptimizedActualFace = 0
        OptimizedFalsePositive = 0
        OptimizedMiss = 0
        BaselineFalsePositive = 0
        Unclear = 0
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
                $summary.OptimizedActualFace++
            }
            elseif ($verdict -eq "NonFace") {
                $summary.OptimizedFalsePositive++
            }
        }
        else {
            if ($verdict -eq "Face") {
                $summary.OptimizedMiss++
            }
            elseif ($verdict -eq "NonFace") {
                $summary.BaselineFalsePositive++
            }
        }
    }

    [pscustomobject]$summary | Format-List
    $rows |
        Group-Object reason, label, verdict |
        Sort-Object Count -Descending |
        Select-Object Count, Name |
        Format-Table -AutoSize

    if ($QualityGate) {
        $passed = $summary.OptimizedMiss -le $MaxOptimizedMiss -and
            $summary.OptimizedFalsePositive -le $MaxOptimizedFalsePositive -and
            $summary.Unclear -le $MaxUnclear -and
            $summary.Unreviewed -le $MaxUnreviewed
        Write-Host (
            "[YoloCropReviewQualityGate] passed={0}, maxOptimizedMiss={1}, maxOptimizedFalsePositive={2}, maxUnclear={3}, maxUnreviewed={4}, optimizedMiss={5}, optimizedFalsePositive={6}, unclear={7}, unreviewed={8}" -f
            $passed,
            $MaxOptimizedMiss,
            $MaxOptimizedFalsePositive,
            $MaxUnclear,
            $MaxUnreviewed,
            $summary.OptimizedMiss,
            $summary.OptimizedFalsePositive,
            $summary.Unclear,
            $summary.Unreviewed)

        if (-not $passed) {
            exit 2
        }
    }
    return
}

$imageRootPath = if ([string]::IsNullOrWhiteSpace($ImageRoot)) {
    Split-Path -Parent $inputPath
}
else {
    Resolve-RepoPath $ImageRoot
}

if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path (Split-Path -Parent $inputPath) "crop-review.csv"
}
else {
    $OutputCsv = Resolve-RepoPath $OutputCsv
}

$reviewRows = foreach ($row in $rows) {
    $imagePath = if ([IO.Path]::IsPathRooted($row.path)) {
        $row.path
    }
    else {
        Join-Path $imageRootPath $row.path
    }

    [pscustomobject]@{
        reviewId = "$($row.reason)-frame-$($row.frame)-$($row.label)-$($row.index)"
        verdict = ""
        reason = $row.reason
        frame = $row.frame
        label = $row.label
        index = $row.index
        x = $row.x
        y = $row.y
        w = $row.w
        h = $row.h
        cx = $row.cx
        cy = $row.cy
        area = $row.area
        confidence = $row.confidence
        impactHint = Get-ImpactHint $row.reason $row.label
        imagePath = $imagePath
        notes = ""
    }
}

$outDir = Split-Path -Parent $OutputCsv
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$reviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
Write-Host "[YoloCropReview] wrote $($reviewRows.Count) rows to $OutputCsv"
