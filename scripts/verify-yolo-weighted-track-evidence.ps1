param()

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-weighted-track-evidence"
$project = Join-Path $work "WeightedEvidenceHarness.csproj"
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
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;

var size = new PixelSize(1280, 720);
var options = AutoMaskTemporalPostProcessor.BuildTrackPostProcessOptions(
    FaceFilterProfile.Yolo,
    continuityOnly: true);

if (!options.EnableWeightedTrackEvidence ||
    options.MinConfirmedTrackEvidenceScore <= 0 ||
    options.LowEvidenceRejectScore <= 0 ||
    options.MaxStrongTrackLostFillFrames <= options.MaxLostFillFrames)
{
    throw new InvalidOperationException("YOLO weighted evidence policy is not enabled.");
}

// Weak, geometrically jittery central candidates should not be admitted as a track.
using (var provider = new FrameMaskProvider())
{
    provider.SetFaceRects(10, new[] { new Rect(100, 180, 80, 80) }, size, 0.55f, new[] { 0.55f });
    provider.SetFaceRects(11, new[] { new Rect(156, 180, 80, 80) }, size, 0.55f, new[] { 0.55f });
    provider.SetFaceRects(12, new[] { new Rect(212, 180, 80, 80) }, size, 0.55f, new[] { 0.55f });
    new FaceTrackInterpolator().Apply(provider, 30, options);
    for (int frame = 10; frame <= 12; frame++)
    {
        if (provider.TryGetFaceMaskData(frame, out var data) && data.Faces.Count > 0)
            throw new InvalidOperationException($"Expected low-evidence jitter track frame {frame} to be removed.");
    }
}

// The same confidence with stable temporal geometry must remain.
using (var provider = new FrameMaskProvider())
{
    provider.SetFaceRects(10, new[] { new Rect(300, 180, 80, 80) }, size, 0.55f, new[] { 0.55f });
    provider.SetFaceRects(11, new[] { new Rect(304, 181, 80, 80) }, size, 0.55f, new[] { 0.55f });
    provider.SetFaceRects(12, new[] { new Rect(308, 182, 80, 80) }, size, 0.55f, new[] { 0.55f });
    new FaceTrackInterpolator().Apply(provider, 30, options);
    for (int frame = 10; frame <= 12; frame++)
    {
        if (!provider.TryGetFaceMaskData(frame, out var data) || data.Faces.Count != 1)
            throw new InvalidOperationException($"Expected stable moderate-confidence face frame {frame} to remain.");
    }
}

// A high-confidence one-frame candidate is retained for privacy; weighted cleanup must not overreach.
using (var provider = new FrameMaskProvider())
{
    provider.SetFaceRects(8, new[] { new Rect(500, 220, 92, 92) }, size, 0.90f, new[] { 0.90f });
    new FaceTrackInterpolator().Apply(provider, 20, options);
    if (!provider.TryGetFaceMaskData(8, out var data) || data.Faces.Count != 1)
        throw new InvalidOperationException("Expected high-confidence short candidate to remain.");
}

// Edge/partial candidates remain privacy-protected even when weak and jittery.
using (var provider = new FrameMaskProvider())
{
    provider.SetFaceRects(5, new[] { new Rect(0, 240, 64, 88) }, size, 0.52f, new[] { 0.52f });
    provider.SetFaceRects(6, new[] { new Rect(18, 242, 64, 88) }, size, 0.53f, new[] { 0.53f });
    provider.SetFaceRects(7, new[] { new Rect(36, 244, 64, 88) }, size, 0.54f, new[] { 0.54f });
    new FaceTrackInterpolator().Apply(provider, 20, options);
    if (!provider.TryGetFaceMaskData(5, out var data) || data.Faces.Count != 1)
        throw new InvalidOperationException("Expected edge partial candidate to remain.");
}

// Strong stable confirmed tracks receive the longer hysteresis hold.
using (var provider = new FrameMaskProvider())
{
    for (int frame = 10; frame <= 14; frame++)
        provider.SetFaceRects(frame, new[] { new Rect(620 + (frame - 10) * 2, 260, 96, 96) }, size, 0.86f, new[] { 0.86f });
    new FaceTrackInterpolator().Apply(provider, 40, options);
    int lastExpected = 14 + options.MaxStrongTrackLostFillFrames;
    if (!provider.TryGetFaceMaskData(lastExpected, out var held) || held.Faces.Count != 1)
        throw new InvalidOperationException($"Expected strong track hold through frame {lastExpected}.");
}

// Moderate confirmed tracks keep the base hold only, avoiding unnecessary long ghost blur.
using (var provider = new FrameMaskProvider())
{
    for (int frame = 10; frame <= 12; frame++)
        provider.SetFaceRects(frame, new[] { new Rect(760 + (frame - 10) * 2, 300, 96, 96) }, size, 0.64f, new[] { 0.64f });
    new FaceTrackInterpolator().Apply(provider, 40, options);
    int baseLast = 12 + options.MaxLostFillFrames;
    if (!provider.TryGetFaceMaskData(baseLast, out var baseHeld) || baseHeld.Faces.Count != 1)
        throw new InvalidOperationException($"Expected moderate track base hold through frame {baseLast}.");
    int beyondBase = baseLast + 1;
    if (provider.TryGetFaceMaskData(beyondBase, out var tooLong) && tooLong.Faces.Count > 0)
        throw new InvalidOperationException($"Expected moderate track to release after base hold at frame {beyondBase}.");
}

Console.WriteLine("[YoloWeightedTrackEvidence] PASS");
'@ | Set-Content -Encoding UTF8 $program

try {
    dotnet run --project $project -c Release -p:UseAppHost=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "weighted evidence harness failed: $LASTEXITCODE" }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
