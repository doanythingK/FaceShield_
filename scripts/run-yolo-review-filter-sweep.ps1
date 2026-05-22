param(
    [string[]]$Sources = @(".tmp/srcTest-smoke/smoke-0600-3s.mp4"),
    [string]$YoloModelPath = ".tmp/models/YoloV5Face.onnx",
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string]$YoloModelType = "Yolo5Face",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [double[]]$MaxConfidences = @(0.35, 0.50),
    [double[]]$MinCenterYRatios = @(0.08, 0.10),
    [switch]$IncludeSmallAreaFilter,
    [double[]]$SmallAreaMaxAreaRatios = @(0.0030, 0.0035),
    [switch]$IncludeUnfilteredBaseline,
    [int]$ParallelDetectorCount = 2,
    [double]$StrictMinAvgIou = 0.90,
    [double]$StrictMinBestIou = 0.75,
    [int]$MaxCases = 0,
    [string]$OutputCsv = "",
    [string]$OutputLog = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$smokeScript = Join-Path $PSScriptRoot "run-srcTest-smoke.ps1"
if (-not (Test-Path $smokeScript)) {
    throw "Smoke script not found: $smokeScript"
}

$modelPath = Join-Path $repo $YoloModelPath
if (-not (Test-Path $modelPath)) {
    throw "YOLO model not found: $modelPath"
}

$outDir = Join-Path $repo ".tmp/yolo-sweep"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path $outDir "yolo-review-filter-$timestamp.csv"
}
elseif (-not [IO.Path]::IsPathRooted($OutputCsv)) {
    $OutputCsv = Join-Path $repo $OutputCsv
}

if ([string]::IsNullOrWhiteSpace($OutputLog)) {
    $OutputLog = Join-Path $outDir "yolo-review-filter-$timestamp.log"
}
elseif (-not [IO.Path]::IsPathRooted($OutputLog)) {
    $OutputLog = Join-Path $repo $OutputLog
}

function ConvertTo-Map {
    param([string]$Line)

    $map = @{}
    foreach ($match in [regex]::Matches($Line, "([A-Za-z][A-Za-z0-9]*)=([^,]+)")) {
        $map[$match.Groups[1].Value] = $match.Groups[2].Value.Trim()
    }

    return $map
}

