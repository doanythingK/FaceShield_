param(
    [string]$VideoPath = ".tmp\srcTest-smoke\smoke-0600-3s.mp4",
    [string]$TemplateCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [string]$OutputDir = ".tmp\yolo-full-gt\review-package-smoke",
    [int]$ExpectedRows = 20,
    [int]$ExpectedFullFrameRows = 19,
    [switch]$ForceRegenerate
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

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloFullGtReviewPackageVerify] pass $Name"
}

if (-not (Test-Path $packageScript)) {
    throw "Review package script not found: $packageScript"
}

$packageScriptText = Get-Content -Raw -Path $packageScript
Assert-Contains "package generator exposes Force" $packageScriptText "[switch]`$Force"
Assert-Contains "package generator exposes RefreshIndexOnly" $packageScriptText "[switch]`$RefreshIndexOnly"
Assert-Contains "package generator protects existing output" $packageScriptText "Pass -Force to overwrite it."
Assert-Contains "package generator review index input rules" $packageScriptText "Input Rules"
Assert-Contains "package generator review index label rule" $packageScriptText "full-gt-review.csv label"
Assert-Contains "package generator review index csv key" $packageScriptText "CSV key:"
Assert-Contains "package generator review index pending fields" $packageScriptText "pending:"
Assert-Contains "package generator writes crop placeholder" $packageScriptText "Write-PlaceholderImage"
Assert-Contains "package generator documents unavailable crop" $packageScriptText "Crop extraction unavailable"
Assert-Contains "package generator documents unavailable full frame" $packageScriptText "Full-frame extraction unavailable"
Assert-Contains "package generator accepts continuity candidate CSV" $packageScriptText "[string]`$ContinuityCandidateCsv"
Assert-Contains "package generator exports continuity metadata columns" $packageScriptText "continuityCandidateTypes"

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedTemplate = Resolve-RepoPath $TemplateCsv
$resolvedOutputDir = Resolve-RepoPath $OutputDir

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

if (-not (Test-Path $resolvedTemplate)) {
    throw "Template CSV not found: $resolvedTemplate"
}

$noClobberDir = Join-Path $repo ".tmp\yolo-full-gt\review-package-no-clobber-selftest"
New-Item -ItemType Directory -Force -Path $noClobberDir | Out-Null
Set-Content -Encoding UTF8 -Path (Join-Path $noClobberDir "full-gt-review.csv") -Value "sentinel"
Set-Content -Encoding UTF8 -Path (Join-Path $noClobberDir "full-frame-review.csv") -Value "sentinel"
Set-Content -Encoding UTF8 -Path (Join-Path $noClobberDir "review-index.html") -Value "sentinel"
$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $noClobberOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
        -VideoPath $resolvedVideo `
        -TemplateCsv $resolvedTemplate `
        -OutputDir $noClobberDir `
        -MaxRows 1 `
        -IncludeFullFrameReview 2>&1
    $noClobberExitCode = $LASTEXITCODE
    $noClobberText = ($noClobberOutput | Out-String)
}
finally {
    $ErrorActionPreference = $oldErrorAction
}
if ($noClobberExitCode -eq 0) {
    throw "Review package generator unexpectedly overwrote an existing package without -Force."
}
Assert-Contains "package generator no-clobber selftest" $noClobberText "Pass -Force to overwrite it."

