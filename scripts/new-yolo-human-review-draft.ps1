param(
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$AiCandidateReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$AiCandidateFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$OutputDir = ".tmp\yolo-manual-gates\human-review-draft",
    [int]$MaxReportRows = 20,
    [switch]$Force,
    [switch]$Verify
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

    return $resolved
}

function Get-CsvValue {
    param(
        [object]$Row,
        [string]$Column
    )

    if ($null -eq $Row) {
        return ""
    }

    if ($null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Get-ReviewKey {
    param([object]$Row)

    $frame = Get-CsvValue $Row "frame"
    $sourcePredictionId = Get-CsvValue $Row "sourcePredictionId"
    if (-not [string]::IsNullOrWhiteSpace($sourcePredictionId)) {
        return "prediction|$frame|$sourcePredictionId"
    }

    $gtId = Get-CsvValue $Row "gtId"
    $x = Get-CsvValue $Row "x"
    $y = Get-CsvValue $Row "y"
    $w = Get-CsvValue $Row "w"
    $h = Get-CsvValue $Row "h"
    return "manual|$frame|$gtId|$x|$y|$w|$h"
}

function Get-FrameKey {
    param([object]$Row)

    return Get-CsvValue $Row "frame"
}

function Add-Property {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Target,
        [string]$Name,
        [string]$Value
    )

    if (-not $Target.Contains($Name)) {
        $Target[$Name] = $Value
    }
}

function Convert-ToDraftReviewRow {
    param(
        [object]$BaseRow,
        [object]$CandidateRow,
        [string]$Action
    )

    $values = [ordered]@{}
    foreach ($property in $BaseRow.PSObject.Properties) {
        $values[$property.Name] = [string]$property.Value
    }

    Add-Property $values "candidateLabel" (Get-CsvValue $CandidateRow "label")
    Add-Property $values "candidateReviewStatus" (Get-CsvValue $CandidateRow "reviewStatus")
    Add-Property $values "candidateEvidenceNotes" (Get-CsvValue $CandidateRow "evidenceNotes")
    Add-Property $values "candidateAction" $Action
    Add-Property $values "candidateRule" "reference-only-not-final-gt"
    Add-Property $values "humanReviewInstruction" "Copy candidate values into label/reviewStatus/evidenceNotes only after visual confirmation."

    [pscustomobject]$values
}

function Convert-ToDraftManualCandidateRow {
    param([object]$CandidateRow)

    $values = [ordered]@{}
    foreach ($property in $CandidateRow.PSObject.Properties) {
        $name = $property.Name
        $value = [string]$property.Value
        if ($name -in @("label", "reviewStatus", "evidenceNotes")) {
            $values[$name] = ""
        }
        else {
            $values[$name] = $value
        }
    }

    Add-Property $values "candidateLabel" (Get-CsvValue $CandidateRow "label")
    Add-Property $values "candidateReviewStatus" (Get-CsvValue $CandidateRow "reviewStatus")
    Add-Property $values "candidateEvidenceNotes" (Get-CsvValue $CandidateRow "evidenceNotes")
    Add-Property $values "candidateAction" "manual-missed-candidate-add-if-confirmed"
    Add-Property $values "candidateRule" "reference-only-not-final-gt"
    Add-Property $values "humanReviewInstruction" "Keep this row only if the missed visible face is confirmed, then fill label/reviewStatus/evidenceNotes."

    [pscustomobject]$values
}

function Convert-ToDraftFrameRow {
    param(
        [object]$BaseRow,
        [object]$CandidateRow
    )

    $values = [ordered]@{}
    foreach ($property in $BaseRow.PSObject.Properties) {
        $values[$property.Name] = [string]$property.Value
    }

    Add-Property $values "candidateMissedFaceCount" (Get-CsvValue $CandidateRow "missedFaceCount")
    Add-Property $values "candidateMissedFaceRowsAdded" (Get-CsvValue $CandidateRow "missedFaceRowsAdded")
    Add-Property $values "candidateReviewStatus" (Get-CsvValue $CandidateRow "reviewStatus")
    Add-Property $values "candidateEvidenceNotes" (Get-CsvValue $CandidateRow "evidenceNotes")
    Add-Property $values "candidateAction" "confirm-or-edit"
    Add-Property $values "candidateRule" "reference-only-not-final-gt"
    Add-Property $values "humanReviewInstruction" "Copy candidate values into missedFaceCount/missedFaceRowsAdded/reviewStatus/evidenceNotes only after full-frame confirmation."

    [pscustomobject]$values
}

function Assert-TextContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloHumanReviewDraft] pass $Name"
}

