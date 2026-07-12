param(
    [string]$OutputLog = ".tmp\yolo-final-mask-cleanup\verify-output.log"
)

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
using Avalonia.Media.Imaging;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System.Runtime.CompilerServices;

var provider = new FrameMaskProvider();
var size = new PixelSize(1920, 1080);

static WriteableBitmap CreateStoredMaskStub()
    => (WriteableBitmap)RuntimeHelpers.GetUninitializedObject(typeof(WriteableBitmap));

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
provider.SetFaceRects(230, new[] { new Rect(1320, 46, 76, 76) }, size, 0.56f, new[] { 0.56f });
provider.SetFaceRects(231, new[] { new Rect(720, 700, 240, 240) }, size, 0.56f, new[] { 0.56f });
provider.SetFaceRects(232, new[] { new Rect(1500, 360, 10, 42) }, size, 0.56f, new[] { 0.56f });
for (int frame = 240; frame <= 242; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(420 + frame - 240, 0, 76, 76) }, size, 0.58f, new[] { 0.58f });
for (int frame = 250; frame <= 252; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(520 + frame - 250, 0, 76, 76) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(253, new[] { new Rect(523, 0, 76, 76) }, size, 0.72f, new[] { 0.72f });
provider.SetFaceRects(
    260,
    new[] { new Rect(1000, 0, 360, 360), new Rect(1120, 20, 100, 100) },
    size,
    0.35f,
    new[] { 0.86f, 0.35f });
provider.SetFaceRects(261, new[] { new Rect(1000, 0, 360, 360) }, size, 0.86f, new[] { 0.86f });
provider.SetFaceRects(
    262,
    new[] { new Rect(1000, 0, 360, 360), new Rect(1120, 20, 240, 240) },
    size,
    0.35f,
    new[] { 0.86f, 0.35f });
for (int frame = 270; frame <= 274; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(780 + frame - 270, 300, 98, 106) }, size, 0.38f, new[] { 0.38f });
for (int frame = 280; frame <= 284; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(980 + frame - 280, 300, 98, 106) }, size, 0.38f, new[] { 0.38f });
provider.SetFaceRects(285, new[] { new Rect(985, 300, 98, 106) }, size, 0.72f, new[] { 0.72f });
provider.SetFaceRects(289, new[] { new Rect(879, 44, 90, 90) }, size, 0.62f, new[] { 0.62f });
for (int frame = 290; frame <= 293; frame++)
    provider.SetFaceRects(frame, new[] { new Rect(880 + frame - 290, 44, 90, 90) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(294, new[] { new Rect(884, 44, 90, 90) }, size, 0.62f, new[] { 0.62f });

var result = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(provider);

if (result.RemovedWeakIsolatedFaces != 44)
    throw new InvalidOperationException($"Expected 44 weak/tiny isolated/short/texture/tiny/top-edge/top-edge-large/upper/lower/aspect-cluster faces to be removed, got {result.RemovedWeakIsolatedFaces}.");

if (result.RemovedWeakUnsupportedFaces != 3)
    throw new InvalidOperationException($"Expected 3 weak unsupported faces to be removed, got {result.RemovedWeakUnsupportedFaces}.");

if (result.RemovedMediumUnsupportedFaces != 3)
    throw new InvalidOperationException($"Expected 3 medium unsupported suspicious faces to be removed, got {result.RemovedMediumUnsupportedFaces}.");

if (result.RemovedWeakShortClusterFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak short-cluster faces to be removed, got {result.RemovedWeakShortClusterFaces}.");

if (result.RemovedWeakTextureClusterFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak texture-cluster faces to be removed, got {result.RemovedWeakTextureClusterFaces}.");

if (result.RemovedWeakTinyClusterFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak tiny-cluster faces to be removed, got {result.RemovedWeakTinyClusterFaces}.");

if (result.RemovedTinyShortClusterFaces != 4)
    throw new InvalidOperationException($"Expected 4 weak/medium-confidence tiny short-cluster faces to be removed, got {result.RemovedTinyShortClusterFaces}.");

if (result.RemovedTinyIsolatedFaces != 1)
    throw new InvalidOperationException($"Expected 1 medium-confidence tiny isolated face to be removed, got {result.RemovedTinyIsolatedFaces}.");

if (result.RemovedTopEdgeWeakClusterFaces != 3)
    throw new InvalidOperationException($"Expected 3 top-edge weak cluster faces to be removed, got {result.RemovedTopEdgeWeakClusterFaces}.");

if (result.RemovedTopEdgeLargeDuplicateFaces != 1)
    throw new InvalidOperationException($"Expected 1 top-edge large duplicate face to be removed, got {result.RemovedTopEdgeLargeDuplicateFaces}.");

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

if (!provider.TryGetFaceMaskData(260, out var topEdgeLargeDuplicate) ||
    topEdgeLargeDuplicate.Faces.Count != 1 ||
    topEdgeLargeDuplicate.Faces[0].Width != 100)
{
    throw new InvalidOperationException("Expected top-edge large duplicate frame 260 to remove only the large box and keep the smaller support candidate.");
}

if (!provider.TryGetFaceMaskData(261, out var topEdgeLargeSingle) ||
    topEdgeLargeSingle.Faces.Count != 1 ||
    topEdgeLargeSingle.Faces[0].Width != 360)
{
    throw new InvalidOperationException("Expected single top-edge large frame 261 to remain.");
}

if (!provider.TryGetFaceMaskData(262, out var topEdgeLargeNoSmallSupport) ||
    topEdgeLargeNoSmallSupport.Faces.Count != 2)
{
    throw new InvalidOperationException("Expected top-edge large frame 262 with oversized support candidate to remain unchanged.");
}

for (int frame = 270; frame <= 274; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var weakTexture) && weakTexture.Faces.Count > 0)
        throw new InvalidOperationException($"Expected five-frame weak mid-frame texture cluster frame {frame} to be removed.");
}

for (int frame = 280; frame <= 285; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var weakTextureStrongContinuation) ||
        weakTextureStrongContinuation.Faces.Count != 1)
    {
        throw new InvalidOperationException($"Expected weak mid-frame texture cluster with strong continuation to remain at frame {frame}.");
    }
}

for (int frame = 289; frame <= 294; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var upperWeakBridge) ||
        upperWeakBridge.Faces.Count != 1)
    {
        throw new InvalidOperationException($"Expected upper weak bridge continuation frame {frame} to remain.");
    }
}

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

