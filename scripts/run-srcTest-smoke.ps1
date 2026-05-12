param(
    [string]$Source = "srcTest\260102_jp_10.mp4",
    [string]$Start = "00:02:00",
    [int]$Seconds = 10,
    [switch]$SkipBaseline,
    [switch]$SkipTrim,
    [double]$DownscaleRatio = 1.0,
    [ValidateSet("FastNearest", "BalancedBilinear")]
    [string]$DownscaleQuality = "BalancedBilinear",
    [switch]$OptimizedNoTracking,
    [switch]$OptimizedUseGpu,
    [switch]$OptimizedCpuOnly,
    [switch]$SkipExport,
    [switch]$UseAutoTune,
    [int]$OptimizedDetectEvery = 1,
    [int]$ParallelDetectorCount = 2,
    [double]$DetectionThreshold = 0.2,
    [double]$ConfidenceThreshold = 0.25,
    [double]$NmsThreshold = 0.7,
    [double]$MinAvgIou = 0.90,
    [double]$MinBestIou = 0.75,
    [switch]$AllowFrameMismatch,
    [switch]$DumpDetections,
    [string]$ScrfdModelPath = "",
    [switch]$ScrfdUseBgr,
    [switch]$ScrfdStretchInput,
    [string]$YuNetModelPath = "",
    [switch]$YuNetUseTiling,
    [switch]$YuNetTileOnly,
    [int]$YuNetTileColumns = 2,
    [int]$YuNetTileRows = 2,
    [double]$YuNetTileOverlapRatio = 0.15
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sourcePath = Join-Path $repo $Source
if (-not (Test-Path $sourcePath)) {
    throw "Source video not found: $sourcePath"
}

$work = Join-Path $repo ".tmp\srcTest-smoke"
$clip = if ($SkipTrim) { $sourcePath } else { Join-Path $work "smoke-${Seconds}s.mp4" }
$clipStem = [IO.Path]::GetFileNameWithoutExtension($clip)
$output = Join-Path $work "${clipStem}_blur.mp4"
$harness = Join-Path $work ("harness-" + [Guid]::NewGuid().ToString("N"))
$project = Join-Path $harness "SmokeHarness.csproj"
$program = Join-Path $harness "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $harness | Out-Null

if (-not $SkipTrim) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) {
        throw "Windows PATH에서 ffmpeg를 찾지 못했습니다. WSL ffmpeg 등으로 짧은 클립을 먼저 만든 뒤 -SkipTrim -Source <clip>으로 실행하세요."
    }

    ffmpeg -y -hide_banner -loglevel error -ss $Start -t $Seconds -i $sourcePath -c copy $clip
}

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;

string input = args[0];
string output = args[1];
double optimizedDownscaleRatio = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
var optimizedDownscaleQuality = Enum.Parse<DownscaleQuality>(args[5]);
bool optimizedUseTracking = bool.Parse(args[6]);
bool optimizedUseGpu = bool.Parse(args[7]);
bool skipExport = bool.Parse(args[8]);
bool useAutoTune = bool.Parse(args[9]);
int optimizedDetectEvery = int.Parse(args[10], System.Globalization.CultureInfo.InvariantCulture);
int optimizedParallel = int.Parse(args[11], System.Globalization.CultureInfo.InvariantCulture);
double minAvgIou = double.Parse(args[12], System.Globalization.CultureInfo.InvariantCulture);
double minBestIou = double.Parse(args[13], System.Globalization.CultureInfo.InvariantCulture);
bool allowFrameMismatch = bool.Parse(args[14]);
bool dumpDetections = bool.Parse(args[15]);
float detectionThreshold = float.Parse(args[16], System.Globalization.CultureInfo.InvariantCulture);
float confidenceThreshold = float.Parse(args[17], System.Globalization.CultureInfo.InvariantCulture);
float nmsThreshold = float.Parse(args[18], System.Globalization.CultureInfo.InvariantCulture);
string scrfdModelPath = args.Length > 19 && args[19] != "__none__" ? args[19] : string.Empty;
bool scrfdUseRgb = args.Length <= 20 || !bool.Parse(args[20]);
bool scrfdUseLetterbox = args.Length <= 21 || !bool.Parse(args[21]);
string yuNetModelPath = args.Length > 22 && args[22] != "__none__" ? args[22] : string.Empty;
bool yuNetUseTiling = args.Length > 23 && bool.Parse(args[23]);
bool yuNetTileOnly = args.Length > 24 && bool.Parse(args[24]);
int yuNetTileColumns = args.Length > 25 ? int.Parse(args[25], System.Globalization.CultureInfo.InvariantCulture) : 2;
int yuNetTileRows = args.Length > 26 ? int.Parse(args[26], System.Globalization.CultureInfo.InvariantCulture) : 2;
double yuNetTileOverlapRatio = args.Length > 27 ? double.Parse(args[27], System.Globalization.CultureInfo.InvariantCulture) : 0.15;

Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
Trace.AutoFlush = true;