$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$candidateReviewCsv = Assert-FileNonEmpty "AI-assisted candidate full GT review CSV" $AiCandidateReviewCsv
$candidateFrameCsv = Assert-FileNonEmpty "AI-assisted candidate full-frame review CSV" $AiCandidateFullFrameReviewCsv

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameCsv)
$candidateReviewRows = @(Import-Csv $candidateReviewCsv)
$candidateFrameRows = @(Import-Csv $candidateFrameCsv)

if ($reviewRows.Count -eq 0) {
    throw "Full GT review CSV has no rows: $reviewCsv"
}
if ($frameRows.Count -eq 0) {
    throw "Full-frame review CSV has no rows: $frameCsv"
}
if ($candidateReviewRows.Count -eq 0) {
    throw "AI candidate full GT review CSV has no rows: $candidateReviewCsv"
}
if ($candidateFrameRows.Count -eq 0) {
    throw "AI candidate full-frame review CSV has no rows: $candidateFrameCsv"
}

$candidateReviewByKey = @{}
foreach ($row in $candidateReviewRows) {
    $candidateReviewByKey[(Get-ReviewKey $row)] = $row
}

$candidateFrameByKey = @{}
foreach ($row in $candidateFrameRows) {
    $candidateFrameByKey[(Get-FrameKey $row)] = $row
}

$matchedReviewRows = 0
$unmatchedReviewRows = 0
$draftReviewRows = foreach ($row in $reviewRows) {
    $key = Get-ReviewKey $row
    if ($candidateReviewByKey.ContainsKey($key)) {
        $matchedReviewRows++
        Convert-ToDraftReviewRow -BaseRow $row -CandidateRow $candidateReviewByKey[$key] -Action "confirm-or-edit"
    }
    else {
        $unmatchedReviewRows++
        Convert-ToDraftReviewRow -BaseRow $row -CandidateRow $null -Action "review-without-candidate"
    }
}

$baseReviewKeys = @{}
foreach ($row in $reviewRows) {
    $baseReviewKeys[(Get-ReviewKey $row)] = $true
}

$manualCandidateRows = @()
foreach ($row in $candidateReviewRows) {
    $key = Get-ReviewKey $row
    if (-not $baseReviewKeys.ContainsKey($key)) {
        $manualCandidateRows += Convert-ToDraftManualCandidateRow -CandidateRow $row
    }
}
$draftReviewRows = @($draftReviewRows) + $manualCandidateRows

