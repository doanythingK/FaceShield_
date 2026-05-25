param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-temporal-smoothing-cut-boundary"
$project = Join-Path $work "YoloTemporalSmoothingCutBoundaryHarness.csproj"
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
using System.Reflection;
using Avalonia;
using FaceShield.ViewModels.Pages;

var viewModelType = typeof(WorkspaceViewModel);
var buildCutStarts = viewModelType.GetMethod("BuildTemporalSmoothingCutStarts", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("BuildTemporalSmoothingCutStarts not found.");
var isBlockedStep = viewModelType.GetMethod("IsBlockedTemporalSmoothingStep", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("IsBlockedTemporalSmoothingStep not found.");
var findNearest = viewModelType.GetMethod("FindNearestTemporalFaces", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("FindNearestTemporalFaces not found.");

var blocked = (IReadOnlySet<int>)buildCutStarts.Invoke(null, new object?[] { new[] { "10->13", "30->31", "bad", "40->x" } })!;
AssertSet(blocked, new[] { 10, 11, 12, 30 }, new[] { 9, 13, 29, 31, 40 }, "direct cut pair should block every internal frame boundary");

bool IsBlocked(int fromFrame, int toFrame)
{
    return (bool)isBlockedStep.Invoke(null, new object?[] { fromFrame, toFrame, blocked })!;
}

if (!IsBlocked(13, 12) || !IsBlocked(12, 13))
    throw new InvalidOperationException("Expected both scan directions to stop at the 12/13 cut boundary.");
if (IsBlocked(14, 13))
    throw new InvalidOperationException("Expected same-scene scan after the cut to remain allowed.");
if (!IsBlocked(31, 30))
    throw new InvalidOperationException("Expected adjacent cut pair 30->31 to block 30/31.");
if (IsBlocked(30, 29))
    throw new InvalidOperationException("Expected frames before the adjacent cut to remain searchable.");

var facesByFrame = new IReadOnlyList<Rect>?[20];
facesByFrame[11] = new List<Rect> { new Rect(100, 100, 40, 40) };
facesByFrame[14] = new List<Rect> { new Rect(104, 102, 40, 40) };

var blockedPrev = InvokeFindNearest(facesByFrame, 13, -1, 2, blocked);
if (blockedPrev != null)
    throw new InvalidOperationException("Expected previous search from frame 13 to stop at cut boundary before reaching frame 11.");

var sameSideNext = InvokeFindNearest(facesByFrame, 13, 1, 2, blocked);
if (sameSideNext == null || sameSideNext.Count != 1)
    throw new InvalidOperationException("Expected next search from frame 13 to find same-scene frame 14.");

var blockedNext = InvokeFindNearest(facesByFrame, 12, 1, 2, blocked);
if (blockedNext != null)
    throw new InvalidOperationException("Expected next search from frame 12 to stop at cut boundary before reaching frame 14.");

var unblocked = (IReadOnlySet<int>)buildCutStarts.Invoke(null, new object?[] { Array.Empty<string>() })!;
var unblockedPrev = InvokeFindNearest(facesByFrame, 13, -1, 2, unblocked);
if (unblockedPrev == null || unblockedPrev.Count != 1)
    throw new InvalidOperationException("Expected previous search to find frame 11 when no cut boundary is provided.");

Console.WriteLine(
    $"[YoloTemporalSmoothingCutBoundaryVerify] blocked={string.Join(",", blocked.OrderBy(x => x))}, prevBlocked=True, nextSameScene=True, nextBlocked=True, malformedIgnored=True");

IReadOnlyList<Rect>? InvokeFindNearest(
    IReadOnlyList<Rect>?[] faces,
    int frameIndex,
    int direction,
    int maxDistanceFrames,
    IReadOnlySet<int> cutStarts)
{
    return (IReadOnlyList<Rect>?)findNearest.Invoke(null, new object?[] { faces, frameIndex, direction, maxDistanceFrames, cutStarts });
}

static void AssertSet(IReadOnlySet<int> values, IReadOnlyList<int> expectedPresent, IReadOnlyList<int> expectedMissing, string message)
{
    foreach (int expected in expectedPresent)
    {
        if (!values.Contains(expected))
            throw new InvalidOperationException($"{message}: missing {expected}.");
    }

    foreach (int unexpected in expectedMissing)
    {
        if (values.Contains(unexpected))
            throw new InvalidOperationException($"{message}: unexpected {unexpected}.");
    }
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
