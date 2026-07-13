param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\face-track-postprocess"
$project = Join-Path $work "FaceTrackPostprocessHarness.csproj"
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
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;

var provider = new FrameMaskProvider();
var size = new PixelSize(1920, 1080);

provider.SetFaceRects(10, new[] { new Rect(100, 100, 80, 80) }, size, 0.82f, new[] { 0.82f });
provider.SetFaceRects(12, new[] { new Rect(110, 104, 82, 80) }, size, 0.81f, new[] { 0.81f });
provider.SetFaceRects(20, new[] { new Rect(900, 300, 70, 70) }, size, 0.55f, new[] { 0.55f });
provider.SetFaceRects(25, new[] { new Rect(0, 420, 42, 76) }, size, 0.96f, new[] { 0.96f });
provider.SetFaceRects(30, new[] { new Rect(300, 220, 90, 92) }, size, 0.90f, new[] { 0.90f });
provider.SetFaceRects(31, new[] { new Rect(306, 224, 90, 92) }, size, 0.89f, new[] { 0.89f });
provider.SetFaceRects(32, new[] { new Rect(312, 228, 90, 92) }, size, 0.88f, new[] { 0.88f });
provider.SetFaceRects(5, new[] { new Rect(1500, 700, 120, 90) }, size, 0.52f, new[] { 0.52f });
provider.SetFaceRects(13, new[] { new Rect(1508, 704, 120, 90) }, size, 0.51f, new[] { 0.51f });
provider.SetFaceRects(18, new[] { new Rect(1516, 708, 120, 90) }, size, 0.50f, new[] { 0.50f });
provider.SetFaceRects(40, new[] { new Rect(820, 410, 34, 42) }, size, 0.94f, new[] { 0.94f });
provider.SetFaceRects(42, new[] { new Rect(825, 413, 34, 42) }, size, 0.93f, new[] { 0.93f });
provider.SetFaceRects(50, new[] { new Rect(1120, 260, 33, 44) }, size, 0.95f, new[] { 0.95f });
provider.SetFaceRects(51, new[] { new Rect(1123, 262, 33, 44) }, size, 0.94f, new[] { 0.94f });
provider.SetFaceRects(52, new[] { new Rect(1126, 264, 33, 44) }, size, 0.93f, new[] { 0.93f });
provider.SetFaceRects(55, new[] { new Rect(200, 40, 80, 80) }, size, 0.86f, new[] { 0.86f });
provider.SetFaceRects(59, new[] { new Rect(980, 260, 420, 360) }, size, 0.88f, new[] { 0.88f });
provider.SetFaceRects(70, new[] { new Rect(120, 320, 84, 96) }, size, 0.82f, new[] { 0.82f });
provider.SetFaceRects(71, new[] { new Rect(96, 320, 84, 96) }, size, 0.74f, new[] { 0.74f });
provider.SetFaceRects(72, new[] { new Rect(50, 320, 84, 96) }, size, 0.46f, new[] { 0.46f });
provider.SetFaceRects(75, new[] { new Rect(1636, 320, 84, 96) }, size, 0.82f, new[] { 0.82f });
provider.SetFaceRects(76, new[] { new Rect(1679, 322, 84, 96) }, size, 0.80f, new[] { 0.80f });
provider.SetFaceRects(77, new[] { new Rect(1722, 324, 84, 96) }, size, 0.58f, new[] { 0.58f });
provider.SetFaceRects(78, new[] { new Rect(1682, 324, 84, 96) }, size, 0.44f, new[] { 0.44f });
provider.SetFaceRects(85, new[] { new Rect(1760, 0, 120, 100) }, size, 0.90f, new[] { 0.90f });
provider.SetFaceRects(86, new[] { new Rect(1740, 2, 130, 110) }, size, 0.89f, new[] { 0.89f });
provider.SetFaceRects(87, new[] { new Rect(1720, 4, 140, 120) }, size, 0.88f, new[] { 0.88f });
provider.SetFaceRects(95, new[] { new Rect(1760, 800, 120, 100) }, size, 0.90f, new[] { 0.90f });
provider.SetFaceRects(96, new[] { new Rect(1780, 800, 120, 100) }, size, 0.89f, new[] { 0.89f });
provider.SetFaceRects(97, new[] { new Rect(1800, 800, 120, 100) }, size, 0.88f, new[] { 0.88f });

