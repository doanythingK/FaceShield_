param(
    [string]$GtCsv = "",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = "",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$RequireData,
    [switch]$SelfTest
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

function Read-DoubleValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0.0
    }

    return [double]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-IntValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0
    }

    return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-Box {
    param(
        [int]$Frame,
        [string]$Id,
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [double]$Confidence = 1.0
    )

    [pscustomobject]@{
        Frame = $Frame
        Id = $Id
        X = $X
        Y = $Y
        W = $W
        H = $H
        Confidence = $Confidence
    }
}

function Get-Iou {
    param(
        [object]$A,
        [object]$B
    )

    $left = [Math]::Max($A.X, $B.X)
    $top = [Math]::Max($A.Y, $B.Y)
    $right = [Math]::Min($A.X + $A.W, $B.X + $B.W)
    $bottom = [Math]::Min($A.Y + $A.H, $B.Y + $B.H)
    $width = [Math]::Max(0.0, $right - $left)
    $height = [Math]::Max(0.0, $bottom - $top)
    $intersection = $width * $height
    if ($intersection -le 0.0) {
        return 0.0
    }

    $areaA = [Math]::Max(0.0, $A.W) * [Math]::Max(0.0, $A.H)
    $areaB = [Math]::Max(0.0, $B.W) * [Math]::Max(0.0, $B.H)
    $union = $areaA + $areaB - $intersection
    if ($union -le 0.0) {
        return 0.0
    }

    return $intersection / $union
}

function Read-GtCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "GT CSV not found: $resolved"
    }

    $rows = @(Import-Csv $resolved)
    if ($rows.Count -eq 0) {
        throw "GT CSV has no rows: $resolved"
    }

    foreach ($row in $rows) {
        $label = $row.label
        if ([string]::IsNullOrWhiteSpace($label)) {
            $label = $row.expected
        }

        if ([string]::IsNullOrWhiteSpace($label)) {
            continue
        }

        if ($label.Trim().ToLowerInvariant() -notin @("face", "actualface", "miss", "true", "1")) {
            continue
        }

        $id = $row.gtId
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = $row.id
        }

        New-Box `
            -Frame (Read-IntValue $row "frame") `
            -Id $id `
            -X (Read-DoubleValue $row "x") `
            -Y (Read-DoubleValue $row "y") `
            -W (Read-DoubleValue $row "w") `
            -H (Read-DoubleValue $row "h")
    }
}