FFmpegBootstrap.Initialize();

static async Task<(string Label, FrameMaskProvider MaskProvider)> RunCaseAsync(
    string label,
    string input,
    string output,
    bool useTracking,
    int detectEvery,
    double downscaleRatio,
    DownscaleQuality downscaleQuality,
    bool useGpu,
    bool skipExport,
    bool useAutoTune,
    int parallelDetectorCount,
    bool dumpDetections,
    float detectionThreshold,
    float confidenceThreshold,
    float nmsThreshold,
    string scrfdModelPath,
    bool scrfdUseRgb,
    bool scrfdUseLetterbox,
    string yuNetModelPath,
    bool yuNetUseTiling,
    bool yuNetTileOnly,
    int yuNetTileColumns,
    int yuNetTileRows,
    double yuNetTileOverlapRatio)
{
    string runId = $"smoke-{label}-{Guid.NewGuid():N}";
    Console.WriteLine($"[SmokeCase] start runId={runId}, label={label}, tracking={useTracking}, everyN={detectEvery}, downscale={downscaleRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, quality={downscaleQuality}, gpu={useGpu}, autoTune={useAutoTune}, parallel={parallelDetectorCount}, detectionThreshold={detectionThreshold:F3}, confidenceThreshold={confidenceThreshold:F3}, nmsThreshold={nmsThreshold:F3}");
    var maskProvider = new FrameMaskProvider();
    var detectorOptions = new FaceOnnxDetectorOptions
    {
        UseOrtOptimization = true,
        UseGpu = useGpu,
        AllowAutoTune = useAutoTune,
        AllowAutoGpu = useGpu,
        EnablePreprocessParallelism = true,
        DetectionThreshold = detectionThreshold,
        ConfidenceThreshold = confidenceThreshold,
        NmsThreshold = nmsThreshold
    };
    bool useScrfd = !string.IsNullOrWhiteSpace(scrfdModelPath);
    bool useYuNet = !string.IsNullOrWhiteSpace(yuNetModelPath);
    if (useAutoTune && !useScrfd && !useYuNet && DetectorAutoTuner.TryTune(
            input,
            downscaleRatio,
            downscaleQuality,
            detectorOptions,
            parallelDetectorCount,
            useGpu,
            CancellationToken.None,
            out var tunedOptions,
            out var tunedSessions,
            out var tuneLabel))
    {
        detectorOptions = tunedOptions;
        parallelDetectorCount = tunedSessions;
        string provider = tunedOptions.UseGpu
            ? FaceOnnxDetector.GetLastExecutionProviderLabel()
            : "CPU";
        Console.WriteLine($"[SmokeTune] label={label}, tuned={tuneLabel}, sessions={tunedSessions}, gpu={tunedOptions.UseGpu}, provider={provider}");
    }

    FaceDetectorFactory factory;
    if (useScrfd)
    {
        factory = new FaceDetectorFactory(FaceDetectorFactoryOptions.ForScrfdOnnx(new ScrfdOnnxDetectorOptions
        {
            ModelPath = scrfdModelPath,
            UseOrtOptimization = true,
            UseGpu = useGpu,
            ConfidenceThreshold = confidenceThreshold,
            NmsThreshold = nmsThreshold,
            IntraOpNumThreads = detectorOptions.IntraOpNumThreads,
            InterOpNumThreads = detectorOptions.InterOpNumThreads,
            UseParallelExecution = detectorOptions.UseParallelExecution,
            UseRgbInput = scrfdUseRgb,
            UseLetterboxResize = scrfdUseLetterbox
        }));
    }
    else if (useYuNet)
    {
        factory = new FaceDetectorFactory(FaceDetectorFactoryOptions.ForYuNetOnnx(new YuNetOnnxDetectorOptions
        {
            ModelPath = yuNetModelPath,
            UseOrtOptimization = true,
            ConfidenceThreshold = confidenceThreshold,
            NmsThreshold = nmsThreshold,
            IntraOpNumThreads = detectorOptions.IntraOpNumThreads,
            InterOpNumThreads = detectorOptions.InterOpNumThreads,
            UseParallelExecution = detectorOptions.UseParallelExecution,
            UseTiling = yuNetUseTiling,
            IncludeFullFrameWhenTiling = !yuNetTileOnly,
            TileColumns = yuNetTileColumns,
            TileRows = yuNetTileRows,
            TileOverlapRatio = yuNetTileOverlapRatio
        }));
    }
    else
    {
        factory = FaceDetectorFactory.ForOnnx(detectorOptions);
    }
    using var detector = factory.CreateDetector();
    var options = new AutoMaskOptions
    {
        DownscaleRatio = downscaleRatio,
        DownscaleQuality = downscaleQuality,
        UseTracking = useTracking,
        DetectEveryNFrames = detectEvery,
        ParallelDetectorCount = parallelDetectorCount,
        RunId = runId
    };

    var generator = new AutoMaskGenerator(detector, maskProvider, options, factory);
    await generator.GenerateAsync(input, new Progress<int>(_ => { }), CancellationToken.None);
    Console.WriteLine(generator.LastRunSummary?.ToLogLine() ?? $"[Smoke] no auto summary label={label}");
    var trackPost = new FaceTrackInterpolator().Apply(
        maskProvider,
        generator.LastRunSummary?.TotalFrames ?? 0,
        new FaceTrackPostProcessOptions
        {
            MaxTrackGap = 8,
            MaxFillGap = 5,
            WeakConfidence = 0.50f,
            StrongConfidence = 0.68f,
            ShortTrackMaxConfidence = 0.68f,
            SmallTrackMaxAreaRatio = 0.00075,
            MinTrackIou = 0.12,
            MaxCenterShiftRatio = 0.55,
            MaxAreaChangeRatio = 3.2,
            DuplicateIou = 0.35
        });
    string lostFrames = trackPost.FilledLostFrameIndices.Count == 0
        ? "none"
        : string.Join(",", trackPost.FilledLostFrameIndices.Take(32));
    Console.WriteLine($"[SmokeFaceTrackPost] label={label}, tracks={trackPost.TrackCount}, filled={trackPost.FilledGapFaces}, lostFilled={trackPost.FilledLostFaces}, lostFrames={lostFrames}, removedShort={trackPost.RemovedShortFaces}, rewritten={trackPost.RewrittenFrames}");
    if (detector is IBgraFaceDetector bgraDetector)
    {
        using var faceOnnxRoiDetector = useScrfd || useYuNet ? null : new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions));
        var roiDetector = useScrfd || useYuNet ? bgraDetector : faceOnnxRoiDetector;
        var refine = new FaceTrackRoiRefiner().Apply(
            maskProvider,
            input,
            roiDetector!,
            trackPost.FilledGapFacesInfo.Concat(trackPost.FilledLostFacesInfo).ToArray(),
            downscaleQuality);
        Console.WriteLine($"[SmokeFaceTrackRoiRefine] label={label}, attempts={refine.Attempts}, hits={refine.Hits}, seeks={refine.SeekCount}, decoded={refine.DecodedFrames}, elapsedMs={refine.ElapsedMs}");
    }
    Console.WriteLine($"[Smoke] label={label}, faceMaskFrames={maskProvider.GetFaceMaskFrameIndices().Length}, storedMaskFrames={maskProvider.GetStoredMaskFrameIndices().Length}");
    if (dumpDetections)
        DumpDetections(label, maskProvider);

    if (skipExport)
    {
        Console.WriteLine($"[Smoke] label={label}, export=skipped");
        return (label, maskProvider);
    }

    var exporter = new VideoExportService(maskProvider);
    exporter.Export(input, output, blurRadius: 28, new Progress<ExportProgress>(_ => { }), CancellationToken.None, runId);
    Console.WriteLine(exporter.LastExportSummary?.ToLogLine() ?? $"[Smoke] no export summary label={label}");
    Console.WriteLine($"[Smoke] label={label}, output={output}");
    return (label, maskProvider);
}