var result = new FaceTrackInterpolator().Apply(
    provider,
    totalFrames: 103,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 8,
        MaxFillGap = 5,
        MaxInitialFillFrames = 3,
        InitialFillRequiresInwardMotion = true,
        DropShortTrackMaxDetections = 1,
        DropShortSmallTrackMaxDetections = 3,
        DropSparseTrackMaxDetections = 3,
        DropSparseTrackMinSpanFrames = 8,
        DropSparseTrackMaxDensity = 0.42,
        WeakConfidence = 0.50f,
        StrongConfidence = 0.68f,
        ShortTrackMaxConfidence = 0.68f,
        SparseTrackMaxConfidence = 0.56f,
        EdgeTailMaxConfidence = 0.50f,
        EdgeTailMinStableDetections = 2,
        EdgeLostFillMaxConfidence = 0.60f,
        UnstableTailMaxConfidence = 0.50f,
        UnstableTailMinStableDetections = 3,
        UnstableTailMinIou = 0.45,
        UnstableTailMaxAreaChangeRatio = 1.8,
        SmallTrackMaxAreaRatio = 0.00075,
        MinTrackIou = 0.12,
        MaxCenterShiftRatio = 0.55,
        MaxAreaChangeRatio = 3.2,
        DuplicateIou = 0.35
    });

if (!provider.TryGetFaceMaskData(11, out var filled) || filled.Faces.Count != 1)
    throw new InvalidOperationException("Expected frame 11 to be filled by track interpolation.");

if (provider.TryGetFaceMaskData(20, out var removed) && removed.Faces.Count > 0)
    throw new InvalidOperationException("Expected isolated low-confidence frame 20 to be removed.");

foreach (int frame in new[] { 5, 13, 18 })
{
    if (provider.TryGetFaceMaskData(frame, out var sparse) && sparse.Faces.Count > 0)
        throw new InvalidOperationException($"Expected sparse low-confidence temporal false-positive frame {frame} to be removed.");
}

if (!provider.TryGetFaceMaskData(10, out var frame10) || frame10.Faces.Count != 1)
    throw new InvalidOperationException("Expected source frame 10 to remain.");

if (!provider.TryGetFaceMaskData(12, out var frame12) || frame12.Faces.Count != 1)
    throw new InvalidOperationException("Expected source frame 12 to remain.");

if (!provider.TryGetFaceMaskData(25, out var partial) || partial.Faces.Count != 1)
    throw new InvalidOperationException("Expected edge partial-face candidate at frame 25 to remain.");

for (int frame = 33; frame <= 35; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var lostFill) || lostFill.Faces.Count != 1)
        throw new InvalidOperationException($"Expected confirmed track lost frame {frame} to be filled.");
}

if (provider.TryGetFaceMaskData(40, out var removedSmallA) && removedSmallA.Faces.Count > 0)
    throw new InvalidOperationException("Expected two-detection small false-positive track frame 40 to be removed.");

if (provider.TryGetFaceMaskData(41, out var revivedSmall) && revivedSmall.Faces.Count > 0)
    throw new InvalidOperationException("Expected removed small false-positive track not to be revived by interpolation.");

if (provider.TryGetFaceMaskData(42, out var removedSmallB) && removedSmallB.Faces.Count > 0)
    throw new InvalidOperationException("Expected two-detection small false-positive track frame 42 to be removed.");

for (int frame = 50; frame <= 52; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var centralPartial) || centralPartial.Faces.Count != 1)
        throw new InvalidOperationException($"Expected confirmed three-detection small central partial-face candidate at frame {frame} to remain.");
}

