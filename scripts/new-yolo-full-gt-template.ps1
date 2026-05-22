param(
    [string]$PredictionCsv = "",
    [string]$PredictionLog = "",
    [string]$OutputCsv = ".tmp\yolo-full-gt\full-gt-template.csv",
    [int]$EveryNFrames = 1,
    [int]$MaxRows = 0
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

function New-TemplateRow {
    param(
        [int]$Frame,
        [string]$SourcePredictionId,
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [double]$Confidence,
        [string]$Source
    )

    [pscustomobject]@{
        frame = $Frame
        gtId = ""
        label = ""
        x = $X.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
        y = $Y.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
        w = $W.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
        h = $H.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
        sourcePredictionId = $SourcePredictionId
        sourceConfidence = $Confidence.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture)
        source = $Source
        notes = "Set label=face for real faces. Leave blank or set nonface for detector false positives. Add rows for visible missed faces."
    }
}

function Read-PredictionCsvRows {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Prediction CSV not found: $resolved"
    }

    foreach ($row in (Import-Csv $resolved)) {
        $id = $row.predictionId
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = $row.id
        }

        New-TemplateRow `
            -Frame (Read-IntValue $row "frame") `
            -SourcePredictionId $id `
            -X (Read-DoubleValue $row "x") `
            -Y (Read-DoubleValue $row "y") `
            -W (Read-DoubleValue $row "w") `
            -H (Read-DoubleValue $row "h") `
            -Confidence (Read-DoubleValue $row "confidence") `
            -Source "prediction-csv"
    }
}

function Read-PredictionLogRows {
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

        New-TemplateRow `
            -Frame ([int]$match.Groups[1].Value) `
            -SourcePredictionId $match.Groups[2].Value `
            -X ([double]::Parse($match.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -Y ([double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -W ([double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -H ([double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -Confidence ([double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
            -Source "smoke-detection-log"
    }
}

if ([string]::IsNullOrWhiteSpace($PredictionCsv) -and [string]::IsNullOrWhiteSpace($PredictionLog)) {
    throw "PredictionCsv or PredictionLog is required."
}

$rows = if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
    @(Read-PredictionCsvRows $PredictionCsv)
}
else {
    @(Read-PredictionLogRows $PredictionLog)
}

if ($EveryNFrames -gt 1) {
    $rows = @($rows | Where-Object { $_.frame % $EveryNFrames -eq 0 })
}

if ($MaxRows -gt 0) {
    $rows = @($rows | Select-Object -First $MaxRows)
}

if ($rows.Count -eq 0) {
    throw "No prediction rows were found for GT template."
}

$outputPath = Resolve-RepoPath $OutputCsv
$outputDir = Split-Path -Parent $outputPath
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$rows |
    Sort-Object frame, sourcePredictionId |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath

Write-Host "[YoloFullGtTemplate] wrote rows=$($rows.Count), output=$OutputCsv"
Write-Host "[YoloFullGtTemplate] label instructions: label=face rows are counted as GT; blank/nonface rows are ignored by GT evaluator; add rows manually for visible missed faces."