function Read-Double {
    param(
        [hashtable]$Map,
        [string]$Key
    )

    if (-not $Map.ContainsKey($Key)) {
        return $null
    }

    return [double]::Parse($Map[$Key], [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-Int {
    param(
        [hashtable]$Map,
        [string]$Key
    )

    if (-not $Map.ContainsKey($Key)) {
        return $null
    }

    return [int]::Parse($Map[$Key], [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Number {
    param([double]$Value)
    return $Value.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-SmokeCase {
    param(
        [string]$Source,
        [bool]$UseFilter,
        [double]$MaxConfidence,
        [double]$MinCenterYRatio,
        [bool]$UseSmallAreaFilter,
        [double]$SmallAreaMaxAreaRatio,
        [int]$CaseIndex
    )

    $caseLabel = "case=$CaseIndex source=$Source model=$YoloModelType input=$YoloInputSize obj=$(Format-Number $YoloObjectnessThreshold) conf=$(Format-Number $YoloConfidenceThreshold) nms=$(Format-Number $YoloNmsThreshold) lowConfidencePositionFilter=$UseFilter maxConfidence=$(Format-Number $MaxConfidence) minCenterY=$(Format-Number $MinCenterYRatio) smallAreaFilter=$UseSmallAreaFilter smallAreaMax=$(Format-Number $SmallAreaMaxAreaRatio)"
    Write-Host "[YoloReviewFilterSweep] start $caseLabel"
    Add-Content -Path $OutputLog -Value ""
    Add-Content -Path $OutputLog -Value "[YoloReviewFilterSweep] start $caseLabel"

    $smokeArgs = @(
        "-SkipTrim",
        "-Source", $Source,
        "-SkipExport",
        "-OptimizedCpuOnly",
        "-AllowFrameMismatch",
        "-MinAvgIou", "0",
        "-MinBestIou", "0",
        "-ParallelDetectorCount", $ParallelDetectorCount.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "-YoloModelPath", $YoloModelPath,
        "-YoloModelType", $YoloModelType,
        "-YoloInputSize", $YoloInputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "-YoloObjectnessThreshold", (Format-Number $YoloObjectnessThreshold),
        "-YoloConfidenceThreshold", (Format-Number $YoloConfidenceThreshold),
        "-YoloNmsThreshold", (Format-Number $YoloNmsThreshold)
    )

    if ($UseFilter) {
        $smokeArgs += @(
            "-YoloUseLowConfidencePositionFilter",
            "-YoloLowConfidencePositionMaxConfidence", (Format-Number $MaxConfidence),
            "-YoloLowConfidencePositionMinCenterYRatio", (Format-Number $MinCenterYRatio)
        )
    }

    if ($UseSmallAreaFilter) {
        $smokeArgs += @(
            "-YoloUseSmallAreaFilter",
            "-YoloSmallAreaMaxAreaRatio", (Format-Number $SmallAreaMaxAreaRatio)
        )
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smokeScript @smokeArgs 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $outputText = $output | Out-String
    Add-Content -Path $OutputLog -Value $outputText

    $autoRunLine = ($output | Select-String "\[AutoRunSummary\]" | Select-Object -Last 1).Line
    $compareLine = ($output | Select-String "\[SmokeCompare\]" | Select-Object -Last 1).Line
    $qualityLine = ($output | Select-String "\[SmokeQualityGate\]" | Select-Object -Last 1).Line
    $postLine = ($output | Select-String "\[SmokeFaceTrackPost\]" | Select-Object -Last 1).Line

    $autoMap = if ($autoRunLine) { ConvertTo-Map $autoRunLine } else { @{} }
    $compareMap = if ($compareLine) { ConvertTo-Map $compareLine } else { @{} }
    $qualityMap = if ($qualityLine) { ConvertTo-Map $qualityLine } else { @{} }
    $postMap = if ($postLine) { ConvertTo-Map $postLine } else { @{} }

    $onlyBaselineValue = Read-Int $compareMap "onlyBaseline"
    $onlyOptimizedValue = Read-Int $compareMap "onlyOptimized"
    $boxCountDiffValue = Read-Int $compareMap "boxCountDiffFrames"
    $avgBestIouValue = Read-Double $compareMap "avgBestIou"
    $minBestIouValue = Read-Double $compareMap "minBestIou"
    $strictFrameMatchOk = $onlyBaselineValue -eq 0 -and $onlyOptimizedValue -eq 0 -and $boxCountDiffValue -eq 0
    $strictIouOk = $avgBestIouValue -ne $null -and $minBestIouValue -ne $null -and $avgBestIouValue -ge $StrictMinAvgIou -and $minBestIouValue -ge $StrictMinBestIou

    $row = [pscustomobject]@{
        Source = $Source
        ModelType = $YoloModelType
        ModelPath = $YoloModelPath
        InputSize = $YoloInputSize
        Objectness = $YoloObjectnessThreshold
        Confidence = $YoloConfidenceThreshold
        Nms = $YoloNmsThreshold
        LowConfidencePositionFilter = $UseFilter
        LowConfidencePositionMaxConfidence = if ($UseFilter) { $MaxConfidence } else { $null }
        LowConfidencePositionMinCenterYRatio = if ($UseFilter) { $MinCenterYRatio } else { $null }
        SmallAreaFilter = $UseSmallAreaFilter
        SmallAreaMaxAreaRatio = if ($UseSmallAreaFilter) { $SmallAreaMaxAreaRatio } else { $null }
        ExitCode = $exitCode
        Detector = $autoMap["detector"]
        TotalMs = Read-Int $autoMap "totalMs"
        DetectMs = Read-Int $autoMap "detectMs"
        Processed = Read-Int $autoMap "processed"
        FaceMaskFrames = Read-Int $compareMap "optimizedFrames"
        BaselineFrames = Read-Int $compareMap "baselineFrames"
        CommonFrames = Read-Int $compareMap "common"
        OnlyBaseline = $onlyBaselineValue
        OnlyOptimized = $onlyOptimizedValue
        AvgBestIou = $avgBestIouValue
        MinBestIou = $minBestIouValue
        AvgBaselineCoverage = Read-Double $compareMap "avgBaselineCoverage"
        MinBaselineCoverage = Read-Double $compareMap "minBaselineCoverage"
        BoxCountDiffFrames = $boxCountDiffValue
        CollectionGatePassed = $qualityMap["passed"]
        StrictFrameMatchOk = $strictFrameMatchOk
        StrictIouOk = $strictIouOk
        StrictGatePassed = $strictFrameMatchOk -and $strictIouOk
        FrameMatchOk = $qualityMap["frameMatchOk"]
        IouOk = $qualityMap["iouOk"]
        RemovedShort = Read-Int $postMap "removedShort"
        RemovedLower = Read-Int $postMap "removedLower"
        RewrittenFrames = Read-Int $postMap "rewritten"
    }

    $row | Export-Csv -NoTypeInformation -Path $OutputCsv -Append
    Write-Host ("[YoloReviewFilterSweep] done case={0} totalMs={1} baseline={2} optimized={3} onlyBaseline={4} onlyOptimized={5} avgIou={6} minIou={7} boxDiff={8} strict={9}" -f `
        $CaseIndex,
        $row.TotalMs,
        $row.BaselineFrames,
        $row.FaceMaskFrames,
        $row.OnlyBaseline,
        $row.OnlyOptimized,
        $row.AvgBestIou,
        $row.MinBestIou,
        $row.BoxCountDiffFrames,
        $row.StrictGatePassed)

    return $row
}

$rows = New-Object System.Collections.Generic.List[object]
$caseIndex = 0

foreach ($source in $Sources) {
    $sourcePath = Join-Path $repo $source
    if (-not (Test-Path $sourcePath)) {
        throw "Source clip not found: $source"
    }

    if ($IncludeUnfilteredBaseline.IsPresent) {
        $caseIndex++
        $rows.Add((Invoke-SmokeCase -Source $source -UseFilter $false -MaxConfidence 0 -MinCenterYRatio 1 -UseSmallAreaFilter $false -SmallAreaMaxAreaRatio 0 -CaseIndex $caseIndex))
        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
            break
        }
    }

    foreach ($maxConfidence in $MaxConfidences) {
        foreach ($minCenterYRatio in $MinCenterYRatios) {
            $caseIndex++
            $rows.Add((Invoke-SmokeCase -Source $source -UseFilter $true -MaxConfidence $maxConfidence -MinCenterYRatio $minCenterYRatio -UseSmallAreaFilter $false -SmallAreaMaxAreaRatio 0 -CaseIndex $caseIndex))
            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
                break
            }
        }

        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
            break
        }
    }

    if ($IncludeSmallAreaFilter.IsPresent) {
        foreach ($smallAreaMaxAreaRatio in $SmallAreaMaxAreaRatios) {
            $caseIndex++
            $rows.Add((Invoke-SmokeCase -Source $source -UseFilter $false -MaxConfidence 0 -MinCenterYRatio 1 -UseSmallAreaFilter $true -SmallAreaMaxAreaRatio $smallAreaMaxAreaRatio -CaseIndex $caseIndex))
            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
                break
            }
        }
    }

    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
        break
    }
}

Write-Host "[YoloReviewFilterSweep] csv=$OutputCsv"
Write-Host "[YoloReviewFilterSweep] log=$OutputLog"

$rows |
    Sort-Object @{ Expression = "StrictGatePassed"; Descending = $true }, OnlyBaseline, OnlyOptimized, BoxCountDiffFrames, @{ Expression = "AvgBestIou"; Descending = $true }, TotalMs |
    Select-Object -First 10 Source, LowConfidencePositionFilter, LowConfidencePositionMaxConfidence, LowConfidencePositionMinCenterYRatio, SmallAreaFilter, SmallAreaMaxAreaRatio, TotalMs, BaselineFrames, FaceMaskFrames, OnlyBaseline, OnlyOptimized, AvgBestIou, MinBestIou, BoxCountDiffFrames, StrictGatePassed |
    Format-Table -AutoSize
