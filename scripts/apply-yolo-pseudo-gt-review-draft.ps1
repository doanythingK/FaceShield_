param(
    [string]$DraftReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-gt-review-draft.csv",
    [string]$DraftFullFrameReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-frame-review-draft.csv",
    [string]$ReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$OutputReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\applied-full-gt-review.csv",
    [string]$OutputFullFrameReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\applied-full-frame-review.csv",
    [string]$ReportPath = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-review-draft-apply-report.md",
    [switch]$InPlace,
    [switch]$AllowPartial,
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

function Assert-FileExists {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
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

function Test-ReviewRowReady {
    param([object]$Row)

    return -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "label")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "reviewStatus")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "evidenceNotes"))
}

function Test-FrameReviewRowReady {
    param([object]$Row)

    return -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "missedFaceCount")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "missedFaceRowsAdded")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "reviewStatus")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "evidenceNotes"))
}

function Get-ReviewKey {
    param([object]$Row)

    $frame = Get-CsvValue $Row "frame"
    $sourcePredictionId = Get-CsvValue $Row "sourcePredictionId"
    $gtId = Get-CsvValue $Row "gtId"
    if ([string]::IsNullOrWhiteSpace($sourcePredictionId)) {
        return "miss|$frame|$gtId"
    }

    return "source|$frame|$sourcePredictionId"
}

function Copy-Columns {
    param(
        [string[]]$Columns,
        [object]$From
    )

    $values = [ordered]@{}
    foreach ($column in $Columns) {
        $values[$column] = Get-CsvValue $From $column
    }

    [pscustomobject]$values
}

function Update-TargetRow {
    param(
        [object]$Target,
        [object]$From,
        [string[]]$Columns
    )

    foreach ($column in $Columns) {
        if ($null -eq $Target.PSObject.Properties[$column]) {
            continue
        }

        $Target.$column = Get-CsvValue $From $column
    }
}