static FaceOnnxDetectorOptions CreateRoiRefinerDetectorOptions(FaceOnnxDetectorOptions source)
{
    var defaults = FaceOnnxDetector.GetDefaultThresholds();
    float detection = source.DetectionThreshold ?? defaults.Detection;
    float confidence = source.ConfidenceThreshold ?? defaults.Confidence;
    float nms = source.NmsThreshold ?? defaults.Nms;

    return new FaceOnnxDetectorOptions
    {
        UseOrtOptimization = true,
        UseGpu = false,
        IntraOpNumThreads = source.IntraOpNumThreads,
        InterOpNumThreads = source.InterOpNumThreads,
        UseParallelExecution = false,
        EnablePreprocessParallelism = true,
        AllowAutoTune = false,
        AllowAutoGpu = false,
        DetectionThreshold = Math.Min(detection, 0.12f),
        ConfidenceThreshold = Math.Min(confidence, 0.12f),
        NmsThreshold = Math.Max(nms, 0.75f)
    };
}

static void DumpDetections(string label, FrameMaskProvider maskProvider)
{
    foreach (var entry in maskProvider.GetFaceMaskEntries().OrderBy(x => x.Key))
    {
        var data = entry.Value;
        for (int i = 0; i < data.Faces.Count; i++)
        {
            var r = data.Faces[i];
            float conf = i < data.Confidences.Count ? data.Confidences[i] : data.MinConfidence ?? 1.0f;
            double area = Math.Max(0.0, r.Width * r.Height);
            Console.WriteLine(
                $"[SmokeDetection] label={label}, frame={entry.Key}, index={i}, x={r.X:F1}, y={r.Y:F1}, w={r.Width:F1}, h={r.Height:F1}, area={area:F1}, conf={conf:F3}");
        }
    }
}

