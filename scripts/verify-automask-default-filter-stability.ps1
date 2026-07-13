param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\automask-default-filter-stability"
$project = Join-Path $work "AutoMaskDefaultFilterStabilityHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 -Path $project

    @'
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.ViewModels.Pages;
using System.Reflection;

var generatorType = typeof(AutoMaskGenerator);
var flags = BindingFlags.NonPublic | BindingFlags.Static;
var runtimeSettingsMethod = generatorType.GetMethod("GetRuntimeFaceFilterSettings", flags)
    ?? throw new InvalidOperationException("GetRuntimeFaceFilterSettings was not found.");
var baseSettingsMethod = generatorType.GetMethod("GetFaceFilterSettings", flags)
    ?? throw new InvalidOperationException("GetFaceFilterSettings was not found.");
var classifyMethod = generatorType.GetMethod("ClassifyFaceCandidate", flags)
    ?? throw new InvalidOperationException("ClassifyFaceCandidate was not found.");
var pixelFilterMethod = generatorType.GetMethod("FilterFacesByAreaAndStats", flags)
    ?? throw new InvalidOperationException("FilterFacesByAreaAndStats was not found.");
var filterStatsType = generatorType.GetNestedType("FaceFilterStats", BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("FaceFilterStats was not found.");
var signatureMethod = typeof(WorkspaceViewModel).GetMethod(
    "BuildAutoRunSignature",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("BuildAutoRunSignature was not found.");

object GetRuntimeSettings(AutoMaskOptions options)
    => runtimeSettingsMethod.Invoke(null, [options])
        ?? throw new InvalidOperationException("Runtime filter settings were not returned.");

object GetBaseSettings(FaceFilterProfile profile, bool precision)
    => baseSettingsMethod.Invoke(null, [profile, precision])
        ?? throw new InvalidOperationException("Base filter settings were not returned.");

T Read<T>(object settings, string property)
    => (T)(settings.GetType().GetProperty(property)?.GetValue(settings)
        ?? throw new InvalidOperationException($"Filter property {property} was not found."));

void AssertStats(string name, AutoMaskOptions options, bool expected)
{
    bool actual = Read<bool>(GetRuntimeSettings(options), "UseStatsFilter");
    if (actual != expected)
        throw new InvalidOperationException($"{name}: expected stats={expected}, actual={actual}.");
}

var trackedFaceOnnx = new AutoMaskOptions().ResolveProcessingMode();
var trackedYolo = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Tracked,
    FilterProfile = FaceFilterProfile.Yolo
}.ResolveProcessingMode();
var rawYolo = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Raw,
    FilterProfile = FaceFilterProfile.Yolo
}.ResolveProcessingMode();
var fullYolo = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Full,
    FilterProfile = FaceFilterProfile.Yolo
}.ResolveProcessingMode();
var legacyYolo = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Legacy,
    FilterProfile = FaceFilterProfile.Yolo
};
AssertStats("default tracked FaceONNX", trackedFaceOnnx, false);
AssertStats(
    "legacy FaceONNX",
    new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Legacy },
    true);
AssertStats(
    "full FaceONNX",
    new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Full }.ResolveProcessingMode(),
    true);
AssertStats("tracked YOLO", trackedYolo, false);
AssertStats("raw YOLO", rawYolo, false);
AssertStats("full YOLO", fullYolo, false);
AssertStats("legacy YOLO", legacyYolo, true);
AssertStats(
    "tracked SCRFD",
    new AutoMaskOptions
    {
        ProcessingMode = AutoMaskProcessingMode.Tracked,
        FilterProfile = FaceFilterProfile.Scrfd
    }.ResolveProcessingMode(),
    false);