for (int frame = 240; frame <= 242; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var topEdgeWeak) && topEdgeWeak.Faces.Count > 0)
        throw new InvalidOperationException($"Expected top-edge weak cluster frame {frame} to be removed.");
}

for (int frame = 250; frame <= 253; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var topEdgeStrongContinuation) ||
        topEdgeStrongContinuation.Faces.Count != 1)
    {
        throw new InvalidOperationException($"Expected top-edge weak cluster with strong continuation to remain at frame {frame}.");
    }
}

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

for (int frame = 230; frame <= 232; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var mediumUnsupported) && mediumUnsupported.Faces.Count > 0)
        throw new InvalidOperationException($"Expected medium-confidence unsupported suspicious non-edge frame {frame} to be removed.");
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

var trackedContinuityProvider = new FrameMaskProvider();
trackedContinuityProvider.SetFaceRects(10, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
trackedContinuityProvider.SetFaceRects(12, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
var trackedContinuityOptions = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Tracked,
    UseTracking = true,
    DetectEveryNFrames = 1,
    EnablePostProcessing = false,
    FilterProfile = FaceFilterProfile.Yolo
}.ResolveProcessingMode();
var trackedContinuityResult = new AutoMaskPostProcessPipeline(
    trackedContinuityProvider,
    trackedContinuityOptions,
    totalFrames: 20,
    sourceFps: 30.0).Apply("unused.mp4", System.Threading.CancellationToken.None);
if (!trackedContinuityProvider.TryGetFaceMaskData(11, out var trackedContinuityFill) ||
    trackedContinuityFill.Faces.Count != 1 ||
    trackedContinuityResult.FinalSummary.FinalMissRecoveryFillCount != 1)
{
    throw new InvalidOperationException(
        $"Expected Tracked mode to fill one stable continuity frame, got recovered={trackedContinuityResult.FinalSummary.FinalMissRecoveryFillCount}.");
}

var rawContinuityProvider = new FrameMaskProvider();
rawContinuityProvider.SetFaceRects(10, new[] { new Rect(500, 260, 90, 90) }, size, 0.82f, new[] { 0.82f });
rawContinuityProvider.SetFaceRects(12, new[] { new Rect(506, 264, 90, 90) }, size, 0.80f, new[] { 0.80f });
var rawContinuityOptions = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Raw,
    UseTracking = true,
    DetectEveryNFrames = 1,
    EnablePostProcessing = false,
    FilterProfile = FaceFilterProfile.Yolo
}.ResolveProcessingMode();
var rawContinuityResult = new AutoMaskPostProcessPipeline(
    rawContinuityProvider,
    rawContinuityOptions,
    totalFrames: 20,
    sourceFps: 30.0).Apply("unused.mp4", System.Threading.CancellationToken.None);
if (rawContinuityProvider.TryGetFaceMaskData(11, out var rawContinuityFill) && rawContinuityFill.Faces.Count > 0 ||
    rawContinuityResult.FinalSummary.FinalMissRecoveryFillCount != 0)
{
    throw new InvalidOperationException(
        $"Expected Raw mode to preserve the unfilled gap, got recovered={rawContinuityResult.FinalSummary.FinalMissRecoveryFillCount}.");
}

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

var topEdgeLargeAnchorGapProvider = new FrameMaskProvider();
topEdgeLargeAnchorGapProvider.SetFaceRects(930, new[] { new Rect(1200, 0, 340, 340) }, size, 0.84f, new[] { 0.84f });
topEdgeLargeAnchorGapProvider.SetFaceRects(932, new[] { new Rect(1208, 0, 340, 340) }, size, 0.83f, new[] { 0.83f });
var topEdgeLargeAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(topEdgeLargeAnchorGapProvider);
if (topEdgeLargeAnchorGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected top-edge large anchors without third support not to create final-mask gap fills, got filled={topEdgeLargeAnchorGapFill.FilledFaces}.");
if (topEdgeLargeAnchorGapFill.SuppressedRiskyGeometryAnchorChecks <= 0)
    throw new InvalidOperationException("Expected top-edge large anchor suppression to be reported as risky geometry.");

var supportedTopEdgeLargeAnchorGapProvider = new FrameMaskProvider();
supportedTopEdgeLargeAnchorGapProvider.SetFaceRects(940, new[] { new Rect(1200, 0, 340, 340) }, size, 0.84f, new[] { 0.84f });
supportedTopEdgeLargeAnchorGapProvider.SetFaceRects(942, new[] { new Rect(1208, 0, 340, 340) }, size, 0.83f, new[] { 0.83f });
supportedTopEdgeLargeAnchorGapProvider.SetFaceRects(944, new[] { new Rect(1216, 0, 340, 340) }, size, 0.85f, new[] { 0.85f });
var supportedTopEdgeLargeAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(supportedTopEdgeLargeAnchorGapProvider);
var supportedTopEdgeLargeAnchorGapFrames = string.Join(",", supportedTopEdgeLargeAnchorGapFill.FilledFrameIndices);
if (supportedTopEdgeLargeAnchorGapFill.FilledFaces != 2 || supportedTopEdgeLargeAnchorGapFrames != "941,943")
    throw new InvalidOperationException($"Expected supported top-edge large anchors to remain eligible, got filled={supportedTopEdgeLargeAnchorGapFill.FilledFaces}, frames={supportedTopEdgeLargeAnchorGapFrames}.");
if (supportedTopEdgeLargeAnchorGapFill.SuppressedRiskyGeometryAnchorChecks != 0)
    throw new InvalidOperationException($"Expected supported top-edge large anchors not to be reported as suppressed, got {supportedTopEdgeLargeAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}.");

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

var cleanupFaceBlockedGapProvider = new FrameMaskProvider();
cleanupFaceBlockedGapProvider.SetFaceRects(800, new[] { new Rect(100, 100, 50, 50) }, size, 0.80f, new[] { 0.80f });
cleanupFaceBlockedGapProvider.SetFaceRects(802, new[] { new Rect(102, 102, 50, 50) }, size, 0.81f, new[] { 0.81f });
var cleanupFaceBlockedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    cleanupFaceBlockedGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 3,
        BlockedFaces = new[] { new FaceTrackFilledFace(801, new Rect(101, 101, 50, 50), size, 0.42f) }
    });