function Export-Rows {
    param(
        [object[]]$Rows,
        [string]$Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $Path
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

    Write-Host "[YoloPseudoGtReviewDraftApply] pass $Name"
}

# This script applies human-owned final review fields only. It does not infer labels from suggestedLabel.
# The pseudo-GT candidate rule remains review CSV-owned: final face/nonface/miss closure comes from label/reviewStatus/evidenceNotes.
$draftReviewPath = Assert-FileExists "pseudo-GT draft review CSV" $DraftReviewCsv
$reviewPath = Assert-FileExists "full-GT review CSV" $ReviewCsv
$draftFramePath = Resolve-RepoPath $DraftFullFrameReviewCsv
$fullFramePath = Resolve-RepoPath $FullFrameReviewCsv
$outputReviewPath = if ($InPlace) { $reviewPath } else { Resolve-RepoPath $OutputReviewCsv }
$outputFramePath = if ($InPlace) { $fullFramePath } else { Resolve-RepoPath $OutputFullFrameReviewCsv }
$reportPathResolved = Resolve-RepoPath $ReportPath

$draftRows = @(Import-Csv $draftReviewPath)
$reviewRows = @(Import-Csv $reviewPath)
if ($draftRows.Count -eq 0) {
    throw "Pseudo-GT draft review CSV has no rows: $draftReviewPath"
}
if ($reviewRows.Count -eq 0) {
    throw "Full-GT review CSV has no rows: $reviewPath"
}

$pendingDraftRows = @($draftRows | Where-Object { -not (Test-ReviewRowReady $_) })
if ($pendingDraftRows.Count -gt 0 -and -not $AllowPartial) {
    throw "Pseudo-GT draft has $($pendingDraftRows.Count) rows without final label/reviewStatus/evidenceNotes. Fill them or pass -AllowPartial."
}

$readyDraftRows = @($draftRows | Where-Object { Test-ReviewRowReady $_ })
if ($readyDraftRows.Count -eq 0) {
    throw "No pseudo-GT draft review rows are ready to apply."
}

$reviewColumns = @($reviewRows[0].PSObject.Properties | ForEach-Object { $_.Name })
$reviewByKey = @{}
for ($index = 0; $index -lt $reviewRows.Count; $index++) {
    $reviewByKey[(Get-ReviewKey $reviewRows[$index])] = $index
}

$mergedReviewRows = [System.Collections.Generic.List[object]]::new()
foreach ($row in $reviewRows) {
    $mergedReviewRows.Add((Copy-Columns -Columns $reviewColumns -From $row))
}

$reviewAdded = 0
$reviewUpdated = 0
foreach ($draftRow in $readyDraftRows) {
    $key = Get-ReviewKey $draftRow
    if ($reviewByKey.ContainsKey($key)) {
        Update-TargetRow -Target $mergedReviewRows[$reviewByKey[$key]] -From $draftRow -Columns $reviewColumns
        $reviewUpdated++
        continue
    }

    $mergedReviewRows.Add((Copy-Columns -Columns $reviewColumns -From $draftRow))
    $reviewByKey[$key] = $mergedReviewRows.Count - 1
    $reviewAdded++
}

$frameAdded = 0
$frameUpdated = 0
$framePending = 0
$readyFrameRows = @()
$frameRows = @()
$mergedFrameRows = @()

if (-not [string]::IsNullOrWhiteSpace($DraftFullFrameReviewCsv) -and (Test-Path $draftFramePath)) {
    if (-not (Test-Path $fullFramePath)) {
        throw "Full-frame review CSV not found: $fullFramePath"
    }

    $draftFrameRows = @(Import-Csv $draftFramePath)
    $frameRows = @(Import-Csv $fullFramePath)
    $framePending = @($draftFrameRows | Where-Object { -not (Test-FrameReviewRowReady $_) }).Count
    if ($framePending -gt 0 -and -not $AllowPartial) {
        throw "Pseudo-GT full-frame draft has $framePending rows without final missedFaceCount/missedFaceRowsAdded/reviewStatus/evidenceNotes. Fill them or pass -AllowPartial."
    }

    $readyFrameRows = @($draftFrameRows | Where-Object { Test-FrameReviewRowReady $_ })
    if ($readyFrameRows.Count -gt 0) {
        if ($frameRows.Count -eq 0) {
            throw "Full-frame review CSV has no rows: $fullFramePath"
        }

        $frameColumns = @($frameRows[0].PSObject.Properties | ForEach-Object { $_.Name })
        $frameByKey = @{}
        for ($index = 0; $index -lt $frameRows.Count; $index++) {
            $frameByKey[(Get-CsvValue $frameRows[$index] "frame")] = $index
        }

        $mergedFrameRows = [System.Collections.Generic.List[object]]::new()
        foreach ($row in $frameRows) {
            $mergedFrameRows.Add((Copy-Columns -Columns $frameColumns -From $row))
        }

        foreach ($draftFrameRow in $readyFrameRows) {
            $key = Get-CsvValue $draftFrameRow "frame"
            if ($frameByKey.ContainsKey($key)) {
                Update-TargetRow -Target $mergedFrameRows[$frameByKey[$key]] -From $draftFrameRow -Columns $frameColumns
                $frameUpdated++
                continue
            }

            $mergedFrameRows.Add((Copy-Columns -Columns $frameColumns -From $draftFrameRow))
            $frameByKey[$key] = $mergedFrameRows.Count - 1
            $frameAdded++
        }
    }
}

Export-Rows -Rows @($mergedReviewRows) -Path $outputReviewPath
if (@($mergedFrameRows).Count -gt 0) {
    Export-Rows -Rows @($mergedFrameRows) -Path $outputFramePath
}

$reportLines = @(
    "# YOLO Pseudo-GT Review Draft Apply",
    "",
    "This is a test-only merge helper. It applies only human-filled final review fields and does not infer labels from suggestedLabel.",
    "",
    "## Rule",
    "- review CSV-owned",
    "- candidateRule=test-only-reference-not-final-gt",
    "- final face/nonface/miss closure must come from label/reviewStatus/evidenceNotes.",
    "",
    "## Inputs",
    "- draftReviewCsv=$draftReviewPath",
    "- draftFullFrameReviewCsv=$draftFramePath",
    "- reviewCsv=$reviewPath",
    "- fullFrameReviewCsv=$fullFramePath",
    "",
    "## Outputs",
    "- outputReviewCsv=$outputReviewPath",
    "- outputFullFrameReviewCsv=$outputFramePath",
    "",
    "## Summary",
    "- draftRows=$($draftRows.Count)",
    "- readyDraftRows=$($readyDraftRows.Count)",
    "- pendingDraftRows=$($pendingDraftRows.Count)",
    "- reviewRowsAdded=$reviewAdded",
    "- reviewRowsUpdated=$reviewUpdated",
    "- readyFullFrameRows=$(@($readyFrameRows).Count)",
    "- pendingFullFrameRows=$framePending",
    "- fullFrameRowsAdded=$frameAdded",
    "- fullFrameRowsUpdated=$frameUpdated",
    "- inPlace=$([bool]$InPlace)",
    "- allowPartial=$([bool]$AllowPartial)"
)

$reportParent = Split-Path -Parent $reportPathResolved
if (-not [string]::IsNullOrWhiteSpace($reportParent)) {
    New-Item -ItemType Directory -Force -Path $reportParent | Out-Null
}
$reportLines | Set-Content -Encoding UTF8 -Path $reportPathResolved

if ($Verify) {
    $scriptText = Get-Content -Raw -Path $MyInvocation.MyCommand.Path
    Assert-TextContains "script refuses suggestedLabel inference" $scriptText "does not infer labels from suggestedLabel"
    Assert-TextContains "script keeps closure review CSV-owned" $scriptText "review CSV-owned"
    Assert-TextContains "script records test-only candidate rule" $scriptText "candidateRule=test-only-reference-not-final-gt"

    $appliedRows = @(Import-Csv $outputReviewPath)
    foreach ($row in $readyDraftRows) {
        $key = Get-ReviewKey $row
        $match = @($appliedRows | Where-Object { (Get-ReviewKey $_) -eq $key })
        if ($match.Count -ne 1) {
            throw "Applied review CSV does not contain exactly one row for key=$key"
        }

        foreach ($requiredColumn in @("label", "reviewStatus", "evidenceNotes")) {
            if ([string]::IsNullOrWhiteSpace((Get-CsvValue $match[0] $requiredColumn))) {
                throw "Applied review row missing $requiredColumn for key=$key"
            }
        }
    }
}

Write-Host "[YoloPseudoGtReviewDraftApply] outputReviewCsv=$outputReviewPath"
if (@($mergedFrameRows).Count -gt 0) {
    Write-Host "[YoloPseudoGtReviewDraftApply] outputFullFrameReviewCsv=$outputFramePath"
}
Write-Host "[YoloPseudoGtReviewDraftApply] report=$reportPathResolved"
Write-Host "[YoloPseudoGtReviewDraftApply] readyDraftRows=$($readyDraftRows.Count), pendingDraftRows=$($pendingDraftRows.Count), reviewRowsAdded=$reviewAdded, reviewRowsUpdated=$reviewUpdated"
Write-Host "[YoloPseudoGtReviewDraftApply] readyFullFrameRows=$(@($readyFrameRows).Count), pendingFullFrameRows=$framePending, fullFrameRowsAdded=$frameAdded, fullFrameRowsUpdated=$frameUpdated"
