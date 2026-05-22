param(
    [string]$VideoPath = ".tmp\srcTest-smoke\smoke-0600-3s.mp4",
    [string]$TemplateCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [string]$OutputDir = ".tmp\yolo-full-gt\review-package-smoke",
    [int]$ExpectedRows = 20
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
    -MaxRows $ExpectedRows
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

Write-Host "[YoloFullGtReviewPackageVerify] pass rows=$($rows.Count), reviewCsv=$reviewCsv, outputDir=$resolvedOutputDir"
Write-Host "[YoloFullGtReviewPackageVerify] all requested checks passed"