if (cleanupFaceBlockedGapFill.FilledFaces != 0 ||
    cleanupFaceBlockedGapFill.BlockedCleanupGapFrames != 1 ||
    string.Join(",", cleanupFaceBlockedGapFill.BlockedCleanupFrameIndices) != "801")
{
    throw new InvalidOperationException($"Expected matching cleanup-blocked face to suppress only frame 801, got filled={cleanupFaceBlockedGapFill.FilledFaces}, cleanupBlocked={cleanupFaceBlockedGapFill.BlockedCleanupGapFrames}, frames={string.Join(",", cleanupFaceBlockedGapFill.BlockedCleanupFrameIndices)}.");
}

var cleanupFaceUnrelatedGapProvider = new FrameMaskProvider();
cleanupFaceUnrelatedGapProvider.SetFaceRects(820, new[] { new Rect(100, 100, 50, 50) }, size, 0.80f, new[] { 0.80f });
cleanupFaceUnrelatedGapProvider.SetFaceRects(822, new[] { new Rect(102, 102, 50, 50) }, size, 0.81f, new[] { 0.81f });
var cleanupFaceUnrelatedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    cleanupFaceUnrelatedGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 3,
        BlockedFaces = new[] { new FaceTrackFilledFace(821, new Rect(500, 500, 50, 50), size, 0.42f) }
    });
if (cleanupFaceUnrelatedGapFill.FilledFaces != 1 ||
    cleanupFaceUnrelatedGapFill.BlockedCleanupGapFrames != 0 ||
    !cleanupFaceUnrelatedGapProvider.TryGetFaceMaskData(821, out var cleanupFaceUnrelatedFill) ||
    cleanupFaceUnrelatedFill.Faces.Count != 1)
{
    throw new InvalidOperationException($"Expected unrelated cleanup-blocked face not to suppress stable gap fill, got filled={cleanupFaceUnrelatedGapFill.FilledFaces}, cleanupBlocked={cleanupFaceUnrelatedGapFill.BlockedCleanupGapFrames}.");
}

var extendedGapProvider = new FrameMaskProvider();
extendedGapProvider.SetFaceRects(300, new[] { new Rect(440, 220, 90, 90) }, size, 0.82f, new[] { 0.82f });
extendedGapProvider.SetFaceRects(309, new[] { new Rect(464, 236, 90, 90) }, size, 0.80f, new[] { 0.80f });
var defaultExtendedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(extendedGapProvider);
if (defaultExtendedGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected default final-mask gap fill to leave an eight-frame gap for conservative callers, got {defaultExtendedGapFill.FilledFaces}.");
var appExtendedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    extendedGapProvider,
    new YoloFinalMaskGapFillOptions { MaxGapFrames = 8 });
var extendedGapFrames = string.Join(",", appExtendedGapFill.FilledFrameIndices);
if (appExtendedGapFill.FilledFaces != 8 || extendedGapFrames != "301,302,303,304,305,306,307,308")
    throw new InvalidOperationException($"Expected app eight-frame stable gap fill at frames 301-308, got filled={appExtendedGapFill.FilledFaces}, frames={extendedGapFrames}.");

var longShiftGapProvider = new FrameMaskProvider();
longShiftGapProvider.SetFaceRects(340, new[] { new Rect(440, 220, 100, 100) }, size, 0.82f, new[] { 0.82f });
longShiftGapProvider.SetFaceRects(346, new[] { new Rect(495, 220, 100, 100) }, size, 0.80f, new[] { 0.80f });
var longShiftGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    longShiftGapProvider,
    new YoloFinalMaskGapFillOptions { MaxGapFrames = 5 });
if (longShiftGapFill.FilledFaces != 0)
    throw new InvalidOperationException($"Expected long final-mask gap with excessive center shift not to be filled, got filled={longShiftGapFill.FilledFaces}, frames={string.Join(",", longShiftGapFill.FilledFrameIndices)}.");

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
sceneCutCarryProvider.SetFaceRects(1007, new[] { new Rect(654, 374, 120, 120) }, size, 0.70f, new[] { 0.70f });
sceneCutCarryProvider.SetFaceRects(1008, new[] { new Rect(656, 376, 120, 120) }, size, 0.76f, new[] { 0.76f });
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
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.95f,
        ExtendedWeakCarryFrames = 8,
        ExtendedWeakMaxConfidence = 0.78f
    });
var sceneCutCarryFrames = string.Join(",", sceneCutCarryCleanup.RemovedFrameIndices);
var sceneCutCarryProtectedFrames = string.Join(",", sceneCutCarryCleanup.ProtectedStrongCarryLikeFrameIndices);
var sceneCutCarryBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
    new[] { "1000->1001", "2000->2004" },
    8);
var sceneCutCarryBlockedFrameText = string.Join(",", sceneCutCarryBlockedFrames);
if (sceneCutCarryCleanup.RemovedFaces != 17 || sceneCutCarryFrames != "1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009")
    throw new InvalidOperationException($"Expected scene-cut carry cleanup to remove weak frames 1001-1005, unsupported strong carry frame 1006, extended weak frames 1007-1008, direct-pair frames 2001-2008, and unsupported strong carry frame 2009, got removed={sceneCutCarryCleanup.RemovedFaces}, frames={sceneCutCarryFrames}.");
