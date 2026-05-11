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
    [double]$MinAvgIou = 0.90,
    [double]$MinBestIou = 0.75,
    [switch]$AllowFrameMismatch
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
$project = Join-Path $work "SmokeHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

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
    int parallelDetectorCount)
{
    string runId = $"smoke-{label}-{Guid.NewGuid():N}";
    Console.WriteLine($"[SmokeCase] start runId={runId}, label={label}, tracking={useTracking}, everyN={detectEvery}, downscale={downscaleRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, quality={downscaleQuality}, gpu={useGpu}, autoTune={useAutoTune}, parallel={parallelDetectorCount}");
    var maskProvider = new FrameMaskProvider();
    var detectorOptions = new FaceOnnxDetectorOptions
    {
        UseOrtOptimization = true,
        UseGpu = useGpu,
        AllowAutoTune = useAutoTune,
        AllowAutoGpu = useGpu,
        EnablePreprocessParallelism = true
    };
    if (useAutoTune && DetectorAutoTuner.TryTune(
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

    var factory = FaceDetectorFactory.ForOnnx(detectorOptions);
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
    Console.WriteLine($"[Smoke] label={label}, faceMaskFrames={maskProvider.GetFaceMaskFrameIndices().Length}, storedMaskFrames={maskProvider.GetStoredMaskFrameIndices().Length}");

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
        parallelDetectorCount: 1);
}

var optimized = await RunCaseAsync(
    $"optimized-{(optimizedUseTracking ? "track" : "all")}-{optimizedDetectEvery}-scale-{optimizedDownscaleRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}-{(optimizedUseGpu ? "gpu" : "cpu")}",
    input,
    output,
    useTracking: optimizedUseTracking,
    detectEvery: optimizedDetectEvery,
    downscaleRatio: optimizedDownscaleRatio,
    downscaleQuality: optimizedDownscaleQuality,
    useGpu: optimizedUseGpu,
    skipExport,
    useAutoTune,
    parallelDetectorCount: optimizedParallel);

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
$minAvgIouArg = $MinAvgIou.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$minBestIouArg = $MinBestIou.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$allowFrameMismatchArg = $AllowFrameMismatch.IsPresent.ToString().ToLowerInvariant()
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
    $allowFrameMismatchArg
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
