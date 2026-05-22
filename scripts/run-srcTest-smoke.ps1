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
    [switch]$ScrfdDebugDump,
    [switch]$ScrfdNoStrideScale,
    [switch]$ScrfdHalfStrideAnchor,
    [switch]$ScrfdCenterLetterbox,
    [int]$ScrfdInputSize = 0,
    [double]$ScrfdInputMean = 127.5,
    [double]$ScrfdInputStd = 128.0,
    [double]$ScrfdPaddingValue = 0.0,
    [string]$YuNetModelPath = "",
    [switch]$YuNetUseTiling,
    [switch]$YuNetTileOnly,
    [int]$YuNetTileColumns = 2,
    [int]$YuNetTileRows = 2,
    [double]$YuNetTileOverlapRatio = 0.15,
    [string]$YoloModelPath = "",
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string]$YoloModelType = "YoloV8Face",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.25,
    [double]$YoloConfidenceThreshold = 0.35,
    [double]$YoloNmsThreshold = 0.45,
    [double]$YoloLargeBoxWidthScale = 1.0,
    [double]$YoloLargeBoxHeightScale = 1.0,
    [double]$YoloLargeBoxMinAreaRatio = 0.0,
    [switch]$YoloUseFaceOnnxRoiRefine,
    [double]$YoloFaceOnnxRoiMinAreaRatio = 0.03,
    [int]$YoloFaceOnnxRoiMaxCandidates = 64,
    [switch]$YoloUseTiling,
    [switch]$YoloTileOnly,
    [int]$YoloTileColumns = 2,
    [int]$YoloTileRows = 2,
    [double]$YoloTileOverlapRatio = 0.15,
    [switch]$YoloDebugDump,
    [switch]$DumpCompareDetails,
    [switch]$DumpCompareOverlays,
    [string]$CompareOverlayDir = ""
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
bool scrfdDebugDump = args.Length > 22 && bool.Parse(args[22]);
bool scrfdMultiplyBboxByStride = args.Length <= 23 || !bool.Parse(args[23]);
float scrfdAnchorCenterOffset = args.Length > 24 && bool.Parse(args[24]) ? 0.5f : 0.0f;
bool scrfdCenterLetterbox = args.Length > 25 && bool.Parse(args[25]);
int scrfdInputSize = args.Length > 26 ? int.Parse(args[26], System.Globalization.CultureInfo.InvariantCulture) : 0;
float scrfdInputMean = args.Length > 27 ? float.Parse(args[27], System.Globalization.CultureInfo.InvariantCulture) : 127.5f;
float scrfdInputStd = args.Length > 28 ? float.Parse(args[28], System.Globalization.CultureInfo.InvariantCulture) : 128.0f;
float scrfdPaddingValue = args.Length > 29 ? float.Parse(args[29], System.Globalization.CultureInfo.InvariantCulture) : 0.0f;
string yuNetModelPath = args.Length > 30 && args[30] != "__none__" ? args[30] : string.Empty;
bool yuNetUseTiling = args.Length > 31 && bool.Parse(args[31]);
bool yuNetTileOnly = args.Length > 32 && bool.Parse(args[32]);
int yuNetTileColumns = args.Length > 33 ? int.Parse(args[33], System.Globalization.CultureInfo.InvariantCulture) : 2;
int yuNetTileRows = args.Length > 34 ? int.Parse(args[34], System.Globalization.CultureInfo.InvariantCulture) : 2;
double yuNetTileOverlapRatio = args.Length > 35 ? double.Parse(args[35], System.Globalization.CultureInfo.InvariantCulture) : 0.15;
string yoloModelPath = args.Length > 36 && args[36] != "__none__" ? args[36] : string.Empty;
var yoloModelType = args.Length > 37 ? Enum.Parse<YoloFaceModelType>(args[37]) : YoloFaceModelType.YoloV8Face;
int yoloInputSize = args.Length > 38 ? int.Parse(args[38], System.Globalization.CultureInfo.InvariantCulture) : 640;
float yoloObjectnessThreshold = args.Length > 39 ? float.Parse(args[39], System.Globalization.CultureInfo.InvariantCulture) : 0.25f;
float yoloConfidenceThreshold = args.Length > 40 ? float.Parse(args[40], System.Globalization.CultureInfo.InvariantCulture) : 0.35f;
float yoloNmsThreshold = args.Length > 41 ? float.Parse(args[41], System.Globalization.CultureInfo.InvariantCulture) : 0.45f;
float yoloLargeBoxWidthScale = args.Length > 42 ? float.Parse(args[42], System.Globalization.CultureInfo.InvariantCulture) : 1.0f;
float yoloLargeBoxHeightScale = args.Length > 43 ? float.Parse(args[43], System.Globalization.CultureInfo.InvariantCulture) : 1.0f;
double yoloLargeBoxMinAreaRatio = args.Length > 44 ? double.Parse(args[44], System.Globalization.CultureInfo.InvariantCulture) : 0.0;
bool yoloUseTiling = args.Length > 45 && bool.Parse(args[45]);
bool yoloUseFaceOnnxRoiRefine = args.Length > 46 && bool.Parse(args[46]);
double yoloFaceOnnxRoiMinAreaRatio = args.Length > 47 ? double.Parse(args[47], System.Globalization.CultureInfo.InvariantCulture) : 0.03;
int yoloFaceOnnxRoiMaxCandidates = args.Length > 48 ? int.Parse(args[48], System.Globalization.CultureInfo.InvariantCulture) : 64;
bool yoloTileOnly = args.Length > 49 && bool.Parse(args[49]);
int yoloTileColumns = args.Length > 50 ? int.Parse(args[50], System.Globalization.CultureInfo.InvariantCulture) : 2;
int yoloTileRows = args.Length > 51 ? int.Parse(args[51], System.Globalization.CultureInfo.InvariantCulture) : 2;
double yoloTileOverlapRatio = args.Length > 52 ? double.Parse(args[52], System.Globalization.CultureInfo.InvariantCulture) : 0.15;
bool yoloDebugDump = args.Length > 53 && bool.Parse(args[53]);
bool dumpCompareDetails = args.Length > 54 && bool.Parse(args[54]);
bool dumpCompareOverlays = args.Length > 55 && bool.Parse(args[55]);
string compareOverlayDir = args.Length > 56 && args[56] != "__none__" ? args[56] : string.Empty;

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
    bool scrfdDebugDump,
    bool scrfdMultiplyBboxByStride,
    float scrfdAnchorCenterOffset,
    bool scrfdCenterLetterbox,
    int scrfdInputSize,
    float scrfdInputMean,
    float scrfdInputStd,
    float scrfdPaddingValue,
    string yuNetModelPath,
    bool yuNetUseTiling,
    bool yuNetTileOnly,
    int yuNetTileColumns,
    int yuNetTileRows,
    double yuNetTileOverlapRatio,
    string yoloModelPath,
    YoloFaceModelType yoloModelType,
    int yoloInputSize,
    float yoloObjectnessThreshold,
    float yoloConfidenceThreshold,
    float yoloNmsThreshold,
    float yoloLargeBoxWidthScale,
    float yoloLargeBoxHeightScale,
    double yoloLargeBoxMinAreaRatio,
    bool yoloUseTiling,
    bool yoloUseFaceOnnxRoiRefine,
    double yoloFaceOnnxRoiMinAreaRatio,
    int yoloFaceOnnxRoiMaxCandidates,
    bool yoloTileOnly,
    int yoloTileColumns,
    int yoloTileRows,
    double yoloTileOverlapRatio,
    bool yoloDebugDump)
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
    bool useYolo = !string.IsNullOrWhiteSpace(yoloModelPath);
    if (useAutoTune && !useScrfd && !useYuNet && !useYolo && DetectorAutoTuner.TryTune(
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
            UseLetterboxResize = scrfdUseLetterbox,
            MultiplyBboxByStride = scrfdMultiplyBboxByStride,
            AnchorCenterOffset = scrfdAnchorCenterOffset,
            CenterLetterboxPadding = scrfdCenterLetterbox,
            InputWidth = scrfdInputSize > 0 ? scrfdInputSize : null,
            InputHeight = scrfdInputSize > 0 ? scrfdInputSize : null,
            InputMean = scrfdInputMean,
            InputStd = scrfdInputStd,
            LetterboxPaddingValue = scrfdPaddingValue,
            DumpDebug = scrfdDebugDump
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
    else if (useYolo)
    {
        factory = new FaceDetectorFactory(FaceDetectorFactoryOptions.ForYoloFaceOnnx(new YoloFaceOnnxDetectorOptions
        {
            ModelPath = yoloModelPath,
            ModelType = yoloModelType,
            UseOrtOptimization = true,
            UseGpu = useGpu,
            ObjectnessThreshold = yoloObjectnessThreshold,
            ConfidenceThreshold = yoloConfidenceThreshold,
            NmsThreshold = yoloNmsThreshold,
            LargeBoxWidthScale = yoloLargeBoxWidthScale,
            LargeBoxHeightScale = yoloLargeBoxHeightScale,
            LargeBoxMinAreaRatio = yoloLargeBoxMinAreaRatio,
            IntraOpNumThreads = detectorOptions.IntraOpNumThreads,
            InterOpNumThreads = detectorOptions.InterOpNumThreads,
            UseParallelExecution = detectorOptions.UseParallelExecution,
            InputWidth = yoloInputSize > 0 ? yoloInputSize : null,
            InputHeight = yoloInputSize > 0 ? yoloInputSize : null,
            UseTiling = yoloUseTiling,
            IncludeFullFrameWhenTiling = !yoloTileOnly,
            TileColumns = yoloTileColumns,
            TileRows = yoloTileRows,
            TileOverlapRatio = yoloTileOverlapRatio,
            DumpDebug = yoloDebugDump
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
        RunId = runId,
        FilterProfile = useYolo ? FaceFilterProfile.Yolo : useScrfd ? FaceFilterProfile.Scrfd : FaceFilterProfile.FaceOnnx,
        DumpDetectionDiagnostics = dumpDetections || scrfdDebugDump || yoloDebugDump
    };

    var generator = new AutoMaskGenerator(detector, maskProvider, options, factory);
    await generator.GenerateAsync(input, new Progress<int>(_ => { }), CancellationToken.None);
    Console.WriteLine(generator.LastRunSummary?.ToLogLine() ?? $"[Smoke] no auto summary label={label}");
    var trackPost = new FaceTrackInterpolator().Apply(
        maskProvider,
        generator.LastRunSummary?.TotalFrames ?? 0,
        useYolo
            ? new FaceTrackPostProcessOptions
            {
                MaxTrackGap = 8,
                MaxFillGap = 5,
                MaxLostFillFrames = 3,
                WeakConfidence = 0.38f,
                StrongConfidence = 0.58f,
                ShortTrackMaxConfidence = 0.18f,
                SmallTrackMaxAreaRatio = 0.00070,
                MinTrackIou = 0.08,
                MaxCenterShiftRatio = 0.72,
                MaxAreaChangeRatio = 4.0,
                DuplicateIou = 0.35,
                UnstableTailMaxConfidence = 0.40f,
                UnstableTailMinStableDetections = 3,
                UnstableTailMinIou = 0.45,
                UnstableTailMaxAreaChangeRatio = 1.8,
                LowerFrameTrackMaxConfidence = 0.50f,
                LowerFrameTrackMinCenterYRatio = 0.58,
                LowerFrameTrackMinAreaRatio = 0.015,
                LowerFrameTrackMaxAreaRatio = 0.045
            }
            : useScrfd
            ? new FaceTrackPostProcessOptions
            {
                MaxTrackGap = 8,
                MaxFillGap = 5,
                WeakConfidence = 0.35f,
                StrongConfidence = 0.55f,
                ShortTrackMaxConfidence = 0.55f,
                SmallTrackMaxAreaRatio = 0.00075,
                MinTrackIou = 0.08,
                MaxCenterShiftRatio = 0.75,
                MaxAreaChangeRatio = 4.0,
                DuplicateIou = 0.35
            }
            : new FaceTrackPostProcessOptions
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
    Console.WriteLine($"[SmokeFaceTrackPost] label={label}, tracks={trackPost.TrackCount}, filled={trackPost.FilledGapFaces}, lostFilled={trackPost.FilledLostFaces}, lostFrames={lostFrames}, removedShort={trackPost.RemovedShortFaces}, removedLower={trackPost.RemovedLowerFrameFaces}, rewritten={trackPost.RewrittenFrames}");
    if (detector is IBgraFaceDetector bgraDetector)
    {
        using var faceOnnxRoiDetector = useScrfd || useYuNet || useYolo ? null : new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions));
        var roiDetector = useScrfd || useYuNet || useYolo ? bgraDetector : faceOnnxRoiDetector;
        var refine = new FaceTrackRoiRefiner().Apply(
            maskProvider,
            input,
            roiDetector!,
            trackPost.FilledGapFacesInfo.Concat(trackPost.FilledLostFacesInfo).ToArray(),
            downscaleQuality);
        Console.WriteLine($"[SmokeFaceTrackRoiRefine] label={label}, attempts={refine.Attempts}, hits={refine.Hits}, seeks={refine.SeekCount}, decoded={refine.DecodedFrames}, elapsedMs={refine.ElapsedMs}");
    }
    if (useYolo && yoloUseFaceOnnxRoiRefine)
    {
        var candidates = BuildLargeFaceRoiCandidates(
            maskProvider,
            yoloFaceOnnxRoiMinAreaRatio,
            yoloFaceOnnxRoiMaxCandidates);
        using var faceOnnxRoiDetector = new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions));
        var refine = new FaceTrackRoiRefiner().Apply(
            maskProvider,
            input,
            faceOnnxRoiDetector,
            candidates,
            downscaleQuality,
            yoloFaceOnnxRoiMaxCandidates);
        Console.WriteLine($"[SmokeYoloFaceOnnxRoiRefine] label={label}, candidates={candidates.Count}, minAreaRatio={yoloFaceOnnxRoiMinAreaRatio:F3}, attempts={refine.Attempts}, hits={refine.Hits}, seeks={refine.SeekCount}, decoded={refine.DecodedFrames}, elapsedMs={refine.ElapsedMs}");
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

static IReadOnlyList<FaceTrackFilledFace> BuildLargeFaceRoiCandidates(
    FrameMaskProvider maskProvider,
    double minAreaRatio,
    int maxCandidates)
{
    var candidates = new List<FaceTrackFilledFace>();
    foreach (var entry in maskProvider.GetFaceMaskEntries().OrderBy(x => x.Key))
    {
        var data = entry.Value;
        double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
        for (int i = 0; i < data.Faces.Count; i++)
        {
            var face = data.Faces[i];
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            if (areaRatio < minAreaRatio)
                continue;

            float conf = i < data.Confidences.Count ? data.Confidences[i] : data.MinConfidence ?? 0.50f;
            candidates.Add(new FaceTrackFilledFace(entry.Key, face, data.Size, conf));
            if (candidates.Count >= maxCandidates)
                return candidates;
        }
    }

    return candidates;
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
    string input,
    (string Label, FrameMaskProvider MaskProvider) baseline,
    (string Label, FrameMaskProvider MaskProvider) optimized,
    double minAvgIou,
    double minBestIou,
    bool allowFrameMismatch,
    bool dumpCompareDetails,
    bool dumpCompareOverlays,
    string compareOverlayDir)
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
    var lowIouFrames = new List<int>();
    var boxCountDiffFrameList = new List<int>();

    foreach (int frame in commonFrames)
    {
        var baseFaces = baselineEntries[frame].Faces;
        var optFaces = optimizedEntries[frame].Faces;
        if (baseFaces.Count != optFaces.Count)
        {
            boxCountDiffFrames++;
            boxCountDiffFrameList.Add(frame);
        }

        foreach (var face in baseFaces)
        {
            double best = 0.0;
            foreach (var candidate in optFaces)
                best = Math.Max(best, IoU(face, candidate));

            iouSum += best;
            minIou = Math.Min(minIou, best);
            if (best < minBestIou && !lowIouFrames.Contains(frame))
                lowIouFrames.Add(frame);
            iouSamples++;
        }
    }

    double avgIou = iouSamples > 0 ? iouSum / iouSamples : 0.0;
    Console.WriteLine(
        $"[SmokeCompare] baseline={baseline.Label}, optimized={optimized.Label}, baselineFrames={baselineFrames.Length}, optimizedFrames={optimizedFrames.Length}, common={commonFrames.Length}, onlyBaseline={onlyBaseline.Length}, onlyOptimized={onlyOptimized.Length}, avgBestIou={avgIou:F3}, minBestIou={minIou:F3}, boxCountDiffFrames={boxCountDiffFrames}");
    Console.WriteLine($"[SmokeCompareFrames] onlyBaseline={FormatFrames(onlyBaseline)}, onlyOptimized={FormatFrames(onlyOptimized)}");
    Console.WriteLine($"[SmokeCompareBadFrames] boxCountDiff={FormatFrames(boxCountDiffFrameList)}, lowIou={FormatFrames(lowIouFrames)}");
    Console.WriteLine("[SmokeCompareNote] onlyBaseline/onlyOptimized are detector-difference frames, not ground-truth false-negative/false-positive labels.");
    if (dumpCompareDetails)
    {
        DumpCompareDetails("onlyBaseline", onlyBaseline, baselineEntries, optimizedEntries);
        DumpCompareDetails("onlyOptimized", onlyOptimized, baselineEntries, optimizedEntries);
        DumpCompareDetails("boxCountDiff", boxCountDiffFrameList, baselineEntries, optimizedEntries);
        DumpCompareDetails("lowIou", lowIouFrames, baselineEntries, optimizedEntries);
    }
    if (dumpCompareOverlays && !string.IsNullOrWhiteSpace(compareOverlayDir))
    {
        DumpCompareOverlays(input, compareOverlayDir, onlyBaseline, onlyOptimized, boxCountDiffFrameList, lowIouFrames, baselineEntries, optimizedEntries);
    }

    bool frameMatchOk = allowFrameMismatch ||
        (onlyBaseline.Length == 0 && onlyOptimized.Length == 0 && boxCountDiffFrames == 0);
    bool iouOk = iouSamples == 0 || (avgIou >= minAvgIou && minIou >= minBestIou);
    bool passed = frameMatchOk && iouOk;
    Console.WriteLine(
        $"[SmokeQualityGate] passed={passed}, frameMatchOk={frameMatchOk}, iouOk={iouOk}, minAvgIou={minAvgIou:F3}, minBestIou={minBestIou:F3}, allowFrameMismatch={allowFrameMismatch}");
    return passed;
}

static void DumpCompareOverlays(
    string input,
    string outputDir,
    IReadOnlyList<int> onlyBaseline,
    IReadOnlyList<int> onlyOptimized,
    IReadOnlyList<int> boxCountDiff,
    IReadOnlyList<int> lowIou,
    Dictionary<int, FrameMaskProvider.FaceMaskData> baselineEntries,
    Dictionary<int, FrameMaskProvider.FaceMaskData> optimizedEntries)
{
    Directory.CreateDirectory(outputDir);
    var items = new List<(string Reason, int Frame)>();
    items.AddRange(onlyBaseline.Take(8).Select(frame => ("onlyBaseline", frame)));
    items.AddRange(onlyOptimized.Take(8).Select(frame => ("onlyOptimized", frame)));
    items.AddRange(boxCountDiff.Take(8).Select(frame => ("boxCountDiff", frame)));
    items.AddRange(lowIou.Take(8).Select(frame => ("lowIou", frame)));
    if (items.Count == 0)
        return;

    var ordered = items.OrderBy(x => x.Frame).ThenBy(x => x.Reason).ToArray();
    using var extractor = new FfFrameExtractor(input, enableHardware: false);
    extractor.StartSequentialRead(Math.Max(0, ordered[0].Frame));
    int next = 0;
    int maxFrame = ordered[^1].Frame;
    while (next < ordered.Length &&
           extractor.TryGetNextFrameRaw(CancellationToken.None, requireBgra: true, out var frame, out int frameIndex))
    {
        if (frameIndex > maxFrame)
            break;

        while (next < ordered.Length && ordered[next].Frame < frameIndex)
        {
            Console.WriteLine($"[SmokeCompareOverlay] reason={ordered[next].Reason}, frame={ordered[next].Frame}, path=decode-skipped");
            next++;
        }

        while (next < ordered.Length && ordered[next].Frame == frameIndex)
        {
            var item = ordered[next];
            using var image = ConvertToRgbImage(frame);
            if (baselineEntries.TryGetValue(item.Frame, out var baseline))
                DrawFaces(image, baseline.Faces, new Rgb24(255, 64, 64));
            if (optimizedEntries.TryGetValue(item.Frame, out var optimized))
                DrawFaces(image, optimized.Faces, new Rgb24(64, 220, 255));

            string path = Path.Combine(outputDir, $"{item.Reason}-frame-{item.Frame:D6}.png");
            image.SaveAsPng(path);
            Console.WriteLine($"[SmokeCompareOverlay] reason={item.Reason}, frame={item.Frame}, path={path}");
            next++;
        }
    }
}

static Image<Rgb24> ConvertToRgbImage(FfFrameExtractor.BgraFrame frame)
{
    int width = frame.Width;
    int height = frame.Height;
    var image = new Image<Rgb24>(width, height);
    unsafe
    {
        byte* src = (byte*)frame.Data;
        int stride = frame.Stride;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = rows.GetRowSpan(y);
                byte* srcRow = src + y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte b = srcRow[x * 4];
                    byte g = srcRow[x * 4 + 1];
                    byte r = srcRow[x * 4 + 2];
                    row[x] = new Rgb24(r, g, b);
                }
            }
        });
    }

    return image;
}

