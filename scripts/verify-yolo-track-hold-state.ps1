param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-track-hold-state"
$project = Join-Path $work "YoloTrackHoldHarness.csproj"
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

provider.SetFaceRects(2, new[] { new Rect(1500, 780, 30, 34) }, size, 0.12f, new[] { 0.12f });

provider.SetFaceRects(10, new[] { new Rect(420, 240, 90, 96) }, size, 0.88f, new[] { 0.88f });
provider.SetFaceRects(11, new[] { new Rect(426, 244, 90, 96) }, size, 0.87f, new[] { 0.87f });
provider.SetFaceRects(12, new[] { new Rect(432, 248, 90, 96) }, size, 0.86f, new[] { 0.86f });
provider.SetFaceRects(20, new[] { new Rect(480, 280, 90, 96) }, size, 0.84f, new[] { 0.84f });

var options = new FaceTrackPostProcessOptions
{
    MaxTrackGap = 8,
    MaxFillGap = 5,
    MaxLostFillFrames = 6,
    MaxConfirmedTrackHoldFrames = 8,
    AllowSmallTrackLostFill = true,
    WeakConfidence = 0.38f,
    StrongConfidence = 0.58f,
    DropShortTrackMaxDetections = 1,
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
};

var result = new FaceTrackInterpolator().Apply(provider, totalFrames: 28, options);
var expectedInternalHoldFrames = Enumerable.Range(13, 7).ToArray();
var gapFrames = result.FilledGapFacesInfo.Select(x => x.FrameIndex).ToArray();
if (!gapFrames.SequenceEqual(expectedInternalHoldFrames))
    throw new InvalidOperationException($"Expected YOLO confirmed-track gap hold frames {string.Join(",", expectedInternalHoldFrames)}, got {string.Join(",", gapFrames)}.");

foreach (int frame in expectedInternalHoldFrames)
{
    if (!provider.TryGetFaceMaskData(frame, out var held) || held.Faces.Count != 1)
        throw new InvalidOperationException($"Expected confirmed YOLO track to hold internal gap frame {frame}.");
}

var expectedHoldFrames = Enumerable.Range(21, 6).ToArray();
var lostFrames = result.FilledLostFrameIndices.ToArray();

if (!lostFrames.SequenceEqual(expectedHoldFrames))
    throw new InvalidOperationException($"Expected YOLO lost-fill frames {string.Join(",", expectedHoldFrames)}, got {string.Join(",", lostFrames)}.");

foreach (int frame in expectedHoldFrames)
{
    if (!provider.TryGetFaceMaskData(frame, out var held) || held.Faces.Count != 1)
        throw new InvalidOperationException($"Expected confirmed YOLO track to hold frame {frame}.");
}

if (provider.TryGetFaceMaskData(27, out var overHeld) && overHeld.Faces.Count > 0)
    throw new InvalidOperationException("Expected YOLO track hold to stop after MaxLostFillFrames=6.");

if (provider.TryGetFaceMaskData(2, out var removedWeak) && removedWeak.Faces.Count > 0)
    throw new InvalidOperationException("Expected one-frame weak YOLO candidate not to be held as a confirmed track.");

Console.WriteLine(
    $"[YoloTrackHoldVerify] tracks={result.TrackCount}, gapHeld={result.FilledGapFaces}, gapFrames={string.Join(",", gapFrames)}, lostFilled={result.FilledLostFaces}, lostFrames={string.Join(",", lostFrames)}, removedShort={result.RemovedShortFaces}, removedSparse={result.RemovedSparseFaces}, removedEdgeTail={result.RemovedEdgeTailFaces}, heldFrames={string.Join(",", expectedHoldFrames)}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