static bool CompareCases(
    (string Label, FrameMaskProvider MaskProvider) baseline,
    (string Label, FrameMaskProvider MaskProvider) optimized,
    double minAvgIou,
    double minBestIou,
    bool allowFrameMismatch)
{
    var baselineEntries = baseline.MaskProvider.GetFaceMaskEntries().ToDictionary(x => x.Key, x => x.Value);
    var optimizedEntries = optimized.MaskProvider.GetFaceMaskEntries().ToDictionary(x => x.Key, x => x.Value);
    var baselineFrames = baselineEntries.Keys.OrderBy(x => x).ToArray();
    var optimizedFrames = optimizedEntries.Keys.OrderBy(x => x).ToArray();
    var commonFrames = baselineFrames.Intersect(optimizedFrames).OrderBy(x => x).ToArray();
    var onlyBaseline = baselineFrames.Except(optimizedFrames).OrderBy(x => x).ToArray();
    var onlyOptimized = optimizedFrames.Except(baselineFrames).OrderBy(x => x).ToArray();

    double iouSum = 0.0;
    double minIou = commonFrames.Length > 0 ? 1.0 : 0.0;
    int iouSamples = 0;
    int boxCountDiffFrames = 0;

    foreach (int frame in commonFrames)
    {
        var baseFaces = baselineEntries[frame].Faces;
        var optFaces = optimizedEntries[frame].Faces;
        if (baseFaces.Count != optFaces.Count)
            boxCountDiffFrames++;

        foreach (var face in baseFaces)
        {
            double best = 0.0;
            foreach (var candidate in optFaces)
                best = Math.Max(best, IoU(face, candidate));

            iouSum += best;
            minIou = Math.Min(minIou, best);
            iouSamples++;
        }
    }

    double avgIou = iouSamples > 0 ? iouSum / iouSamples : 0.0;
    Console.WriteLine(
        $"[SmokeCompare] baseline={baseline.Label}, optimized={optimized.Label}, baselineFrames={baselineFrames.Length}, optimizedFrames={optimizedFrames.Length}, common={commonFrames.Length}, onlyBaseline={onlyBaseline.Length}, onlyOptimized={onlyOptimized.Length}, avgBestIou={avgIou:F3}, minBestIou={minIou:F3}, boxCountDiffFrames={boxCountDiffFrames}");
    Console.WriteLine($"[SmokeCompareFrames] onlyBaseline={FormatFrames(onlyBaseline)}, onlyOptimized={FormatFrames(onlyOptimized)}");

    bool frameMatchOk = allowFrameMismatch ||
        (onlyBaseline.Length == 0 && onlyOptimized.Length == 0 && boxCountDiffFrames == 0);
    bool iouOk = iouSamples == 0 || (avgIou >= minAvgIou && minIou >= minBestIou);
    bool passed = frameMatchOk && iouOk;
    Console.WriteLine(
        $"[SmokeQualityGate] passed={passed}, frameMatchOk={frameMatchOk}, iouOk={iouOk}, minAvgIou={minAvgIou:F3}, minBestIou={minBestIou:F3}, allowFrameMismatch={allowFrameMismatch}");
    return passed;
}

