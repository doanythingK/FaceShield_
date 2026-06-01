param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\automask-sparse-materialize-scene-cut"
$project = Join-Path $work "AutoMaskSparseMaterializeSceneCutHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

@'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;

static object CreateDetection(
    Type detectionType,
    int index,
    Rect[] bounds,
    PixelSize size,
    float[] confidences,
    double[] signature)
{
    object detection = Activator.CreateInstance(detectionType)!;
    detectionType.GetProperty("Index")!.SetValue(detection, index);
    detectionType.GetProperty("Bounds")!.SetValue(detection, bounds);
    detectionType.GetProperty("Size")!.SetValue(detection, size);
    detectionType.GetProperty("MinConfidence")!.SetValue(detection, confidences.Length == 0 ? null : confidences[0]);
    detectionType.GetProperty("Confidences")!.SetValue(detection, confidences);
    detectionType.GetProperty("FrameSignature")!.SetValue(detection, signature);
    return detection;
}

static object CreateResults(Type detectionType, params object[] detections)
{
    Type dictionaryType = typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(int), detectionType);
    object dictionary = Activator.CreateInstance(dictionaryType)!;
    MethodInfo tryAdd = dictionaryType.GetMethod("TryAdd")!;
    foreach (object detection in detections)
    {
        int index = (int)detectionType.GetProperty("Index")!.GetValue(detection)!;
        tryAdd.Invoke(dictionary, new[] { (object)index, detection });
    }

    return dictionary;
}