object runtime = GetRuntimeSettings(trackedFaceOnnx);
object baseFaceOnnx = GetBaseSettings(FaceFilterProfile.FaceOnnx, true);
foreach (string property in new[]
{
    "MinFaceAreaRatio",
    "MinSmallFaceAreaRatio",
    "MinFaceAspectRatio",
    "MaxFaceAspectRatio",
    "SmallFaceConfidenceMin"
})
{
    object expected = baseFaceOnnx.GetType().GetProperty(property)!.GetValue(baseFaceOnnx)!;
    object actual = runtime.GetType().GetProperty(property)!.GetValue(runtime)!;
    if (!Equals(expected, actual))
        throw new InvalidOperationException($"Geometry property changed: {property} expected={expected} actual={actual}.");
}

void AssertSettingsEqual(string name, object actual, object expected)
{
    foreach (string property in new[]
    {
        "MinFaceAreaRatio",
        "MinSmallFaceAreaRatio",
        "MinFaceAspectRatio",
        "MaxFaceAspectRatio",
        "SmallFaceConfidenceMin",
        "UseStatsFilter"
    })
    {
        object expectedValue = expected.GetType().GetProperty(property)!.GetValue(expected)!;
        object actualValue = actual.GetType().GetProperty(property)!.GetValue(actual)!;
        if (!Equals(expectedValue, actualValue))
            throw new InvalidOperationException($"{name}: {property} expected={expectedValue}, actual={actualValue}.");
    }
}

object yoloRecallSettings = GetBaseSettings(FaceFilterProfile.Yolo, false);
object yoloPrecisionSettings = GetBaseSettings(FaceFilterProfile.Yolo, true);
AssertSettingsEqual("tracked YOLO recall policy", GetRuntimeSettings(trackedYolo), yoloRecallSettings);
AssertSettingsEqual("raw YOLO recall policy", GetRuntimeSettings(rawYolo), yoloRecallSettings);
AssertSettingsEqual("full YOLO recall policy", GetRuntimeSettings(fullYolo), yoloRecallSettings);
AssertSettingsEqual("legacy YOLO precision policy", GetRuntimeSettings(legacyYolo), yoloPrecisionSettings);

void AssertCandidate(string name, Rect bounds, float confidence, string expected)
{
    var face = new FaceDetectionResult { Bounds = bounds, Confidence = confidence };
    string actual = classifyMethod.Invoke(null, [face, 320.0 * 180.0, runtime])?.ToString()
        ?? throw new InvalidOperationException($"{name}: classification was not returned.");
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}.");
}

AssertCandidate("regular face", new Rect(32, 32, 32, 32), 0.79f, "Regular");
AssertCandidate("invalid aspect", new Rect(100, 32, 40, 8), 0.99f, "Rejected");
AssertCandidate("too small", new Rect(150, 32, 2, 2), 0.99f, "Rejected");

string ClassifyWithSettings(Rect bounds, float confidence, object settings)
{
    var face = new FaceDetectionResult { Bounds = bounds, Confidence = confidence };
    return classifyMethod.Invoke(null, [face, 320.0 * 180.0, settings])?.ToString()
        ?? throw new InvalidOperationException("YOLO boundary classification was not returned.");
}

var boundarySmallFace = new Rect(200, 32, 4, 4);
string trackedYoloBoundary = ClassifyWithSettings(boundarySmallFace, 0.55f, GetRuntimeSettings(trackedYolo));
string legacyYoloBoundary = ClassifyWithSettings(boundarySmallFace, 0.55f, GetRuntimeSettings(legacyYolo));
if (trackedYoloBoundary != "Small" || legacyYoloBoundary != "Rejected")
{
    throw new InvalidOperationException(
        $"Expected tracked YOLO to retain the small boundary face while Legacy preserves precision filtering; tracked={trackedYoloBoundary}, legacy={legacyYoloBoundary}.");
}