static string FormatFrames(IReadOnlyList<int> frames)
{
    if (frames.Count == 0)
        return "none";
    return string.Join(",", frames.Take(20)) + (frames.Count > 20 ? ",..." : string.Empty);
}

static double IoU(Rect a, Rect b)
{
    double x1 = Math.Max(a.X, b.X);
    double y1 = Math.Max(a.Y, b.Y);
    double x2 = Math.Min(a.Right, b.Right);
    double y2 = Math.Min(a.Bottom, b.Bottom);
    double w = Math.Max(0.0, x2 - x1);
    double h = Math.Max(0.0, y2 - y1);
    double intersection = w * h;
    double union = Math.Max(0.0, a.Width * a.Height) + Math.Max(0.0, b.Width * b.Height) - intersection;
    return union <= 0.0 ? 0.0 : intersection / union;
}

(string Label, FrameMaskProvider MaskProvider)? baseline = null;
if (!bool.Parse(args[2]))
{
    baseline = await RunCaseAsync(
        "baseline-all-frames",
        input,
        args[3],
        useTracking: false,
        detectEvery: 1,
        downscaleRatio: 1.0,
        downscaleQuality: DownscaleQuality.BalancedBilinear,
        useGpu: false,
        skipExport,
        useAutoTune: false,
        parallelDetectorCount: 1,
        dumpDetections,
        detectionThreshold,
        confidenceThreshold,
        nmsThreshold,
        scrfdModelPath: string.Empty,
        scrfdUseRgb,
        scrfdUseLetterbox,
        yuNetModelPath: string.Empty,
        yuNetUseTiling,
        yuNetTileOnly,
        yuNetTileColumns,
        yuNetTileRows,
        yuNetTileOverlapRatio);
}

