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
    [switch]$IncludeTileOnly,
    [int]$YoloTileColumns = 2,
    [int]$YoloTileRows = 2,
    [double]$YoloTileOverlapRatio = 0.15,
    [switch]$IncludeLargeBoxScale,
    [double[]]$YoloLargeBoxWidthScales = @(1.0),
    [double[]]$YoloLargeBoxHeightScales = @(1.0),
    [double[]]$YoloLargeBoxMinAreaRatios = @(0.0),
    [switch]$IncludeLandmarkBoxRefine,
    [double[]]$YoloLandmarkBoxMinAreaRatios = @(0.03),
    [double[]]$YoloLandmarkBoxWidthScales = @(1.80),
    [double[]]$YoloLandmarkBoxHeightScales = @(2.10),
    [double[]]$YoloLandmarkBoxCenterYOffsetRatios = @(-0.04),
    [double[]]$YoloLandmarkBoxMinOriginalIous = @(0.30),
    [switch]$IncludeFaceOnnxRoiRefine,
    [double[]]$YoloFaceOnnxRoiMinAreaRatios = @(0.03),
    [int[]]$YoloFaceOnnxRoiMaxCandidates = @(32),
    [switch]$IncludeTrackPostProcess,
    [int[]]$YoloDropShortTrackMaxDetections = @(1),
    [double[]]$YoloShortTrackMaxConfidences = @(0.18),
    [double[]]$YoloLowerFrameTrackMaxConfidences = @(0.50),
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
                    $tilingModes = @([pscustomobject]@{ Tiling = $false; TileOnly = $false })
                    if ($IncludeTiling.IsPresent) {
                        $tilingModes += [pscustomobject]@{ Tiling = $true; TileOnly = $false }
                        if ($IncludeTileOnly.IsPresent) {
                            $tilingModes += [pscustomobject]@{ Tiling = $true; TileOnly = $true }
                        }
                    }
                    $largeBoxScaleValues = if ($IncludeLargeBoxScale.IsPresent) { @($true) } else { @($false) }
                    $landmarkRefineValues = if ($IncludeLandmarkBoxRefine.IsPresent) { @($false, $true) } else { @($false) }
                    $faceOnnxRoiRefineValues = if ($IncludeFaceOnnxRoiRefine.IsPresent) { @($false, $true) } else { @($false) }
                    $dropShortTrackValues = if ($IncludeTrackPostProcess.IsPresent) { $YoloDropShortTrackMaxDetections } else { @(1) }
                    $shortTrackConfidenceValues = if ($IncludeTrackPostProcess.IsPresent) { $YoloShortTrackMaxConfidences } else { @(0.18) }
                    $lowerFrameConfidenceValues = if ($IncludeTrackPostProcess.IsPresent) { $YoloLowerFrameTrackMaxConfidences } else { @(0.50) }

                    foreach ($tilingMode in $tilingModes) {
                        $tiling = [bool]$tilingMode.Tiling
                        $tileOnly = [bool]$tilingMode.TileOnly
                        foreach ($useLargeBoxScale in $largeBoxScaleValues) {
                            $largeBoxWidthScaleValues = if ($useLargeBoxScale) { $YoloLargeBoxWidthScales } else { @(1.0) }
                            $largeBoxHeightScaleValues = if ($useLargeBoxScale) { $YoloLargeBoxHeightScales } else { @(1.0) }
                            $largeBoxMinAreaRatioValues = if ($useLargeBoxScale) { $YoloLargeBoxMinAreaRatios } else { @(0.0) }
                            foreach ($largeBoxWidthScale in $largeBoxWidthScaleValues) {
                                foreach ($largeBoxHeightScale in $largeBoxHeightScaleValues) {
                                    foreach ($largeBoxMinAreaRatio in $largeBoxMinAreaRatioValues) {
                                        foreach ($useLandmarkRefine in $landmarkRefineValues) {
                                            $landmarkMinAreaRatioValues = if ($useLandmarkRefine) { $YoloLandmarkBoxMinAreaRatios } else { @(0.03) }
                                            $landmarkWidthScaleValues = if ($useLandmarkRefine) { $YoloLandmarkBoxWidthScales } else { @(1.80) }
                                            $landmarkHeightScaleValues = if ($useLandmarkRefine) { $YoloLandmarkBoxHeightScales } else { @(2.10) }
                                            $landmarkCenterYOffsetValues = if ($useLandmarkRefine) { $YoloLandmarkBoxCenterYOffsetRatios } else { @(-0.04) }
                                            $landmarkMinOriginalIouValues = if ($useLandmarkRefine) { $YoloLandmarkBoxMinOriginalIous } else { @(0.30) }
                                            foreach ($landmarkMinAreaRatio in $landmarkMinAreaRatioValues) {
                                                foreach ($landmarkWidthScale in $landmarkWidthScaleValues) {
                                                    foreach ($landmarkHeightScale in $landmarkHeightScaleValues) {
                                                        foreach ($landmarkCenterYOffset in $landmarkCenterYOffsetValues) {
                                                            foreach ($landmarkMinOriginalIou in $landmarkMinOriginalIouValues) {
                                                                foreach ($useFaceOnnxRoiRefine in $faceOnnxRoiRefineValues) {
                                                                    $faceOnnxRoiMinAreaRatioValues = if ($useFaceOnnxRoiRefine) { $YoloFaceOnnxRoiMinAreaRatios } else { @(0.03) }
                                                                    $faceOnnxRoiMaxCandidateValues = if ($useFaceOnnxRoiRefine) { $YoloFaceOnnxRoiMaxCandidates } else { @(32) }
                                                                    foreach ($faceOnnxRoiMinAreaRatio in $faceOnnxRoiMinAreaRatioValues) {
                                                                        foreach ($faceOnnxRoiMaxCandidates in $faceOnnxRoiMaxCandidateValues) {
                                                                            foreach ($dropShortTrackMaxDetections in $dropShortTrackValues) {
                                                                                foreach ($shortTrackMaxConfidence in $shortTrackConfidenceValues) {
                                                                                    foreach ($lowerFrameTrackMaxConfidence in $lowerFrameConfidenceValues) {
                                                                $caseIndex++
                                                                if ($MaxCases -gt 0 -and $caseIndex -gt $MaxCases) {
                                                                    break
                                                                }

                                                                $caseLabel = "case=$caseIndex source=$source model=$YoloModelType input=$inputSize obj=$(Format-Number $objectness) conf=$(Format-Number $confidence) nms=$(Format-Number $nms) tiling=$tiling tileOnly=$tileOnly largeBoxScale=$useLargeBoxScale largeBoxWidth=$(Format-Number $largeBoxWidthScale) largeBoxHeight=$(Format-Number $largeBoxHeightScale) largeBoxMinArea=$(Format-Number $largeBoxMinAreaRatio) landmarkRefine=$useLandmarkRefine landmarkMinArea=$(Format-Number $landmarkMinAreaRatio) landmarkWidth=$(Format-Number $landmarkWidthScale) landmarkHeight=$(Format-Number $landmarkHeightScale) landmarkCenterYOffset=$(Format-Number $landmarkCenterYOffset) landmarkMinOriginalIou=$(Format-Number $landmarkMinOriginalIou) faceOnnxRoiRefine=$useFaceOnnxRoiRefine faceOnnxRoiMinArea=$(Format-Number $faceOnnxRoiMinAreaRatio) faceOnnxRoiMaxCandidates=$faceOnnxRoiMaxCandidates dropShortTrackMaxDetections=$dropShortTrackMaxDetections shortTrackMaxConfidence=$(Format-Number $shortTrackMaxConfidence) lowerFrameTrackMaxConfidence=$(Format-Number $lowerFrameTrackMaxConfidence)"
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
                            "-YoloNmsThreshold", (Format-Number $nms),
                            "-YoloLargeBoxWidthScale", (Format-Number $largeBoxWidthScale),
                            "-YoloLargeBoxHeightScale", (Format-Number $largeBoxHeightScale),
                            "-YoloLargeBoxMinAreaRatio", (Format-Number $largeBoxMinAreaRatio),
                            "-YoloLandmarkBoxMinAreaRatio", (Format-Number $landmarkMinAreaRatio),
                            "-YoloLandmarkBoxWidthScale", (Format-Number $landmarkWidthScale),
                            "-YoloLandmarkBoxHeightScale", (Format-Number $landmarkHeightScale),
                            "-YoloLandmarkBoxCenterYOffsetRatio", (Format-Number $landmarkCenterYOffset),
                            "-YoloLandmarkBoxMinOriginalIou", (Format-Number $landmarkMinOriginalIou),
                            "-YoloDropShortTrackMaxDetections", $dropShortTrackMaxDetections.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                            "-YoloShortTrackMaxConfidence", (Format-Number $shortTrackMaxConfidence),
                            "-YoloLowerFrameTrackMaxConfidence", (Format-Number $lowerFrameTrackMaxConfidence)
                        )

                        if ($tiling) {
                            $smokeArgs += @(
                                "-YoloUseTiling",
                                "-YoloTileColumns", $YoloTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                                "-YoloTileRows", $YoloTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                                "-YoloTileOverlapRatio", (Format-Number $YoloTileOverlapRatio)
                            )
                            if ($tileOnly) {
                                $smokeArgs += @("-YoloTileOnly")
                            }
                        }
                        if ($useLandmarkRefine) {
                            $smokeArgs += @("-YoloUseLandmarkBoxRefine")
                        }
                        if ($useFaceOnnxRoiRefine) {
                            $smokeArgs += @(
                                "-YoloUseFaceOnnxRoiRefine",
                                "-YoloFaceOnnxRoiMinAreaRatio", (Format-Number $faceOnnxRoiMinAreaRatio),
                                "-YoloFaceOnnxRoiMaxCandidates", $faceOnnxRoiMaxCandidates.ToString([System.Globalization.CultureInfo]::InvariantCulture)
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
                        $yoloRoiLine = ($output | Select-String "\[SmokeYoloFaceOnnxRoiRefine\]" | Select-Object -Last 1).Line

                        $autoMap = if ($autoRunLine) { ConvertTo-Map $autoRunLine } else { @{} }
                        $compareMap = if ($compareLine) { ConvertTo-Map $compareLine } else { @{} }
                        $qualityMap = if ($qualityLine) { ConvertTo-Map $qualityLine } else { @{} }
                        $postMap = if ($postLine) { ConvertTo-Map $postLine } else { @{} }
                        $yoloRoiMap = if ($yoloRoiLine) { ConvertTo-Map $yoloRoiLine } else { @{} }

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
                            TileOnly = $tileOnly
                            LargeBoxScale = $useLargeBoxScale
                            LargeBoxWidthScale = $largeBoxWidthScale
                            LargeBoxHeightScale = $largeBoxHeightScale
                            LargeBoxMinAreaRatio = $largeBoxMinAreaRatio
                            LandmarkBoxRefine = $useLandmarkRefine
                            LandmarkBoxMinAreaRatio = $landmarkMinAreaRatio
                            LandmarkBoxWidthScale = $landmarkWidthScale
                            LandmarkBoxHeightScale = $landmarkHeightScale
                            LandmarkBoxCenterYOffsetRatio = $landmarkCenterYOffset
                            LandmarkBoxMinOriginalIou = $landmarkMinOriginalIou
                            FaceOnnxRoiRefine = $useFaceOnnxRoiRefine
                            FaceOnnxRoiMinAreaRatio = $faceOnnxRoiMinAreaRatio
                            FaceOnnxRoiMaxCandidates = $faceOnnxRoiMaxCandidates
                            DropShortTrackMaxDetections = $dropShortTrackMaxDetections
                            ShortTrackMaxConfidence = $shortTrackMaxConfidence
                            LowerFrameTrackMaxConfidence = $lowerFrameTrackMaxConfidence
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
                            FaceOnnxRoiAttempts = Read-Int $yoloRoiMap "attempts"
                            FaceOnnxRoiHits = Read-Int $yoloRoiMap "hits"
                            FaceOnnxRoiElapsedMs = Read-Int $yoloRoiMap "elapsedMs"
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
                                                                                    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                                                }
                                                                                if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                                            }
                                                                            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                                        }
                                                                        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                                    }
                                                                    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                                }
                                                                if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                            }
                                                            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                        }
                                                        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                    }
                                                    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                                }
                                                if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                            }
                                            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                        }
                                        if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                    }
                                    if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                                }
                                if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
                            }
                            if ($MaxCases -gt 0 -and $caseIndex -ge $MaxCases) { break }
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
        Select-Object -First 5 Source, ModelType, InputSize, Objectness, Confidence, Nms, Tiling, TileOnly, TotalMs, BaselineFrames, FaceMaskFrames, OnlyBaseline, OnlyOptimized, AvgBestIou, MinBestIou, AvgBaselineCoverage, MinBaselineCoverage, BoxCountDiffFrames |
        Format-Table -AutoSize
}