if (!provider.TryGetFaceMaskData(55, out var largeJumpSource) || largeJumpSource.Faces.Count != 1)
    throw new InvalidOperationException("Expected large-jump source frame 55 to remain.");

for (int frame = 56; frame <= 58; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var largeJumpFill) && largeJumpFill.Faces.Count > 0)
        throw new InvalidOperationException($"Expected large box jump not to be auto-filled at frame {frame}.");
}

if (!provider.TryGetFaceMaskData(59, out var largeJumpTarget) || largeJumpTarget.Faces.Count != 1)
    throw new InvalidOperationException("Expected large-jump target frame 59 to remain.");

if (provider.TryGetFaceMaskData(72, out var edgeTail) && edgeTail.Faces.Count > 0)
    throw new InvalidOperationException("Expected low-confidence edge tail frame 72 to be removed.");

if (provider.TryGetFaceMaskData(73, out var blockedEdgeLostFill) && blockedEdgeLostFill.Faces.Count > 0)
    throw new InvalidOperationException("Expected low-confidence edge track not to extend lost fill past frame 72.");

if (result.RemovedUnstableTailFaces != 1)
    throw new InvalidOperationException($"Expected one unstable low-confidence tail face to be removed, got {result.RemovedUnstableTailFaces}.");

for (int frame = 75; frame <= 77; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var stableBeforeTail) || stableBeforeTail.Faces.Count != 1)
        throw new InvalidOperationException($"Expected stable pre-tail track frame {frame} to remain.");
}

for (int frame = 82; frame <= 84; frame++)
{
    if (!provider.TryGetFaceMaskData(frame, out var initialFill) || initialFill.Faces.Count != 1)
        throw new InvalidOperationException($"Expected confirmed edge-start track initial frame {frame} to be backfilled.");
}

for (int frame = 92; frame <= 94; frame++)
{
    if (provider.TryGetFaceMaskData(frame, out var outwardInitialFill) && outwardInitialFill.Faces.Count > 0)
        throw new InvalidOperationException($"Expected edge-start track moving outward not to backfill frame {frame}.");
}

if (result.BlockedInitialFillTracks != 1)
    throw new InvalidOperationException($"Expected one outward edge-start track to be blocked from initial backfill; got {result.BlockedInitialFillTracks}.");

var sceneGuardProvider = new FrameMaskProvider();
sceneGuardProvider.SetFaceRects(10, new[] { new Rect(100, 100, 80, 80) }, size, 0.82f, new[] { 0.82f });
sceneGuardProvider.SetFaceRects(12, new[] { new Rect(110, 104, 82, 80) }, size, 0.81f, new[] { 0.81f });
sceneGuardProvider.SetFaceRects(20, new[] { new Rect(900, 300, 70, 70) }, size, 0.84f, new[] { 0.84f });
sceneGuardProvider.SetFaceRects(22, new[] { new Rect(908, 304, 70, 70) }, size, 0.83f, new[] { 0.83f });

new FaceTrackInterpolator().Apply(
    sceneGuardProvider,
    totalFrames: 24,
    AutoMaskTemporalPostProcessor.BuildTrackPostProcessOptions(
        FaceFilterProfile.Yolo,
        continuityOnly: true),
    new HashSet<int> { 21 });

if (!sceneGuardProvider.TryGetFaceMaskData(11, out var sameSceneFill) || sameSceneFill.Faces.Count != 1)
    throw new InvalidOperationException("Expected a same-scene one-frame detection gap to be filled.");

if (sceneGuardProvider.TryGetFaceMaskData(21, out var crossCutFill) && crossCutFill.Faces.Count > 0)
    throw new InvalidOperationException("Expected a scene-cut boundary to block synthetic gap filling.");

var faceOnnxContinuityProvider = new FrameMaskProvider();
faceOnnxContinuityProvider.SetFaceRects(2, new[] { new Rect(300, 200, 90, 90) }, size, 0.84f, new[] { 0.84f });
faceOnnxContinuityProvider.SetFaceRects(4, new[] { new Rect(308, 204, 90, 90) }, size, 0.83f, new[] { 0.83f });
faceOnnxContinuityProvider.SetFaceRects(6, new[] { new Rect(800, 400, 90, 90) }, size, 0.84f, new[] { 0.84f });
faceOnnxContinuityProvider.SetFaceRects(8, new[] { new Rect(808, 404, 90, 90) }, size, 0.83f, new[] { 0.83f });