static void DrawFaces(Image<Rgb24> image, IReadOnlyList<Rect> faces, Rgb24 color)
{
    foreach (var face in faces)
        DrawRect(image, face, color, 6);
}

static void DrawRect(Image<Rgb24> image, Rect rect, Rgb24 color, int thickness)
{
    int x1 = Math.Clamp((int)Math.Round(rect.X), 0, image.Width - 1);
    int y1 = Math.Clamp((int)Math.Round(rect.Y), 0, image.Height - 1);
    int x2 = Math.Clamp((int)Math.Round(rect.Right), 0, image.Width - 1);
    int y2 = Math.Clamp((int)Math.Round(rect.Bottom), 0, image.Height - 1);
    if (x2 <= x1 || y2 <= y1)
        return;

    image.ProcessPixelRows(rows =>
    {
        for (int t = 0; t < thickness; t++)
        {
            int top = Math.Clamp(y1 + t, 0, image.Height - 1);
            int bottom = Math.Clamp(y2 - t, 0, image.Height - 1);
            for (int x = x1; x <= x2; x++)
            {
                rows.GetRowSpan(top)[x] = color;
                rows.GetRowSpan(bottom)[x] = color;
            }

            int left = Math.Clamp(x1 + t, 0, image.Width - 1);
            int right = Math.Clamp(x2 - t, 0, image.Width - 1);
            for (int y = y1; y <= y2; y++)
            {
                rows.GetRowSpan(y)[left] = color;
                rows.GetRowSpan(y)[right] = color;
            }
        }
    });
}