$matchedFrameRows = 0
$unmatchedFrameRows = 0
$draftFrameRows = foreach ($row in $frameRows) {
    $key = Get-FrameKey $row
    if ($candidateFrameByKey.ContainsKey($key)) {
        $matchedFrameRows++
        Convert-ToDraftFrameRow -BaseRow $row -CandidateRow $candidateFrameByKey[$key]
    }
    else {
        $unmatchedFrameRows++
        Convert-ToDraftFrameRow -BaseRow $row -CandidateRow $null
    }
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
if ((Test-Path $resolvedOutputDir) -and -not $Force) {
    throw "OutputDir already exists: $resolvedOutputDir. Pass -Force to overwrite draft outputs."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
$draftReviewCsv = Join-Path $resolvedOutputDir "full-gt-review-human-draft.csv"
$draftFrameCsv = Join-Path $resolvedOutputDir "full-frame-review-human-draft.csv"
$reportPath = Join-Path $resolvedOutputDir "human-review-draft-report.md"

$draftReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftReviewCsv
$draftFrameRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftFrameCsv

$maxRows = [Math]::Max(1, $MaxReportRows)
$reportLines = @(
    "# YOLO Human Review Draft",
    "",
    "This draft compares pending manual review CSVs with AI-assisted candidate CSVs. It is reference-only and does not complete the YOLO goal.",
    "",
    "## Rule",
    "- reference-only-not-final-gt",
    "- The draft keeps final review fields blank for pending rows.",
    "- A human must copy confirmed values into the real review CSVs and set real review evidence before completion.",
    "",
    "## Outputs",
    "- draftFullGtReviewCsv=$draftReviewCsv",
    "- draftFullFrameReviewCsv=$draftFrameCsv",
    "",
    "## Summary",
    "- fullGtBaseRows=$($reviewRows.Count)",
    "- fullGtCandidateRows=$($candidateReviewRows.Count)",
    "- fullGtMatchedCandidateRows=$matchedReviewRows",
    "- fullGtUnmatchedBaseRows=$unmatchedReviewRows",
    "- manualMissedCandidateRows=$($manualCandidateRows.Count)",
    "- fullFrameBaseRows=$($frameRows.Count)",
    "- fullFrameCandidateRows=$($candidateFrameRows.Count)",
    "- fullFrameMatchedCandidateRows=$matchedFrameRows",
    "- fullFrameUnmatchedBaseRows=$unmatchedFrameRows",
    "",
    "## Suggested Crop Decisions",
    "",
    "| frame | pred | candidateLabel | candidateAction | cropPath |",
    "| --- | --- | --- | --- | --- |"
)

foreach ($row in ($draftReviewRows | Select-Object -First $maxRows)) {
    $reportLines += "| $(Get-CsvValue $row "frame") | $(Get-CsvValue $row "sourcePredictionId") | $(Get-CsvValue $row "candidateLabel") | $(Get-CsvValue $row "candidateAction") | $(Get-CsvValue $row "cropPath") |"
}

$reportLines += @(
    "",
    "## Suggested Full-Frame Decisions",
    "",
    "| frame | candidateMissedFaceCount | candidateMissedFaceRowsAdded | candidateAction | frameImagePath |",
    "| --- | --- | --- | --- | --- |"
)

foreach ($row in ($draftFrameRows | Select-Object -First $maxRows)) {
    $reportLines += "| $(Get-CsvValue $row "frame") | $(Get-CsvValue $row "candidateMissedFaceCount") | $(Get-CsvValue $row "candidateMissedFaceRowsAdded") | $(Get-CsvValue $row "candidateAction") | $(Get-CsvValue $row "frameImagePath") |"
}

$reportLines | Set-Content -Encoding UTF8 -Path $reportPath

$draftReviewRowsRead = @(Import-Csv $draftReviewCsv)
$draftFrameRowsRead = @(Import-Csv $draftFrameCsv)
$draftReviewFinalFilled = @($draftReviewRowsRead | Where-Object {
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "label")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
}).Count
$draftFrameFinalFilled = @($draftFrameRowsRead | Where-Object {
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceCount")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceRowsAdded")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
}).Count

if ($draftReviewFinalFilled -ne 0) {
    throw "Draft full GT review CSV unexpectedly filled final review fields: $draftReviewFinalFilled"
}
if ($draftFrameFinalFilled -ne 0) {
    throw "Draft full-frame review CSV unexpectedly filled final review fields: $draftFrameFinalFilled"
}

$reportText = Get-Content -Raw -Path $reportPath
foreach ($required in @(
    "YOLO Human Review Draft",
    "reference-only-not-final-gt",
    "draftFullGtReviewCsv=",
    "draftFullFrameReviewCsv=",
    "manualMissedCandidateRows=",
    "candidateLabel",
    "candidateMissedFaceCount")) {
    Assert-TextContains "report token $required" $reportText $required
}

if ($Verify) {
    if ($matchedReviewRows -le 0) {
        throw "No matched candidate review rows were found"
    }
    if ($matchedFrameRows -le 0) {
        throw "No matched candidate full-frame rows were found"
    }
    if ($manualCandidateRows.Count -le 0) {
        throw "No manual missed candidate rows were found"
    }
    Assert-TextContains "draft review has candidate label column" ((Get-Content -Raw -Path $draftReviewCsv)) "candidateLabel"
    Assert-TextContains "draft frame has candidate miss column" ((Get-Content -Raw -Path $draftFrameCsv)) "candidateMissedFaceCount"
}

Write-Host "[YoloHumanReviewDraft] draftFullGtReviewCsv=$draftReviewCsv"
Write-Host "[YoloHumanReviewDraft] draftFullFrameReviewCsv=$draftFrameCsv"
Write-Host "[YoloHumanReviewDraft] report=$reportPath"
Write-Host "[YoloHumanReviewDraft] fullGtMatchedCandidateRows=$matchedReviewRows, manualMissedCandidateRows=$($manualCandidateRows.Count), fullFrameMatchedCandidateRows=$matchedFrameRows"
Write-Host "[YoloHumanReviewDraft] all requested checks passed"
