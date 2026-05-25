param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-final-mask-cleanup"
$project = Join-Path $work "YoloFinalMaskCleanupHarness.csproj"
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
using System.Linq;
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;

var provider = new FrameMaskProvider();
var size = new PixelSize(1920, 1080);

provider.SetFaceRects(10, new[] { new Rect(900, 300, 80, 80) }, size, 0.44f, new[] { 0.44f });
provider.SetFaceRects(20, new[] { new Rect(0, 420, 52, 76) }, size, 0.42f, new[] { 0.42f });
provider.SetFaceRects(30, new[] { new Rect(500, 260, 90, 90) }, size, 0.78f, new[] { 0.78f });
provider.SetFaceRects(40, new[] { new Rect(700, 260, 84, 84) }, size, 0.41f, new[] { 0.41f });
provider.SetFaceRects(41, new[] { new Rect(704, 264, 84, 84) }, size, 0.40f, new[] { 0.40f });
provider.SetFaceRects(
    50,
    new[] { new Rect(1000, 260, 90, 90), new Rect(1200, 260, 90, 90) },
    size,
    0.45f,
    new[] { 0.45f, 0.83f });
provider.SetFaceRects(59, new[] { new Rect(300, 300, 90, 90) }, size, 0.82f, new[] { 0.82f });
provider.SetFaceRects(
    60,
    new[] { new Rect(304, 304, 90, 90), new Rect(1300, 520, 50, 50) },
    size,
    0.43f,
    new[] { 0.82f, 0.43f });
