param(
    [string]$VideoPath = ".tmp\srcTest-smoke\smoke-0600-3s.mp4",
    [string]$TemplateCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [string]$OutputDir = ".tmp\yolo-full-gt\review-package-smoke",
    [int]$ExpectedRows = 20,
    [int]$ExpectedFullFrameRows = 19
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$packageScript = Join-Path $repo "scripts\new-yolo-full-gt-review-package.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Assert-Column {
    param(
        [object]$Row,
        [string]$Name
    )

    if ($null -eq $Row.PSObject.Properties[$Name]) {
        throw "review package missing column: $Name"
    }

    Write-Host "[YoloFullGtReviewPackageVerify] pass column $Name"
}

if (-not (Test-Path $packageScript)) {
    throw "Review package script not found: $packageScript"
}

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedTemplate = Resolve-RepoPath $TemplateCsv
$resolvedOutputDir = Resolve-RepoPath $OutputDir

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

if (-not (Test-Path $resolvedTemplate)) {
    throw "Template CSV not found: $resolvedTemplate"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
    -VideoPath $resolvedVideo `
    -TemplateCsv $resolvedTemplate `
    -OutputDir $resolvedOutputDir `
    -MaxRows $ExpectedRows `
    -IncludeFullFrameReview `
    -MaxFullFrameRows $ExpectedFullFrameRows `
    -FullFrameScaleWidth 1280
if ($LASTEXITCODE -ne 0) {
    throw "Review package generation failed with exit code $LASTEXITCODE"
}

$reviewCsv = Join-Path $resolvedOutputDir "full-gt-review.csv"
if (-not (Test-Path $reviewCsv)) {
    throw "Review CSV not found: $reviewCsv"
}

$rows = @(Import-Csv $reviewCsv)
if ($rows.Count -ne $ExpectedRows) {
    throw "Review CSV row count expected $ExpectedRows but got $($rows.Count)"
}

$first = $rows[0]
foreach ($column in @("frame", "gtId", "label", "x", "y", "w", "h", "sourcePredictionId", "sourceConfidence", "cropPath", "cropX", "cropY", "cropW", "cropH", "reviewStatus", "evidenceNotes")) {
    Assert-Column $first $column
}

foreach ($row in $rows) {
    if (-not (Test-Path $row.cropPath)) {
        throw "Crop image not found: $($row.cropPath)"
    }

    $cropInfo = Get-Item $row.cropPath
    if ($cropInfo.Length -le 0) {
        throw "Crop image is empty: $($row.cropPath)"
    }
}

$nonBlankLabels = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.label) })
if ($nonBlankLabels.Count -ne 0) {
    throw "Generated review package should not pre-label rows; nonBlankLabels=$($nonBlankLabels.Count)"
}

$frameReviewCsv = Join-Path $resolvedOutputDir "full-frame-review.csv"
if (-not (Test-Path $frameReviewCsv)) {
    throw "Full-frame review CSV not found: $frameReviewCsv"
}

$frameRows = @(Import-Csv $frameReviewCsv)
if ($frameRows.Count -ne $ExpectedFullFrameRows) {
    throw "Full-frame review row count expected $ExpectedFullFrameRows but got $($frameRows.Count)"
}

$candidateFrames = @($rows | ForEach-Object { [int]$_.frame } | Sort-Object -Unique)
foreach ($candidateFrame in $candidateFrames) {
    $match = @($frameRows | Where-Object { [int]$_.frame -eq $candidateFrame })
    if ($match.Count -eq 0) {
        throw "Full-frame review is missing candidate frame: $candidateFrame"
    }
}

$firstFrameRow = $frameRows[0]
foreach ($column in @("frame", "frameImagePath", "detectedCandidateCount", "missedFaceCount", "missedFaceRowsAdded", "reviewStatus", "evidenceNotes")) {
    Assert-Column $firstFrameRow $column
}

foreach ($row in $frameRows) {
    if (-not (Test-Path $row.frameImagePath)) {
        throw "Full-frame image not found: $($row.frameImagePath)"
    }

    $frameInfo = Get-Item $row.frameImagePath
    if ($frameInfo.Length -le 0) {
        throw "Full-frame image is empty: $($row.frameImagePath)"
    }
}

$reviewedFrameRows = @($frameRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.missedFaceCount) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($reviewedFrameRows.Count -ne 0) {
    throw "Generated full-frame review package should not pre-review rows; reviewedFrameRows=$($reviewedFrameRows.Count)"
}

$reviewIndex = Join-Path $resolvedOutputDir "review-index.html"
if (-not (Test-Path $reviewIndex)) {
    throw "Review index not found: $reviewIndex"
}

$reviewIndexText = Get-Content -Raw -Path $reviewIndex
foreach ($text in @("Detection crops", "Full-frame missed-face scan", "full-gt-review.csv", "full-frame-review.csv")) {
    if (-not $reviewIndexText.Contains($text)) {
        throw "Review index missing text: $text"
    }
}

Write-Host "[YoloFullGtReviewPackageVerify] pass rows=$($rows.Count), fullFrameRows=$($frameRows.Count), reviewCsv=$reviewCsv, frameReviewCsv=$frameReviewCsv, reviewIndex=$reviewIndex, outputDir=$resolvedOutputDir"
Write-Host "[YoloFullGtReviewPackageVerify] all requested checks passed"