static (int interpolated, int sceneCutStops, string sceneCutTransitions) InvokeMaterialize(
    AutoMaskGenerator generator,
    object results)
{
    MethodInfo method = typeof(AutoMaskGenerator).GetMethod(
        "MaterializeSparseTrackingResults",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    object result = method.Invoke(generator, new[] { results, (object)0, (object)10 })!;
    Type resultType = result.GetType();
    object transitions = resultType.GetProperty("SceneCutTransitions")!.GetValue(result)!;
    var transitionText = new List<string>();
    foreach (object transition in (System.Collections.IEnumerable)transitions)
    {
        Type transitionType = transition.GetType();
        int source = (int)transitionType.GetProperty("SourceFrameIndex")!.GetValue(transition)!;
        int next = (int)transitionType.GetProperty("NextFrameIndex")!.GetValue(transition)!;
        transitionText.Add($"{source}->{next}");
    }

    return (
        (int)resultType.GetProperty("Interpolated")!.GetValue(result)!,
        (int)resultType.GetProperty("SceneCutStops")!.GetValue(result)!,
        string.Join(",", transitionText));
}

static void AssertNoMaterializedFaces(FrameMaskProvider provider, int start, int endExclusive)
{
    for (int frame = start; frame < endExclusive; frame++)
    {
        if (provider.TryGetFaceMaskData(frame, out var data) && data.Faces.Count > 0)
            throw new InvalidOperationException($"Expected no materialized face at frame {frame}.");
    }
}

var detectionType = typeof(AutoMaskGenerator).GetNestedType(
    "DetectionResult",
    BindingFlags.NonPublic)!;

var size = new PixelSize(1280, 720);
var face = new[] { new Rect(200, 160, 88, 92) };
var conf = new[] { 0.82f };
var dark = new double[24 * 14];
var bright = new double[24 * 14];
Array.Fill(bright, 0.72);
var similar = new double[24 * 14];
Array.Fill(similar, 0.03);

var cutProvider = new FrameMaskProvider();
var cutGenerator = new AutoMaskGenerator(
    new DummyDetector(),
    cutProvider,
    new AutoMaskOptions
    {
        UseTracking = true,
        DetectEveryNFrames = 5,
        FilterProfile = FaceFilterProfile.Yolo
    });
object cutResults = CreateResults(
    detectionType,
    CreateDetection(detectionType, 0, face, size, conf, dark),
    CreateDetection(detectionType, 5, Array.Empty<Rect>(), size, Array.Empty<float>(), bright));
var cut = InvokeMaterialize(cutGenerator, cutResults);
if (cut.interpolated != 0 || cut.sceneCutStops != 1)
    throw new InvalidOperationException($"Expected cut interpolated=0 sceneCutStops=1, got interpolated={cut.interpolated} sceneCutStops={cut.sceneCutStops}.");
if (cut.sceneCutTransitions != "0->5")
    throw new InvalidOperationException($"Expected cut sceneCutTransitions=0->5, got {cut.sceneCutTransitions}.");
AssertNoMaterializedFaces(cutProvider, 1, 5);

var cutBeforePositiveProvider = new FrameMaskProvider();
var cutBeforePositiveGenerator = new AutoMaskGenerator(
    new DummyDetector(),
    cutBeforePositiveProvider,
    new AutoMaskOptions
    {
        UseTracking = true,
        DetectEveryNFrames = 5,
        FilterProfile = FaceFilterProfile.Yolo
    });
object cutBeforePositiveResults = CreateResults(
    detectionType,
    CreateDetection(detectionType, 0, face, size, conf, dark),
    CreateDetection(detectionType, 5, Array.Empty<Rect>(), size, Array.Empty<float>(), bright),
    CreateDetection(detectionType, 10, face, size, conf, similar));
var cutBeforePositive = InvokeMaterialize(cutBeforePositiveGenerator, cutBeforePositiveResults);
if (cutBeforePositive.interpolated != 0 || cutBeforePositive.sceneCutStops != 1)
    throw new InvalidOperationException($"Expected cut-before-positive interpolated=0 sceneCutStops=1, got interpolated={cutBeforePositive.interpolated} sceneCutStops={cutBeforePositive.sceneCutStops}.");
if (cutBeforePositive.sceneCutTransitions != "0->5")
    throw new InvalidOperationException($"Expected cut-before-positive sceneCutTransitions=0->5, got {cutBeforePositive.sceneCutTransitions}.");
AssertNoMaterializedFaces(cutBeforePositiveProvider, 1, 10);

var sameProvider = new FrameMaskProvider();
var sameGenerator = new AutoMaskGenerator(
    new DummyDetector(),
    sameProvider,
    new AutoMaskOptions
    {
        UseTracking = true,
        DetectEveryNFrames = 5,
        FilterProfile = FaceFilterProfile.Yolo
    });
object sameResults = CreateResults(
    detectionType,
    CreateDetection(detectionType, 0, face, size, conf, dark),
    CreateDetection(detectionType, 5, Array.Empty<Rect>(), size, Array.Empty<float>(), similar));
var same = InvokeMaterialize(sameGenerator, sameResults);
if (same.interpolated != 4 || same.sceneCutStops != 0)
    throw new InvalidOperationException($"Expected same-scene interpolated=4 sceneCutStops=0, got interpolated={same.interpolated} sceneCutStops={same.sceneCutStops}.");
if (same.sceneCutTransitions.Length != 0)
    throw new InvalidOperationException($"Expected same-scene sceneCutTransitions to be empty, got {same.sceneCutTransitions}.");
for (int frame = 1; frame < 5; frame++)
{
    if (!sameProvider.TryGetFaceMaskData(frame, out var data) || data.Faces.Count != 1)
        throw new InvalidOperationException($"Expected same-scene materialized face at frame {frame}.");
}

var farNextProvider = new FrameMaskProvider();
var farNextGenerator = new AutoMaskGenerator(
    new DummyDetector(),
    farNextProvider,
    new AutoMaskOptions
    {
        UseTracking = true,
        DetectEveryNFrames = 5,
        FilterProfile = FaceFilterProfile.Yolo
    });
object farNextResults = CreateResults(
    detectionType,
    CreateDetection(detectionType, 0, face, size, conf, dark),
    CreateDetection(detectionType, 20, Array.Empty<Rect>(), size, Array.Empty<float>(), similar));
var farNext = InvokeMaterialize(farNextGenerator, farNextResults);
if (farNext.interpolated != 4 || farNext.sceneCutStops != 0)
    throw new InvalidOperationException($"Expected far-next fallback carry to cap at detect interval with interpolated=4 sceneCutStops=0, got interpolated={farNext.interpolated} sceneCutStops={farNext.sceneCutStops}.");
for (int frame = 1; frame < 5; frame++)
{
    if (!farNextProvider.TryGetFaceMaskData(frame, out var data) || data.Faces.Count != 1)
        throw new InvalidOperationException($"Expected far-next materialized face inside detect interval at frame {frame}.");
}
AssertNoMaterializedFaces(farNextProvider, 5, 20);

var faceOnnxProvider = new FrameMaskProvider();
var faceOnnxGenerator = new AutoMaskGenerator(
    new DummyDetector(),
    faceOnnxProvider,
    new AutoMaskOptions
    {
        UseTracking = true,
        DetectEveryNFrames = 5,
        FilterProfile = FaceFilterProfile.FaceOnnx
    });
object faceOnnxResults = CreateResults(
    detectionType,
    CreateDetection(detectionType, 0, face, size, conf, dark),
    CreateDetection(detectionType, 5, Array.Empty<Rect>(), size, Array.Empty<float>(), bright));
var faceOnnx = InvokeMaterialize(faceOnnxGenerator, faceOnnxResults);
if (faceOnnx.interpolated != 4 || faceOnnx.sceneCutStops != 0)
    throw new InvalidOperationException($"Expected FaceONNX profile to keep existing sparse materialization, got interpolated={faceOnnx.interpolated} sceneCutStops={faceOnnx.sceneCutStops}.");
if (faceOnnx.sceneCutTransitions.Length != 0)
    throw new InvalidOperationException($"Expected FaceONNX sceneCutTransitions to be empty, got {faceOnnx.sceneCutTransitions}.");

Console.WriteLine("[AutoMaskSparseMaterializeSceneCutVerify] yoloHardCutInterpolated=0, yoloSceneCutStops=1, yoloSceneCutTransitions=0->5, cutBeforePositiveInterpolated=0, sameSceneInterpolated=4, farNextInterpolated=4, faceOnnxInterpolated=4");

internal sealed class DummyDetector : IFaceDetector
{
    public IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame)
        => Array.Empty<FaceDetectionResult>();

    public void Dispose()
    {
    }
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
