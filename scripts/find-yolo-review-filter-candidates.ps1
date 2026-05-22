param(
    [string[]]$ReviewCsv = @(
        ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
        ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv"
    ),
    [string]$OutputCsv = ".tmp/yolo-review-filter-candidates.csv",
    [int]$Top = 20,
    [int]$MaxFaceDrops = 0,
    [int]$MinNonFaceDrops = 1
)

$ErrorActionPreference = "Stop"

function Read-DoubleValue($Row, [string]$Name) {
    $value = $Row.$Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0.0
    }

    return [double]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Number([double]$Value) {
    return $Value.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-Condition([string]$Field, [string]$Operator, [double]$Value) {
    [pscustomobject]@{
        Field = $Field
        Operator = $Operator
        Value = $Value
    }
}

function New-Rule([string]$Name, [string]$Description, [object[]]$Conditions) {
    [pscustomobject]@{
        Name = $Name
        Description = $Description
        Conditions = $Conditions
    }
}

function Test-Rule($Rule, $Row) {
    foreach ($condition in $Rule.Conditions) {
        $actual = [double]$Row.PSObject.Properties[$condition.Field].Value
        if ($condition.Operator -eq "le") {
            if ($actual -gt $condition.Value) {
                return $false
            }
        } elseif ($condition.Operator -eq "ge") {
            if ($actual -lt $condition.Value) {
                return $false
            }
        } else {
            throw "Unknown rule operator: $($condition.Operator)"
        }
    }

    return $true
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$labeled = New-Object System.Collections.Generic.List[object]

foreach ($csv in $ReviewCsv) {
    $path = if ([System.IO.Path]::IsPathRooted($csv)) { $csv } else { Join-Path $repo $csv }
    if (-not (Test-Path $path)) {
        throw "Review CSV not found: $csv"
    }

    $source = Split-Path (Split-Path $path -Parent) -Leaf
    foreach ($row in (Import-Csv $path)) {
        if ($row.label -ne "optimized") {
            continue
        }

        if ($row.verdict -ne "Face" -and $row.verdict -ne "NonFace") {
            continue
        }

        $w = Read-DoubleValue $row "w"
        $h = Read-DoubleValue $row "h"
        $aspect = if ($h -gt 0) { $w / $h } else { 0.0 }

        $labeled.Add([pscustomobject]@{
            Source = $source
            ReviewId = $row.reviewId
            Verdict = $row.verdict
            Reason = $row.reason
            Frame = [int]$row.frame
            Confidence = Read-DoubleValue $row "confidence"
            Area = Read-DoubleValue $row "area"
            Cx = Read-DoubleValue $row "cx"
            Cy = Read-DoubleValue $row "cy"
            Aspect = $aspect
        })
    }
}

if ($labeled.Count -eq 0) {
    throw "No labeled optimized review rows were found."
}

$faceTotal = @($labeled | Where-Object { $_.Verdict -eq "Face" }).Count
$nonFaceTotal = @($labeled | Where-Object { $_.Verdict -eq "NonFace" }).Count
if ($faceTotal -eq 0 -or $nonFaceTotal -eq 0) {
    throw "Both Face and NonFace optimized rows are required. face=$faceTotal, nonFace=$nonFaceTotal"
}

$rules = New-Object System.Collections.Generic.List[object]
$confidenceThresholds = @(0.18, 0.20, 0.22, 0.25, 0.28, 0.30, 0.35, 0.40, 0.45, 0.50, 0.60)
$areaThresholds = @(0.0015, 0.0020, 0.0025, 0.0030, 0.0035, 0.0040, 0.0045, 0.0050, 0.0060, 0.0080, 0.0100, 0.0150)
$centerThresholds = @(0.03, 0.04, 0.05, 0.06, 0.07, 0.08, 0.10, 0.15, 0.20, 0.30, 0.70, 0.80, 0.90, 0.95, 0.97)
$aspectThresholds = @(0.50, 0.65, 0.75, 0.90, 1.10, 1.30, 1.50, 1.80, 2.00)

foreach ($threshold in $confidenceThresholds) {
    $formatted = Format-Number $threshold
    $rules.Add((New-Rule "drop-confidence-le-$formatted" "confidence <= $formatted" @((New-Condition "Confidence" "le" $threshold))))
    $rules.Add((New-Rule "drop-confidence-ge-$formatted" "confidence >= $formatted" @((New-Condition "Confidence" "ge" $threshold))))
}

foreach ($threshold in $areaThresholds) {
    $formatted = Format-Number $threshold
    $rules.Add((New-Rule "drop-area-le-$formatted" "area <= $formatted" @((New-Condition "Area" "le" $threshold))))
    $rules.Add((New-Rule "drop-area-ge-$formatted" "area >= $formatted" @((New-Condition "Area" "ge" $threshold))))
}

foreach ($threshold in $centerThresholds) {
    $formatted = Format-Number $threshold
    $rules.Add((New-Rule "drop-cx-le-$formatted" "cx <= $formatted" @((New-Condition "Cx" "le" $threshold))))
    $rules.Add((New-Rule "drop-cx-ge-$formatted" "cx >= $formatted" @((New-Condition "Cx" "ge" $threshold))))
    $rules.Add((New-Rule "drop-cy-le-$formatted" "cy <= $formatted" @((New-Condition "Cy" "le" $threshold))))
    $rules.Add((New-Rule "drop-cy-ge-$formatted" "cy >= $formatted" @((New-Condition "Cy" "ge" $threshold))))
}

foreach ($threshold in $aspectThresholds) {
    $formatted = Format-Number $threshold
    $rules.Add((New-Rule "drop-aspect-le-$formatted" "aspect <= $formatted" @((New-Condition "Aspect" "le" $threshold))))
    $rules.Add((New-Rule "drop-aspect-ge-$formatted" "aspect >= $formatted" @((New-Condition "Aspect" "ge" $threshold))))
}

foreach ($confidence in $confidenceThresholds) {
    foreach ($area in $areaThresholds) {
        $confText = Format-Number $confidence
        $areaText = Format-Number $area
        $rules.Add((New-Rule "drop-confidence-le-$confText-area-le-$areaText" "confidence <= $confText and area <= $areaText" @(
            (New-Condition "Confidence" "le" $confidence),
            (New-Condition "Area" "le" $area))))
        $rules.Add((New-Rule "drop-confidence-le-$confText-area-ge-$areaText" "confidence <= $confText and area >= $areaText" @(
            (New-Condition "Confidence" "le" $confidence),
            (New-Condition "Area" "ge" $area))))
    }
}

foreach ($confidence in $confidenceThresholds) {
    foreach ($cy in $centerThresholds) {
        $confText = Format-Number $confidence
        $cyText = Format-Number $cy
        $rules.Add((New-Rule "drop-confidence-le-$confText-cy-le-$cyText" "confidence <= $confText and cy <= $cyText" @(
            (New-Condition "Confidence" "le" $confidence),
            (New-Condition "Cy" "le" $cy))))
        $rules.Add((New-Rule "drop-confidence-le-$confText-cy-ge-$cyText" "confidence <= $confText and cy >= $cyText" @(
            (New-Condition "Confidence" "le" $confidence),
            (New-Condition "Cy" "ge" $cy))))
    }
}

foreach ($area in $areaThresholds) {
    foreach ($cy in $centerThresholds) {
        $areaText = Format-Number $area
        $cyText = Format-Number $cy
        $rules.Add((New-Rule "drop-area-le-$areaText-cy-le-$cyText" "area <= $areaText and cy <= $cyText" @(
            (New-Condition "Area" "le" $area),
            (New-Condition "Cy" "le" $cy))))
        $rules.Add((New-Rule "drop-area-ge-$areaText-cy-le-$cyText" "area >= $areaText and cy <= $cyText" @(
            (New-Condition "Area" "ge" $area),
            (New-Condition "Cy" "le" $cy))))
    }
}

$results = foreach ($rule in $rules) {
    $dropped = @($labeled | Where-Object { Test-Rule $rule $_ })
    if ($dropped.Count -eq 0) {
        continue
    }

    $droppedFace = @($dropped | Where-Object { $_.Verdict -eq "Face" }).Count
    $droppedNonFace = @($dropped | Where-Object { $_.Verdict -eq "NonFace" }).Count
    $rejectionPrecision = if ($dropped.Count -gt 0) { $droppedNonFace / [double]$dropped.Count } else { 0.0 }
    $nonFaceRecall = $droppedNonFace / [double]$nonFaceTotal
    $faceDropRate = $droppedFace / [double]$faceTotal

    [pscustomobject]@{
        Rule = $rule.Name
        Description = $rule.Description
        FaceTotal = $faceTotal
        NonFaceTotal = $nonFaceTotal
        DroppedFace = $droppedFace
        DroppedNonFace = $droppedNonFace
        KeptFace = $faceTotal - $droppedFace
        KeptNonFace = $nonFaceTotal - $droppedNonFace
        RejectionPrecision = [Math]::Round($rejectionPrecision, 4)
        NonFaceRecall = [Math]::Round($nonFaceRecall, 4)
        FaceDropRate = [Math]::Round($faceDropRate, 4)
        StrictPass = ($droppedFace -le $MaxFaceDrops -and $droppedNonFace -ge $MinNonFaceDrops)
    }
}

$ranked = @($results | Sort-Object @{ Expression = "StrictPass"; Descending = $true }, @{ Expression = "DroppedFace"; Ascending = $true }, @{ Expression = "DroppedNonFace"; Descending = $true }, @{ Expression = "RejectionPrecision"; Descending = $true }, Rule)

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputCsv)) { $OutputCsv } else { Join-Path $repo $OutputCsv }
$outputDir = Split-Path $outputPath -Parent
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

$ranked | Export-Csv -NoTypeInformation -Path $outputPath -Encoding UTF8

Write-Host "[YoloReviewFilterCandidates] labeledOptimized=$($labeled.Count), face=$faceTotal, nonFace=$nonFaceTotal, rules=$($ranked.Count), strictPass=$(@($ranked | Where-Object { $_.StrictPass }).Count), output=$OutputCsv"
$ranked |
    Select-Object -First $Top Rule, Description, DroppedFace, DroppedNonFace, KeptFace, KeptNonFace, RejectionPrecision, NonFaceRecall, FaceDropRate, StrictPass |
    Format-Table -AutoSize
