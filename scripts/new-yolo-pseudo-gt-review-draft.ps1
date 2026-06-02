param(
    [string]$PseudoGtReviewQueueCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-queue.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$OutputDir = ".tmp\yolo-pseudo-gt\review-draft",
    [int]$MaxReportRows = 30,
    [switch]$Force,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

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

    if ($null -eq $Row -or $null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
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

function Convert-ToPseudoGtReviewDraftRow {
    param([object]$Row)

    $candidateType = Get-CsvValue $Row "candidateType"
    $gtId = ""
    $sourcePredictionId = ""
    if ($candidateType -eq "missCandidate") {
        $gtId = Get-CsvValue $Row "candidateId"
    }
    if ($candidateType -ne "missCandidate") {
        $sourcePredictionId = Get-CsvValue $Row "basePredictionId"
    }

    $values = [ordered]@{
        frame = Get-CsvValue $Row "frame"
        gtId = $gtId
        label = ""
        x = Get-CsvValue $Row "x"
        y = Get-CsvValue $Row "y"
        w = Get-CsvValue $Row "w"
        h = Get-CsvValue $Row "h"
        sourcePredictionId = $sourcePredictionId
        sourceConfidence = Get-CsvValue $Row "baseFaceConfidence"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "Pseudo-GT draft only. Visually confirm before copying into full-gt-review.csv."
    }

    foreach ($property in $Row.PSObject.Properties) {
        Add-Property $values "pseudoGt_$($property.Name)" ([string]$property.Value)
    }

    Add-Property $values "suggestedLabel" (Get-CsvValue $Row "expectedReviewLabel")
    Add-Property $values "candidateRule" "test-only-reference-not-final-gt"
    Add-Property $values "humanReviewInstruction" "After visual confirmation, copy this row into full-gt-review.csv and fill label/reviewStatus/evidenceNotes."

    [pscustomobject]$values
}

function Convert-ToPseudoGtFrameDraftRow {
    param(
        [object]$FrameRow,
        [object[]]$MissRows
    )

    $frame = if ($null -ne $FrameRow) { Get-CsvValue $FrameRow "frame" } else { Get-CsvValue $MissRows[0] "frame" }
    $values = [ordered]@{
        frame = $frame
        frameImagePath = Get-CsvValue $FrameRow "frameImagePath"
        overlayFrameImagePath = Get-CsvValue $FrameRow "overlayFrameImagePath"
        detectedCandidateCount = Get-CsvValue $FrameRow "detectedCandidateCount"
        candidateSummary = Get-CsvValue $FrameRow "candidateSummary"
        continuityCandidateTypes = Get-CsvValue $FrameRow "continuityCandidateTypes"
        continuityCandidateReasons = Get-CsvValue $FrameRow "continuityCandidateReasons"
        continuityReviewPriority = Get-CsvValue $FrameRow "continuityReviewPriority"
        continuityCandidateRanges = Get-CsvValue $FrameRow "continuityCandidateRanges"
        continuityReviewHints = Get-CsvValue $FrameRow "continuityReviewHints"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        pseudoGtMissCandidateCount = "$($MissRows.Count)"
        pseudoGtMissCandidateIds = [string]::Join(";", @($MissRows | ForEach-Object { Get-CsvValue $_ "candidateId" }))
        suggestedMissedFaceRowsAdded = "$($MissRows.Count)"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "If these miss candidates are visible faces not covered by existing detections, set missedFaceRowsAdded and add matching manual rows to full-gt-review.csv."
    }

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

    Write-Host "[YoloPseudoGtReviewDraft] pass $Name"
}

$queuePath = Assert-FileNonEmpty "pseudo-GT review queue CSV" $PseudoGtReviewQueueCsv
$fullFramePath = Resolve-RepoPath $FullFrameReviewCsv

$queueRows = @(Import-Csv $queuePath)
if ($queueRows.Count -eq 0) {
    throw "Pseudo-GT review queue has no rows: $queuePath"
}

$fullFrameRows = @()
if (-not [string]::IsNullOrWhiteSpace($FullFrameReviewCsv) -and (Test-Path $fullFramePath)) {
    $fullFrameRows = @(Import-Csv $fullFramePath)
}

$fullFrameByFrame = @{}
foreach ($row in $fullFrameRows) {
    $fullFrameByFrame[(Get-CsvValue $row "frame")] = $row
}

$draftReviewRows = @($queueRows | ForEach-Object { Convert-ToPseudoGtReviewDraftRow $_ })
$missGroups = @($queueRows | Where-Object { (Get-CsvValue $_ "candidateType") -eq "missCandidate" } | Group-Object -Property frame)
$draftFrameRows = foreach ($group in $missGroups) {
    $frameRow = $null
    if ($fullFrameByFrame.ContainsKey($group.Name)) {
        $frameRow = $fullFrameByFrame[$group.Name]
    }

    Convert-ToPseudoGtFrameDraftRow -FrameRow $frameRow -MissRows @($group.Group)
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
if ((Test-Path $resolvedOutputDir) -and -not $Force) {
    throw "OutputDir already exists: $resolvedOutputDir. Pass -Force to overwrite draft outputs."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
$draftReviewCsv = Join-Path $resolvedOutputDir "pseudo-gt-full-gt-review-draft.csv"
$draftFrameCsv = Join-Path $resolvedOutputDir "pseudo-gt-full-frame-review-draft.csv"
$reportPath = Join-Path $resolvedOutputDir "pseudo-gt-review-draft-report.md"

$draftReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftReviewCsv
$draftFrameRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftFrameCsv

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
    throw "Pseudo-GT review draft unexpectedly filled final review fields: $draftReviewFinalFilled"
}
if ($draftFrameFinalFilled -ne 0) {
    throw "Pseudo-GT full-frame draft unexpectedly filled final review fields: $draftFrameFinalFilled"
}

$supportedCount = @($queueRows | Where-Object { (Get-CsvValue $_ "candidateType") -eq "supportedFaceCandidate" }).Count
$falsePositiveCount = @($queueRows | Where-Object { (Get-CsvValue $_ "candidateType") -eq "falsePositiveCandidate" }).Count
$missCount = @($queueRows | Where-Object { (Get-CsvValue $_ "candidateType") -eq "missCandidate" }).Count
$missFrameRowCount = @($draftFrameRows).Count
$maxRows = [Math]::Max(1, $MaxReportRows)

$reportLines = @(
    "# YOLO Pseudo-GT Review Draft",
    "",
    "This is test-only review preparation. The app runtime path does not read this file, and this draft does not finalize face/nonface/miss labels.",
    "",
    "## Rule",
    "- test-only-reference-not-final-gt",
    "- Copy rows into full-gt-review.csv only after visual confirmation.",
    "- For supportedFaceCandidate/falsePositiveCandidate rows, keep sourcePredictionId so closure can verify source id plus IoU geometry.",
    "- For missCandidate rows, keep sourcePredictionId blank and fill full-frame-review.csv missed-face evidence after visual confirmation.",
    "",
    "## Outputs",
    "- draftFullGtReviewCsv=$draftReviewCsv",
    "- draftFullFrameReviewCsv=$draftFrameCsv",
    "",
    "## Summary",
    "- queueRows=$($queueRows.Count)",
    "- supportedFaceCandidate=$supportedCount",
    "- falsePositiveCandidate=$falsePositiveCount",
    "- missCandidate=$missCount",
    "- missFrameRows=$missFrameRowCount",
    "",
    "## Top Review Draft Rows",
    "",
    "| rank | frame | candidateId | candidateType | suggestedLabel | sourcePredictionId | box | evidence metrics | reason |",
    "| ---: | ---: | --- | --- | --- | --- | --- | --- | --- |"
)

foreach ($row in ($draftReviewRows | Select-Object -First $maxRows)) {
    $box = "x=$(Get-CsvValue $row "x"), y=$(Get-CsvValue $row "y"), w=$(Get-CsvValue $row "w"), h=$(Get-CsvValue $row "h")"
    $metrics = "base=$(Get-CsvValue $row "pseudoGt_baseFaceConfidence"), tile=$(Get-CsvValue $row "pseudoGt_tileFaceConfidence")/support=$(Get-CsvValue $row "pseudoGt_tileSupportCount"), verify=$(Get-CsvValue $row "pseudoGt_faceVerificationConfidence")/dist=$(Get-CsvValue $row "pseudoGt_faceVerificationDistance"), iou=$(Get-CsvValue $row "pseudoGt_bestIou"), center=$(Get-CsvValue $row "pseudoGt_centerDistanceRatio"), area=$(Get-CsvValue $row "pseudoGt_areaChangeRatio"), fp=$(Get-CsvValue $row "pseudoGt_fpProbability"), miss=$(Get-CsvValue $row "pseudoGt_missProbability"), support=$(Get-CsvValue $row "pseudoGt_supportEvidenceIds")"
    $reportLines += "| $(Get-CsvValue $row "pseudoGt_reviewRank") | $(Get-CsvValue $row "frame") | $(Get-CsvValue $row "pseudoGt_candidateId") | $(Get-CsvValue $row "pseudoGt_candidateType") | $(Get-CsvValue $row "suggestedLabel") | $(Get-CsvValue $row "sourcePredictionId") | $box | $metrics | $(Get-CsvValue $row "pseudoGt_reviewPriorityReason") |"
}

$reportLines += @(
    "",
    "## Miss Frame Draft Rows",
    "",
    "| frame | pseudoGtMissCandidateCount | pseudoGtMissCandidateIds | continuity | suggestedMissedFaceRowsAdded |",
    "| ---: | ---: | --- | --- | ---: |"
)

foreach ($row in ($draftFrameRows | Select-Object -First $maxRows)) {
    $continuity = "types=$(Get-CsvValue $row "continuityCandidateTypes"), ranges=$(Get-CsvValue $row "continuityCandidateRanges"), hints=$(Get-CsvValue $row "continuityReviewHints")"
    $reportLines += "| $(Get-CsvValue $row "frame") | $(Get-CsvValue $row "pseudoGtMissCandidateCount") | $(Get-CsvValue $row "pseudoGtMissCandidateIds") | $continuity | $(Get-CsvValue $row "suggestedMissedFaceRowsAdded") |"
}

$reportLines | Set-Content -Encoding UTF8 -Path $reportPath
$reportText = Get-Content -Raw -Path $reportPath

foreach ($required in @(
    "YOLO Pseudo-GT Review Draft",
    "test-only-reference-not-final-gt",
    "source id plus IoU geometry",
    "sourcePredictionId blank",
    "draftFullGtReviewCsv=",
    "draftFullFrameReviewCsv=",
    "suggestedMissedFaceRowsAdded")) {
    Assert-TextContains "report token $required" $reportText $required
}

if ($Verify) {
    if ($supportedCount -le 0) {
        throw "No supportedFaceCandidate rows were present in the draft input."
    }
    if ($falsePositiveCount -le 0) {
        throw "No falsePositiveCandidate rows were present in the draft input."
    }
    if ($missCount -le 0) {
        throw "No missCandidate rows were present in the draft input."
    }

    $baseDraftRows = @($draftReviewRowsRead | Where-Object { (Get-CsvValue $_ "pseudoGt_candidateType") -ne "missCandidate" })
    $missDraftRows = @($draftReviewRowsRead | Where-Object { (Get-CsvValue $_ "pseudoGt_candidateType") -eq "missCandidate" })
    if (@($baseDraftRows | Where-Object { [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "sourcePredictionId")) }).Count -gt 0) {
        throw "Base pseudo-GT draft rows must keep sourcePredictionId for source-id plus IoU closure."
    }
    if (@($missDraftRows | Where-Object { -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "sourcePredictionId")) }).Count -gt 0) {
        throw "Miss pseudo-GT draft rows must keep sourcePredictionId blank."
    }
    if ($draftFrameRowsRead.Count -le 0) {
        throw "No pseudo-GT miss frame draft rows were generated."
    }
}

Write-Host "[YoloPseudoGtReviewDraft] draftFullGtReviewCsv=$draftReviewCsv"
Write-Host "[YoloPseudoGtReviewDraft] draftFullFrameReviewCsv=$draftFrameCsv"
Write-Host "[YoloPseudoGtReviewDraft] report=$reportPath"
Write-Host "[YoloPseudoGtReviewDraft] queueRows=$($queueRows.Count), supportedFaceCandidate=$supportedCount, falsePositiveCandidate=$falsePositiveCount, missCandidate=$missCount, missFrameRows=$missFrameRowCount"
Write-Host "[YoloPseudoGtReviewDraft] all requested checks passed"
