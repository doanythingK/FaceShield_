param(
    [string]$PredictionLogPath = "",
    [string]$PredictionLogDir = ".tmp\yolo-ten-minute-detection-smoke",
    [string]$PredictionLogPattern = "yolo-ten-minute-yolo-only-*.log",
    [string]$OutputCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [int]$MinRows = 20
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$templateScript = Join-Path $repo "scripts\new-yolo-full-gt-template.ps1"

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

function Resolve-LatestLog {
    param(
        [string]$Path,
        [string]$Directory,
        [string]$Pattern
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return Resolve-RepoPath $Path
    }

    $resolvedDirectory = Resolve-RepoPath $Directory
    if (-not (Test-Path $resolvedDirectory)) {
        throw "Prediction log directory not found: $resolvedDirectory"
    }

    $latest = Get-ChildItem -Path $resolvedDirectory -Filter $Pattern -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        throw "No prediction log found in $resolvedDirectory with pattern $Pattern"
    }

    return $latest.FullName
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

    Write-Host "[YoloFullGtTemplateVerify] pass $Name"
}

function Assert-Column {
    param(
        [object]$Row,
        [string]$Name
    )

    if ($null -eq $Row.PSObject.Properties[$Name]) {
        throw "template missing column: $Name"
    }

    Write-Host "[YoloFullGtTemplateVerify] pass column $Name"
}

if (-not (Test-Path $templateScript)) {
    throw "Template script not found: $templateScript"
}

$predictionLog = Resolve-LatestLog $PredictionLogPath $PredictionLogDir $PredictionLogPattern
if (-not (Test-Path $predictionLog)) {
    throw "Prediction log not found: $predictionLog"
}

$logText = Get-Content -Raw -Path $predictionLog
Assert-Contains "prediction log has smoke detections" $logText "[SmokeDetection]"
Assert-Contains "prediction log has yolo detector" $logText "detector=YoloFaceOnnxDetector"

$outputPath = Resolve-RepoPath $OutputCsv
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $templateScript -PredictionLog $predictionLog -OutputCsv $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "GT template generation failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $outputPath)) {
    throw "GT template output not found: $outputPath"
}

$rows = @(Import-Csv $outputPath)
if ($rows.Count -lt $MinRows) {
    throw "GT template row count $($rows.Count) is below expected minimum $MinRows"
}

$first = $rows[0]
foreach ($column in @("frame", "gtId", "label", "x", "y", "w", "h", "sourcePredictionId", "sourceConfidence", "source", "notes")) {
    Assert-Column $first $column
}

$sourceValues = @($rows | Select-Object -ExpandProperty source -Unique)
if ($sourceValues.Count -ne 1 -or $sourceValues[0] -ne "smoke-detection-log") {
    throw "Unexpected template source values: $($sourceValues -join ',')"
}

$nonBlankLabels = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.label) })
if ($nonBlankLabels.Count -ne 0) {
    throw "Generated template should not pre-label GT rows; nonBlankLabels=$($nonBlankLabels.Count)"
}

Write-Host "[YoloFullGtTemplateVerify] pass rows=$($rows.Count), predictionLog=$predictionLog, output=$outputPath"
Write-Host "[YoloFullGtTemplateVerify] all requested checks passed"
