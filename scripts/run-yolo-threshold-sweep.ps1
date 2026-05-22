param(
    [string[]]$Sources = @(".tmp/srcTest-smoke/smoke-0600-3s.mp4"),
    [string]$YoloModelPath = ".tmp/models/YoloV5Face.onnx",
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string]$YoloModelType = "Yolo5Face",
    [int[]]$InputSizes = @(640),
    [double[]]$ObjectnessThresholds = @(0.10, 0.12, 0.15, 0.18, 0.25),
    [double[]]$ConfidenceThresholds = @(0.10, 0.18, 0.25),
    [double[]]$NmsThresholds = @(0.45),
    [int]$ParallelDetectorCount = 2,
    [double]$StrictMinAvgIou = 0.90,
    [double]$StrictMinBestIou = 0.75,
    [switch]$IncludeTiling,
    [int]$YoloTileColumns = 2,
    [int]$YoloTileRows = 2,
    [double]$YoloTileOverlapRatio = 0.15,
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
    $OutputCsv = Join-Path $outDir "yolo-sweep-$timestamp.csv"
}
elseif (-not [IO.Path]::IsPathRooted($OutputCsv)) {
    $OutputCsv = Join-Path $repo $OutputCsv
}

if ([string]::IsNullOrWhiteSpace($OutputLog)) {
    $OutputLog = Join-Path $outDir "yolo-sweep-$timestamp.log"
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
    return $Value.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture)
}

$rows = New-Object System.Collections.Generic.List[object]
$caseIndex = 0

foreach ($source in $Sources) {
    $sourcePath = Join-Path $repo $source
    if (-not (Test-Path $sourcePath)) {
        throw "Source clip not found: $sourcePath"
    }

    foreach ($inputSize in $InputSizes) {
        foreach ($objectness in $ObjectnessThresholds) {
            foreach ($confidence in $ConfidenceThresholds) {
                foreach ($nms in $NmsThresholds) {
                    foreach ($tiling in @($false, $true)) {
                        if ($tiling -and -not $IncludeTiling.IsPresent) {
                            continue
                        }

                        $caseIndex++
                        if ($MaxCases -gt 0 -and $caseIndex -gt $MaxCases) {
                            break
                        }

                        $caseLabel = "case=$caseIndex source=$source model=$YoloModelType input=$inputSize obj=$(Format-Number $objectness) conf=$(Format-Number $confidence) nms=$(Format-Number $nms) tiling=$tiling"
                        Write-Host "[YoloSweep] start $caseLabel"
                        Add-Content -Path $OutputLog -Value ""
                        Add-Content -Path $OutputLog -Value "[YoloSweep] start $caseLabel"

                        $smokeArgs = @(
                            "-SkipTrim",
                            "-Source", $source,
                            "-SkipExport",
                            "-OptimizedCpuOnly",
                            "-AllowFrameMismatch",
                            "-MinAvgIou", "0",
                            "-MinBestIou", "0",
                            "-ParallelDetectorCount", $ParallelDetectorCount.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                            "-YoloModelPath", $YoloModelPath,
                            "-YoloModelType", $YoloModelType,
                            "-YoloInputSize", $inputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                            "-YoloObjectnessThreshold", (Format-Number $objectness),
                            "-YoloConfidenceThreshold", (Format-Number $confidence),
                            "-YoloNmsThreshold", (Format-Number $nms)
                        )

                        if ($tiling) {
                            $smokeArgs += @(
                                "-YoloUseTiling",
                                "-YoloTileColumns", $YoloTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                                "-YoloTileRows", $YoloTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                                "-YoloTileOverlapRatio", (Format-Number $YoloTileOverlapRatio)
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
                        $avgBaselineCoverageValue = Read-Double $compareMap "avgBaselineCoverage"
                        $minBaselineCoverageValue = Read-Double $compareMap "minBaselineCoverage"
                        $strictFrameMatchOk = $onlyBaselineValue -eq 0 -and $onlyOptimizedValue -eq 0 -and $boxCountDiffValue -eq 0
                        $strictIouOk = $avgBestIouValue -ne $null -and $minBestIouValue -ne $null -and $avgBestIouValue -ge $StrictMinAvgIou -and $minBestIouValue -ge $StrictMinBestIou

                        $row = [pscustomobject]@{
                            Source = $source
                            ModelType = $YoloModelType
                            ModelPath = $YoloModelPath
                            InputSize = $inputSize
                            Objectness = $objectness
                            Confidence = $confidence
                            Nms = $nms
                            Tiling = $tiling
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
                            AvgBaselineCoverage = $avgBaselineCoverageValue
                            MinBaselineCoverage = $minBaselineCoverageValue
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

                        $rows.Add($row)
                        $row | Export-Csv -NoTypeInformation -Path $OutputCsv -Append
                        Write-Host ("[YoloSweep] done case={0} totalMs={1} baseline={2} optimized={3} onlyBaseline={4} onlyOptimized={5} avgIou={6} minIou={7} avgCoverage={8} minCoverage={9} boxDiff={10}" -f `
                            $caseIndex,
                            $row.TotalMs,
                            $row.BaselineFrames,
                            $row.FaceMaskFrames,
                            $row.OnlyBaseline,
                            $row.OnlyOptimized,
                            $row.AvgBestIou,
                            $row.MinBestIou,
                            $row.AvgBaselineCoverage,
                            $row.MinBaselineCoverage,
                            $row.BoxCountDiffFrames)
                    }

                    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
                        break
                    }
                }

                if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
                    break
                }
            }

            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
                break
            }
        }

        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) {
            break
        }
    }
}

Write-Host "[YoloSweep] csv=$OutputCsv"
Write-Host "[YoloSweep] log=$OutputLog"

if ($rows.Count -gt 0) {
    Write-Host "[YoloSweep] best by baseline-diff:"
    $rows |
        Sort-Object `
            @{ Expression = { if ($_.OnlyBaseline -ne $null) { $_.OnlyBaseline } else { 999999 } } }, `
            @{ Expression = { if ($_.OnlyOptimized -ne $null) { $_.OnlyOptimized } else { 999999 } } }, `
            @{ Expression = { if ($_.BoxCountDiffFrames -ne $null) { $_.BoxCountDiffFrames } else { 999999 } } }, `
            @{ Expression = { if ($_.AvgBestIou -ne $null) { -1 * $_.AvgBestIou } else { 999999 } } }, `
            @{ Expression = { if ($_.TotalMs -ne $null) { $_.TotalMs } else { 999999 } } } |
        Select-Object -First 5 Source, ModelType, InputSize, Objectness, Confidence, Nms, Tiling, TotalMs, BaselineFrames, FaceMaskFrames, OnlyBaseline, OnlyOptimized, AvgBestIou, MinBestIou, AvgBaselineCoverage, MinBaselineCoverage, BoxCountDiffFrames |
        Format-Table -AutoSize
}
