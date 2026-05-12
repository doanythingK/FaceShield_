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
provider.SetFaceRects(40, new[] { new Rect(820, 410, 34, 42) }, size, 0.94f, new[] { 0.94f });
provider.SetFaceRects(42, new[] { new Rect(825, 413, 34, 42) }, size, 0.93f, new[] { 0.93f });
provider.SetFaceRects(50, new[] { new Rect(1120, 260, 33, 44) }, size, 0.95f, new[] { 0.95f });
provider.SetFaceRects(51, new[] { new Rect(1123, 262, 33, 44) }, size, 0.94f, new[] { 0.94f });
provider.SetFaceRects(52, new[] { new Rect(1126, 264, 33, 44) }, size, 0.93f, new[] { 0.93f });

var result = new FaceTrackInterpolator().Apply(
    provider,
    totalFrames: 60,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 8,
        MaxFillGap = 5,
        DropShortTrackMaxDetections = 1,
        WeakConfidence = 0.50f,
        StrongConfidence = 0.68f,
        ShortTrackMaxConfidence = 0.68f,
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
        throw new InvalidOperationException($"Expected three-detection central partial-face candidate at frame {frame} to remain.");
}

Console.WriteLine(
    $"[FaceTrackPostVerify] tracks={result.TrackCount}, filled={result.FilledGapFaces}, gapFrames={string.Join(",", result.FilledGapFacesInfo.Select(x => x.FrameIndex))}, lostFilled={result.FilledLostFaces}, lostFrames={string.Join(",", result.FilledLostFrameIndices)}, removedShort={result.RemovedShortFaces}, rewritten={result.RewrittenFrames}, filledFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
