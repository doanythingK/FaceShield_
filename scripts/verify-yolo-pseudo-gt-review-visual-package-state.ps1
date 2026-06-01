param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$scriptPath = Join-Path $repo "scripts\new-yolo-pseudo-gt-review-visual-package.ps1"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-review-visual-package-state"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloPseudoGtReviewVisualVerify] pass $Name"
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }

    $item = Get-Item $Path
    if ($item.Length -le 0) {
        throw "$Name is empty: $Path"
    }

    Write-Host "[YoloPseudoGtReviewVisualVerify] pass $Name"
}

function Resolve-Tool {
    param([string]$ToolName)

    $native = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $native) {
        return [pscustomobject]@{
            Command = $native.Source
            ToolName = ""
            UseWsl = $false
        }
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -ne $wsl) {
        return [pscustomobject]@{
            Command = $wsl.Source
            ToolName = $ToolName
            UseWsl = $true
        }
    }

    return $null
}

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Invoke-Tool {
    param(
        [object]$Tool,
        [string[]]$Arguments
    )

    if ($Tool.UseWsl) {
        $converted = @()
        foreach ($arg in $Arguments) {
            if ($arg -match '^[A-Za-z]:\\') {
                $converted += Convert-ToWslPath $arg
            }
            else {
                $converted += $arg
            }
        }

        return & $Tool.Command --exec $Tool.ToolName @converted
    }

    return & $Tool.Command @Arguments
}

if (-not (Test-Path $scriptPath)) {
    throw "Script not found: $scriptPath"
}

$scriptText = Get-Content -Raw -Path $scriptPath
Assert-Contains "script records test-only visual boundary" $scriptText "test-only visual review evidence"
Assert-Contains "script keeps final labels human owned" $scriptText "does not finalize face/nonface/miss labels"
Assert-Contains "script rejects suggestedLabel inference" $scriptText "does not infer labels from suggestedLabel"
Assert-Contains "script writes visual draft csv" $scriptText "pseudo-gt-full-gt-review-visual-draft.csv"
Assert-Contains "script writes crop path" $scriptText "cropPath"
Assert-Contains "script writes overlay path" $scriptText "visualOverlayPath"

if (Test-Path $work) {
    Remove-Item -Recurse -Force -Path $work
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

$frameDir = Join-Path $work "frames"
New-Item -ItemType Directory -Force -Path $frameDir | Out-Null
$ffmpeg = Resolve-Tool "ffmpeg"
if ($null -eq $ffmpeg) {
    throw "ffmpeg not found for pseudo-GT visual package self-test"
}

foreach ($frame in @(4, 17, 20)) {
    $framePath = Join-Path $frameDir ("frame-{0:D6}.png" -f $frame)
    Invoke-Tool $ffmpeg @(
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "lavfi",
        "-i",
        "color=c=gray:s=320x180",
        "-frames:v",
        "1",
        $framePath
    ) | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $framePath)) {
        throw "ffmpeg frame fixture failed for frame $frame"
    }
}

$draftCsv = Join-Path $work "pseudo-gt-full-gt-review-draft.csv"
$frameDraftCsv = Join-Path $work "pseudo-gt-full-frame-review-draft.csv"
$outputDir = Join-Path $work "visual"