static void DumpCompareDetails(
    string reason,
    IReadOnlyList<int> frames,
    Dictionary<int, FrameMaskProvider.FaceMaskData> baselineEntries,
    Dictionary<int, FrameMaskProvider.FaceMaskData> optimizedEntries)
{
    foreach (int frame in frames.Take(16))
    {
        FrameMaskProvider.FaceMaskData? baseline = baselineEntries.TryGetValue(frame, out var baselineData)
            ? baselineData
            : null;
        FrameMaskProvider.FaceMaskData? optimized = optimizedEntries.TryGetValue(frame, out var optimizedData)
            ? optimizedData
            : null;
        Console.WriteLine(
            $"[SmokeCompareDetail] reason={reason}, frame={frame}, baseline={FormatFaceData(baseline)}, optimized={FormatFaceData(optimized)}");
    }
}

static string FormatFaceData(FrameMaskProvider.FaceMaskData? data)
{
    if (!data.HasValue || data.Value.Faces.Count == 0)
        return "none";

    var value = data.Value;
    return string.Join("|", value.Faces.Select((r, i) =>
    {
        float conf = i < value.Confidences.Count ? value.Confidences[i] : value.MinConfidence ?? 1.0f;
        double areaRatio = r.Width * r.Height / Math.Max(1.0, value.Size.Width * (double)value.Size.Height);
        double centerX = (r.X + r.Width * 0.5) / Math.Max(1.0, value.Size.Width);
        double centerY = (r.Y + r.Height * 0.5) / Math.Max(1.0, value.Size.Height);
        return $"{i}:x={r.X:F1},y={r.Y:F1},w={r.Width:F1},h={r.Height:F1},cx={centerX:F3},cy={centerY:F3},area={areaRatio:F5},conf={conf:F3}";
    }));
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
        scrfdDebugDump: false,
        scrfdMultiplyBboxByStride: true,
        scrfdAnchorCenterOffset,
        scrfdCenterLetterbox,
        scrfdInputSize,
        scrfdInputMean,
        scrfdInputStd,
        scrfdPaddingValue,
        yuNetModelPath: string.Empty,
        yuNetUseTiling,
        yuNetTileOnly,
        yuNetTileColumns,
        yuNetTileRows,
        yuNetTileOverlapRatio,
        yoloModelPath: string.Empty,
        yoloModelType,
        yoloInputSize,
        yoloObjectnessThreshold,
        yoloConfidenceThreshold,
        yoloNmsThreshold,
        yoloLargeBoxWidthScale,
        yoloLargeBoxHeightScale,
        yoloLargeBoxMinAreaRatio,
        yoloUseTiling: false,
        yoloUseFaceOnnxRoiRefine: false,
        yoloFaceOnnxRoiMinAreaRatio,
        yoloFaceOnnxRoiMaxCandidates,
        yoloTileOnly: false,
        yoloTileColumns,
        yoloTileRows,
        yoloTileOverlapRatio,
        yoloDebugDump: false);
}