new FaceTrackInterpolator().Apply(
    faceOnnxContinuityProvider,
    totalFrames: 10,
    AutoMaskTemporalPostProcessor.BuildTrackPostProcessOptions(
        FaceFilterProfile.FaceOnnx,
        continuityOnly: true),
    new HashSet<int> { 3 });

if (faceOnnxContinuityProvider.TryGetFaceMaskData(3, out var faceOnnxCrossCut) && faceOnnxCrossCut.Faces.Count > 0)
    throw new InvalidOperationException("Expected FaceONNX continuity to respect a scene-cut boundary.");

if (!faceOnnxContinuityProvider.TryGetFaceMaskData(7, out var faceOnnxSameScene) || faceOnnxSameScene.Faces.Count != 1)
    throw new InvalidOperationException("Expected FaceONNX continuity to fill a same-scene detection gap.");

var resumeProvider = new FrameMaskProvider();
resumeProvider.SetFaceRects(0, new[] { new Rect(100, 100, 80, 80) }, size, 0.84f, new[] { 0.84f });
resumeProvider.SetFaceRects(2, new[] { new Rect(108, 104, 80, 80) }, size, 0.83f, new[] { 0.83f });
resumeProvider.SetFaceRects(4, new[] { new Rect(600, 300, 90, 90) }, size, 0.84f, new[] { 0.84f });
resumeProvider.SetFaceRects(6, new[] { new Rect(608, 304, 90, 90) }, size, 0.83f, new[] { 0.83f });
resumeProvider.SetFaceRects(8, new[] { new Rect(616, 308, 90, 90) }, size, 0.82f, new[] { 0.82f });

new FaceTrackInterpolator().Apply(
    resumeProvider,
    totalFrames: 10,
    AutoMaskTemporalPostProcessor.BuildTrackPostProcessOptions(
        FaceFilterProfile.FaceOnnx,
        continuityOnly: true),
    blockedSceneCutStarts: new HashSet<int> { 5 },
    mutableStartFrameIndex: 5);

if (resumeProvider.TryGetFaceMaskData(1, out var preResumeFill) && preResumeFill.Faces.Count > 0)
    throw new InvalidOperationException("Expected resume processing not to modify frames before the resume boundary.");

if (resumeProvider.TryGetFaceMaskData(5, out var resumeBoundaryFill) && resumeBoundaryFill.Faces.Count > 0)
    throw new InvalidOperationException("Expected resume processing not to interpolate from a pre-resume anchor.");

if (!resumeProvider.TryGetFaceMaskData(7, out var postResumeFill) || postResumeFill.Faces.Count != 1)
    throw new InvalidOperationException("Expected resume processing to fill gaps with post-resume anchors.");

Console.WriteLine(
    $"[FaceTrackPostVerify] tracks={result.TrackCount}, filled={result.FilledGapFaces}, gapFrames={string.Join(",", result.FilledGapFacesInfo.Select(x => x.FrameIndex))}, lostFilled={result.FilledLostFaces}, initialFilled={result.FilledInitialFaces}, outwardInitialFilled=False, blockedInitialFill={result.BlockedInitialFillTracks}, lostFrames={string.Join(",", result.FilledLostFrameIndices)}, removedShort={result.RemovedShortFaces}, removedSparse={result.RemovedSparseFaces}, removedUnstableTail={result.RemovedUnstableTailFaces}, removedEdgeTail={result.RemovedEdgeTailFaces}, removedLower={result.RemovedLowerFrameFaces}, largeJumpFilled=False, sceneGuard=True, faceOnnxContinuity=True, resumeBoundary=True, rewritten={result.RewrittenFrames}, filledFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
