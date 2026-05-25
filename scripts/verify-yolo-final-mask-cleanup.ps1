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

var result = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(provider);

if (result.RemovedWeakIsolatedFaces != 5)
    throw new InvalidOperationException($"Expected 5 weak isolated/short-cluster faces to be removed, got {result.RemovedWeakIsolatedFaces}.");

if (result.RemovedWeakUnsupportedFaces != 3)
    throw new InvalidOperationException($"Expected 3 weak unsupported faces to be removed, got {result.RemovedWeakUnsupportedFaces}.");

if (result.RemovedWeakShortClusterFaces != 2)
    throw new InvalidOperationException($"Expected 2 weak short-cluster faces to be removed, got {result.RemovedWeakShortClusterFaces}.");

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

Console.WriteLine(
    $"[YoloFinalMaskCleanupVerify] removedWeakIsolated={result.RemovedWeakIsolatedFaces}, removedWeakUnsupported={result.RemovedWeakUnsupportedFaces}, removedWeakShortClusters={result.RemovedWeakShortClusterFaces}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, remainingFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