var optimized = await RunCaseAsync(
    $"optimized-{(optimizedUseTracking ? "track" : "all")}-{optimizedDetectEvery}-scale-{optimizedDownscaleRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}-{(optimizedUseGpu ? "gpu" : "cpu")}{(!string.IsNullOrWhiteSpace(scrfdModelPath) ? "-scrfd" : "")}{(!string.IsNullOrWhiteSpace(yuNetModelPath) ? "-yunet" : "")}{(!string.IsNullOrWhiteSpace(yoloModelPath) ? "-yolo" : "")}",
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
    scrfdDebugDump,
    scrfdMultiplyBboxByStride,
    scrfdAnchorCenterOffset,
    scrfdCenterLetterbox,
    scrfdInputSize,
    scrfdInputMean,
    scrfdInputStd,
    scrfdPaddingValue,
    yuNetModelPath,
    yuNetUseTiling,
    yuNetTileOnly,
    yuNetTileColumns,
    yuNetTileRows,
    yuNetTileOverlapRatio,
    yoloModelPath,
    yoloModelType,
    yoloInputSize,
    yoloObjectnessThreshold,
    yoloConfidenceThreshold,
    yoloNmsThreshold,
    yoloLargeBoxWidthScale,
    yoloLargeBoxHeightScale,
    yoloLargeBoxMinAreaRatio,
    yoloUseTiling,
    yoloUseFaceOnnxRoiRefine,
    yoloFaceOnnxRoiMinAreaRatio,
    yoloFaceOnnxRoiMaxCandidates,
    yoloTileOnly,
    yoloTileColumns,
    yoloTileRows,
    yoloTileOverlapRatio,
    yoloDebugDump);

