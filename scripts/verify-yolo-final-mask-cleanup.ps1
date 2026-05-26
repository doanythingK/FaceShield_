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
provider.SetFaceRects(97, new[] { new Rect(1350, 720, 34, 34) }, size, 0.59f, new[] { 0.59f });
provider.SetFaceRects(98, new[] { new Rect(1353, 722, 34, 34) }, size, 0.60f, new[] { 0.60f });
provider.SetFaceRects(99, new[] { new Rect(1250, 720, 34, 34) }, size, 0.48f, new[] { 0.48f });
provider.SetFaceRects(100, new[] { new Rect(1253, 722, 34, 34) }, size, 0.49f, new[] { 0.49f });
for (int frame = 210; frame <= 212; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(900 + frame - 210, 420, 92, 92) }, size, 0.47f, new[] { 0.47f });
for (int frame = 220; frame <= 222; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(1040 + frame - 220, 420, 92, 92) }, size, 0.47f, new[] { 0.47f });
provider.SetFaceRects(223, new[] { new Rect(1043, 420, 92, 92) }, size, 0.72f, new[] { 0.72f });
for (int frame = 120; frame <= 124; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(620 + frame - 120, 36, 50, 50) }, size, 0.46f, new[] { 0.46f });
for (int frame = 130; frame <= 134; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(820 + frame - 130, 40, 50, 50) }, size, 0.46f, new[] { 0.46f });
provider.SetFaceRects(135, new[] { new Rect(825, 40, 50, 50) }, size, 0.72f, new[] { 0.72f });
provider.SetFaceRects(139, new[] { new Rect(980, 0, 76, 76) }, size, 0.58f, new[] { 0.58f });
for (int frame = 140; frame <= 142; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(980 + frame - 140, 46, 76, 76) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(143, new[] { new Rect(983, 46, 76, 76) }, size, 0.61f, new[] { 0.61f });
for (int frame = 150; frame <= 152; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(1120 + frame - 150, 44, 76, 76) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(153, new[] { new Rect(1123, 44, 76, 76) }, size, 0.72f, new[] { 0.72f });
for (int frame = 160; frame <= 162; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(520 + frame - 160, 700, 240, 240) }, size, 0.49f, new[] { 0.49f });
for (int frame = 170; frame <= 172; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(820 + frame - 170, 700, 240, 240) }, size, 0.49f, new[] { 0.49f });
provider.SetFaceRects(173, new[] { new Rect(823, 700, 240, 240) }, size, 0.72f, new[] { 0.72f });
for (int frame = 180; frame <= 182; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(1060 + frame - 180, 360, 10, 42) }, size, 0.68f, new[] { 0.68f });
for (int frame = 190; frame <= 192; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(1220 + frame - 190, 360, 10, 42) }, size, 0.68f, new[] { 0.68f });
provider.SetFaceRects(193, new[] { new Rect(1223, 360, 30, 42) }, size, 0.82f, new[] { 0.82f });

var result = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(provider);

if (result.RemovedWeakIsolatedFaces != 32)
    throw new InvalidOperationException($"Expected 32 weak/tiny isolated/short/tiny/upper/lower/aspect-cluster faces to be removed, got {result.RemovedWeakIsolatedFaces}.");

if (result.RemovedWeakUnsupportedFaces != 3)
    throw new InvalidOperationException($"Expected 3 weak unsupported faces to be removed, got {result.RemovedWeakUnsupportedFaces}.");

if (result.RemovedWeakShortClusterFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak short-cluster faces to be removed, got {result.RemovedWeakShortClusterFaces}.");

if (result.RemovedWeakTinyClusterFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak tiny-cluster faces to be removed, got {result.RemovedWeakTinyClusterFaces}.");

if (result.RemovedTinyShortClusterFaces != 4)
    throw new InvalidOperationException($"Expected 4 weak/medium-confidence tiny short-cluster faces to be removed, got {result.RemovedTinyShortClusterFaces}.");

if (result.RemovedTinyIsolatedFaces != 1)
    throw new InvalidOperationException($"Expected 1 medium-confidence tiny isolated face to be removed, got {result.RemovedTinyIsolatedFaces}.");

if (result.RemovedUpperWeakClusterFaces != 8)
    throw new InvalidOperationException($"Expected 8 upper weak non-edge cluster faces to be removed, got {result.RemovedUpperWeakClusterFaces}.");

if (result.RemovedLowerWeakClusterFaces != 3)
    throw new InvalidOperationException($"Expected 3 lower weak non-edge cluster faces to be removed, got {result.RemovedLowerWeakClusterFaces}.");

if (result.RemovedAspectOutlierClusterFaces != 3)
    throw new InvalidOperationException($"Expected 3 aspect-outlier non-edge cluster faces to be removed, got {result.RemovedAspectOutlierClusterFaces}.");

if (provider.TryGetFaceMaskData(10, out var weakIsolated) && weakIsolated.Faces.Count > 0)
    throw new InvalidOperationException("Expected weak isolated non-edge frame 10 to be removed.");

if (!provider.TryGetFaceMaskData(20, out var edgePartial) || edgePartial.Faces.Count != 1)
    throw new InvalidOperationException("Expected weak edge partial-face frame 20 to remain.");

if (!provider.TryGetFaceMaskData(30, out var strongIsolated) || strongIsolated.Faces.Count != 1)
    throw new InvalidOperationException("Expected strong isolated frame 30 to remain.");

if (provider.TryGetFaceMaskData(40, out var neighborA) && neighborA.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(41, out var neighborB) && neighborB.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected two-frame weak non-edge cluster frames 40-41 to be removed without strong continuation.");
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

if (provider.TryGetFaceMaskData(97, out var tinyMediumClusterA) && tinyMediumClusterA.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(98, out var tinyMediumClusterB) && tinyMediumClusterB.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected a two-frame medium-confidence tiny non-edge cluster to be removed.");
}

if (provider.TryGetFaceMaskData(99, out var tinyWeakClusterA) && tinyWeakClusterA.Faces.Count > 0 ||
    provider.TryGetFaceMaskData(100, out var tinyWeakClusterB) && tinyWeakClusterB.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected a two-frame weak tiny non-edge cluster above the old weak-tiny cutoff to be removed.");
}

for (int frame = 210; frame <= 212; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var threeFrameWeakCluster) && threeFrameWeakCluster.Faces.Count > 0)
        throw new InvalidOperationException($"Expected three-frame weak non-edge cluster frame {frame} to be removed.");
}

for (int frame = 220; frame <= 223; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var threeFrameWeakWithStrongContinuation) ||
        threeFrameWeakWithStrongContinuation.Faces.Count != 1)
    {
        throw new InvalidOperationException($"Expected weak three-frame cluster with strong continuation to remain at frame {frame}.");
    }
}

for (int frame = 120; frame <= 124; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var upperWeak) && upperWeak.Faces.Count > 0)
        throw new InvalidOperationException($"Expected upper weak non-edge cluster frame {frame} to be removed.");
}

for (int frame = 130; frame <= 135; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var upperWeakStrongContinuation) || upperWeakStrongContinuation.Faces.Count != 1)
        throw new InvalidOperationException($"Expected upper weak cluster with strong continuation to remain at frame {frame}.");
}

for (int frame = 140; frame <= 142; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var upperMediumWeak) && upperMediumWeak.Faces.Count > 0)
        throw new InvalidOperationException($"Expected upper medium-weak non-edge cluster frame {frame} to be removed.");
}
if (!provider.TryGetFaceMaskData(139, out var upperEdgeWeak) || upperEdgeWeak.Faces.Count != 1)
    throw new InvalidOperationException("Expected upper edge weak frame 139 to remain while no longer protecting non-edge frames 140-142.");
if (!provider.TryGetFaceMaskData(143, out var upperMediumNonCluster) || upperMediumNonCluster.Faces.Count != 1)
    throw new InvalidOperationException("Expected upper medium non-cluster frame 143 to remain while no longer protecting frames 140-142.");

for (int frame = 150; frame <= 153; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var upperMediumWeakStrongContinuation) || upperMediumWeakStrongContinuation.Faces.Count != 1)
        throw new InvalidOperationException($"Expected upper medium-weak cluster with strong continuation to remain at frame {frame}.");
}

for (int frame = 160; frame <= 162; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var lowerWeak) && lowerWeak.Faces.Count > 0)
        throw new InvalidOperationException($"Expected lower weak non-edge cluster frame {frame} to be removed.");
}

for (int frame = 170; frame <= 173; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var lowerWeakStrongContinuation) || lowerWeakStrongContinuation.Faces.Count != 1)
        throw new InvalidOperationException($"Expected lower weak cluster with strong continuation to remain at frame {frame}.");
}

for (int frame = 180; frame <= 182; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var aspectOutlier) && aspectOutlier.Faces.Count > 0)
        throw new InvalidOperationException($"Expected aspect-outlier non-edge cluster frame {frame} to be removed.");
}

for (int frame = 190; frame <= 193; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var aspectOutlierStrongContinuation) || aspectOutlierStrongContinuation.Faces.Count != 1)
        throw new InvalidOperationException($"Expected aspect-outlier cluster with strong continuation to remain at frame {frame}.");
}