if (sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces != 2 ||
    string.Join(",", sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices) != "1006,2009")
{
    throw new InvalidOperationException($"Expected scene-cut carry cleanup to remove unsupported strong carry-like frames 1006 and 2009, got removedUnsupportedStrong={sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, frames={string.Join(",", sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)}.");
}
if (sceneCutCarryCleanup.ProtectedStrongCarryLikeFaces != 0 || sceneCutCarryProtectedFrames != "")
    throw new InvalidOperationException($"Expected no protected strong carry-like frames when no new-scene support exists, got protected={sceneCutCarryCleanup.ProtectedStrongCarryLikeFaces}, frames={sceneCutCarryProtectedFrames}.");
if (sceneCutCarryBlockedFrameText != "1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,2011")
    throw new InvalidOperationException($"Expected scene-cut carry blocked frames to cover the extended post-cut carry windows, got frames={sceneCutCarryBlockedFrameText}.");
if (!sceneCutCarryProvider.TryGetFaceMaskData(1002, out var mixedCarryFrame) ||
    mixedCarryFrame.Faces.Count != 1 ||
    mixedCarryFrame.Confidences[0] < 0.90f)
{
    throw new InvalidOperationException("Expected scene-cut carry cleanup to keep unrelated strong face on mixed frame 1002.");
}
if (sceneCutCarryProvider.TryGetFaceMaskData(2009, out var directCarryStrongAfter) &&
    directCarryStrongAfter.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected unsupported strong direct-pair carry frame 2009 to be removed.");
}
if (sceneCutCarryProvider.TryGetFaceMaskData(1006, out var strongExtendedCarry) &&
    strongExtendedCarry.Faces.Count > 0)
{
    throw new InvalidOperationException("Expected unsupported strong extended carry frame 1006 to be removed.");
}

var lookbackCarryProvider = new FrameMaskProvider();
lookbackCarryProvider.SetFaceRects(3998, new[] { new Rect(860, 360, 110, 110) }, size, 0.87f, new[] { 0.87f });
lookbackCarryProvider.SetFaceRects(4000, new[] { new Rect(862, 362, 110, 110) }, size, 0.64f, new[] { 0.64f });
lookbackCarryProvider.SetFaceRects(4001, new[] { new Rect(864, 364, 110, 110) }, size, 0.63f, new[] { 0.63f });
lookbackCarryProvider.SetFaceRects(4002, new[] { new Rect(866, 366, 110, 110) }, size, 0.62f, new[] { 0.62f });
var noLookbackCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    lookbackCarryProvider,
    new[] { "3999->4000" },
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.95f,
        SourceLookbackFrames = 0
    });
if (noLookbackCarryCleanup.RemovedFaces != 0)
    throw new InvalidOperationException($"Expected missing source frame to skip carry cleanup without lookback, got removed={noLookbackCarryCleanup.RemovedFaces}.");
var lookbackCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    lookbackCarryProvider,
    new[] { "3999->4000" },
    new YoloSceneCutCarryCleanupOptions { MaxConfidence = 0.95f });
var lookbackCarryFrames = string.Join(",", lookbackCarryCleanup.RemovedFrameIndices);
if (lookbackCarryCleanup.RemovedFaces != 3 || lookbackCarryFrames != "4000,4001,4002")
    throw new InvalidOperationException($"Expected scene-cut carry cleanup to use source lookback when the cut-start frame has no mask, got removed={lookbackCarryCleanup.RemovedFaces}, frames={lookbackCarryFrames}.");

var sceneCutCarryBlockedFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    sceneCutCarryProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = sceneCutCarryBlockedFrames
    });
if (sceneCutCarryBlockedFill.FilledFaces != 0 ||
    sceneCutCarryBlockedFill.BlockedCleanupGapFrames != 0)
{
    throw new InvalidOperationException($"Expected scene-cut carry cleanup not to recreate removed carry masks, got filled={sceneCutCarryBlockedFill.FilledFaces}, cleanupBlocked={sceneCutCarryBlockedFill.BlockedCleanupGapFrames}, sceneCarryBlocked={sceneCutCarryBlockedFill.BlockedSceneCarryGapFrames}.");
}

var postCutWindowGapProvider = new FrameMaskProvider();
postCutWindowGapProvider.SetFaceRects(3000, new[] { new Rect(700, 320, 100, 100) }, size, 0.95f, new[] { 0.95f });
postCutWindowGapProvider.SetFaceRects(3002, new[] { new Rect(702, 322, 100, 100) }, size, 0.93f, new[] { 0.93f });
postCutWindowGapProvider.SetFaceRects(3006, new[] { new Rect(706, 326, 100, 100) }, size, 0.94f, new[] { 0.94f });
var postCutWindowCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    postCutWindowGapProvider,
    new[] { "3000->3001" });
if (postCutWindowCleanup.RemovedFaces != 2 ||
    postCutWindowCleanup.RemovedUnsupportedStrongCarryLikeFaces != 2 ||
    string.Join(",", postCutWindowCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices) != "3002,3006")
{
    throw new InvalidOperationException($"Expected unsupported high-confidence post-cut carry frames 3002 and 3006 to be removed when there is not enough continuation support, got removed={postCutWindowCleanup.RemovedFaces}, unsupportedStrong={postCutWindowCleanup.RemovedUnsupportedStrongCarryLikeFaces}, frames={string.Join(",", postCutWindowCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)}.");
}
if (postCutWindowCleanup.ProtectedStrongCarryLikeFaces != 0 ||
    string.Join(",", postCutWindowCleanup.ProtectedStrongCarryLikeFrameIndices) != "")
{
    throw new InvalidOperationException($"Expected no protected high-confidence post-cut anchor without enough later support, got protected={postCutWindowCleanup.ProtectedStrongCarryLikeFaces}, frames={string.Join(",", postCutWindowCleanup.ProtectedStrongCarryLikeFrameIndices)}.");
}
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
    postCutWindowGapFill.BlockedSceneCarryGapFrames != 0)
{
    throw new InvalidOperationException($"Expected scene-cut carry window to block post-cut gap refill around the remaining protected strong carry anchor, got filled={postCutWindowGapFill.FilledFaces}, cleanupBlocked={postCutWindowGapFill.BlockedCleanupGapFrames}, sceneCarryBlocked={postCutWindowGapFill.BlockedSceneCarryGapFrames}.");
}

