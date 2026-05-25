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
    [switch]$SkipOptimized,
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
    [switch]$YoloUseLandmarkBoxRefine,
    [double]$YoloLandmarkBoxMinAreaRatio = 0.03,
    [double]$YoloLandmarkBoxWidthScale = 1.80,
    [double]$YoloLandmarkBoxHeightScale = 2.10,
    [double]$YoloLandmarkBoxCenterYOffsetRatio = -0.04,
    [double]$YoloLandmarkBoxMinOriginalIou = 0.30,
    [switch]$YoloUseFaceOnnxRoiRefine,
    [double]$YoloFaceOnnxRoiMinAreaRatio = 0.03,
    [int]$YoloFaceOnnxRoiMaxCandidates = 64,
    [switch]$YoloUseTiling,
    [switch]$YoloTileOnly,
    [int]$YoloTileColumns = 2,
    [int]$YoloTileRows = 2,
    [double]$YoloTileOverlapRatio = 0.15,
    [int]$YoloMaxLostFillFrames = 0,
    [int]$YoloMaxInitialFillFrames = 3,
    [int]$YoloDropShortTrackMaxDetections = 1,
    [double]$YoloShortTrackMaxConfidence = 0.18,
    [double]$YoloLowerFrameTrackMaxConfidence = 0.50,
    [switch]$YoloUseLowConfidencePositionFilter,
    [double]$YoloLowConfidencePositionMaxConfidence = 0.50,
    [double]$YoloLowConfidencePositionMinCenterYRatio = 0.08,
    [switch]$YoloUseSmallAreaFilter,
    [double]$YoloSmallAreaMaxAreaRatio = 0.0035,
    [switch]$YoloUseAspectRatioFilter,
    [double]$YoloMinAspectRatio = 0.35,
    [double]$YoloMaxAspectRatio = 1.65,
    [switch]$YoloDebugDump,
    [switch]$DumpCompareDetails,
    [switch]$DumpCompareOverlays,
    [string]$CompareOverlayDir = "",
    [int]$CompareOverlayMaxFrames = 16,
    [switch]$DumpCompareCrops,
    [string]$CompareCropDir = "",
    [double]$CompareCropPaddingRatio = 0.65,
    [int]$CompareCropMaxOnlyFrames = 16,
    [int]$CompareCropMaxBoxDiffFrames = 16
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$yoloModelResolver = Join-Path $PSScriptRoot "resolve-yolo-model-path.ps1"
if (Test-Path $yoloModelResolver) {
    . $yoloModelResolver
}
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
using SixLabors.ImageSharp.Processing;