var gapProvider = new FrameMaskProvider();
gapProvider.SetFaceRects(10, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
gapProvider.SetFaceRects(12, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
gapProvider.SetFaceRects(30, new[] { new Rect(760, 300, 100, 100) }, size, 0.78f, new[] { 0.78f });
gapProvider.SetFaceRects(34, new[] { new Rect(772, 308, 100, 100) }, size, 0.76f, new[] { 0.76f });
gapProvider.SetFaceRects(50, new[] { new Rect(980, 320, 88, 88) }, size, 0.46f, new[] { 0.46f });
gapProvider.SetFaceRects(52, new[] { new Rect(986, 324, 88, 88) }, size, 0.47f, new[] { 0.47f });
gapProvider.SetFaceRects(70, new[] { new Rect(1120, 300, 80, 80) }, size, 0.82f, new[] { 0.82f });
gapProvider.SetFaceRects(72, new[] { new Rect(1500, 640, 180, 180) }, size, 0.84f, new[] { 0.84f });
gapProvider.SetFaceRects(110, new[] { new Rect(640, 280, 88, 88) }, size, 0.56f, new[] { 0.56f });
gapProvider.SetFaceRects(112, new[] { new Rect(646, 284, 88, 88) }, size, 0.55f, new[] { 0.55f });

var gapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(gapProvider);
var filledFrames = string.Join(",", gapFill.FilledFrameIndices);
if (gapFill.FilledFaces != 5 || filledFrames != "11,31,32,33,111")
    throw new InvalidOperationException($"Expected stable final-mask gaps at frames 11,31,32,33,111 to be filled, got filled={gapFill.FilledFaces}, frames={filledFrames}.");

if (!gapProvider.TryGetFaceMaskData(11, out var filledSingle) || filledSingle.Faces.Count != 1)
    throw new InvalidOperationException("Expected frame 11 to be filled between strong matching anchors.");
if (!gapProvider.TryGetFaceMaskData(31, out var filledRangeA) || filledRangeA.Faces.Count != 1 ||
    !gapProvider.TryGetFaceMaskData(32, out var filledRangeB) || filledRangeB.Faces.Count != 1 ||
    !gapProvider.TryGetFaceMaskData(33, out var filledRangeC) || filledRangeC.Faces.Count != 1)
{
    throw new InvalidOperationException("Expected frames 31-33 to be filled between strong matching anchors.");
}
if (!gapProvider.TryGetFaceMaskData(111, out var mediumFilled) || mediumFilled.Faces.Count != 1)
    throw new InvalidOperationException("Expected frame 111 to be filled between stable medium-confidence matching anchors.");
if (gapProvider.TryGetFaceMaskData(51, out var weakGap) && weakGap.Faces.Count > 0)
    throw new InvalidOperationException("Expected weak-anchor gap at frame 51 to remain unfilled.");
if (gapProvider.TryGetFaceMaskData(71, out var jumpGap) && jumpGap.Faces.Count > 0)
    throw new InvalidOperationException("Expected large-jump gap at frame 71 to remain unfilled.");

var supportedWeakGapProvider = new FrameMaskProvider();
supportedWeakGapProvider.SetFaceRects(500, new[] { new Rect(640, 280, 88, 88) }, size, 0.51f, new[] { 0.51f });
supportedWeakGapProvider.SetFaceRects(502, new[] { new Rect(646, 284, 88, 88) }, size, 0.50f, new[] { 0.50f });
supportedWeakGapProvider.SetFaceRects(504, new[] { new Rect(652, 288, 88, 88) }, size, 0.51f, new[] { 0.51f });
var supportedWeakGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(supportedWeakGapProvider);
var supportedWeakGapFrames = string.Join(",", supportedWeakGapFill.FilledFrameIndices);
if (supportedWeakGapFill.FilledFaces != 2 || supportedWeakGapFrames != "501,503")
    throw new InvalidOperationException($"Expected supported weak final-mask gaps at frames 501,503 to be filled, got filled={supportedWeakGapFill.FilledFaces}, frames={supportedWeakGapFrames}.");

var unsupportedWeakGapProvider = new FrameMaskProvider();
unsupportedWeakGapProvider.SetFaceRects(600, new[] { new Rect(740, 300, 88, 88) }, size, 0.51f, new[] { 0.51f });
unsupportedWeakGapProvider.SetFaceRects(602, new[] { new Rect(746, 304, 88, 88) }, size, 0.50f, new[] { 0.50f });
var unsupportedWeakGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(unsupportedWeakGapProvider);
if (unsupportedWeakGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected unsupported weak-anchor gap to remain unfilled, got filled={unsupportedWeakGapFill.FilledFaces}.");

var weakEdgeAnchorGapProvider = new FrameMaskProvider();
weakEdgeAnchorGapProvider.SetFaceRects(800, new[] { new Rect(640, 0, 88, 88) }, size, 0.61f, new[] { 0.61f });
weakEdgeAnchorGapProvider.SetFaceRects(802, new[] { new Rect(646, 0, 88, 88) }, size, 0.60f, new[] { 0.60f });
weakEdgeAnchorGapProvider.SetFaceRects(804, new[] { new Rect(652, 0, 88, 88) }, size, 0.61f, new[] { 0.61f });
var weakEdgeAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(weakEdgeAnchorGapProvider);
if (weakEdgeAnchorGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected weak edge anchors not to create final-mask gap fills, got filled={weakEdgeAnchorGapFill.FilledFaces}.");

var strongEdgeAnchorGapProvider = new FrameMaskProvider();
strongEdgeAnchorGapProvider.SetFaceRects(900, new[] { new Rect(640, 0, 88, 88) }, size, 0.82f, new[] { 0.82f });
strongEdgeAnchorGapProvider.SetFaceRects(902, new[] { new Rect(646, 0, 88, 88) }, size, 0.80f, new[] { 0.80f });
var strongEdgeAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(strongEdgeAnchorGapProvider);
if (strongEdgeAnchorGapFill.FilledFaces != 1 || string.Join(",", strongEdgeAnchorGapFill.FilledFrameIndices) != "901")
    throw new InvalidOperationException($"Expected strong edge anchors to remain eligible for final-mask gap fill, got filled={strongEdgeAnchorGapFill.FilledFaces}, frames={string.Join(",", strongEdgeAnchorGapFill.FilledFrameIndices)}.");

var mediumRiskyAnchorGapProvider = new FrameMaskProvider();
mediumRiskyAnchorGapProvider.SetFaceRects(910, new[] { new Rect(640, 320, 28, 28) }, size, 0.68f, new[] { 0.68f });
mediumRiskyAnchorGapProvider.SetFaceRects(912, new[] { new Rect(646, 326, 28, 28) }, size, 0.67f, new[] { 0.67f });
var mediumRiskyAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(mediumRiskyAnchorGapProvider);
if (mediumRiskyAnchorGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected medium-confidence risky geometry anchors without third support not to create final-mask gap fills, got filled={mediumRiskyAnchorGapFill.FilledFaces}.");
if (mediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks <= 0)
    throw new InvalidOperationException("Expected medium-confidence risky geometry anchor suppression to be reported.");

var supportedMediumRiskyAnchorGapProvider = new FrameMaskProvider();
supportedMediumRiskyAnchorGapProvider.SetFaceRects(920, new[] { new Rect(640, 320, 28, 28) }, size, 0.68f, new[] { 0.68f });
supportedMediumRiskyAnchorGapProvider.SetFaceRects(922, new[] { new Rect(646, 326, 28, 28) }, size, 0.67f, new[] { 0.67f });
supportedMediumRiskyAnchorGapProvider.SetFaceRects(924, new[] { new Rect(652, 332, 28, 28) }, size, 0.69f, new[] { 0.69f });
var supportedMediumRiskyAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(supportedMediumRiskyAnchorGapProvider);
var supportedMediumRiskyAnchorGapFrames = string.Join(",", supportedMediumRiskyAnchorGapFill.FilledFrameIndices);
if (supportedMediumRiskyAnchorGapFill.FilledFaces != 2 || supportedMediumRiskyAnchorGapFrames != "921,923")
    throw new InvalidOperationException($"Expected medium-confidence risky geometry anchors with third support to remain eligible, got filled={supportedMediumRiskyAnchorGapFill.FilledFaces}, frames={supportedMediumRiskyAnchorGapFrames}.");
if (supportedMediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks != 0)
    throw new InvalidOperationException($"Expected supported medium-confidence risky geometry anchors not to be reported as suppressed, got {supportedMediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}.");

var blockedFrameGapProvider = new FrameMaskProvider();
blockedFrameGapProvider.SetFaceRects(700, new[] { new Rect(540, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
blockedFrameGapProvider.SetFaceRects(702, new[] { new Rect(546, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
var blockedFrameGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    blockedFrameGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        BlockedFrameIndices = new[] { 701 }
    });
if (blockedFrameGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected cleanup-blocked final-mask frame 701 to remain empty, got filled={blockedFrameGapFill.FilledFaces}.");
if (blockedFrameGapFill.BlockedCleanupGapFrames != 1 || string.Join(",", blockedFrameGapFill.BlockedCleanupFrameIndices) != "701")
    throw new InvalidOperationException($"Expected cleanup-blocked final-mask frame 701 to be reported, got blocked={blockedFrameGapFill.BlockedCleanupGapFrames}, frames={string.Join(",", blockedFrameGapFill.BlockedCleanupFrameIndices)}.");
if (blockedFrameGapProvider.TryGetFaceMaskData(701, out var cleanupBlockedGap) && cleanupBlockedGap.Faces.Count > 0)
    throw new InvalidOperationException("Expected cleanup-blocked final-mask frame 701 not to be recreated by stable gap fill.");

var partialBlockedFrameGapProvider = new FrameMaskProvider();
partialBlockedFrameGapProvider.SetFaceRects(710, new[] { new Rect(540, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
partialBlockedFrameGapProvider.SetFaceRects(714, new[] { new Rect(552, 268, 90, 90) }, size, 0.80f, new[] { 0.80f });
var partialBlockedFrameGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    partialBlockedFrameGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedFrameIndices = new[] { 712 }
    });
if (partialBlockedFrameGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected a cleanup-blocked frame inside a stable gap to block the whole gap, got filled={partialBlockedFrameGapFill.FilledFaces}.");
if (partialBlockedFrameGapFill.BlockedCleanupGapFrames != 3 || string.Join(",", partialBlockedFrameGapFill.BlockedCleanupFrameIndices) != "711,712,713")
    throw new InvalidOperationException($"Expected cleanup-blocked gap 711-713 to be reported as blocked, got blocked={partialBlockedFrameGapFill.BlockedCleanupGapFrames}, frames={string.Join(",", partialBlockedFrameGapFill.BlockedCleanupFrameIndices)}.");
if (partialBlockedFrameGapProvider.TryGetFaceMaskData(711, out var partialCleanupBlockedA) && partialCleanupBlockedA.Faces.Count > 0 ||
    partialBlockedFrameGapProvider.TryGetFaceMaskData(713, out var partialCleanupBlockedB) && partialCleanupBlockedB.Faces.Count > 0)
    throw new InvalidOperationException("Expected cleanup-blocked final-mask gap 711-713 not to be partially recreated around frame 712.");

var extendedGapProvider = new FrameMaskProvider();
extendedGapProvider.SetFaceRects(300, new[] { new Rect(440, 220, 90, 90) }, size, 0.82f, new[] { 0.82f });
extendedGapProvider.SetFaceRects(306, new[] { new Rect(455, 230, 90, 90) }, size, 0.80f, new[] { 0.80f });
var defaultExtendedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(extendedGapProvider);
if (defaultExtendedGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected default final-mask gap fill to leave a five-frame gap for conservative callers, got {defaultExtendedGapFill.FilledFaces}.");
var appExtendedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    extendedGapProvider,
    new YoloFinalMaskGapFillOptions { MaxGapFrames = 5 });
var extendedGapFrames = string.Join(",", appExtendedGapFill.FilledFrameIndices);
if (appExtendedGapFill.FilledFaces != 5 || extendedGapFrames != "301,302,303,304,305")
    throw new InvalidOperationException($"Expected app five-frame stable gap fill at frames 301-305, got filled={appExtendedGapFill.FilledFaces}, frames={extendedGapFrames}.");

var mixedFrameGapProvider = new FrameMaskProvider();
mixedFrameGapProvider.SetFaceRects(10, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
mixedFrameGapProvider.SetFaceRects(11, new[] { new Rect(1200, 500, 100, 100) }, size, 0.91f, new[] { 0.91f });
mixedFrameGapProvider.SetFaceRects(12, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
var mixedFrameGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(mixedFrameGapProvider);
if (mixedFrameGapFill.FilledFaces != 1 ||
    !mixedFrameGapProvider.TryGetFaceMaskData(11, out var mixedFrameFilled) ||
    mixedFrameFilled.Faces.Count != 2)
{
    throw new InvalidOperationException("Expected final-mask gap fill to add a missing face even when another face already exists on that frame.");
}

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

var blockedCutGapProvider = new FrameMaskProvider();
blockedCutGapProvider.SetFaceRects(100, new[] { new Rect(400, 220, 90, 90) }, size, 0.82f, new[] { 0.82f });
blockedCutGapProvider.SetFaceRects(102, new[] { new Rect(406, 224, 90, 90) }, size, 0.80f, new[] { 0.80f });
var blockedCutGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    blockedCutGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        BlockedCutFramePairs = new[] { "100->101" }
    });
if (blockedCutGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected known scene-cut boundary to block final gap fill, got filled={blockedCutGapFill.FilledFaces}.");
if (blockedCutGapFill.BlockedCutGapFaces != 1 || string.Join(",", blockedCutGapFill.BlockedCutFrameIndices) != "101")
    throw new InvalidOperationException($"Expected known scene-cut boundary to report blocked frame 101, got blocked={blockedCutGapFill.BlockedCutGapFaces}, frames={string.Join(",", blockedCutGapFill.BlockedCutFrameIndices)}.");
if (blockedCutGapProvider.TryGetFaceMaskData(101, out var blockedCutFilled) && blockedCutFilled.Faces.Count > 0)
    throw new InvalidOperationException("Expected known scene-cut boundary to keep the gap frame empty.");

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

var postSceneCleanupProvider = new FrameMaskProvider();
postSceneCleanupProvider.SetFaceRects(400, new[] { new Rect(300, 220, 90, 90) }, size, 0.82f, new[] { 0.82f });
postSceneCleanupProvider.SetFaceRects(401, new[] { new Rect(520, 260, 90, 90) }, size, 0.45f, new[] { 0.45f });
postSceneCleanupProvider.SetFaceRects(402, new[] { new Rect(524, 264, 90, 90) }, size, 0.45f, new[] { 0.45f });
var preSceneCleanup = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(postSceneCleanupProvider);
if (preSceneCleanup.RemovedWeakIsolatedFaces != 2 ||
    preSceneCleanup.RemovedWeakShortClusterFaces != 2 ||
    string.Join(",", preSceneCleanup.RemovedFrameIndices) != "401,402")
{
    throw new InvalidOperationException($"Expected early post-cut cleanup to remove weak short residual frames 401-402, got removed={preSceneCleanup.RemovedWeakIsolatedFaces}, short={preSceneCleanup.RemovedWeakShortClusterFaces}, frames={string.Join(",", preSceneCleanup.RemovedFrameIndices)}.");
}
if (postSceneCleanupProvider.TryGetFaceMaskData(401, out var postCutFrameA) && postCutFrameA.Faces.Count > 0 ||
    postSceneCleanupProvider.TryGetFaceMaskData(402, out var postCutFrameB) && postCutFrameB.Faces.Count > 0)
    throw new InvalidOperationException("Expected early post-cut weak residual frames 401-402 to be cleared before scene-cut guard is needed.");

var sceneCutCarryProvider = new FrameMaskProvider();
sceneCutCarryProvider.SetFaceRects(1000, new[] { new Rect(640, 360, 120, 120) }, size, 0.88f, new[] { 0.88f });
sceneCutCarryProvider.SetFaceRects(1001, new[] { new Rect(642, 362, 120, 120) }, size, 0.64f, new[] { 0.64f });
sceneCutCarryProvider.SetFaceRects(
    1002,
    new[] { new Rect(644, 364, 120, 120), new Rect(1200, 600, 80, 80) },
    size,
    0.62f,
    new[] { 0.62f, 0.93f });
sceneCutCarryProvider.SetFaceRects(1003, new[] { new Rect(646, 366, 120, 120) }, size, 0.63f, new[] { 0.63f });
sceneCutCarryProvider.SetFaceRects(1004, new[] { new Rect(648, 368, 120, 120) }, size, 0.65f, new[] { 0.65f });
sceneCutCarryProvider.SetFaceRects(1005, new[] { new Rect(650, 370, 120, 120) }, size, 0.93f, new[] { 0.93f });
sceneCutCarryProvider.SetFaceRects(1006, new[] { new Rect(652, 372, 120, 120) }, size, 0.95f, new[] { 0.95f });
sceneCutCarryProvider.SetFaceRects(2000, new[] { new Rect(420, 280, 100, 100) }, size, 0.86f, new[] { 0.86f });
for (int frame = 2001; frame <= 2008; frame++)
{
    float confidence = frame == 2008 ? 0.94f : 0.61f;
    sceneCutCarryProvider.SetFaceRects(frame, new[] { new Rect(420 + frame - 2000, 280 + frame - 2000, 100, 100) }, size, confidence, new[] { confidence });
}
sceneCutCarryProvider.SetFaceRects(2009, new[] { new Rect(429, 289, 100, 100) }, size, 0.93f, new[] { 0.93f });
var sceneCutCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    sceneCutCarryProvider,
    new[] { "1000->1001", "2000->2004" },
    new YoloSceneCutCarryCleanupOptions { MaxConfidence = 0.95f });
var sceneCutCarryFrames = string.Join(",", sceneCutCarryCleanup.RemovedFrameIndices);
var sceneCutCarryBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
    new[] { "1000->1001", "2000->2004" },
    5);
var sceneCutCarryBlockedFrameText = string.Join(",", sceneCutCarryBlockedFrames);
if (sceneCutCarryCleanup.RemovedFaces != 13 || sceneCutCarryFrames != "1001,1002,1003,1004,1005,2001,2002,2003,2004,2005,2006,2007,2008")
    throw new InvalidOperationException($"Expected scene-cut carry cleanup to remove weak frames 1001-1005 and direct-pair frames 2001-2008, got removed={sceneCutCarryCleanup.RemovedFaces}, frames={sceneCutCarryFrames}.");
if (sceneCutCarryBlockedFrameText != "1001,1002,1003,1004,1005,2001,2002,2003,2004,2005,2006,2007,2008")
    throw new InvalidOperationException($"Expected scene-cut carry blocked frames to cover the same post-cut carry windows, got frames={sceneCutCarryBlockedFrameText}.");
if (!sceneCutCarryProvider.TryGetFaceMaskData(1002, out var mixedCarryFrame) ||
    mixedCarryFrame.Faces.Count != 1 ||
    mixedCarryFrame.Confidences[0] < 0.90f)
{
    throw new InvalidOperationException("Expected scene-cut carry cleanup to keep unrelated strong face on mixed frame 1002.");
}
if (!sceneCutCarryProvider.TryGetFaceMaskData(2009, out var directCarryStrongAfter) ||
    directCarryStrongAfter.Faces.Count != 1 ||
    directCarryStrongAfter.Confidences[0] < 0.90f)
{
    throw new InvalidOperationException("Expected direct-pair scene-cut carry cleanup to keep strong post-range face 2009.");
}
var sceneCutCarryBlockedFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    sceneCutCarryProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = sceneCutCarryBlockedFrames
    });
if (sceneCutCarryBlockedFill.FilledFaces != 0 ||
    sceneCutCarryBlockedFill.BlockedCleanupGapFrames != 0 ||
    sceneCutCarryBlockedFill.BlockedSceneCarryGapFrames != 5)
{
    throw new InvalidOperationException($"Expected scene-cut carry cleanup frames to block final gap refill as scene-carry blocks, got filled={sceneCutCarryBlockedFill.FilledFaces}, cleanupBlocked={sceneCutCarryBlockedFill.BlockedCleanupGapFrames}, sceneCarryBlocked={sceneCutCarryBlockedFill.BlockedSceneCarryGapFrames}.");
}

var postCutWindowGapProvider = new FrameMaskProvider();
postCutWindowGapProvider.SetFaceRects(3000, new[] { new Rect(700, 320, 100, 100) }, size, 0.95f, new[] { 0.95f });
postCutWindowGapProvider.SetFaceRects(3002, new[] { new Rect(702, 322, 100, 100) }, size, 0.93f, new[] { 0.93f });
postCutWindowGapProvider.SetFaceRects(3006, new[] { new Rect(706, 326, 100, 100) }, size, 0.94f, new[] { 0.94f });
var postCutWindowCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    postCutWindowGapProvider,
    new[] { "3000->3001" });
if (postCutWindowCleanup.RemovedFaces != 0)
    throw new InvalidOperationException($"Expected high-confidence post-cut anchors to survive carry cleanup, got removed={postCutWindowCleanup.RemovedFaces}.");
var postCutWindowBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
    new[] { "3000->3001" },
    5);
var postCutWindowGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    postCutWindowGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = postCutWindowBlockedFrames
    });
if (postCutWindowGapFill.FilledFaces != 0 ||
    postCutWindowGapFill.BlockedCleanupGapFrames != 0 ||
    postCutWindowGapFill.BlockedSceneCarryGapFrames != 4)
{
    throw new InvalidOperationException($"Expected scene-cut carry window to block empty post-cut gap refill even without removed carry masks, got filled={postCutWindowGapFill.FilledFaces}, cleanupBlocked={postCutWindowGapFill.BlockedCleanupGapFrames}, sceneCarryBlocked={postCutWindowGapFill.BlockedSceneCarryGapFrames}.");
}

var partialPostCutWindowGapProvider = new FrameMaskProvider();
partialPostCutWindowGapProvider.SetFaceRects(3100, new[] { new Rect(700, 320, 100, 100) }, size, 0.95f, new[] { 0.95f });
partialPostCutWindowGapProvider.SetFaceRects(3104, new[] { new Rect(706, 326, 100, 100) }, size, 0.94f, new[] { 0.94f });
var partialPostCutWindowGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    partialPostCutWindowGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = new[] { 3102 }
    });
if (partialPostCutWindowGapFill.FilledFaces != 0 ||
    partialPostCutWindowGapFill.BlockedCleanupGapFrames != 0 ||
    partialPostCutWindowGapFill.BlockedSceneCarryGapFrames != 3 ||
    string.Join(",", partialPostCutWindowGapFill.BlockedSceneCarryFrameIndices) != "3101,3102,3103")
{
    throw new InvalidOperationException($"Expected one scene-carry blocked frame inside a stable gap to block the whole gap, got filled={partialPostCutWindowGapFill.FilledFaces}, sceneCarryBlocked={partialPostCutWindowGapFill.BlockedSceneCarryGapFrames}, frames={string.Join(",", partialPostCutWindowGapFill.BlockedSceneCarryFrameIndices)}.");
}
if (partialPostCutWindowGapProvider.TryGetFaceMaskData(3101, out var partialSceneCarryBlockedA) && partialSceneCarryBlockedA.Faces.Count > 0 ||
    partialPostCutWindowGapProvider.TryGetFaceMaskData(3103, out var partialSceneCarryBlockedB) && partialSceneCarryBlockedB.Faces.Count > 0)
    throw new InvalidOperationException("Expected scene-carry blocked final-mask gap 3101-3103 not to be partially recreated around frame 3102.");

Console.WriteLine(
    $"[YoloFinalMaskCleanupVerify] removedWeakIsolated={result.RemovedWeakIsolatedFaces}, removedWeakUnsupported={result.RemovedWeakUnsupportedFaces}, removedWeakShortClusters={result.RemovedWeakShortClusterFaces}, removedWeakTinyClusters={result.RemovedWeakTinyClusterFaces}, removedTinyShortClusters={result.RemovedTinyShortClusterFaces}, removedTinyIsolated={result.RemovedTinyIsolatedFaces}, removedUpperWeakClusters={result.RemovedUpperWeakClusterFaces}, removedLowerWeakClusters={result.RemovedLowerWeakClusterFaces}, removedAspectOutliers={result.RemovedAspectOutlierClusterFaces}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, remainingFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}, gapFilled={gapFill.FilledFaces}, gapFrames={filledFrames}, supportedWeakGapFilled={supportedWeakGapFill.FilledFaces}, supportedWeakGapFrames={supportedWeakGapFrames}, unsupportedWeakGapFilled={unsupportedWeakGapFill.FilledFaces}, weakEdgeAnchorGapFilled={weakEdgeAnchorGapFill.FilledFaces}, strongEdgeAnchorGapFilled={strongEdgeAnchorGapFill.FilledFaces}, mediumRiskyAnchorGapFilled={mediumRiskyAnchorGapFill.FilledFaces}, mediumRiskyAnchorSuppressed={mediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, supportedMediumRiskyAnchorGapFilled={supportedMediumRiskyAnchorGapFill.FilledFaces}, supportedMediumRiskyAnchorGapFrames={supportedMediumRiskyAnchorGapFrames}, supportedMediumRiskyAnchorSuppressed={supportedMediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, cleanupBlockedGapFilled={blockedFrameGapFill.FilledFaces}, cleanupBlockedGapFrames={blockedFrameGapFill.BlockedCleanupGapFrames}, cleanupBlockedFrames={string.Join(",", blockedFrameGapFill.BlockedCleanupFrameIndices)}, partialCleanupBlockedGapFilled={partialBlockedFrameGapFill.FilledFaces}, partialCleanupBlockedGapFrames={partialBlockedFrameGapFill.BlockedCleanupGapFrames}, partialCleanupBlockedFrames={string.Join(",", partialBlockedFrameGapFill.BlockedCleanupFrameIndices)}, extendedGapFilled={appExtendedGapFill.FilledFaces}, extendedGapFrames={extendedGapFrames}, mixedFrameGapFilled={mixedFrameGapFill.FilledFaces}, gapCutRemoved={cutGuard.Removed + afterCutGuard.Removed}, gapCutAnchorCandidates={cutGapFill.CutGuardFacesInfo.Count}, blockedCutGapFilled={blockedCutGapFill.FilledFaces}, blockedCutGapFaces={blockedCutGapFill.BlockedCutGapFaces}, blockedCutGapFrames={string.Join(",", blockedCutGapFill.BlockedCutFrameIndices)}, gapCutAfterRemoved={afterCutGuard.Removed}, postSceneCleanupRemoved={preSceneCleanup.RemovedWeakIsolatedFaces}, sceneCutCarryRemoved={sceneCutCarryCleanup.RemovedFaces}, sceneCutCarryFrames={sceneCutCarryFrames}, sceneCutCarryBlockedFrames={sceneCutCarryBlockedFrameText}, sceneCutCarryRefillBlocked={sceneCutCarryBlockedFill.BlockedSceneCarryGapFrames}, emptyPostCutRefillBlocked={postCutWindowGapFill.BlockedSceneCarryGapFrames}, partialSceneCarryRefillBlocked={partialPostCutWindowGapFill.BlockedSceneCarryGapFrames}, partialSceneCarryBlockedFrames={string.Join(",", partialPostCutWindowGapFill.BlockedSceneCarryFrameIndices)}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