if (baseline.HasValue && !CompareCases(input, baseline.Value, optimized, minAvgIou, minBestIou, allowFrameMismatch, dumpCompareDetails, dumpCompareOverlays, compareOverlayDir))
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
$scrfdDebugDumpArg = $ScrfdDebugDump.IsPresent.ToString().ToLowerInvariant()
$scrfdNoStrideScaleArg = $ScrfdNoStrideScale.IsPresent.ToString().ToLowerInvariant()
$scrfdHalfStrideAnchorArg = $ScrfdHalfStrideAnchor.IsPresent.ToString().ToLowerInvariant()
$scrfdCenterLetterboxArg = $ScrfdCenterLetterbox.IsPresent.ToString().ToLowerInvariant()
$scrfdInputSizeArg = $ScrfdInputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$scrfdInputMeanArg = $ScrfdInputMean.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$scrfdInputStdArg = $ScrfdInputStd.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$scrfdPaddingValueArg = $ScrfdPaddingValue.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yuNetModelPathArg = if ([string]::IsNullOrWhiteSpace($YuNetModelPath)) { "__none__" } else { (Resolve-Path $YuNetModelPath).Path }
$yuNetUseTilingArg = $YuNetUseTiling.IsPresent.ToString().ToLowerInvariant()
$yuNetTileOnlyArg = $YuNetTileOnly.IsPresent.ToString().ToLowerInvariant()
$yuNetTileColumnsArg = $YuNetTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yuNetTileRowsArg = $YuNetTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yuNetTileOverlapRatioArg = $YuNetTileOverlapRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloModelPathArg = if ([string]::IsNullOrWhiteSpace($YoloModelPath)) { "__none__" } else { (Resolve-Path $YoloModelPath).Path }
$yoloInputSizeArg = $YoloInputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloObjectnessThresholdArg = $YoloObjectnessThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloConfidenceThresholdArg = $YoloConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloNmsThresholdArg = $YoloNmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxWidthScaleArg = $YoloLargeBoxWidthScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxHeightScaleArg = $YoloLargeBoxHeightScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxMinAreaRatioArg = $YoloLargeBoxMinAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseTilingArg = $YoloUseTiling.IsPresent.ToString().ToLowerInvariant()
$yoloUseFaceOnnxRoiRefineArg = $YoloUseFaceOnnxRoiRefine.IsPresent.ToString().ToLowerInvariant()
$yoloFaceOnnxRoiMinAreaRatioArg = $YoloFaceOnnxRoiMinAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloFaceOnnxRoiMaxCandidatesArg = $YoloFaceOnnxRoiMaxCandidates.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileOnlyArg = $YoloTileOnly.IsPresent.ToString().ToLowerInvariant()
$yoloTileColumnsArg = $YoloTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileRowsArg = $YoloTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileOverlapRatioArg = $YoloTileOverlapRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloDebugDumpArg = $YoloDebugDump.IsPresent.ToString().ToLowerInvariant()
$dumpCompareDetailsArg = $DumpCompareDetails.IsPresent.ToString().ToLowerInvariant()
$dumpCompareOverlaysArg = $DumpCompareOverlays.IsPresent.ToString().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($CompareOverlayDir)) {
    $compareOverlayDirArg = Join-Path $work ("compare-overlays-" + $clipStem)
} elseif ([IO.Path]::IsPathRooted($CompareOverlayDir)) {
    $compareOverlayDirArg = $CompareOverlayDir
} else {
    $compareOverlayDirArg = Join-Path $repo $CompareOverlayDir
}
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
    $scrfdDebugDumpArg `
    $scrfdNoStrideScaleArg `
    $scrfdHalfStrideAnchorArg `
    $scrfdCenterLetterboxArg `
    $scrfdInputSizeArg `
    $scrfdInputMeanArg `
    $scrfdInputStdArg `
    $scrfdPaddingValueArg `
    $yuNetModelPathArg `
    $yuNetUseTilingArg `
    $yuNetTileOnlyArg `
    $yuNetTileColumnsArg `
    $yuNetTileRowsArg `
    $yuNetTileOverlapRatioArg `
    $yoloModelPathArg `
    $YoloModelType `
    $yoloInputSizeArg `
    $yoloObjectnessThresholdArg `
    $yoloConfidenceThresholdArg `
    $yoloNmsThresholdArg `
    $yoloLargeBoxWidthScaleArg `
    $yoloLargeBoxHeightScaleArg `
    $yoloLargeBoxMinAreaRatioArg `
    $yoloUseTilingArg `
    $yoloUseFaceOnnxRoiRefineArg `
    $yoloFaceOnnxRoiMinAreaRatioArg `
    $yoloFaceOnnxRoiMaxCandidatesArg `
    $yoloTileOnlyArg `
    $yoloTileColumnsArg `
    $yoloTileRowsArg `
    $yoloTileOverlapRatioArg `
    $yoloDebugDumpArg `
    $dumpCompareDetailsArg `
    $dumpCompareOverlaysArg `
    $compareOverlayDirArg
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