var stickyStrongCarryProvider = new FrameMaskProvider();
stickyStrongCarryProvider.SetFaceRects(3400, new[] { new Rect(700, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
for (int frame = 3401; frame <= 3405; frame++)
    stickyStrongCarryProvider.SetFaceRects(frame, new[] { new Rect(702, 322, 100, 100) }, size, 0.99f, new[] { 0.99f });
var stickyStrongCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    stickyStrongCarryProvider,
    new[] { "3400->3401" },
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.98f,
        ExtendedWeakCarryFrames = 5,
        ExtendedWeakMaxConfidence = 0.78f
    });
if (stickyStrongCarryCleanup.RemovedFaces != 5 ||
    stickyStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces != 5 ||
    string.Join(",", stickyStrongCarryCleanup.RemovedFrameIndices) != "3401,3402,3403,3404,3405" ||
    stickyStrongCarryCleanup.ProtectedStrongCarryLikeFaces != 0)
{
    throw new InvalidOperationException($"Expected sustained high-confidence same-position carry to be removed instead of protected, got removed={stickyStrongCarryCleanup.RemovedFaces}, unsupportedStrong={stickyStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, protected={stickyStrongCarryCleanup.ProtectedStrongCarryLikeFaces}, frames={string.Join(",", stickyStrongCarryCleanup.RemovedFrameIndices)}.");
}

var driftingStrongCarryProvider = new FrameMaskProvider();
driftingStrongCarryProvider.SetFaceRects(3500, new[] { new Rect(700, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
for (int frame = 3501; frame <= 3505; frame++)
    driftingStrongCarryProvider.SetFaceRects(frame, new[] { new Rect(700 + (frame - 3500) * 25, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
var driftingStrongCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    driftingStrongCarryProvider,
    new[] { "3500->3501" },
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.98f,
        ExtendedWeakCarryFrames = 5,
        ExtendedWeakMaxConfidence = 0.78f
    });
if (driftingStrongCarryCleanup.RemovedFaces != 5 ||
    driftingStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces != 5 ||
    driftingStrongCarryCleanup.ProtectedStrongCarryLikeFaces != 0 ||
    driftingStrongCarryProvider.TryGetFaceMaskData(3501, out var driftingRemovedA) && driftingRemovedA.Faces.Count > 0 ||
    driftingStrongCarryProvider.TryGetFaceMaskData(3502, out var driftingRemovedB) && driftingRemovedB.Faces.Count > 0)
{
    throw new InvalidOperationException($"Expected same-size drifting high-confidence post-cut carry to be removed instead of protected, got removed={driftingStrongCarryCleanup.RemovedFaces}, unsupportedStrong={driftingStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, protected={driftingStrongCarryCleanup.ProtectedStrongCarryLikeFaces}, removedFrames={string.Join(",", driftingStrongCarryCleanup.RemovedFrameIndices)}.");
}

var areaChangedStrongCarryProvider = new FrameMaskProvider();
areaChangedStrongCarryProvider.SetFaceRects(3600, new[] { new Rect(700, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
for (int frame = 3601; frame <= 3605; frame++)
    areaChangedStrongCarryProvider.SetFaceRects(frame, new[] { new Rect(720 + (frame - 3600) * 2, 320, 130, 130) }, size, 0.99f, new[] { 0.99f });
var areaChangedStrongCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    areaChangedStrongCarryProvider,
    new[] { "3600->3601" },
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.98f,
        ExtendedWeakCarryFrames = 5,
        ExtendedWeakMaxConfidence = 0.78f
    });
if (areaChangedStrongCarryCleanup.ProtectedStrongCarryLikeFaces < 2 ||
    !areaChangedStrongCarryProvider.TryGetFaceMaskData(3601, out var areaChangedProtectedA) ||
    areaChangedProtectedA.Faces.Count != 1 ||
    !areaChangedStrongCarryProvider.TryGetFaceMaskData(3602, out var areaChangedProtectedB) ||
    areaChangedProtectedB.Faces.Count != 1)
{
    throw new InvalidOperationException($"Expected area-changed high-confidence post-cut support to remain protected, got protected={areaChangedStrongCarryCleanup.ProtectedStrongCarryLikeFaces}, removedFrames={string.Join(",", areaChangedStrongCarryCleanup.RemovedFrameIndices)}.");
}

var sameCenterAreaChangedStrongCarryProvider = new FrameMaskProvider();
sameCenterAreaChangedStrongCarryProvider.SetFaceRects(3700, new[] { new Rect(700, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
for (int frame = 3701; frame <= 3705; frame++)
    sameCenterAreaChangedStrongCarryProvider.SetFaceRects(frame, new[] { new Rect(685, 305, 130, 130) }, size, 0.99f, new[] { 0.99f });
var sameCenterAreaChangedStrongCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
    sameCenterAreaChangedStrongCarryProvider,
    new[] { "3700->3701" },
    new YoloSceneCutCarryCleanupOptions
    {
        MaxConfidence = 0.98f,
        ExtendedWeakCarryFrames = 5,
        ExtendedWeakMaxConfidence = 0.78f
    });
if (sameCenterAreaChangedStrongCarryCleanup.RemovedFaces != 5 ||
    sameCenterAreaChangedStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces != 5 ||
    sameCenterAreaChangedStrongCarryCleanup.ProtectedStrongCarryLikeFaces != 0 ||
    sameCenterAreaChangedStrongCarryProvider.TryGetFaceMaskData(3701, out var sameCenterAreaChangedRemovedA) && sameCenterAreaChangedRemovedA.Faces.Count > 0 ||
    sameCenterAreaChangedStrongCarryProvider.TryGetFaceMaskData(3702, out var sameCenterAreaChangedRemovedB) && sameCenterAreaChangedRemovedB.Faces.Count > 0)
{
    throw new InvalidOperationException($"Expected same-center area-changed high-confidence post-cut carry to be removed instead of protected, got removed={sameCenterAreaChangedStrongCarryCleanup.RemovedFaces}, unsupportedStrong={sameCenterAreaChangedStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, protected={sameCenterAreaChangedStrongCarryCleanup.ProtectedStrongCarryLikeFaces}, removedFrames={string.Join(",", sameCenterAreaChangedStrongCarryCleanup.RemovedFrameIndices)}.");
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
    throw new InvalidOperationException($"Expected a scene-carry blocked frame inside a stable gap to suppress the whole gap, got filled={partialPostCutWindowGapFill.FilledFaces}, sceneCarryBlocked={partialPostCutWindowGapFill.BlockedSceneCarryGapFrames}, frames={string.Join(",", partialPostCutWindowGapFill.BlockedSceneCarryFrameIndices)}.");
}
if (partialPostCutWindowGapProvider.TryGetFaceMaskData(3101, out var partialSceneCarryBlockedA) && partialSceneCarryBlockedA.Faces.Count > 0 ||
    partialPostCutWindowGapProvider.TryGetFaceMaskData(3102, out var partialSceneCarryBlockedMid) && partialSceneCarryBlockedMid.Faces.Count > 0 ||
    partialPostCutWindowGapProvider.TryGetFaceMaskData(3103, out var partialSceneCarryBlockedB) && partialSceneCarryBlockedB.Faces.Count > 0)
    throw new InvalidOperationException("Expected scene-carry blocked final-mask gap 3101-3103 not to be partially recreated around frame 3102.");

var partialSceneCarryFaceBlockedGapProvider = new FrameMaskProvider();
partialSceneCarryFaceBlockedGapProvider.SetFaceRects(3110, new[] { new Rect(740, 340, 100, 100) }, size, 0.95f, new[] { 0.95f });
partialSceneCarryFaceBlockedGapProvider.SetFaceRects(3114, new[] { new Rect(748, 348, 100, 100) }, size, 0.94f, new[] { 0.94f });
var partialSceneCarryFaceBlockedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    partialSceneCarryFaceBlockedGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFaces = new[] { new FaceTrackFilledFace(3112, new Rect(744, 344, 100, 100), size, 0.64f) }
    });
if (partialSceneCarryFaceBlockedGapFill.FilledFaces != 0 ||
    partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryGapFrames != 3 ||
    string.Join(",", partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryFrameIndices) != "3111,3112,3113")
{
    throw new InvalidOperationException($"Expected a matching scene-carry blocked face inside a stable gap to suppress the whole gap, got filled={partialSceneCarryFaceBlockedGapFill.FilledFaces}, sceneCarryBlocked={partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryGapFrames}, frames={string.Join(",", partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryFrameIndices)}.");
}
if (partialSceneCarryFaceBlockedGapProvider.TryGetFaceMaskData(3111, out var partialSceneCarryFaceBlockedA) && partialSceneCarryFaceBlockedA.Faces.Count > 0 ||
    partialSceneCarryFaceBlockedGapProvider.TryGetFaceMaskData(3112, out var partialSceneCarryFaceBlockedMid) && partialSceneCarryFaceBlockedMid.Faces.Count > 0 ||
    partialSceneCarryFaceBlockedGapProvider.TryGetFaceMaskData(3113, out var partialSceneCarryFaceBlockedB) && partialSceneCarryFaceBlockedB.Faces.Count > 0)
    throw new InvalidOperationException("Expected scene-carry blocked-face final-mask gap 3111-3113 not to be partially recreated around frame 3112.");

var sceneCarryAnchorGapProvider = new FrameMaskProvider();
sceneCarryAnchorGapProvider.SetFaceRects(3208, new[] { new Rect(700, 320, 100, 100) }, size, 0.99f, new[] { 0.99f });
sceneCarryAnchorGapProvider.SetFaceRects(3210, new[] { new Rect(706, 326, 100, 100) }, size, 0.94f, new[] { 0.94f });
var sceneCarryAnchorGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    sceneCarryAnchorGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = new[] { 3208 }
    });
if (sceneCarryAnchorGapFill.FilledFaces != 0 ||
    sceneCarryAnchorGapFill.BlockedSceneCarryGapFrames != 1 ||
    string.Join(",", sceneCarryAnchorGapFill.BlockedSceneCarryFrameIndices) != "3209")
{
    throw new InvalidOperationException($"Expected a scene-carry blocked anchor not to extend a post-cut stable gap, got filled={sceneCarryAnchorGapFill.FilledFaces}, sceneCarryBlocked={sceneCarryAnchorGapFill.BlockedSceneCarryGapFrames}, frames={string.Join(",", sceneCarryAnchorGapFill.BlockedSceneCarryFrameIndices)}.");
}
if (sceneCarryAnchorGapProvider.TryGetFaceMaskData(3209, out var sceneCarryAnchorExtended) && sceneCarryAnchorExtended.Faces.Count > 0)
    throw new InvalidOperationException("Expected scene-carry blocked anchor not to recreate frame 3209.");

var storedCleanupBlockedGapProvider = new FrameMaskProvider();
storedCleanupBlockedGapProvider.SetFaceRects(3300, new[] { new Rect(700, 320, 100, 100) }, size, 0.95f, new[] { 0.95f });
storedCleanupBlockedGapProvider.SetMask(3302, CreateStoredMaskStub());
storedCleanupBlockedGapProvider.SetFaceRects(3304, new[] { new Rect(708, 328, 100, 100) }, size, 0.94f, new[] { 0.94f });
var storedCleanupBlockedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    storedCleanupBlockedGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedFrameIndices = new[] { 3302 }
    });
if (storedCleanupBlockedGapFill.FilledFaces != 0 ||
    storedCleanupBlockedGapFill.BlockedCleanupGapFrames != 3 ||
    string.Join(",", storedCleanupBlockedGapFill.BlockedCleanupFrameIndices) != "3301,3302,3303")
{
    throw new InvalidOperationException($"Expected a stored cleanup-blocked frame to suppress the whole stable gap, got filled={storedCleanupBlockedGapFill.FilledFaces}, cleanupBlocked={storedCleanupBlockedGapFill.BlockedCleanupGapFrames}, frames={string.Join(",", storedCleanupBlockedGapFill.BlockedCleanupFrameIndices)}.");
}
if (storedCleanupBlockedGapProvider.TryGetFaceMaskData(3301, out var storedCleanupBlockedA) && storedCleanupBlockedA.Faces.Count > 0 ||
    storedCleanupBlockedGapProvider.TryGetFaceMaskData(3303, out var storedCleanupBlockedB) && storedCleanupBlockedB.Faces.Count > 0 ||
    !storedCleanupBlockedGapProvider.TryGetStoredMask(3302, out _))
{
    throw new InvalidOperationException("Expected stored cleanup-blocked frame 3302 to remain stored and surrounding gap frames not to be recreated.");
}

var storedSceneCarryBlockedGapProvider = new FrameMaskProvider();
storedSceneCarryBlockedGapProvider.SetFaceRects(3310, new[] { new Rect(720, 340, 100, 100) }, size, 0.95f, new[] { 0.95f });
storedSceneCarryBlockedGapProvider.SetMask(3312, CreateStoredMaskStub());
storedSceneCarryBlockedGapProvider.SetFaceRects(3314, new[] { new Rect(728, 348, 100, 100) }, size, 0.94f, new[] { 0.94f });
var storedSceneCarryBlockedGapFill = new YoloFinalMaskPostProcessor().FillShortStableGaps(
    storedSceneCarryBlockedGapProvider,
    new YoloFinalMaskGapFillOptions
    {
        MaxGapFrames = 5,
        BlockedSceneCarryFrameIndices = new[] { 3312 }
    });
if (storedSceneCarryBlockedGapFill.FilledFaces != 0 ||
    storedSceneCarryBlockedGapFill.BlockedSceneCarryGapFrames != 3 ||
    string.Join(",", storedSceneCarryBlockedGapFill.BlockedSceneCarryFrameIndices) != "3311,3312,3313")
{
    throw new InvalidOperationException($"Expected a stored scene-carry blocked frame to suppress the whole stable gap, got filled={storedSceneCarryBlockedGapFill.FilledFaces}, sceneCarryBlocked={storedSceneCarryBlockedGapFill.BlockedSceneCarryGapFrames}, frames={string.Join(",", storedSceneCarryBlockedGapFill.BlockedSceneCarryFrameIndices)}.");
}
if (storedSceneCarryBlockedGapProvider.TryGetFaceMaskData(3311, out var storedSceneCarryBlockedA) && storedSceneCarryBlockedA.Faces.Count > 0 ||
    storedSceneCarryBlockedGapProvider.TryGetFaceMaskData(3313, out var storedSceneCarryBlockedB) && storedSceneCarryBlockedB.Faces.Count > 0 ||
    !storedSceneCarryBlockedGapProvider.TryGetStoredMask(3312, out _))
{
    throw new InvalidOperationException("Expected stored scene-carry blocked frame 3312 to remain stored and surrounding gap frames not to be recreated.");
}

Console.WriteLine(
    $"[YoloFinalMaskCleanupVerify] removedWeakIsolated={result.RemovedWeakIsolatedFaces}, removedWeakUnsupported={result.RemovedWeakUnsupportedFaces}, removedMediumUnsupported={result.RemovedMediumUnsupportedFaces}, removedWeakShortClusters={result.RemovedWeakShortClusterFaces}, removedWeakTextureClusters={result.RemovedWeakTextureClusterFaces}, removedWeakTinyClusters={result.RemovedWeakTinyClusterFaces}, removedTinyShortClusters={result.RemovedTinyShortClusterFaces}, removedTinyIsolated={result.RemovedTinyIsolatedFaces}, removedTopEdgeWeakClusters={result.RemovedTopEdgeWeakClusterFaces}, removedTopEdgeLargeDuplicates={result.RemovedTopEdgeLargeDuplicateFaces}, removedUpperWeakClusters={result.RemovedUpperWeakClusterFaces}, removedLowerWeakClusters={result.RemovedLowerWeakClusterFaces}, removedAspectOutliers={result.RemovedAspectOutlierClusterFaces}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, remainingFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}, trackedContinuityFilled={trackedContinuityResult.FinalSummary.FinalMissRecoveryFillCount}, rawContinuityFilled={rawContinuityResult.FinalSummary.FinalMissRecoveryFillCount}, gapFilled={gapFill.FilledFaces}, gapFrames={filledFrames}, supportedWeakGapFilled={supportedWeakGapFill.FilledFaces}, supportedWeakGapFrames={supportedWeakGapFrames}, unsupportedWeakGapFilled={unsupportedWeakGapFill.FilledFaces}, weakEdgeAnchorGapFilled={weakEdgeAnchorGapFill.FilledFaces}, strongEdgeAnchorGapFilled={strongEdgeAnchorGapFill.FilledFaces}, mediumRiskyAnchorGapFilled={mediumRiskyAnchorGapFill.FilledFaces}, mediumRiskyAnchorSuppressed={mediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, supportedMediumRiskyAnchorGapFilled={supportedMediumRiskyAnchorGapFill.FilledFaces}, supportedMediumRiskyAnchorGapFrames={supportedMediumRiskyAnchorGapFrames}, supportedMediumRiskyAnchorSuppressed={supportedMediumRiskyAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, topEdgeLargeAnchorGapFilled={topEdgeLargeAnchorGapFill.FilledFaces}, topEdgeLargeAnchorSuppressed={topEdgeLargeAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, supportedTopEdgeLargeAnchorGapFilled={supportedTopEdgeLargeAnchorGapFill.FilledFaces}, supportedTopEdgeLargeAnchorGapFrames={supportedTopEdgeLargeAnchorGapFrames}, supportedTopEdgeLargeAnchorSuppressed={supportedTopEdgeLargeAnchorGapFill.SuppressedRiskyGeometryAnchorChecks}, cleanupBlockedGapFilled={blockedFrameGapFill.FilledFaces}, cleanupBlockedGapFrames={blockedFrameGapFill.BlockedCleanupGapFrames}, cleanupBlockedFrames={string.Join(",", blockedFrameGapFill.BlockedCleanupFrameIndices)}, partialCleanupBlockedGapFilled={partialBlockedFrameGapFill.FilledFaces}, partialCleanupBlockedGapFrames={partialBlockedFrameGapFill.BlockedCleanupGapFrames}, partialCleanupBlockedFrames={string.Join(",", partialBlockedFrameGapFill.BlockedCleanupFrameIndices)}, extendedGapFilled={appExtendedGapFill.FilledFaces}, extendedGapFrames={extendedGapFrames}, mixedFrameGapFilled={mixedFrameGapFill.FilledFaces}, gapCutRemoved={cutGuard.Removed + afterCutGuard.Removed}, gapCutAnchorCandidates={cutGapFill.CutGuardFacesInfo.Count}, blockedCutGapFilled={blockedCutGapFill.FilledFaces}, blockedCutGapFaces={blockedCutGapFill.BlockedCutGapFaces}, blockedCutGapFrames={string.Join(",", blockedCutGapFill.BlockedCutFrameIndices)}, gapCutAfterRemoved={afterCutGuard.Removed}, postSceneCleanupRemoved={preSceneCleanup.RemovedWeakIsolatedFaces}, sceneCutCarryRemoved={sceneCutCarryCleanup.RemovedFaces}, sceneCutCarryFrames={sceneCutCarryFrames}, sceneCutCarryRemovedUnsupportedStrong={sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, sceneCutCarryRemovedUnsupportedStrongFrames={string.Join(",", sceneCutCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)}, sceneCutCarryProtected={sceneCutCarryCleanup.ProtectedStrongCarryLikeFaces}, sceneCutCarryProtectedFrames={sceneCutCarryProtectedFrames}, lookbackSceneCutCarryRemoved={lookbackCarryCleanup.RemovedFaces}, lookbackSceneCutCarryFrames={lookbackCarryFrames}, sceneCutCarryBlockedFrames={sceneCutCarryBlockedFrameText}, sceneCutCarryRefillBlocked={sceneCutCarryBlockedFill.BlockedSceneCarryGapFrames}, emptyPostCutRemovedUnsupportedStrong={postCutWindowCleanup.RemovedUnsupportedStrongCarryLikeFaces}, emptyPostCutProtected={postCutWindowCleanup.ProtectedStrongCarryLikeFaces}, emptyPostCutRefillBlocked={postCutWindowGapFill.BlockedSceneCarryGapFrames}, stickyStrongCarryRemoved={stickyStrongCarryCleanup.RemovedFaces}, stickyStrongCarryRemovedUnsupportedStrong={stickyStrongCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces}, driftingStrongCarryRemoved={driftingStrongCarryCleanup.RemovedFaces}, areaChangedStrongCarryProtected={areaChangedStrongCarryCleanup.ProtectedStrongCarryLikeFaces}, sameCenterAreaChangedStrongCarryRemoved={sameCenterAreaChangedStrongCarryCleanup.RemovedFaces}, partialSceneCarryRefillBlocked={partialPostCutWindowGapFill.BlockedSceneCarryGapFrames}, partialSceneCarryBlockedFrames={string.Join(",", partialPostCutWindowGapFill.BlockedSceneCarryFrameIndices)}, partialSceneCarryFaceRefillBlocked={partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryGapFrames}, partialSceneCarryFaceBlockedFrames={string.Join(",", partialSceneCarryFaceBlockedGapFill.BlockedSceneCarryFrameIndices)}, sceneCarryAnchorRefillBlocked={sceneCarryAnchorGapFill.BlockedSceneCarryGapFrames}, sceneCarryAnchorBlockedFrames={string.Join(",", sceneCarryAnchorGapFill.BlockedSceneCarryFrameIndices)}, storedCleanupRefillBlocked={storedCleanupBlockedGapFill.BlockedCleanupGapFrames}, storedCleanupBlockedFrames={string.Join(",", storedCleanupBlockedGapFill.BlockedCleanupFrameIndices)}, storedSceneCarryRefillBlocked={storedSceneCarryBlockedGapFill.BlockedSceneCarryGapFrames}, storedSceneCarryBlockedFrames={string.Join(",", storedSceneCarryBlockedGapFill.BlockedSceneCarryFrameIndices)}");
'@ | Set-Content -Encoding UTF8 $program

$resolvedOutputLog = if ([IO.Path]::IsPathRooted($OutputLog)) {
    $OutputLog
}
else {
    Join-Path $repo $OutputLog
}
$outputLogDir = Split-Path -Parent $resolvedOutputLog
if (-not [string]::IsNullOrWhiteSpace($outputLogDir)) {
    New-Item -ItemType Directory -Force -Path $outputLogDir | Out-Null
}

$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $output = & dotnet run --project $project 2>&1
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

$text = ($output | Out-String)
$text | Set-Content -Encoding UTF8 -Path $resolvedOutputLog
Write-Host $text

if ($exitCode -ne 0) {
    throw "YoloFinalMaskCleanupHarness failed with exit code $exitCode. Output log: $resolvedOutputLog"
}