$requiredFrameDir = Join-Path $repo ".tmp\yolo-full-gt\review-package-required-frame-selftest"
$requiredFrameTemplate = Join-Path $repo ".tmp\yolo-full-gt\required-frame-template.csv"
$requiredFrameContinuityCsv = Join-Path $repo ".tmp\yolo-full-gt\required-frame-continuity.csv"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $requiredFrameTemplate) | Out-Null
@(
    [pscustomobject]@{
        frame = 0
        gtId = ""
        label = ""
        x = "10.0"
        y = "10.0"
        w = "40.0"
        h = "40.0"
        sourcePredictionId = "0"
        sourceConfidence = "0.500"
        source = "selftest"
        notes = "selftest candidate frame should lose priority to required full-frame review frames."
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $requiredFrameTemplate
@(
    [pscustomobject]@{
        candidateType = "perFaceShortGap"
        evidenceReason = "per-face-short-gap"
        reviewPriority = "high"
        frame = ""
        range = "9"
        previousFrame = "8"
        nextFrame = "10"
        detectionIndex = ""
        confidence = ""
        previousConfidence = "0.900000"
        nextConfidence = "0.910000"
        centerX = ""
        centerY = ""
        areaRatio = ""
        aspectRatio = ""
        x = ""
        y = ""
        w = ""
        h = ""
        areaChange = "0.100000"
        centerShift = "0.050000"
        reviewHint = "specific face missing while another mask may exist"
        requiresHumanLabel = "true"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $requiredFrameContinuityCsv

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
    -VideoPath $resolvedVideo `
    -TemplateCsv $requiredFrameTemplate `
    -OutputDir $requiredFrameDir `
    -MaxRows 1 `
    -IncludeFullFrameReview `
    -MaxFullFrameRows 1 `
    -VideoFrameCount 12 `
    -RequiredFullFrameNumbers "9" `
    -ContinuityCandidateCsv $requiredFrameContinuityCsv `
    -FullFrameScaleWidth 320 `
    -Force
if ($LASTEXITCODE -ne 0) {
    throw "Required full-frame selftest package generation failed with exit code $LASTEXITCODE"
}

$requiredFrameCsv = Join-Path $requiredFrameDir "full-frame-review.csv"
if (-not (Test-Path $requiredFrameCsv)) {
    throw "Required full-frame selftest CSV not found: $requiredFrameCsv"
}

$requiredFrameRows = @(Import-Csv $requiredFrameCsv)
if ($requiredFrameRows.Count -ne 1 -or [int]$requiredFrameRows[0].frame -ne 9) {
    throw "Required full-frame selftest expected only frame 9, got: $($requiredFrameRows | ForEach-Object { $_.frame } | Out-String)"
}

if (-not (Test-Path $requiredFrameRows[0].frameImagePath) -or -not (Test-Path $requiredFrameRows[0].overlayFrameImagePath)) {
    throw "Required full-frame selftest did not create frame and overlay files."
}

if ($requiredFrameRows[0].continuityCandidateTypes -ne "perFaceShortGap" -or
    $requiredFrameRows[0].continuityReviewPriority -ne "high" -or
    $requiredFrameRows[0].continuityCandidateRanges -ne "9" -or
    -not ([string]$requiredFrameRows[0].continuityReviewHints).Contains("specific face missing")) {
    throw "Required full-frame selftest did not preserve continuity candidate metadata for frame 9."
}

$requiredFrameIndex = Join-Path $requiredFrameDir "review-index.html"
$requiredFrameIndexText = Get-Content -Raw -Path $requiredFrameIndex
Assert-Contains "required full-frame selftest review index continuity label" $requiredFrameIndexText "continuity=perFaceShortGap"
Assert-Contains "required full-frame selftest review index continuity hint" $requiredFrameIndexText "specific face missing while another mask may exist"

Write-Host "[YoloFullGtReviewPackageVerify] pass required full-frame priority selftest"

$reviewCsv = Join-Path $resolvedOutputDir "full-gt-review.csv"
$frameReviewCsv = Join-Path $resolvedOutputDir "full-frame-review.csv"
$reviewIndex = Join-Path $resolvedOutputDir "review-index.html"
$packageExists = (Test-Path $reviewCsv) -and (Test-Path $frameReviewCsv) -and (Test-Path $reviewIndex)
if ($packageExists) {
    $frameReviewHeader = Get-Content -Path $frameReviewCsv -TotalCount 1
    if (-not ([string]$frameReviewHeader).Contains("continuityCandidateTypes")) {
        $packageExists = $false
    }
}
$generatedPackage = $false
if ($ForceRegenerate -or -not $packageExists) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
        -VideoPath $resolvedVideo `
        -TemplateCsv $resolvedTemplate `
        -OutputDir $resolvedOutputDir `
        -MaxRows $ExpectedRows `
        -IncludeFullFrameReview `
        -MaxFullFrameRows $ExpectedFullFrameRows `
        -FullFrameScaleWidth 1280 `
        -Force
    if ($LASTEXITCODE -ne 0) {
        throw "Review package generation failed with exit code $LASTEXITCODE"
    }

    $generatedPackage = $true
}
else {
    Write-Host "[YoloFullGtReviewPackageVerify] using existing package: $resolvedOutputDir"
}

if (-not (Test-Path $reviewCsv)) {
    throw "Review CSV not found: $reviewCsv"
}

$rows = @(Import-Csv $reviewCsv)
$predictionRows = @($rows | Where-Object {
    $null -ne $_.PSObject.Properties["sourcePredictionId"] -and
    -not [string]::IsNullOrWhiteSpace($_.sourcePredictionId)
})
$manualMissRows = @($rows | Where-Object {
    $null -ne $_.PSObject.Properties["source"] -and
    $_.source -eq "manual-missed"
})
if ($predictionRows.Count -ne $ExpectedRows) {
    throw "Review CSV prediction row count expected $ExpectedRows but got $($predictionRows.Count); totalRows=$($rows.Count), manualMissRows=$($manualMissRows.Count)"
}

$first = $rows[0]
foreach ($column in @("frame", "gtId", "label", "x", "y", "w", "h", "sourcePredictionId", "sourceConfidence", "cropPath", "cropX", "cropY", "cropW", "cropH", "reviewStatus", "evidenceNotes")) {
    Assert-Column $first $column
}

foreach ($row in $predictionRows) {
    if (-not (Test-Path $row.cropPath)) {
        throw "Crop image not found: $($row.cropPath)"
    }

    $cropInfo = Get-Item $row.cropPath
    if ($cropInfo.Length -le 0) {
        throw "Crop image is empty: $($row.cropPath)"
    }
}

$nonBlankLabels = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.label) })
if ($generatedPackage -and $nonBlankLabels.Count -ne 0) {
    throw "Generated review package should not pre-label rows; nonBlankLabels=$($nonBlankLabels.Count)"
}

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
foreach ($column in @("frame", "frameImagePath", "overlayFrameImagePath", "detectedCandidateCount", "candidateSummary", "continuityCandidateTypes", "continuityCandidateReasons", "continuityReviewPriority", "continuityCandidateRanges", "continuityReviewHints", "missedFaceCount", "missedFaceRowsAdded", "reviewStatus", "evidenceNotes")) {
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

    if (-not (Test-Path $row.overlayFrameImagePath)) {
        throw "Full-frame overlay image not found: $($row.overlayFrameImagePath)"
    }

    $overlayInfo = Get-Item $row.overlayFrameImagePath
    if ($overlayInfo.Length -le 0) {
        throw "Full-frame overlay image is empty: $($row.overlayFrameImagePath)"
    }

    if ([int]$row.detectedCandidateCount -gt 0 -and [string]::IsNullOrWhiteSpace($row.candidateSummary)) {
        throw "Full-frame review row with candidates is missing candidateSummary: frame=$($row.frame)"
    }
}

$reviewedFrameRows = @($frameRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.missedFaceCount) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($generatedPackage -and $reviewedFrameRows.Count -ne 0) {
    throw "Generated full-frame review package should not pre-review rows; reviewedFrameRows=$($reviewedFrameRows.Count)"
}

if (-not (Test-Path $reviewIndex)) {
    throw "Review index not found: $reviewIndex"
}

$reviewIndexText = Get-Content -Raw -Path $reviewIndex
$requiredReviewIndexTexts = @("Input Rules", "full-gt-review.csv label", "missedFaceRowsAdded", "CSV key:", "Set: label=face|nonface", "label=miss rows", "Set: missedFaceCount=N", "Detection crops", "Full-frame missed-face scan", "full-gt-review.csv", "full-frame-review.csv", "-overlay.png", "pred=")
$missingReviewIndexTexts = @($requiredReviewIndexTexts | Where-Object { -not $reviewIndexText.Contains($_) })
if ($missingReviewIndexTexts.Count -gt 0) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
        -OutputDir $resolvedOutputDir `
        -RefreshIndexOnly
    if ($LASTEXITCODE -ne 0) {
        throw "Review index refresh failed with exit code $LASTEXITCODE"
    }

    $reviewIndexText = Get-Content -Raw -Path $reviewIndex
}

foreach ($text in $requiredReviewIndexTexts) {
    if (-not $reviewIndexText.Contains($text)) {
        throw "Review index missing text: $text"
    }
}

Write-Host "[YoloFullGtReviewPackageVerify] pass rows=$($rows.Count), fullFrameRows=$($frameRows.Count), overlayFrames=pass, candidateSummary=pass, reviewCsv=$reviewCsv, frameReviewCsv=$frameReviewCsv, reviewIndex=$reviewIndex, outputDir=$resolvedOutputDir"
Write-Host "[YoloFullGtReviewPackageVerify] all requested checks passed"