unsafe (int Count, string Summary) RunPixelFilter(object settings)
{
    const int width = 320;
    const int height = 180;
    const int stride = width * 4;
    var pixels = new byte[stride * height];
    for (int i = 3; i < pixels.Length; i += 4)
        pixels[i] = 255;

    IReadOnlyList<FaceDetectionResult> candidates =
    [
        new FaceDetectionResult { Bounds = new Rect(32, 32, 32, 32), Confidence = 0.79f },
        new FaceDetectionResult { Bounds = new Rect(100, 32, 40, 8), Confidence = 0.99f },
        new FaceDetectionResult { Bounds = new Rect(150, 32, 2, 2), Confidence = 0.99f }
    ];
    object stats = Activator.CreateInstance(filterStatsType)
        ?? throw new InvalidOperationException("FaceFilterStats could not be created.");

    fixed (byte* basePtr = pixels)
    {
        object pointer = Pointer.Box(basePtr, typeof(byte*));
        var result = pixelFilterMethod.Invoke(
            null,
            [
                candidates,
                new PixelSize(width, height),
                pointer,
                stride,
                width,
                height,
                1.0,
                1.0,
                settings,
                stats
            ]) as IReadOnlyList<FaceDetectionResult>
            ?? throw new InvalidOperationException("Pixel filter did not return candidates.");
        string summary = (string)(filterStatsType.GetMethod("BuildSummary")!.Invoke(stats, null)
            ?? throw new InvalidOperationException("Filter summary was not returned."));
        return (result.Count, summary);
    }
}

var trackedPixels = RunPixelFilter(runtime);
if (trackedPixels != (1, "regular=1, small=0, rejected=2, statsRejected=0"))
    throw new InvalidOperationException($"Tracked pixel filter mismatch: {trackedPixels}.");

var legacyPixels = RunPixelFilter(GetRuntimeSettings(
    new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Legacy }));
if (legacyPixels != (0, "regular=0, small=0, rejected=2, statsRejected=1"))
    throw new InvalidOperationException($"Legacy pixel filter mismatch: {legacyPixels}.");

var factoryOptions = FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions());
string trackedSignature = (string)(signatureMethod.Invoke(null, [trackedFaceOnnx, factoryOptions])
    ?? throw new InvalidOperationException("Tracked signature was not returned."));
if (!trackedSignature.StartsWith("v6|", StringComparison.Ordinal) || trackedSignature.StartsWith("v5|", StringComparison.Ordinal))
    throw new InvalidOperationException($"Tracked signature did not invalidate v5: {trackedSignature}.");

string legacySignature = (string)(signatureMethod.Invoke(
    null,
    [new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Legacy }, factoryOptions])
    ?? throw new InvalidOperationException("Legacy signature was not returned."));
if (!legacySignature.StartsWith("v3|", StringComparison.Ordinal))
    throw new InvalidOperationException($"Legacy-compatible signature changed: {legacySignature}.");

Console.WriteLine("[AutoMaskDefaultFilterStabilityVerify] PASS policies=8 geometry=3 yoloBoundary=tracked-small/legacy-rejected pixelPolicies=2 signatures=v6/v3");
'@ | Set-Content -Encoding UTF8 -Path $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $source = Get-Content -Raw -Path (Join-Path $repo "Services\Analysis\AutoMaskGenerator.cs")
    $runtimeUses = [regex]::Matches(
        $source,
        "FilterFacesByArea(?:AndStats)?\([\s\S]{0,800}?_faceFilterSettings").Count
    if ($runtimeUses -ne 7) {
        throw "Expected 7 runtime filter paths to use the resolved policy, found $runtimeUses."
    }
    if ($source -notmatch "settings\.UseStatsFilter\s*&&\s*!PassesStatsBgra") {
        throw "Pixel stats gate is no longer controlled by the resolved filter policy."
    }
    if ($source -notmatch "usePrecisionMode\s*=\s*options\.ProcessingMode\s*==\s*AutoMaskProcessingMode\.Legacy\s*&&\s*!options\.EnablePostProcessing") {
        throw "Precision filtering is no longer isolated to Legacy compatibility mode."
    }

    $workspace = Get-Content -Raw -Path (Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs")
    if ($workspace -notmatch 'useLegacyCompatibleSignature\s*\?\s*"v3"\s*:\s*"v6"') {
        throw "Auto-run signature was not advanced for the default filter policy change."
    }

    Write-Host "[AutoMaskDefaultFilterStabilityVerify] PASS runtimePaths=7 resumeSignature=v6"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
}