function Read-PredictionCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Prediction CSV not found: $resolved"
    }

    $rows = @(Import-Csv $resolved)
    foreach ($row in $rows) {
        $id = $row.predictionId
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = $row.id
        }

        New-Box `
            -Frame (Read-IntValue $row "frame") `
            -Id $id `
            -X (Read-DoubleValue $row "x") `
            -Y (Read-DoubleValue $row "y") `
            -W (Read-DoubleValue $row "w") `
            -H (Read-DoubleValue $row "h") `
            -Confidence (Read-DoubleValue $row "confidence")
    }
}

function Read-PredictionLog {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Prediction log not found: $resolved"
    }

    $pattern = '\[SmokeDetection\].*frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=[0-9.\-]+, conf=([0-9.\-]+)'
    foreach ($line in (Get-Content -Path $resolved)) {
        $match = [regex]::Match($line, $pattern)
        if (-not $match.Success) {
            continue
        }

        New-Box `
            -Frame ([int]$match.Groups[1].Value) `
            -Id $match.Groups[2].Value `
            -X ([double]::Parse($match.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -Y ([double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -W ([double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -H ([double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -Confidence ([double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture))
    }
}

function Measure-GtMatch {
    param(
        [object[]]$Gt,
        [object[]]$Predictions,
        [double]$IouThreshold
    )

    $matchedPredictionKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    $truePositive = 0
    $miss = 0
    $lowIou = 0
    $iouSum = 0.0
    $minIouSeen = 1.0

    foreach ($gtBox in $Gt) {
        $framePredictions = @($Predictions | Where-Object { $_.Frame -eq $gtBox.Frame })
        $best = $null
        $bestKey = ""
        $bestIou = 0.0

        foreach ($prediction in $framePredictions) {
            $key = "$($prediction.Frame):$($prediction.Id):$($prediction.X):$($prediction.Y):$($prediction.W):$($prediction.H)"
            if ($matchedPredictionKeys.Contains($key)) {
                continue
            }

            $iou = Get-Iou $gtBox $prediction
            if ($iou -gt $bestIou) {
                $best = $prediction
                $bestKey = $key
                $bestIou = $iou
            }
        }

        if ($best -ne $null -and $bestIou -ge $IouThreshold) {
            [void]$matchedPredictionKeys.Add($bestKey)
            $truePositive++
            $iouSum += $bestIou
            $minIouSeen = [Math]::Min($minIouSeen, $bestIou)
        }
        else {
            $miss++
            if ($best -ne $null -and $bestIou -gt 0.0) {
                $lowIou++
            }
        }
    }

    $falsePositive = 0
    foreach ($prediction in $Predictions) {
        $key = "$($prediction.Frame):$($prediction.Id):$($prediction.X):$($prediction.Y):$($prediction.W):$($prediction.H)"
        if (-not $matchedPredictionKeys.Contains($key)) {
            $falsePositive++
        }
    }

    $avgIou = if ($truePositive -gt 0) { $iouSum / [double]$truePositive } else { 0.0 }
    if ($truePositive -eq 0) {
        $minIouSeen = 0.0
    }

    [pscustomobject]@{
        GtFaces = $Gt.Count
        Predictions = $Predictions.Count
        TruePositive = $truePositive
        Miss = $miss
        FalsePositive = $falsePositive
        LowIou = $lowIou
        AvgIou = [Math]::Round($avgIou, 4)
        MinIou = [Math]::Round($minIouSeen, 4)
        MinIouThreshold = $IouThreshold
    }
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

    Write-Host "[YoloFullGtLabelVerify] pass $Name=$Actual"
}

if ($SelfTest -or ([string]::IsNullOrWhiteSpace($GtCsv) -and [string]::IsNullOrWhiteSpace($PredictionCsv) -and [string]::IsNullOrWhiteSpace($PredictionLog) -and -not $RequireData)) {
    $gt = @(
        (New-Box -Frame 10 -Id "face-a" -X 100 -Y 100 -W 80 -H 80),
        (New-Box -Frame 20 -Id "face-b" -X 200 -Y 120 -W 60 -H 60)
    )
    $predictions = @(
        (New-Box -Frame 10 -Id "pred-a" -X 104 -Y 104 -W 78 -H 78 -Confidence 0.92),
        (New-Box -Frame 30 -Id "pred-fp" -X 400 -Y 300 -W 40 -H 40 -Confidence 0.41)
    )
    $summary = Measure-GtMatch -Gt $gt -Predictions $predictions -IouThreshold $MinIou
    Assert-Equal "selftest gt faces" $summary.GtFaces 2
    Assert-Equal "selftest predictions" $summary.Predictions 2
    Assert-Equal "selftest true positive" $summary.TruePositive 1
    Assert-Equal "selftest miss" $summary.Miss 1
    Assert-Equal "selftest false positive" $summary.FalsePositive 1

    $missLabelGt = @(
        [pscustomobject]@{ frame = "40"; gtId = "miss-face"; label = "miss"; x = "600"; y = "220"; w = "50"; h = "50" }
    )
    $missLabelCsv = Join-Path $repo ".tmp\yolo-full-gt-label-state\miss-label-gt.csv"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $missLabelCsv) | Out-Null
    $missLabelGt | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $missLabelCsv
    $missLabelRows = @(Read-GtCsv $missLabelCsv)
    Assert-Equal "selftest explicit miss label gt rows" $missLabelRows.Count 1

    Write-Host "[YoloFullGtLabelSummary] mode=selftest, gtFaces=$($summary.GtFaces), predictions=$($summary.Predictions), truePositive=$($summary.TruePositive), miss=$($summary.Miss), falsePositive=$($summary.FalsePositive), lowIou=$($summary.LowIou), avgIou=$($summary.AvgIou), minIou=$($summary.MinIou), minIouThreshold=$($summary.MinIouThreshold)"
    Write-Host "[YoloFullGtLabelVerify] all requested checks passed"
    return
}

if ([string]::IsNullOrWhiteSpace($GtCsv)) {
    throw "GtCsv is required unless -SelfTest is used."
}

if ([string]::IsNullOrWhiteSpace($PredictionCsv) -and [string]::IsNullOrWhiteSpace($PredictionLog)) {
    throw "PredictionCsv or PredictionLog is required unless -SelfTest is used."
}

$gtRows = @(Read-GtCsv $GtCsv)
$predictionRows = if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
    @(Read-PredictionCsv $PredictionCsv)
}
else {
    @(Read-PredictionLog $PredictionLog)
}

if ($gtRows.Count -eq 0) {
    throw "No GT face rows were found."
}

$result = Measure-GtMatch -Gt $gtRows -Predictions $predictionRows -IouThreshold $MinIou
Write-Host "[YoloFullGtLabelSummary] mode=data, gtFaces=$($result.GtFaces), predictions=$($result.Predictions), truePositive=$($result.TruePositive), miss=$($result.Miss), falsePositive=$($result.FalsePositive), lowIou=$($result.LowIou), avgIou=$($result.AvgIou), minIou=$($result.MinIou), minIouThreshold=$($result.MinIouThreshold)"

$passed = $result.Miss -le $MaxMisses -and
    $result.FalsePositive -le $MaxFalsePositives -and
    $result.LowIou -le $MaxLowIou

Write-Host "[YoloFullGtLabelQualityGate] passed=$passed, maxMisses=$MaxMisses, maxFalsePositives=$MaxFalsePositives, maxLowIou=$MaxLowIou"
if (-not $passed) {
    if ($AllowQualityGateFailure) {
        Write-Host "[YoloFullGtLabelQualityGate] failureAllowed=True"
        Write-Host "[YoloFullGtLabelVerify] all requested checks passed"
        return
    }

    exit 2
}

Write-Host "[YoloFullGtLabelVerify] all requested checks passed"