string input = args[0];
string output = args[1];
bool skipBaseline = bool.Parse(args[2]);
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
bool yoloUseLandmarkBoxRefine = args.Length > 45 && bool.Parse(args[45]);
double yoloLandmarkBoxMinAreaRatio = args.Length > 46 ? double.Parse(args[46], System.Globalization.CultureInfo.InvariantCulture) : 0.03;
float yoloLandmarkBoxWidthScale = args.Length > 47 ? float.Parse(args[47], System.Globalization.CultureInfo.InvariantCulture) : 1.80f;
float yoloLandmarkBoxHeightScale = args.Length > 48 ? float.Parse(args[48], System.Globalization.CultureInfo.InvariantCulture) : 2.10f;
float yoloLandmarkBoxCenterYOffsetRatio = args.Length > 49 ? float.Parse(args[49], System.Globalization.CultureInfo.InvariantCulture) : -0.04f;
float yoloLandmarkBoxMinOriginalIou = args.Length > 50 ? float.Parse(args[50], System.Globalization.CultureInfo.InvariantCulture) : 0.30f;
bool yoloUseTiling = args.Length > 51 && bool.Parse(args[51]);
bool yoloUseFaceOnnxRoiRefine = args.Length > 52 && bool.Parse(args[52]);
double yoloFaceOnnxRoiMinAreaRatio = args.Length > 53 ? double.Parse(args[53], System.Globalization.CultureInfo.InvariantCulture) : 0.03;
int yoloFaceOnnxRoiMaxCandidates = args.Length > 54 ? int.Parse(args[54], System.Globalization.CultureInfo.InvariantCulture) : 64;
bool yoloTileOnly = args.Length > 55 && bool.Parse(args[55]);
int yoloTileColumns = args.Length > 56 ? int.Parse(args[56], System.Globalization.CultureInfo.InvariantCulture) : 2;
int yoloTileRows = args.Length > 57 ? int.Parse(args[57], System.Globalization.CultureInfo.InvariantCulture) : 2;
double yoloTileOverlapRatio = args.Length > 58 ? double.Parse(args[58], System.Globalization.CultureInfo.InvariantCulture) : 0.15;
int yoloDropShortTrackMaxDetections = args.Length > 59 ? int.Parse(args[59], System.Globalization.CultureInfo.InvariantCulture) : 1;
float yoloShortTrackMaxConfidence = args.Length > 60 ? float.Parse(args[60], System.Globalization.CultureInfo.InvariantCulture) : 0.18f;
float yoloLowerFrameTrackMaxConfidence = args.Length > 61 ? float.Parse(args[61], System.Globalization.CultureInfo.InvariantCulture) : 0.50f;
bool yoloDebugDump = args.Length > 62 && bool.Parse(args[62]);
bool dumpCompareDetails = args.Length > 63 && bool.Parse(args[63]);
bool dumpCompareOverlays = args.Length > 64 && bool.Parse(args[64]);
string compareOverlayDir = args.Length > 65 && args[65] != "__none__" ? args[65] : string.Empty;
int compareOverlayMaxFrames = args.Length > 66 ? int.Parse(args[66], System.Globalization.CultureInfo.InvariantCulture) : 16;
bool dumpCompareCrops = args.Length > 67 && bool.Parse(args[67]);
string compareCropDir = args.Length > 68 && args[68] != "__none__" ? args[68] : string.Empty;
double compareCropPaddingRatio = args.Length > 69 ? double.Parse(args[69], System.Globalization.CultureInfo.InvariantCulture) : 0.65;
int compareCropMaxOnlyFrames = args.Length > 70 ? int.Parse(args[70], System.Globalization.CultureInfo.InvariantCulture) : 16;
int compareCropMaxBoxDiffFrames = args.Length > 71 ? int.Parse(args[71], System.Globalization.CultureInfo.InvariantCulture) : 16;
bool yoloUseLowConfidencePositionFilter = args.Length > 72 && bool.Parse(args[72]);
float yoloLowConfidencePositionMaxConfidence = args.Length > 73 ? float.Parse(args[73], System.Globalization.CultureInfo.InvariantCulture) : 0.50f;
double yoloLowConfidencePositionMinCenterYRatio = args.Length > 74 ? double.Parse(args[74], System.Globalization.CultureInfo.InvariantCulture) : 0.08;
bool yoloUseSmallAreaFilter = args.Length > 75 && bool.Parse(args[75]);
double yoloSmallAreaMaxAreaRatio = args.Length > 76 ? double.Parse(args[76], System.Globalization.CultureInfo.InvariantCulture) : 0.0035;
bool skipOptimized = args.Length > 77 && bool.Parse(args[77]);
int yoloMaxLostFillFrames = args.Length > 78 ? int.Parse(args[78], System.Globalization.CultureInfo.InvariantCulture) : 0;
bool yoloUseAspectRatioFilter = args.Length > 79 && bool.Parse(args[79]);
double yoloMinAspectRatio = args.Length > 80 ? double.Parse(args[80], System.Globalization.CultureInfo.InvariantCulture) : 0.35;
double yoloMaxAspectRatio = args.Length > 81 ? double.Parse(args[81], System.Globalization.CultureInfo.InvariantCulture) : 1.65;
int yoloMaxInitialFillFrames = args.Length > 82 ? int.Parse(args[82], System.Globalization.CultureInfo.InvariantCulture) : 3;

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
    bool yoloUseLandmarkBoxRefine,
    double yoloLandmarkBoxMinAreaRatio,
    float yoloLandmarkBoxWidthScale,
    float yoloLandmarkBoxHeightScale,
    float yoloLandmarkBoxCenterYOffsetRatio,
    float yoloLandmarkBoxMinOriginalIou,
    bool yoloUseTiling,
    bool yoloUseFaceOnnxRoiRefine,
    double yoloFaceOnnxRoiMinAreaRatio,
    int yoloFaceOnnxRoiMaxCandidates,
    bool yoloTileOnly,
    int yoloTileColumns,
    int yoloTileRows,
    double yoloTileOverlapRatio,
    int yoloMaxLostFillFrames,
    int yoloMaxInitialFillFrames,
    int yoloDropShortTrackMaxDetections,
    float yoloShortTrackMaxConfidence,
    float yoloLowerFrameTrackMaxConfidence,
    bool yoloUseLowConfidencePositionFilter,
    float yoloLowConfidencePositionMaxConfidence,
    double yoloLowConfidencePositionMinCenterYRatio,
    bool yoloUseSmallAreaFilter,
    double yoloSmallAreaMaxAreaRatio,
    bool yoloUseAspectRatioFilter,
    double yoloMinAspectRatio,
    double yoloMaxAspectRatio,
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
            UseLowConfidencePositionFilter = yoloUseLowConfidencePositionFilter,
            LowConfidencePositionMaxConfidence = yoloLowConfidencePositionMaxConfidence,
            LowConfidencePositionMinCenterYRatio = yoloLowConfidencePositionMinCenterYRatio,
            UseSmallAreaFilter = yoloUseSmallAreaFilter,
            SmallAreaMaxAreaRatio = yoloSmallAreaMaxAreaRatio,
            UseAspectRatioFilter = yoloUseAspectRatioFilter,
            MinAspectRatio = yoloMinAspectRatio,
            MaxAspectRatio = yoloMaxAspectRatio,
            LargeBoxWidthScale = yoloLargeBoxWidthScale,
            LargeBoxHeightScale = yoloLargeBoxHeightScale,
            LargeBoxMinAreaRatio = yoloLargeBoxMinAreaRatio,
            UseYolo5LandmarkBoxRefine = yoloUseLandmarkBoxRefine,
            Yolo5LandmarkBoxMinAreaRatio = yoloLandmarkBoxMinAreaRatio,
            Yolo5LandmarkBoxWidthScale = yoloLandmarkBoxWidthScale,
            Yolo5LandmarkBoxHeightScale = yoloLandmarkBoxHeightScale,
            Yolo5LandmarkBoxCenterYOffsetRatio = yoloLandmarkBoxCenterYOffsetRatio,
            Yolo5LandmarkBoxMinOriginalIou = yoloLandmarkBoxMinOriginalIou,
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
    const float yoloSceneCutDirectCarryMaxConfidence = 0.90f;
    const float yoloSceneCutDirectCarryMinSourceConfidence = 0.58f;
    const float yoloSceneCutPostCutCarryMaxConfidence = 0.78f;
    const double yoloSceneCutDifferenceThreshold = 0.15;
    const double yoloSceneCutDirectDifferenceThreshold = 0.36;
    const int yoloSceneCutDirectDifferenceMaxCandidates = 24;
    const int yoloSceneCutMatchingTailMaxFrames = 5;
    const float yoloSceneCutMatchingTailMaxConfidence = 0.90f;
    const double yoloSceneCutCandidateMatchMinIou = 0.55;
    const double yoloSceneCutCandidateMatchMaxCenterShiftRatio = 0.45;
    const double yoloSceneCutCandidateMatchMaxAreaChangeRatio = 2.0;
    const int yoloSceneCutPostCutLookbackFrames = 3;
    const int yoloFinalMaskStableGapMaxFrames = 5;
    var trackOptions = useYolo
        ? new FaceTrackPostProcessOptions
            {
                MaxTrackGap = 8,
                MaxFillGap = 5,
                MaxLostFillFrames = yoloMaxLostFillFrames,
                MaxInitialFillFrames = yoloMaxInitialFillFrames,
                MaxConfirmedTrackHoldFrames = 8,
                AllowSmallTrackLostFill = true,
                WeakConfidence = 0.38f,
                StrongConfidence = 0.58f,
                SyntheticFillConfidenceMax = yoloSceneCutPostCutCarryMaxConfidence,
                DropShortTrackMaxDetections = yoloDropShortTrackMaxDetections,
                ShortTrackMaxConfidence = yoloShortTrackMaxConfidence,
                DropSparseTrackMaxDetections = 3,
                DropSparseTrackMinSpanFrames = 8,
                DropSparseTrackMaxDensity = 0.42,
                SparseTrackMaxConfidence = 0.56f,
                EdgeTailMaxConfidence = 0.50f,
                EdgeTailMinStableDetections = 3,
                EdgeLostFillMaxConfidence = 0.60f,
                SmallTrackMaxAreaRatio = 0.00070,
                MinTrackIou = 0.08,
                MaxCenterShiftRatio = 0.72,
                MaxAreaChangeRatio = 4.0,
                DuplicateIou = 0.35,
                UnstableTailMaxConfidence = 0.40f,
                UnstableTailMinStableDetections = 3,
                UnstableTailMinIou = 0.45,
                UnstableTailMaxAreaChangeRatio = 1.8,
                LowerFrameTrackMaxConfidence = yoloLowerFrameTrackMaxConfidence,
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
        };
    var trackPost = new FaceTrackInterpolator().Apply(
        maskProvider,
        generator.LastRunSummary?.TotalFrames ?? 0,
        trackOptions);
    string lostFrames = trackPost.FilledLostFrameIndices.Count == 0
        ? "none"
        : string.Join(",", trackPost.FilledLostFrameIndices.Take(32));
    Console.WriteLine($"[SmokeFaceTrackPost] label={label}, tracks={trackPost.TrackCount}, filled={trackPost.FilledGapFaces}, lostFilled={trackPost.FilledLostFaces}, initialFilled={trackPost.FilledInitialFaces}, lostFrames={lostFrames}, removedShort={trackPost.RemovedShortFaces}, removedSparse={trackPost.RemovedSparseFaces}, removedEdgeTail={trackPost.RemovedEdgeTailFaces}, removedLower={trackPost.RemovedLowerFrameFaces}, rewritten={trackPost.RewrittenFrames}");
    if (detector is IBgraFaceDetector bgraDetector)
    {
        using var faceOnnxRoiDetector = useScrfd || useYuNet || useYolo ? null : new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions));
        var roiDetector = useScrfd || useYuNet || useYolo ? bgraDetector : faceOnnxRoiDetector;
        var refine = new FaceTrackRoiRefiner().Apply(
            maskProvider,
            input,
            roiDetector!,
            trackPost.FilledGapFacesInfo.Concat(trackPost.FilledLostFacesInfo).Concat(trackPost.FilledInitialFacesInfo).ToArray(),
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
    if (useYolo)
    {
        var postProcessor = new YoloFinalMaskPostProcessor();
        var cleanup = postProcessor.RemoveWeakIsolatedMasks(maskProvider);
        var cleanupBlockedFrameIndices = cleanup.RemovedFrameIndices;
        Console.WriteLine($"[SmokeYoloFinalMaskCleanup] label={label}, removedWeakIsolated={cleanup.RemovedWeakIsolatedFaces}, removedWeakUnsupported={cleanup.RemovedWeakUnsupportedFaces}, removedWeakShortClusters={cleanup.RemovedWeakShortClusterFaces}, removedWeakTinyClusters={cleanup.RemovedWeakTinyClusterFaces}, removedTinyShortClusters={cleanup.RemovedTinyShortClusterFaces}, removedTinyIsolated={cleanup.RemovedTinyIsolatedFaces}, removedUpperWeakClusters={cleanup.RemovedUpperWeakClusterFaces}, removedLowerWeakClusters={cleanup.RemovedLowerWeakClusterFaces}, removedAspectOutliers={cleanup.RemovedAspectOutlierClusterFaces}, removedFrames={FormatFrames(cleanup.RemovedFrameIndices)}");
        var gapFill = postProcessor.FillShortStableGaps(
            maskProvider,
            new YoloFinalMaskGapFillOptions
            {
                MaxGapFrames = yoloFinalMaskStableGapMaxFrames,
                BlockedFrameIndices = cleanup.RemovedFrameIndices
            });
        Console.WriteLine($"[SmokeYoloFinalMaskGapFill] label={label}, filled={gapFill.FilledFaces}, frames={FormatFrames(gapFill.FilledFrameIndices)}, blockedByCut={gapFill.BlockedCutGapFaces}, cutBlockedFrames={FormatFrames(gapFill.BlockedCutFrameIndices)}, blockedByCleanup={gapFill.BlockedCleanupGapFrames}, cleanupBlockedFrames={FormatFrames(gapFill.BlockedCleanupFrameIndices)}");
        var gapFillGuard = gapFill.CutGuardFacesInfo.Count == 0
            ? FaceTrackSceneCutGuardResult.Empty
            : new FaceTrackSceneCutGuard().Apply(
                maskProvider,
                input,
                gapFill.CutGuardFacesInfo,
                differenceThreshold: yoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: yoloSceneCutDirectDifferenceThreshold,
                candidateMatchMinIou: yoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: yoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: yoloSceneCutCandidateMatchMaxAreaChangeRatio);
        Console.WriteLine($"[SmokeYoloFinalMaskGapFillSceneCutGuard] label={label}, candidates={gapFill.CutGuardFacesInfo.Count}, checked={gapFillGuard.Checked}, checkedPairs={FormatTextValues(gapFillGuard.CheckedFramePairs)}, maxDiff={gapFillGuard.MaxDifference:F3}, cutPairs={FormatTextValues(gapFillGuard.CutFramePairs)}, removed={gapFillGuard.Removed}, removedFrames={FormatFrames(gapFillGuard.RemovedFrameIndices)}, threshold={gapFillGuard.Threshold:F3}, elapsedMs={gapFillGuard.ElapsedMs}, error={gapFillGuard.Error ?? "none"}");
        var sceneCutGuard = new FaceTrackSceneCutGuard();
        var directCandidates = sceneCutGuard.BuildWeakTrackTransitionCandidates(
            maskProvider,
            trackOptions,
            maxTargetConfidence: yoloSceneCutDirectCarryMaxConfidence,
            maxTransitionGap: 8,
            minConfidenceDrop: 0.0f,
            maxPostCutCarryFrames: 5,
            minSourceConfidence: yoloSceneCutDirectCarryMinSourceConfidence);
        var postCutCandidates = sceneCutGuard.BuildWeakPostCutCarryCandidates(
            maskProvider,
            maxTargetConfidence: yoloSceneCutPostCutCarryMaxConfidence,
            maxCarryFrames: 5,
            sourceLookbackFrames: yoloSceneCutPostCutLookbackFrames,
            includeEdgeCandidates: true);
        var sceneCutCandidates = trackPost.FilledGapFacesInfo
            .Concat(trackPost.FilledLostFacesInfo)
            .Concat(trackPost.FilledInitialFacesInfo)
            .Concat(directCandidates)
            .Concat(postCutCandidates)
            .ToArray();
        var sceneCut = sceneCutGuard.Apply(
            maskProvider,
            input,
            sceneCutCandidates,
            differenceThreshold: yoloSceneCutDifferenceThreshold,
            directDifferenceThreshold: yoloSceneCutDirectDifferenceThreshold,
            directDifferenceMaxChecks: yoloSceneCutDirectDifferenceMaxCandidates,
            removeMatchingTailFrames: yoloSceneCutMatchingTailMaxFrames,
            removeMatchingTailMaxConfidence: yoloSceneCutMatchingTailMaxConfidence,
            candidateMatchMinIou: yoloSceneCutCandidateMatchMinIou,
            candidateMatchMaxCenterShiftRatio: yoloSceneCutCandidateMatchMaxCenterShiftRatio,
            candidateMatchMaxAreaChangeRatio: yoloSceneCutCandidateMatchMaxAreaChangeRatio);
        Console.WriteLine($"[SmokeFaceTrackSceneCutGuard] label={label}, directCandidates={directCandidates.Count}, postCutCandidates={postCutCandidates.Count}, checked={sceneCut.Checked}, directChecked={sceneCut.DirectDifferenceChecks}, directSkipped={sceneCut.DirectDifferenceSkipped}, checkedPairs={FormatTextValues(sceneCut.CheckedFramePairs)}, maxDiff={sceneCut.MaxDifference:F3}, cutPairs={FormatTextValues(sceneCut.CutFramePairs)}, removed={sceneCut.Removed}, removedFrames={FormatFrames(sceneCut.RemovedFrameIndices)}, threshold={sceneCut.Threshold:F3}, elapsedMs={sceneCut.ElapsedMs}, error={sceneCut.Error ?? "none"}");
        var postSceneCleanup = postProcessor.RemoveWeakIsolatedMasks(maskProvider);
        var postSceneBlockedFrameIndices = cleanupBlockedFrameIndices
            .Concat(postSceneCleanup.RemovedFrameIndices)
            .Distinct()
            .OrderBy(static frame => frame)
            .ToArray();
        Console.WriteLine($"[SmokeYoloFinalMaskPostSceneCleanup] label={label}, removedWeakIsolated={postSceneCleanup.RemovedWeakIsolatedFaces}, removedWeakUnsupported={postSceneCleanup.RemovedWeakUnsupportedFaces}, removedWeakShortClusters={postSceneCleanup.RemovedWeakShortClusterFaces}, removedWeakTinyClusters={postSceneCleanup.RemovedWeakTinyClusterFaces}, removedTinyShortClusters={postSceneCleanup.RemovedTinyShortClusterFaces}, removedTinyIsolated={postSceneCleanup.RemovedTinyIsolatedFaces}, removedUpperWeakClusters={postSceneCleanup.RemovedUpperWeakClusterFaces}, removedLowerWeakClusters={postSceneCleanup.RemovedLowerWeakClusterFaces}, removedAspectOutliers={postSceneCleanup.RemovedAspectOutlierClusterFaces}, removedFrames={FormatFrames(postSceneCleanup.RemovedFrameIndices)}");
        var postSceneGapFill = postProcessor.FillShortStableGaps(
            maskProvider,
            new YoloFinalMaskGapFillOptions
            {
                MaxGapFrames = yoloFinalMaskStableGapMaxFrames,
                BlockedCutFramePairs = sceneCut.CutFramePairs,
                BlockedFrameIndices = postSceneBlockedFrameIndices
            });
        Console.WriteLine($"[SmokeYoloFinalMaskPostSceneGapFill] label={label}, filled={postSceneGapFill.FilledFaces}, frames={FormatFrames(postSceneGapFill.FilledFrameIndices)}, blockedByCut={postSceneGapFill.BlockedCutGapFaces}, cutBlockedFrames={FormatFrames(postSceneGapFill.BlockedCutFrameIndices)}, blockedByCleanup={postSceneGapFill.BlockedCleanupGapFrames}, cleanupBlockedFrames={FormatFrames(postSceneGapFill.BlockedCleanupFrameIndices)}");
        var postSceneGapFillGuard = postSceneGapFill.CutGuardFacesInfo.Count == 0
            ? FaceTrackSceneCutGuardResult.Empty
            : new FaceTrackSceneCutGuard().Apply(
                maskProvider,
                input,
                postSceneGapFill.CutGuardFacesInfo,
                differenceThreshold: yoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: yoloSceneCutDirectDifferenceThreshold,
                candidateMatchMinIou: yoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: yoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: yoloSceneCutCandidateMatchMaxAreaChangeRatio);
        Console.WriteLine($"[SmokeYoloFinalMaskPostSceneGapFillSceneCutGuard] label={label}, candidates={postSceneGapFill.CutGuardFacesInfo.Count}, checked={postSceneGapFillGuard.Checked}, checkedPairs={FormatTextValues(postSceneGapFillGuard.CheckedFramePairs)}, maxDiff={postSceneGapFillGuard.MaxDifference:F3}, cutPairs={FormatTextValues(postSceneGapFillGuard.CutFramePairs)}, removed={postSceneGapFillGuard.Removed}, removedFrames={FormatFrames(postSceneGapFillGuard.RemovedFrameIndices)}, threshold={postSceneGapFillGuard.Threshold:F3}, elapsedMs={postSceneGapFillGuard.ElapsedMs}, error={postSceneGapFillGuard.Error ?? "none"}");
    }
    Console.WriteLine($"[Smoke] label={label}, faceMaskFrames={maskProvider.GetFaceMaskFrameIndices().Length}, storedMaskFrames={maskProvider.GetStoredMaskFrameIndices().Length}");
    if (useYolo)
        LogFinalMaskSummary(label, maskProvider);
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

static void LogFinalMaskSummary(string label, FrameMaskProvider maskProvider)
{
    const float lowConfidenceThreshold = 0.38f;
    const float weakNonEdgeThreshold = 0.50f;
    const float tinyShortThreshold = 0.62f;
    const float upperWeakThreshold = 0.60f;
    const double edgeMarginRatio = 0.02;
    const double tinyWeakAreaRatio = 0.0012;
    const double tinyShortAreaRatio = 0.0009;
    const double upperWeakCenterYRatio = 0.10;
    const double upperWeakAreaRatio = 0.0065;
    const float lowerWeakThreshold = 0.50f;
    const double lowerWeakCenterYRatio = 0.58;
    const double lowerWeakMinAreaRatio = 0.015;
    const double lowerWeakMaxAreaRatio = 0.045;
    const double finalMaskMinAspectRatio = 0.35;
    const double finalMaskMaxAspectRatio = 1.65;
    const int shortGapMaxFrames = 3;
    const double largeJumpAreaChangeRatio = 4.0;
    const double largeJumpCenterShift = 0.20;

    var entries = maskProvider.GetFaceMaskEntries()
        .Where(x => x.Value.Faces.Count > 0)
        .OrderBy(x => x.Key)
        .ToArray();
    if (entries.Length == 0)
    {
        Console.WriteLine($"[SmokeFinalMaskSummary] label={label}, frames=0, rows=0, frameRange=none, shortGaps=0, shortGapRanges=none, largeJumpGaps=0, largeJumpRanges=none, isolated=0, isolatedFrames=none, lowConf=0, lowConfFrames=none, weakNonEdge=0, weakNonEdgeFrames=none, edgeWeak=0, edgeWeakFrames=none, topEdgeWeak=0, topEdgeWeakFrames=none, upperWeak=0, upperWeakFrames=none, lowerWeak=0, lowerWeakFrames=none, aspectBad=0, aspectBadFrames=none, tinyWeak=0, tinyWeakFrames=none, tinyShort=0, tinyShortFrames=none");
        return;
    }

    var frames = entries.Select(x => x.Key).ToArray();
    int rows = entries.Sum(x => x.Value.Faces.Count);
    var shortGapRanges = new List<string>();
    var largeJumpGapRanges = new List<string>();
    int shortGapCount = 0;
    for (int i = 1; i < frames.Length; i++)
    {
        int missing = frames[i] - frames[i - 1] - 1;
        if (missing <= 0 || missing > shortGapMaxFrames)
            continue;

        shortGapCount++;
        int start = frames[i - 1] + 1;
        int end = frames[i] - 1;
        string range = FormatFrameRange(start, end);
        shortGapRanges.Add(range);

        if (TryGetBestFinalMaskFace(entries[i - 1].Value, out var previousFace) &&
            TryGetBestFinalMaskFace(entries[i].Value, out var nextFace))
        {
            double areaChange = GetFinalMaskAreaChange(previousFace, nextFace);
            double centerShift = GetFinalMaskCenterShift(previousFace, nextFace);
            if (areaChange >= largeJumpAreaChangeRatio ||
                centerShift >= largeJumpCenterShift)
            {
                largeJumpGapRanges.Add(range);
            }
        }
    }

    var isolatedFrames = new List<int>();
    for (int i = 0; i < frames.Length; i++)
    {
        bool hasPreviousNeighbor = i > 0 && frames[i] - frames[i - 1] <= 1;
        bool hasNextNeighbor = i < frames.Length - 1 && frames[i + 1] - frames[i] <= 1;
        if (!hasPreviousNeighbor && !hasNextNeighbor)
            isolatedFrames.Add(frames[i]);
    }

    int lowConfidenceRows = 0;
    int weakNonEdgeRows = 0;
    int edgeWeakRows = 0;
    int topEdgeWeakRows = 0;
    int upperWeakRows = 0;
    int lowerWeakRows = 0;
    int aspectBadRows = 0;
    int tinyWeakRows = 0;
    int tinyShortRows = 0;
    var lowConfidenceFrames = new SortedSet<int>();
    var weakNonEdgeFrames = new SortedSet<int>();
    var edgeWeakFrames = new SortedSet<int>();
    var topEdgeWeakFrames = new SortedSet<int>();
    var upperWeakFrames = new SortedSet<int>();
    var lowerWeakFrames = new SortedSet<int>();
    var aspectBadFrames = new SortedSet<int>();
    var tinyWeakFrames = new SortedSet<int>();
    var tinyShortFrames = new SortedSet<int>();
    foreach (var entry in entries)
    {
        int frameIndex = entry.Key;
        var data = entry.Value;
        for (int i = 0; i < data.Faces.Count; i++)
        {
            var face = data.Faces[i];
            float confidence = i < data.Confidences.Count
                ? data.Confidences[i]
                : data.MinConfidence ?? 1.0f;
            if (confidence <= lowConfidenceThreshold)
            {
                lowConfidenceRows++;
                lowConfidenceFrames.Add(frameIndex);
            }

            bool touchesEdge = TouchesFinalMaskFrameEdge(face, data.Size, edgeMarginRatio);
            if (confidence <= weakNonEdgeThreshold)
            {
                if (touchesEdge)
                {
                    edgeWeakRows++;
                    edgeWeakFrames.Add(frameIndex);
                    if (IsUpperWeakFinalMaskFace(face, data.Size, upperWeakCenterYRatio, upperWeakAreaRatio))
                    {
                        topEdgeWeakRows++;
                        topEdgeWeakFrames.Add(frameIndex);
                    }
                }
                else
                {
                    weakNonEdgeRows++;
                    weakNonEdgeFrames.Add(frameIndex);
                    if (IsTinyFinalMaskFace(face, data.Size, tinyWeakAreaRatio))
                    {
                        tinyWeakRows++;
                        tinyWeakFrames.Add(frameIndex);
                    }
                }
            }
            if (confidence <= upperWeakThreshold &&
                !touchesEdge &&
                IsUpperWeakFinalMaskFace(face, data.Size, upperWeakCenterYRatio, upperWeakAreaRatio))
            {
                upperWeakRows++;
                upperWeakFrames.Add(frameIndex);
            }
            if (confidence <= lowerWeakThreshold &&
                !touchesEdge &&
                IsLowerWeakFinalMaskFace(face, data.Size, lowerWeakCenterYRatio, lowerWeakMinAreaRatio, lowerWeakMaxAreaRatio))
            {
                lowerWeakRows++;
                lowerWeakFrames.Add(frameIndex);
            }
            if (IsAbnormalFinalMaskAspect(face, finalMaskMinAspectRatio, finalMaskMaxAspectRatio))
            {
                aspectBadRows++;
                aspectBadFrames.Add(frameIndex);
            }
            if (confidence <= tinyShortThreshold &&
                !touchesEdge &&
                IsTinyFinalMaskFace(face, data.Size, tinyShortAreaRatio))
            {
                tinyShortRows++;
                tinyShortFrames.Add(frameIndex);
            }
        }
    }

    Console.WriteLine(
        $"[SmokeFinalMaskSummary] label={label}, frames={frames.Length}, rows={rows}, frameRange={frames[0]}-{frames[^1]}, shortGaps={shortGapCount}, shortGapRanges={FormatTextValues(shortGapRanges)}, largeJumpGaps={largeJumpGapRanges.Count}, largeJumpRanges={FormatTextValues(largeJumpGapRanges)}, isolated={isolatedFrames.Count}, isolatedFrames={FormatFrames(isolatedFrames)}, lowConf={lowConfidenceRows}, lowConfFrames={FormatFrames(lowConfidenceFrames.ToArray())}, weakNonEdge={weakNonEdgeRows}, weakNonEdgeFrames={FormatFrames(weakNonEdgeFrames.ToArray())}, edgeWeak={edgeWeakRows}, edgeWeakFrames={FormatFrames(edgeWeakFrames.ToArray())}, topEdgeWeak={topEdgeWeakRows}, topEdgeWeakFrames={FormatFrames(topEdgeWeakFrames.ToArray())}, upperWeak={upperWeakRows}, upperWeakFrames={FormatFrames(upperWeakFrames.ToArray())}, lowerWeak={lowerWeakRows}, lowerWeakFrames={FormatFrames(lowerWeakFrames.ToArray())}, aspectBad={aspectBadRows}, aspectBadFrames={FormatFrames(aspectBadFrames.ToArray())}, tinyWeak={tinyWeakRows}, tinyWeakFrames={FormatFrames(tinyWeakFrames.ToArray())}, tinyShort={tinyShortRows}, tinyShortFrames={FormatFrames(tinyShortFrames.ToArray())}");
}

static bool TouchesFinalMaskFrameEdge(Rect face, PixelSize size, double edgeMarginRatio)
{
    if (size.Width <= 0 || size.Height <= 0)
        return false;

    double marginX = size.Width * edgeMarginRatio;
    double marginY = size.Height * edgeMarginRatio;
    return face.X <= marginX ||
        face.Y <= marginY ||
        face.Right >= size.Width - marginX ||
        face.Bottom >= size.Height - marginY;
}

static bool IsTinyFinalMaskFace(Rect face, PixelSize size, double maxAreaRatio)
{
    if (size.Width <= 0 || size.Height <= 0)
        return false;

    double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
    double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
    return areaRatio <= maxAreaRatio;
}

static bool IsUpperWeakFinalMaskFace(Rect face, PixelSize size, double maxCenterYRatio, double maxAreaRatio)
{
    if (size.Width <= 0 || size.Height <= 0)
        return false;

    double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
    double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
    double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
    return centerYRatio <= maxCenterYRatio && areaRatio <= maxAreaRatio;
}

static bool IsLowerWeakFinalMaskFace(Rect face, PixelSize size, double minCenterYRatio, double minAreaRatio, double maxAreaRatio)
{
    if (size.Width <= 0 || size.Height <= 0)
        return false;

    double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
    double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
    double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
    return centerYRatio >= minCenterYRatio &&
        areaRatio >= minAreaRatio &&
        areaRatio <= maxAreaRatio;
}

static bool IsAbnormalFinalMaskAspect(Rect face, double minAspectRatio, double maxAspectRatio)
{
    if (face.Width <= 0 || face.Height <= 0)
        return false;

    double aspectRatio = face.Width / face.Height;
    return aspectRatio < minAspectRatio || aspectRatio > maxAspectRatio;
}

static bool TryGetBestFinalMaskFace(
    FrameMaskProvider.FaceMaskData data,
    out (double CenterX, double CenterY, double AreaRatio, double Confidence) evidence)
{
    evidence = default;
    if (data.Faces.Count == 0 || data.Size.Width <= 0 || data.Size.Height <= 0)
        return false;

    double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
    int bestIndex = 0;
    double bestConfidence = double.NegativeInfinity;
    for (int i = 0; i < data.Faces.Count; i++)
    {
        double confidence = i < data.Confidences.Count
            ? data.Confidences[i]
            : data.MinConfidence ?? 1.0f;
        if (confidence > bestConfidence)
        {
            bestConfidence = confidence;
            bestIndex = i;
        }
    }

    var face = data.Faces[bestIndex];
    evidence = (
        (face.X + face.Width * 0.5) / data.Size.Width,
        (face.Y + face.Height * 0.5) / data.Size.Height,
        Math.Max(0.0, face.Width * face.Height) / frameArea,
        bestConfidence);
    return true;
}

static double GetFinalMaskAreaChange(
    (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
    (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
{
    double a = Math.Max(0.000001, previous.AreaRatio);
    double b = Math.Max(0.000001, next.AreaRatio);
    return Math.Max(a / b, b / a);
}

static double GetFinalMaskCenterShift(
    (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
    (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
{
    double dx = next.CenterX - previous.CenterX;
    double dy = next.CenterY - previous.CenterY;
    return Math.Sqrt(dx * dx + dy * dy);
}

static string FormatFrameRange(int start, int end)
{
    return start == end
        ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{start}-{end}");
}

static void DumpDetections(string label, FrameMaskProvider maskProvider)
{
    int frameCount = 0;
    int detectionCount = 0;
    int firstFrame = int.MaxValue;
    int lastFrame = int.MinValue;
    double confSum = 0.0;
    double minConf = double.PositiveInfinity;
    double maxConf = double.NegativeInfinity;
    double areaRatioSum = 0.0;
    double minAreaRatio = double.PositiveInfinity;
    double maxAreaRatio = double.NegativeInfinity;
    double aspectRatioSum = 0.0;
    double minAspectRatio = double.PositiveInfinity;
    double maxAspectRatio = double.NegativeInfinity;

    foreach (var entry in maskProvider.GetFaceMaskEntries().OrderBy(x => x.Key))
    {
        var data = entry.Value;
        if (data.Faces.Count == 0)
            continue;

        frameCount++;
        firstFrame = Math.Min(firstFrame, entry.Key);
        lastFrame = Math.Max(lastFrame, entry.Key);
        for (int i = 0; i < data.Faces.Count; i++)
        {
            var r = data.Faces[i];
            float conf = i < data.Confidences.Count ? data.Confidences[i] : data.MinConfidence ?? 1.0f;
            double area = Math.Max(0.0, r.Width * r.Height);
            double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
            double areaRatio = area / frameArea;
            double aspectRatio = r.Height > 0.0 ? r.Width / r.Height : 0.0;
            double centerX = (r.X + r.Width * 0.5) / Math.Max(1.0, data.Size.Width);
            double centerY = (r.Y + r.Height * 0.5) / Math.Max(1.0, data.Size.Height);
            detectionCount++;
            confSum += conf;
            minConf = Math.Min(minConf, conf);
            maxConf = Math.Max(maxConf, conf);
            areaRatioSum += areaRatio;
            minAreaRatio = Math.Min(minAreaRatio, areaRatio);
            maxAreaRatio = Math.Max(maxAreaRatio, areaRatio);
            aspectRatioSum += aspectRatio;
            minAspectRatio = Math.Min(minAspectRatio, aspectRatio);
            maxAspectRatio = Math.Max(maxAspectRatio, aspectRatio);
            Console.WriteLine(
                $"[SmokeDetection] label={label}, frame={entry.Key}, index={i}, x={r.X:F1}, y={r.Y:F1}, w={r.Width:F1}, h={r.Height:F1}, area={area:F1}, conf={conf:F3}, cx={centerX:F3}, cy={centerY:F3}, areaRatio={areaRatio:F6}, aspectRatio={aspectRatio:F3}");
        }
    }

    string frameRange = detectionCount == 0 ? "none" : $"{firstFrame}-{lastFrame}";
    double avgConf = detectionCount == 0 ? 0.0 : confSum / detectionCount;
    double avgAreaRatio = detectionCount == 0 ? 0.0 : areaRatioSum / detectionCount;
    double avgAspectRatio = detectionCount == 0 ? 0.0 : aspectRatioSum / detectionCount;
    if (detectionCount == 0)
    {
        minConf = 0.0;
        maxConf = 0.0;
        minAreaRatio = 0.0;
        maxAreaRatio = 0.0;
        minAspectRatio = 0.0;
        maxAspectRatio = 0.0;
    }

    Console.WriteLine(
        $"[SmokeDetectionSummary] label={label}, frames={frameCount}, detections={detectionCount}, frameRange={frameRange}, confMin={minConf:F3}, confAvg={avgConf:F3}, confMax={maxConf:F3}, areaRatioMin={minAreaRatio:F6}, areaRatioAvg={avgAreaRatio:F6}, areaRatioMax={maxAreaRatio:F6}, aspectRatioMin={minAspectRatio:F3}, aspectRatioAvg={avgAspectRatio:F3}, aspectRatioMax={maxAspectRatio:F3}");
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
    string compareOverlayDir,
    int compareOverlayMaxFrames,
    bool dumpCompareCrops,
    string compareCropDir,
    double compareCropPaddingRatio,
    int compareCropMaxOnlyFrames,
    int compareCropMaxBoxDiffFrames)
{
    var baselineEntries = baseline.MaskProvider.GetFaceMaskEntries().ToDictionary(x => x.Key, x => x.Value);
    var optimizedEntries = optimized.MaskProvider.GetFaceMaskEntries().ToDictionary(x => x.Key, x => x.Value);
    var baselineFrames = baselineEntries.Keys.OrderBy(x => x).ToArray();
    var optimizedFrames = optimizedEntries.Keys.OrderBy(x => x).ToArray();
    var commonFrames = baselineFrames.Intersect(optimizedFrames).OrderBy(x => x).ToArray();
    var onlyBaseline = baselineFrames.Except(optimizedFrames).OrderBy(x => x).ToArray();
    var onlyOptimized = optimizedFrames.Except(baselineFrames).OrderBy(x => x).ToArray();

    double iouSum = 0.0;
    double coverageSum = 0.0;
    double minIou = commonFrames.Length > 0 ? 1.0 : 0.0;
    double minCoverage = commonFrames.Length > 0 ? 1.0 : 0.0;
    int iouSamples = 0;
    int coverageSamples = 0;
    int boxCountDiffFrames = 0;
    var lowIouFrames = new List<int>();
    var lowCoverageFrames = new List<int>();
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
            double bestCoverage = 0.0;
            foreach (var candidate in optFaces)
            {
                best = Math.Max(best, IoU(face, candidate));
                bestCoverage = Math.Max(bestCoverage, Coverage(face, candidate));
            }

            iouSum += best;
            coverageSum += bestCoverage;
            minIou = Math.Min(minIou, best);
            minCoverage = Math.Min(minCoverage, bestCoverage);
            if (best < minBestIou && !lowIouFrames.Contains(frame))
                lowIouFrames.Add(frame);
            if (bestCoverage < 0.95 && !lowCoverageFrames.Contains(frame))
                lowCoverageFrames.Add(frame);
            iouSamples++;
            coverageSamples++;
        }
    }

    double avgIou = iouSamples > 0 ? iouSum / iouSamples : 0.0;
    double avgCoverage = coverageSamples > 0 ? coverageSum / coverageSamples : 0.0;
    Console.WriteLine(
        $"[SmokeCompare] baseline={baseline.Label}, optimized={optimized.Label}, baselineFrames={baselineFrames.Length}, optimizedFrames={optimizedFrames.Length}, common={commonFrames.Length}, onlyBaseline={onlyBaseline.Length}, onlyOptimized={onlyOptimized.Length}, avgBestIou={avgIou:F3}, minBestIou={minIou:F3}, avgBaselineCoverage={avgCoverage:F3}, minBaselineCoverage={minCoverage:F3}, boxCountDiffFrames={boxCountDiffFrames}");
    Console.WriteLine($"[SmokeCompareFrames] onlyBaseline={FormatFrames(onlyBaseline)}, onlyOptimized={FormatFrames(onlyOptimized)}");
    Console.WriteLine($"[SmokeCompareBadFrames] boxCountDiff={FormatFrames(boxCountDiffFrameList)}, lowIou={FormatFrames(lowIouFrames)}, lowBaselineCoverage={FormatFrames(lowCoverageFrames)}");
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
        DumpCompareOverlays(input, compareOverlayDir, compareOverlayMaxFrames, onlyBaseline, onlyOptimized, boxCountDiffFrameList, lowIouFrames, baselineEntries, optimizedEntries);
    }
    if (dumpCompareCrops && !string.IsNullOrWhiteSpace(compareCropDir))
    {
        DumpCompareCrops(input, compareCropDir, compareCropPaddingRatio, compareCropMaxOnlyFrames, compareCropMaxBoxDiffFrames, onlyBaseline, onlyOptimized, boxCountDiffFrameList, baselineEntries, optimizedEntries);
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
    int maxFramesPerReason,
    IReadOnlyList<int> onlyBaseline,
    IReadOnlyList<int> onlyOptimized,
    IReadOnlyList<int> boxCountDiff,
    IReadOnlyList<int> lowIou,
    Dictionary<int, FrameMaskProvider.FaceMaskData> baselineEntries,
    Dictionary<int, FrameMaskProvider.FaceMaskData> optimizedEntries)
{
    Directory.CreateDirectory(outputDir);
    var items = new List<(string Reason, int Frame)>();
    int limit = Math.Max(0, maxFramesPerReason);
    items.AddRange(onlyBaseline.Take(limit).Select(frame => ("onlyBaseline", frame)));
    items.AddRange(onlyOptimized.Take(limit).Select(frame => ("onlyOptimized", frame)));
    items.AddRange(boxCountDiff.Take(limit).Select(frame => ("boxCountDiff", frame)));
    items.AddRange(lowIou.Take(limit).Select(frame => ("lowIou", frame)));
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

static void DumpCompareCrops(
    string input,
    string outputDir,
    double paddingRatio,
    int maxOnlyFrames,
    int maxBoxDiffFrames,
    IReadOnlyList<int> onlyBaseline,
    IReadOnlyList<int> onlyOptimized,
    IReadOnlyList<int> boxCountDiff,
    Dictionary<int, FrameMaskProvider.FaceMaskData> baselineEntries,
    Dictionary<int, FrameMaskProvider.FaceMaskData> optimizedEntries)
{
    Directory.CreateDirectory(outputDir);
    var items = BuildCompareCropItems(onlyBaseline, onlyOptimized, boxCountDiff, baselineEntries, optimizedEntries, maxOnlyFrames, maxBoxDiffFrames);
    if (items.Count == 0)
        return;

    string csvPath = Path.Combine(outputDir, "compare-crops.csv");
    File.WriteAllText(csvPath, "reason,frame,label,index,x,y,w,h,cx,cy,area,confidence,path" + Environment.NewLine);
    var ordered = items.OrderBy(x => x.Frame).ThenBy(x => x.Reason).ThenBy(x => x.Label).ThenBy(x => x.Index).ToArray();
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
            Console.WriteLine($"[SmokeCompareCrop] reason={ordered[next].Reason}, frame={ordered[next].Frame}, label={ordered[next].Label}, path=decode-skipped");
            next++;
        }

        using var image = ConvertToRgbImage(frame);
        while (next < ordered.Length && ordered[next].Frame == frameIndex)
        {
            var item = ordered[next];
            var cropRect = BuildCropRect(item.Face, image.Width, image.Height, paddingRatio);
            using var crop = CropImage(image, cropRect);
            DrawRect(crop, new Rect(item.Face.X - cropRect.X, item.Face.Y - cropRect.Y, item.Face.Width, item.Face.Height), new Rgb24(64, 220, 255), 4);

            string fileName = $"{item.Reason}-{item.Label}-frame-{item.Frame:D6}-{item.Index:D2}.png";
            string path = Path.Combine(outputDir, fileName);
            crop.SaveAsPng(path);
            double areaRatio = item.Face.Width * item.Face.Height / Math.Max(1.0, image.Width * (double)image.Height);
            double cx = (item.Face.X + item.Face.Width * 0.5) / Math.Max(1.0, image.Width);
            double cy = (item.Face.Y + item.Face.Height * 0.5) / Math.Max(1.0, image.Height);
            File.AppendAllText(
                csvPath,
                $"{item.Reason},{item.Frame},{item.Label},{item.Index},{item.Face.X:F1},{item.Face.Y:F1},{item.Face.Width:F1},{item.Face.Height:F1},{cx:F4},{cy:F4},{areaRatio:F6},{item.Confidence:F3},{fileName}{Environment.NewLine}");
            Console.WriteLine($"[SmokeCompareCrop] reason={item.Reason}, frame={item.Frame}, label={item.Label}, index={item.Index}, path={path}");
            next++;
        }
    }
}

static List<(string Reason, int Frame, string Label, int Index, Rect Face, float Confidence)> BuildCompareCropItems(
    IReadOnlyList<int> onlyBaseline,
    IReadOnlyList<int> onlyOptimized,
    IReadOnlyList<int> boxCountDiff,
    Dictionary<int, FrameMaskProvider.FaceMaskData> baselineEntries,
    Dictionary<int, FrameMaskProvider.FaceMaskData> optimizedEntries,
    int maxOnlyFrames,
    int maxBoxDiffFrames)
{
    const double MatchIou = 0.35;
    var items = new List<(string Reason, int Frame, string Label, int Index, Rect Face, float Confidence)>();
    int onlyLimit = Math.Max(0, maxOnlyFrames);
    int boxDiffLimit = Math.Max(0, maxBoxDiffFrames);
    foreach (int frame in onlyBaseline.Take(onlyLimit))
    {
        if (!baselineEntries.TryGetValue(frame, out var baseline))
            continue;

        AddCropItems(items, "onlyBaseline", frame, "baseline", baseline);
    }

    foreach (int frame in onlyOptimized.Take(onlyLimit))
    {
        if (!optimizedEntries.TryGetValue(frame, out var optimized))
            continue;

        AddCropItems(items, "onlyOptimized", frame, "optimized", optimized);
    }

    foreach (int frame in boxCountDiff.Take(boxDiffLimit))
    {
        var baseline = baselineEntries.TryGetValue(frame, out var baselineData)
            ? baselineData
            : new FrameMaskProvider.FaceMaskData(default, Array.Empty<Rect>(), null, Array.Empty<float>());
        var optimized = optimizedEntries.TryGetValue(frame, out var optimizedData)
            ? optimizedData
            : new FrameMaskProvider.FaceMaskData(default, Array.Empty<Rect>(), null, Array.Empty<float>());
        if (optimized.Faces.Count > 0)
        {
            for (int i = 0; i < optimized.Faces.Count; i++)
            {
                double best = baseline.Faces.Count == 0 ? 0.0 : baseline.Faces.Max(face => IoU(face, optimized.Faces[i]));
                if (best < MatchIou)
                    items.Add(("boxCountDiffOptimizedExtra", frame, "optimized", i, optimized.Faces[i], GetFaceConfidence(optimized, i)));
            }
        }

        if (baseline.Faces.Count > 0)
        {
            for (int i = 0; i < baseline.Faces.Count; i++)
            {
                double best = optimized.Faces.Count == 0 ? 0.0 : optimized.Faces.Max(face => IoU(face, baseline.Faces[i]));
                if (best < MatchIou)
                    items.Add(("boxCountDiffBaselineUnmatched", frame, "baseline", i, baseline.Faces[i], GetFaceConfidence(baseline, i)));
            }
        }
    }

    return items;
}

static void AddCropItems(List<(string Reason, int Frame, string Label, int Index, Rect Face, float Confidence)> items, string reason, int frame, string label, FrameMaskProvider.FaceMaskData data)
{
    for (int i = 0; i < data.Faces.Count; i++)
        items.Add((reason, frame, label, i, data.Faces[i], GetFaceConfidence(data, i)));
}

static float GetFaceConfidence(FrameMaskProvider.FaceMaskData data, int index)
{
    return index < data.Confidences.Count ? data.Confidences[index] : data.MinConfidence ?? 1.0f;
}

static Rect BuildCropRect(Rect face, int imageWidth, int imageHeight, double paddingRatio)
{
    double padding = Math.Max(face.Width, face.Height) * Math.Clamp(paddingRatio, 0.0, 3.0);
    double x1 = Math.Clamp(face.X - padding, 0, Math.Max(0, imageWidth - 1));
    double y1 = Math.Clamp(face.Y - padding, 0, Math.Max(0, imageHeight - 1));
    double x2 = Math.Clamp(face.Right + padding, 0, Math.Max(0, imageWidth - 1));
    double y2 = Math.Clamp(face.Bottom + padding, 0, Math.Max(0, imageHeight - 1));
    return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
}

static Image<Rgb24> CropImage(Image<Rgb24> image, Rect cropRect)
{
    int x = Math.Clamp((int)Math.Floor(cropRect.X), 0, image.Width - 1);
    int y = Math.Clamp((int)Math.Floor(cropRect.Y), 0, image.Height - 1);
    int width = Math.Clamp((int)Math.Ceiling(cropRect.Width), 1, image.Width - x);
    int height = Math.Clamp((int)Math.Ceiling(cropRect.Height), 1, image.Height - y);
    return image.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(x, y, width, height)));
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

static string FormatTextValues(IReadOnlyList<string> values)
{
    if (values.Count == 0)
        return "none";
    return string.Join(",", values.Take(20)) + (values.Count > 20 ? ",..." : string.Empty);
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

static double Coverage(Rect reference, Rect candidate)
{
    double x1 = Math.Max(reference.X, candidate.X);
    double y1 = Math.Max(reference.Y, candidate.Y);
    double x2 = Math.Min(reference.Right, candidate.Right);
    double y2 = Math.Min(reference.Bottom, candidate.Bottom);
    double w = Math.Max(0.0, x2 - x1);
    double h = Math.Max(0.0, y2 - y1);
    double referenceArea = Math.Max(0.0, reference.Width * reference.Height);
    return referenceArea <= 0.0 ? 0.0 : (w * h) / referenceArea;
}

(string Label, FrameMaskProvider MaskProvider)? baseline = null;
if (!skipBaseline)
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
        yoloUseLandmarkBoxRefine: false,
        yoloLandmarkBoxMinAreaRatio,
        yoloLandmarkBoxWidthScale,
        yoloLandmarkBoxHeightScale,
        yoloLandmarkBoxCenterYOffsetRatio,
        yoloLandmarkBoxMinOriginalIou,
        yoloUseTiling: false,
        yoloUseFaceOnnxRoiRefine: false,
        yoloFaceOnnxRoiMinAreaRatio,
        yoloFaceOnnxRoiMaxCandidates,
        yoloTileOnly: false,
        yoloTileColumns,
        yoloTileRows,
        yoloTileOverlapRatio,
        yoloMaxLostFillFrames,
        yoloMaxInitialFillFrames,
        yoloDropShortTrackMaxDetections,
        yoloShortTrackMaxConfidence,
        yoloLowerFrameTrackMaxConfidence,
        yoloUseLowConfidencePositionFilter: false,
        yoloLowConfidencePositionMaxConfidence,
        yoloLowConfidencePositionMinCenterYRatio,
        yoloUseSmallAreaFilter: false,
        yoloSmallAreaMaxAreaRatio,
        yoloUseAspectRatioFilter: false,
        yoloMinAspectRatio,
        yoloMaxAspectRatio,
        yoloDebugDump: false);
}

(string Label, FrameMaskProvider MaskProvider)? optimized = null;
if (!skipOptimized)
{
    optimized = await RunCaseAsync(
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
        yoloUseLandmarkBoxRefine,
        yoloLandmarkBoxMinAreaRatio,
        yoloLandmarkBoxWidthScale,
        yoloLandmarkBoxHeightScale,
        yoloLandmarkBoxCenterYOffsetRatio,
        yoloLandmarkBoxMinOriginalIou,
        yoloUseTiling,
        yoloUseFaceOnnxRoiRefine,
        yoloFaceOnnxRoiMinAreaRatio,
        yoloFaceOnnxRoiMaxCandidates,
        yoloTileOnly,
        yoloTileColumns,
        yoloTileRows,
        yoloTileOverlapRatio,
        yoloMaxLostFillFrames,
        yoloMaxInitialFillFrames,
        yoloDropShortTrackMaxDetections,
        yoloShortTrackMaxConfidence,
        yoloLowerFrameTrackMaxConfidence,
        yoloUseLowConfidencePositionFilter,
        yoloLowConfidencePositionMaxConfidence,
        yoloLowConfidencePositionMinCenterYRatio,
        yoloUseSmallAreaFilter,
        yoloSmallAreaMaxAreaRatio,
        yoloUseAspectRatioFilter,
        yoloMinAspectRatio,
        yoloMaxAspectRatio,
        yoloDebugDump);
}

if (!baseline.HasValue && !optimized.HasValue)
{
    Console.WriteLine("[SmokeRun] no cases selected");
    Environment.Exit(2);
}

if (baseline.HasValue && optimized.HasValue && !CompareCases(input, baseline.Value, optimized.Value, minAvgIou, minBestIou, allowFrameMismatch, dumpCompareDetails, dumpCompareOverlays, compareOverlayDir, compareOverlayMaxFrames, dumpCompareCrops, compareCropDir, compareCropPaddingRatio, compareCropMaxOnlyFrames, compareCropMaxBoxDiffFrames))
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
$resolvedYoloModelPath = if ([string]::IsNullOrWhiteSpace($YoloModelPath)) {
    ""
}
elseif (Get-Command Resolve-YoloModelPath -ErrorAction SilentlyContinue) {
    Resolve-YoloModelPath -Repo $repo -YoloModelPath $YoloModelPath -YoloModelType $YoloModelType
}
else {
    (Resolve-Path $YoloModelPath).Path
}
$yoloModelPathArg = if ([string]::IsNullOrWhiteSpace($resolvedYoloModelPath)) { "__none__" } else { $resolvedYoloModelPath }
$yoloInputSizeArg = $YoloInputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloObjectnessThresholdArg = $YoloObjectnessThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloConfidenceThresholdArg = $YoloConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloNmsThresholdArg = $YoloNmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxWidthScaleArg = $YoloLargeBoxWidthScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxHeightScaleArg = $YoloLargeBoxHeightScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLargeBoxMinAreaRatioArg = $YoloLargeBoxMinAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseLandmarkBoxRefineArg = $YoloUseLandmarkBoxRefine.IsPresent.ToString().ToLowerInvariant()
$yoloLandmarkBoxMinAreaRatioArg = $YoloLandmarkBoxMinAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLandmarkBoxWidthScaleArg = $YoloLandmarkBoxWidthScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLandmarkBoxHeightScaleArg = $YoloLandmarkBoxHeightScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLandmarkBoxCenterYOffsetRatioArg = $YoloLandmarkBoxCenterYOffsetRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLandmarkBoxMinOriginalIouArg = $YoloLandmarkBoxMinOriginalIou.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseTilingArg = $YoloUseTiling.IsPresent.ToString().ToLowerInvariant()
$yoloUseFaceOnnxRoiRefineArg = $YoloUseFaceOnnxRoiRefine.IsPresent.ToString().ToLowerInvariant()
$yoloFaceOnnxRoiMinAreaRatioArg = $YoloFaceOnnxRoiMinAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloFaceOnnxRoiMaxCandidatesArg = $YoloFaceOnnxRoiMaxCandidates.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileOnlyArg = $YoloTileOnly.IsPresent.ToString().ToLowerInvariant()
$yoloTileColumnsArg = $YoloTileColumns.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileRowsArg = $YoloTileRows.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloTileOverlapRatioArg = $YoloTileOverlapRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloMaxLostFillFramesArg = $YoloMaxLostFillFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloMaxInitialFillFramesArg = $YoloMaxInitialFillFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloDropShortTrackMaxDetectionsArg = $YoloDropShortTrackMaxDetections.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloShortTrackMaxConfidenceArg = $YoloShortTrackMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLowerFrameTrackMaxConfidenceArg = $YoloLowerFrameTrackMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseLowConfidencePositionFilterArg = $YoloUseLowConfidencePositionFilter.IsPresent.ToString().ToLowerInvariant()
$yoloLowConfidencePositionMaxConfidenceArg = $YoloLowConfidencePositionMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloLowConfidencePositionMinCenterYRatioArg = $YoloLowConfidencePositionMinCenterYRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseSmallAreaFilterArg = $YoloUseSmallAreaFilter.IsPresent.ToString().ToLowerInvariant()
$yoloSmallAreaMaxAreaRatioArg = $YoloSmallAreaMaxAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloUseAspectRatioFilterArg = $YoloUseAspectRatioFilter.IsPresent.ToString().ToLowerInvariant()
$yoloMinAspectRatioArg = $YoloMinAspectRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$yoloMaxAspectRatioArg = $YoloMaxAspectRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$skipOptimizedArg = $SkipOptimized.IsPresent.ToString().ToLowerInvariant()
$yoloDebugDumpArg = $YoloDebugDump.IsPresent.ToString().ToLowerInvariant()
$dumpCompareDetailsArg = $DumpCompareDetails.IsPresent.ToString().ToLowerInvariant()
$dumpCompareOverlaysArg = $DumpCompareOverlays.IsPresent.ToString().ToLowerInvariant()
$compareOverlayMaxFramesArg = $CompareOverlayMaxFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
if ([string]::IsNullOrWhiteSpace($CompareOverlayDir)) {
    $compareOverlayDirArg = Join-Path $work ("compare-overlays-" + $clipStem)
} elseif ([IO.Path]::IsPathRooted($CompareOverlayDir)) {
    $compareOverlayDirArg = $CompareOverlayDir
} else {
    $compareOverlayDirArg = Join-Path $repo $CompareOverlayDir
}
$dumpCompareCropsArg = $DumpCompareCrops.IsPresent.ToString().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($CompareCropDir)) {
    $compareCropDirArg = Join-Path $work ("compare-crops-" + $clipStem)
} elseif ([IO.Path]::IsPathRooted($CompareCropDir)) {
    $compareCropDirArg = $CompareCropDir
} else {
    $compareCropDirArg = Join-Path $repo $CompareCropDir
}
$compareCropPaddingRatioArg = $CompareCropPaddingRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$compareCropMaxOnlyFramesArg = $CompareCropMaxOnlyFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$compareCropMaxBoxDiffFramesArg = $CompareCropMaxBoxDiffFrames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
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
    $yoloUseLandmarkBoxRefineArg `
    $yoloLandmarkBoxMinAreaRatioArg `
    $yoloLandmarkBoxWidthScaleArg `
    $yoloLandmarkBoxHeightScaleArg `
    $yoloLandmarkBoxCenterYOffsetRatioArg `
    $yoloLandmarkBoxMinOriginalIouArg `
    $yoloUseTilingArg `
    $yoloUseFaceOnnxRoiRefineArg `
    $yoloFaceOnnxRoiMinAreaRatioArg `
    $yoloFaceOnnxRoiMaxCandidatesArg `
    $yoloTileOnlyArg `
    $yoloTileColumnsArg `
    $yoloTileRowsArg `
    $yoloTileOverlapRatioArg `
    $yoloDropShortTrackMaxDetectionsArg `
    $yoloShortTrackMaxConfidenceArg `
    $yoloLowerFrameTrackMaxConfidenceArg `
    $yoloDebugDumpArg `
    $dumpCompareDetailsArg `
    $dumpCompareOverlaysArg `
    $compareOverlayDirArg `
    $compareOverlayMaxFramesArg `
    $dumpCompareCropsArg `
    $compareCropDirArg `
    $compareCropPaddingRatioArg `
    $compareCropMaxOnlyFramesArg `
    $compareCropMaxBoxDiffFramesArg `
    $yoloUseLowConfidencePositionFilterArg `
    $yoloLowConfidencePositionMaxConfidenceArg `
    $yoloLowConfidencePositionMinCenterYRatioArg `
    $yoloUseSmallAreaFilterArg `
    $yoloSmallAreaMaxAreaRatioArg `
    $skipOptimizedArg `
    $yoloMaxLostFillFramesArg `
    $yoloUseAspectRatioFilterArg `
    $yoloMinAspectRatioArg `
    $yoloMaxAspectRatioArg `
    $yoloMaxInitialFillFramesArg
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