provider.SetFaceRects(61, new[] { new Rect(308, 308, 90, 90) }, size, 0.81f, new[] { 0.81f });
provider.SetFaceRects(70, new[] { new Rect(1500, 500, 46, 46) }, size, 0.32f, new[] { 0.32f });
provider.SetFaceRects(71, new[] { new Rect(1504, 504, 46, 46) }, size, 0.31f, new[] { 0.31f });
provider.SetFaceRects(80, new[] { new Rect(1400, 420, 40, 40) }, size, 0.42f, new[] { 0.42f });
provider.SetFaceRects(81, new[] { new Rect(1403, 422, 40, 40) }, size, 0.43f, new[] { 0.43f });
provider.SetFaceRects(82, new[] { new Rect(1406, 424, 40, 40) }, size, 0.44f, new[] { 0.44f });
provider.SetFaceRects(90, new[] { new Rect(900, 620, 40, 40) }, size, 0.42f, new[] { 0.42f });
provider.SetFaceRects(91, new[] { new Rect(903, 622, 40, 40) }, size, 0.43f, new[] { 0.43f });
provider.SetFaceRects(92, new[] { new Rect(906, 624, 40, 40) }, size, 0.44f, new[] { 0.44f });
provider.SetFaceRects(93, new[] { new Rect(909, 626, 40, 40) }, size, 0.71f, new[] { 0.71f });
provider.SetFaceRects(95, new[] { new Rect(1500, 700, 34, 34) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(96, new[] { new Rect(1600, 720, 34, 34) }, size, 0.74f, new[] { 0.74f });

var result = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(provider);

if (result.RemovedWeakIsolatedFaces != 9)
    throw new InvalidOperationException($"Expected 9 weak/tiny isolated/short/tiny-cluster faces to be removed, got {result.RemovedWeakIsolatedFaces}.");

if (result.RemovedWeakUnsupportedFaces != 3)
    throw new InvalidOperationException($"Expected 3 weak unsupported faces to be removed, got {result.RemovedWeakUnsupportedFaces}.");

if (result.RemovedWeakShortClusterFaces != 2)
    throw new InvalidOperationException($"Expected 2 weak short-cluster faces to be removed, got {result.RemovedWeakShortClusterFaces}.");

if (result.RemovedWeakTinyClusterFaces != 3)
    throw new InvalidOperationException($"Expected 3 weak tiny-cluster faces to be removed, got {result.RemovedWeakTinyClusterFaces}.");

if (result.RemovedTinyIsolatedFaces != 1)
    throw new InvalidOperationException($"Expected 1 medium-confidence tiny isolated face to be removed, got {result.RemovedTinyIsolatedFaces}.");

if (provider.TryGetFaceMaskData(10, out var weakIsolated) && weakIsolated.Faces.Count > 0)
    throw new InvalidOperationException("Expected weak isolated non-edge frame 10 to be removed.");

if (!provider.TryGetFaceMaskData(20, out var edgePartial) || edgePartial.Faces.Count != 1)
    throw new InvalidOperationException("Expected weak edge partial-face frame 20 to remain.");

if (!provider.TryGetFaceMaskData(30, out var strongIsolated) || strongIsolated.Faces.Count != 1)
    throw new InvalidOperationException("Expected strong isolated frame 30 to remain.");

if (!provider.TryGetFaceMaskData(40, out var neighborA) || neighborA.Faces.Count != 1 ||
    !provider.TryGetFaceMaskData(41, out var neighborB) || neighborB.Faces.Count != 1)
{
    throw new InvalidOperationException("Expected adjacent weak frames 40-41 to remain for temporal review.");
}

if (!provider.TryGetFaceMaskData(50, out var mixed) || mixed.Faces.Count != 1)
    throw new InvalidOperationException("Expected only the weak isolated face in mixed frame 50 to be removed.");

if (mixed.Confidences.Count != 1 || Math.Abs(mixed.Confidences[0] - 0.83f) > 0.001f)
    throw new InvalidOperationException("Expected mixed frame 50 to keep the strong face confidence.");

if (!provider.TryGetFaceMaskData(60, out var mixedTemporal) ||
    mixedTemporal.Faces.Count != 1 ||
    Math.Abs(mixedTemporal.Confidences[0] - 0.82f) > 0.001f)
{
    throw new InvalidOperationException("Expected frame 60 to remove only the weak unrelated face even though another face has temporal neighbors.");
}

if (provider.TryGetFaceMaskData(70, out var weakPairA) && weakPairA.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(71, out var weakPairB) && weakPairB.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected a two-frame very-low-confidence non-edge cluster to be removed.");
}

if (provider.TryGetFaceMaskData(80, out var tinyClusterA) && tinyClusterA.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(81, out var tinyClusterB) && tinyClusterB.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(82, out var tinyClusterC) && tinyClusterC.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected a three-frame weak tiny non-edge cluster without strong continuation to be removed.");
}

if (!provider.TryGetFaceMaskData(90, out var tinyStrongA) || tinyStrongA.Faces.Count != 1 ||
    !provider.TryGetFaceMaskData(91, out var tinyStrongB) || tinyStrongB.Faces.Count != 1 ||
    !provider.TryGetFaceMaskData(92, out var tinyStrongC) || tinyStrongC.Faces.Count != 1 ||
    !provider.TryGetFaceMaskData(93, out var tinyStrongD) || tinyStrongD.Faces.Count != 1)
{
    throw new InvalidOperationException("Expected weak tiny cluster with strong continuation to remain.");
}

if (provider.TryGetFaceMaskData(95, out var tinyMediumIsolated) && tinyMediumIsolated.Faces.Count > 0)
    throw new InvalidOperationException("Expected a medium-confidence tiny isolated non-edge candidate to be removed.");

if (!provider.TryGetFaceMaskData(96, out var tinyStrongIsolated) || tinyStrongIsolated.Faces.Count != 1)
    throw new InvalidOperationException("Expected a high-confidence tiny isolated candidate to remain for review.");

var gapProvider = new FrameMaskProvider();
gapProvider.SetFaceRects(10, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
gapProvider.SetFaceRects(12, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
gapProvider.SetFaceRects(30, new[] { new Rect(760, 300, 100, 100) }, size, 0.78f, new[] { 0.78f });
gapProvider.SetFaceRects(34, new[] { new Rect(772, 308, 100, 100) }, size, 0.76f, new[] { 0.76f });
gapProvider.SetFaceRects(50, new[] { new Rect(980, 320, 88, 88) }, size, 0.46f, new[] { 0.46f });
gapProvider.SetFaceRects(52, new[] { new Rect(986, 324, 88, 88) }, size, 0.47f, new[] { 0.47f });
gapProvider.SetFaceRects(70, new[] { new Rect(1120, 300, 80, 80) }, size, 0.82f, new[] { 0.82f });
gapProvider.SetFaceRects(72, new[] { new Rect(1500, 640, 180, 180) }, size, 0.84f, new[] { 0.84f });

var gapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(gapProvider);
var filledFrames = string.Join(",", gapFill.FilledFrameIndices);
if (gapFill.FilledFaces != 4 || filledFrames != "11,31,32,33")
    throw new InvalidOperationException($"Expected stable strong final-mask gaps at frames 11,31,32,33 to be filled, got filled={gapFill.FilledFaces}, frames={filledFrames}.");

if (!gapProvider.TryGetFaceMaskData(11, out var filledSingle) || filledSingle.Faces.Count != 1)
    throw new InvalidOperationException("Expected frame 11 to be filled between strong matching anchors.");
if (!gapProvider.TryGetFaceMaskData(31, out var filledRangeA) || filledRangeA.Faces.Count != 1 ||
    !gapProvider.TryGetFaceMaskData(32, out var filledRangeB) || filledRangeB.Faces.Count != 1 ||
    !gapProvider.TryGetFaceMaskData(33, out var filledRangeC) || filledRangeC.Faces.Count != 1)
{
    throw new InvalidOperationException("Expected frames 31-33 to be filled between strong matching anchors.");
}
if (gapProvider.TryGetFaceMaskData(51, out var weakGap) && weakGap.Faces.Count > 0)
    throw new InvalidOperationException("Expected weak-anchor gap at frame 51 to remain unfilled.");
if (gapProvider.TryGetFaceMaskData(71, out var jumpGap) && jumpGap.Faces.Count > 0)
    throw new InvalidOperationException("Expected large-jump gap at frame 71 to remain unfilled.");

var cutGapProvider = new FrameMaskProvider();
cutGapProvider.SetFaceRects(100, new[] { new Rect(400, 220, 90, 90) }, size, 0.82f, new[] { 0.82f });
cutGapProvider.SetFaceRects(102, new[] { new Rect(406, 224, 90, 90) }, size, 0.80f, new[] { 0.80f });
var cutGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(cutGapProvider);
if (cutGapFill.FilledFaces != 1 || cutGapFill.FilledFacesInfo.Count != 1 || cutGapFill.CutGuardFacesInfo.Count != 2)
    throw new InvalidOperationException($"Expected one filled gap and two scene-cut-checkable anchor candidates, got filled={cutGapFill.FilledFaces}, filledCandidates={cutGapFill.FilledFacesInfo.Count}, cutCandidates={cutGapFill.CutGuardFacesInfo.Count}.");

var cutGuard = new FaceTrackSceneCutGuard().Apply(
    cutGapProvider,
    cutGapFill.CutGuardFacesInfo,
    static (source, target) => source == 100 && target == 101 ? 0.50 : 0.0);
if (cutGuard.Removed != 1 || string.Join(",", cutGuard.RemovedFrameIndices) != "101")
    throw new InvalidOperationException($"Expected final gap fill across a hard cut to be removed, got removed={cutGuard.Removed}, frames={string.Join(",", cutGuard.RemovedFrameIndices)}.");
if (cutGapProvider.TryGetFaceMaskData(101, out var cutFilled) && cutFilled.Faces.Count > 0)
    throw new InvalidOperationException("Expected scene-cut guard to remove the filled gap frame.");

var afterCutGapProvider = new FrameMaskProvider();
afterCutGapProvider.SetFaceRects(200, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
afterCutGapProvider.SetFaceRects(202, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
var afterCutGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(afterCutGapProvider);
if (afterCutGapFill.FilledFaces != 1 || afterCutGapFill.CutGuardFacesInfo.Count != 2)
    throw new InvalidOperationException($"Expected one after-cut gap fill and two anchor candidates, got filled={afterCutGapFill.FilledFaces}, cutCandidates={afterCutGapFill.CutGuardFacesInfo.Count}.");

var afterCutGuard = new FaceTrackSceneCutGuard().Apply(
    afterCutGapProvider,
    afterCutGapFill.CutGuardFacesInfo,
    static (source, target) => source == 201 && target == 202 ? 0.50 : 0.0);
if (afterCutGuard.Removed != 1 || string.Join(",", afterCutGuard.RemovedFrameIndices) != "201")
    throw new InvalidOperationException($"Expected final gap fill before a hard cut to be removed by next-anchor evidence, got removed={afterCutGuard.Removed}, frames={string.Join(",", afterCutGuard.RemovedFrameIndices)}.");
if (afterCutGapProvider.TryGetFaceMaskData(201, out var afterCutFilled) && afterCutFilled.Faces.Count > 0)
    throw new InvalidOperationException("Expected next-anchor scene-cut guard to remove the filled gap frame.");

Console.WriteLine(
    $"[YoloFinalMaskCleanupVerify] removedWeakIsolated={result.RemovedWeakIsolatedFaces}, removedWeakUnsupported={result.RemovedWeakUnsupportedFaces}, removedWeakShortClusters={result.RemovedWeakShortClusterFaces}, removedWeakTinyClusters={result.RemovedWeakTinyClusterFaces}, removedTinyIsolated={result.RemovedTinyIsolatedFaces}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, remainingFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}, gapFilled={gapFill.FilledFaces}, gapFrames={filledFrames}, gapCutRemoved={cutGuard.Removed + afterCutGuard.Removed}, gapCutAnchorCandidates={cutGapFill.CutGuardFacesInfo.Count}, gapCutAfterRemoved={afterCutGuard.Removed}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