@(
    [pscustomobject]@{
        frame = "17"
        gtId = ""
        label = ""
        x = "20"
        y = "20"
        w = "60"
        h = "70"
        sourcePredictionId = "17-1"
        sourceConfidence = "0.45"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture false positive"
        pseudoGt_candidateId = "fp-17"
        pseudoGt_candidateType = "falsePositiveCandidate"
        pseudoGt_tileFaceConfidence = "0.05"
        pseudoGt_tileSupportCount = "0"
        pseudoGt_faceVerificationConfidence = "0.01"
        pseudoGt_faceVerificationDistance = "0.92"
        pseudoGt_personConfidence = "0.10"
        pseudoGt_personUpperOverlap = "0.00"
        pseudoGt_fpProbability = "0.90"
        pseudoGt_missProbability = "0.00"
        pseudoGt_pseudoGtReason = "base unsupported by tile and verifier"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    },
    [pscustomobject]@{
        frame = "20"
        gtId = ""
        label = ""
        x = "80"
        y = "30"
        w = "55"
        h = "65"
        sourcePredictionId = "20-1"
        sourceConfidence = "0.82"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture supported"
        pseudoGt_candidateId = "support-20"
        pseudoGt_candidateType = "supportedFaceCandidate"
        pseudoGt_tileFaceConfidence = "0.88"
        pseudoGt_tileSupportCount = "3"
        pseudoGt_faceVerificationConfidence = "0.91"
        pseudoGt_faceVerificationDistance = "0.08"
        pseudoGt_personConfidence = "0.40"
        pseudoGt_personUpperOverlap = "0.22"
        pseudoGt_fpProbability = "0.02"
        pseudoGt_missProbability = "0.00"
        pseudoGt_pseudoGtReason = "tile and verifier support base candidate"
        suggestedLabel = "face"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    },
    [pscustomobject]@{
        frame = "4"
        gtId = "miss-4"
        label = ""
        x = "180"
        y = "40"
        w = "42"
        h = "48"
        sourcePredictionId = ""
        sourceConfidence = ""
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture miss"
        pseudoGt_candidateId = "miss-4"
        pseudoGt_candidateType = "missCandidate"
        pseudoGt_tileFaceConfidence = "0.76"
        pseudoGt_tileSupportCount = "2"
        pseudoGt_faceVerificationConfidence = "0.84"
        pseudoGt_faceVerificationDistance = "0.11"
        pseudoGt_personConfidence = "0.50"
        pseudoGt_personUpperOverlap = "0.30"
        pseudoGt_fpProbability = "0.00"
        pseudoGt_missProbability = "0.85"
        pseudoGt_pseudoGtReason = "tile face appears without base candidate"
        suggestedLabel = "miss"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftCsv

@(
    [pscustomobject]@{
        frame = "4"
        frameImagePath = (Join-Path $frameDir "frame-000004.png")
        overlayFrameImagePath = ""
        detectedCandidateCount = "0"
        candidateSummary = "miss fixture"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-4"
        suggestedMissedFaceRowsAdded = "1"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameDraftCsv

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -DraftReviewCsv $draftCsv `
    -DraftFullFrameReviewCsv $frameDraftCsv `
    -FrameSourceDir $frameDir `
    -OutputDir $outputDir `
    -FrameWidth 320 `
    -FrameHeight 180 `
    -Force `
    -Verify `
    -RequireAllVisuals 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-review-visual-package.ps1 failed: $($output | Out-String)"
}

$text = ($output | Out-String)
Assert-Contains "script selftest completed" $text "[YoloPseudoGtReviewVisual] all requested checks passed"

$visualDraftCsv = Join-Path $outputDir "pseudo-gt-full-gt-review-visual-draft.csv"
$visualFrameCsv = Join-Path $outputDir "pseudo-gt-full-frame-review-visual-draft.csv"
$indexHtml = Join-Path $outputDir "pseudo-gt-review-visual-index.html"
$reportPath = Join-Path $outputDir "pseudo-gt-review-visual-report.md"
Assert-FileNonEmpty "visual draft CSV" $visualDraftCsv
Assert-FileNonEmpty "visual frame draft CSV" $visualFrameCsv
Assert-FileNonEmpty "visual index HTML" $indexHtml
Assert-FileNonEmpty "visual report" $reportPath

$rows = @(Import-Csv $visualDraftCsv)
if ($rows.Count -ne 3) {
    throw "Expected 3 visual draft rows, got $($rows.Count)"
}

$finalFilled = @($rows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.label) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($finalFilled.Count -ne 0) {
    throw "Visual package filled final review fields unexpectedly: $($finalFilled.Count)"
}
Write-Host "[YoloPseudoGtReviewVisualVerify] pass final review fields stay blank"

foreach ($row in $rows) {
    Assert-FileNonEmpty "crop for frame $($row.frame)" $row.cropPath
    Assert-FileNonEmpty "overlay for frame $($row.frame)" $row.visualOverlayPath
    if ($row.visualReviewStatus -ne "visual-ready-test-only") {
        throw "Unexpected visualReviewStatus for frame $($row.frame): $($row.visualReviewStatus)"
    }
}

$indexText = Get-Content -Raw -Path $indexHtml
Assert-Contains "index shows false positives" $indexText "falsePositiveCandidate"
Assert-Contains "index shows supported faces" $indexText "supportedFaceCandidate"
Assert-Contains "index shows miss candidates" $indexText "missCandidate"
Assert-Contains "index warns suggested label is not final" $indexText "Do not copy"

$reportText = Get-Content -Raw -Path $reportPath
Assert-Contains "report records zero missing visuals" $reportText "missingVisualRows: 0"
Assert-Contains "report records final fields blank" $reportText "finalFilledRows: 0"

Write-Host "[YoloPseudoGtReviewVisualVerify] all requested checks passed"