var optimized = await RunCaseAsync(
    $"optimized-{(optimizedUseTracking ? "track" : "all")}-{optimizedDetectEvery}-scale-{optimizedDownscaleRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}-{(optimizedUseGpu ? "gpu" : "cpu")}{(!string.IsNullOrWhiteSpace(scrfdModelPath) ? "-scrfd" : "")}{(!string.IsNullOrWhiteSpace(yuNetModelPath) ? "-yunet" : "")}",
    input,
    output,
    useTracking: optimizedUseTracking,
    detectEvery: optimizedDetectEvery,
    downscaleRatio: optimizedDownscaleRatio,
    downscaleQuality: optimizedDownscaleQuality,
    useGpu: optimizedUseGpu,
    skipExport,
    useAutoTune,
    parallelDetectorCount: optimizedParallel,
    dumpDetections,
    detectionThreshold,
    confidenceThreshold,
    nmsThreshold,
    scrfdModelPath,
    scrfdUseRgb,
    scrfdUseLetterbox,
    yuNetModelPath,
    yuNetUseTiling,
    yuNetTileOnly,
    yuNetTileColumns,
    yuNetTileRows,
    yuNetTileOverlapRatio);

if (baseline.HasValue && !CompareCases(baseline.Value, optimized, minAvgIou, minBestIou, allowFrameMismatch))
    Environment.Exit(2);
'@ | Set-Content -Encoding UTF8 $program

$baselineOutput = Join-Path $work "${clipStem}_baseline_blur.mp4"
$downscaleArg = $DownscaleRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$trackingArg = (-not $OptimizedNoTracking).ToString().ToLowerInvariant()
$gpuArg = ((-not $OptimizedCpuOnly.IsPresent) -or $OptimizedUseGpu.IsPresent).ToString().ToLowerInvariant()
$skipExportArg = $SkipExport.IsPresent.ToString().ToLowerInvariant()
$autoTuneArg = $UseAutoTune.IsPresent.ToString().ToLowerInvariant()
$detectEveryArg = $OptimizedDetectEvery.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$parallelArg = $ParallelDetectorCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$detectionThresholdArg = $DetectionThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$confidenceThresholdArg = $ConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$nmsThresholdArg = $NmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$minAvgIouArg = $MinAvgIou.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$minBestIouArg = $MinBestIou.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$allowFrameMismatchArg = $AllowFrameMismatch.IsPresent.ToString().ToLowerInvariant()
$dumpDetectionsArg = $DumpDetections.IsPresent.ToString().ToLowerInvariant()
$scrfdModelPathArg = if ([string]::IsNullOrWhiteSpace($ScrfdModelPath)) { "__none__" } else { (Resolve-Path $ScrfdModelPath).Path }
$scrfdUseBgrArg = $ScrfdUseBgr.IsPresent.ToString().ToLowerInvariant()
$scrfdStretchInputArg = $ScrfdStretchInput.IsPresent.ToString().ToLowerInvariant()
$yuNetModelPathArg = if ([string]::IsNullOrWhiteSpace($YuNetModelPath)) { "__none__" } else { (Resolve-Path $YuNetModelPath).Path }
$yuNetUseTilingArg = $YuNetUseTiling.IsPresent.ToString().ToLowerInvariant()
$yuNetTileOnlyArg = $YuNetTileOnly.IsPresent.ToString().ToLowerInvariant()
$yuNetTileColumnsArg = $YuNetTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yuNetTileRowsArg = $YuNetTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yuNetTileOverlapRatioArg = $YuNetTileOverlapRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
dotnet run --project $project -- `
    $clip `
    $output `
    ([bool]$SkipBaseline).ToString().ToLowerInvariant() `
    $baselineOutput `
    $downscaleArg `
    $DownscaleQuality `
    $trackingArg `
    $gpuArg `
    $skipExportArg `
    $autoTuneArg `
    $detectEveryArg `
    $parallelArg `
    $minAvgIouArg `
    $minBestIouArg `
    $allowFrameMismatchArg `
    $dumpDetectionsArg `
    $detectionThresholdArg `
    $confidenceThresholdArg `
    $nmsThresholdArg `
    $scrfdModelPathArg `
    $scrfdUseBgrArg `
    $scrfdStretchInputArg `
    $yuNetModelPathArg `
    $yuNetUseTilingArg `
    $yuNetTileOnlyArg `
    $yuNetTileColumnsArg `
    $yuNetTileRowsArg `
    $yuNetTileOverlapRatioArg
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
