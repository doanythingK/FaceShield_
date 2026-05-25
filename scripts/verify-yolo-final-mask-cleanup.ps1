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

var result = new YoloFinalMaskPostProcessor().RemoveWeakIsolatedMasks(provider);

if (result.RemovedWeakIsolatedFaces != 2)
    throw new InvalidOperationException($"Expected 2 weak isolated faces to be removed, got {result.RemovedWeakIsolatedFaces}.");

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

Console.WriteLine(
    $"[YoloFinalMaskCleanupVerify] removedWeakIsolated={result.RemovedWeakIsolatedFaces}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, remainingFrames={string.Join(",", provider.GetFaceMaskFrameIndices().OrderBy(x => x))}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
