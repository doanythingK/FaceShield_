param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\face-track-scene-cut-guard"
$project = Join-Path $work "FaceTrackSceneCutGuardHarness.csproj"
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

var provider = new FrameMaskProvider();
var size = new PixelSize(1280, 720);
var ghost = new Rect(250, 120, 90, 92);
var sameScene = new Rect(520, 180, 86, 88);

provider.SetFaceRects(2, new[] { ghost }, size, 0.38f, new[] { 0.38f });
provider.SetFaceRects(3, new[] { sameScene }, size, 0.40f, new[] { 0.40f });

var candidates = new[]
{
    new FaceTrackFilledFace(2, ghost, size, 0.38f, 0),
    new FaceTrackFilledFace(3, sameScene, size, 0.40f, 2)
};

var result = new FaceTrackSceneCutGuard().Apply(
    provider,
    candidates,
    static (source, target) => source == 1 && target == 2 ? 0.48 : 0.10);

if (result.Checked != 2)
    throw new InvalidOperationException($"Expected checked=2, got {result.Checked}.");

if (result.Removed != 1)
    throw new InvalidOperationException($"Expected removed=1, got {result.Removed}.");

if (string.Join(",", result.CheckedFramePairs) != "0->2,2->3")
    throw new InvalidOperationException($"Unexpected checked frame pairs: {string.Join(",", result.CheckedFramePairs)}.");

if (Math.Abs(result.MaxDifference - 0.48) > 0.001)
    throw new InvalidOperationException($"Expected max difference 0.48, got {result.MaxDifference:0.000}.");

if (string.Join(",", result.CutFramePairs) != "1->2")
    throw new InvalidOperationException($"Unexpected cut frame pairs: {string.Join(",", result.CutFramePairs)}.");

if (string.Join(",", result.RemovedFrameIndices) != "2")
    throw new InvalidOperationException($"Unexpected removed frame indices: {string.Join(",", result.RemovedFrameIndices)}.");

if (provider.TryGetFaceMaskData(2, out var cutFrame) && cutFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected hard-cut track-fill candidate at frame 2 to be removed.");

if (!provider.TryGetFaceMaskData(3, out var sameFrame) || sameFrame.Faces.Count != 1)
    throw new InvalidOperationException("Expected same-scene track-fill candidate at frame 3 to remain.");

var reverseProvider = new FrameMaskProvider();
var reverseInitial = new Rect(1040, 0, 120, 110);
reverseProvider.SetFaceRects(1, new[] { reverseInitial }, size, 0.90f, new[] { 0.90f });

var reverseCandidates = new[]
{
    new FaceTrackFilledFace(1, reverseInitial, size, 0.90f, 3)
};

var reverseResult = new FaceTrackSceneCutGuard().Apply(
    reverseProvider,
    reverseCandidates,
    static (source, target) => source == 1 && target == 2 ? 0.49 : 0.08);

if (reverseResult.Checked != 1)
    throw new InvalidOperationException($"Expected reverse checked=1, got {reverseResult.Checked}.");

if (string.Join(",", reverseResult.CheckedFramePairs) != "1->3")
    throw new InvalidOperationException($"Unexpected reverse checked frame pairs: {string.Join(",", reverseResult.CheckedFramePairs)}.");

if (string.Join(",", reverseResult.CutFramePairs) != "1->2")
    throw new InvalidOperationException($"Unexpected reverse cut frame pairs: {string.Join(",", reverseResult.CutFramePairs)}.");

if (reverseResult.Removed != 1 || string.Join(",", reverseResult.RemovedFrameIndices) != "1")
    throw new InvalidOperationException($"Expected reverse initial fill at frame 1 to be removed, got removed={reverseResult.Removed}, frames={string.Join(",", reverseResult.RemovedFrameIndices)}.");

if (reverseProvider.TryGetFaceMaskData(1, out var reverseFrame) && reverseFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected reverse initial-fill candidate before a hard cut to be removed.");

var directProvider = new FrameMaskProvider();
var directPrevious = new Rect(300, 220, 80, 82);
var directGhost = new Rect(306, 224, 82, 84);
var directGhostTailA = new Rect(308, 225, 82, 84);
var directGhostTailB = new Rect(309, 226, 82, 84);
directProvider.SetFaceRects(10, new[] { directPrevious }, size, 0.86f, new[] { 0.86f });
directProvider.SetFaceRects(11, new[] { directGhost }, size, 0.42f, new[] { 0.42f });
directProvider.SetFaceRects(12, new[] { directGhostTailA }, size, 0.43f, new[] { 0.43f });
directProvider.SetFaceRects(13, new[] { directGhostTailB }, size, 0.44f, new[] { 0.44f });

var guard = new FaceTrackSceneCutGuard();
var directCandidates = guard.BuildWeakTrackTransitionCandidates(
    directProvider,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 3,
        MaxFillGap = 3,
        WeakConfidence = 0.38f,
        StrongConfidence = 0.58f,
        MinTrackIou = 0.08,
        MaxCenterShiftRatio = 0.72,
        MaxAreaChangeRatio = 4.0,
        DuplicateIou = 0.35
    },
    maxTargetConfidence: 0.60f,
    maxTransitionGap: 3);

var directResult = guard.Apply(
    directProvider,
    directCandidates,
    static (source, target) => source == 10 && target >= 11 && target <= 13 ? 0.50 : 0.0);

if (directCandidates.Count != 3)
    throw new InvalidOperationException($"Expected three weak direct transition carry candidates, got {directCandidates.Count}.");

if (directResult.Removed != 3)
    throw new InvalidOperationException($"Expected three weak direct transition carry detections to be removed, got {directResult.Removed}.");

if (directProvider.TryGetFaceMaskData(11, out var directFrame) && directFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected weak direct detection after a hard cut to be removed.");
if (directProvider.TryGetFaceMaskData(12, out var directTailFrameA) && directTailFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected weak direct carry detection after a hard cut to be removed.");
if (directProvider.TryGetFaceMaskData(13, out var directTailFrameB) && directTailFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected weak direct carry tail detection after a hard cut to be removed.");

var adjacentCutDirectProvider = new FrameMaskProvider();
var adjacentCutSource = new Rect(410, 280, 84, 86);
var adjacentCutGhost = new Rect(414, 284, 84, 86);
adjacentCutDirectProvider.SetFaceRects(400, new[] { adjacentCutSource }, size, 0.91f, new[] { 0.91f });
adjacentCutDirectProvider.SetFaceRects(402, new[] { adjacentCutGhost }, size, 0.49f, new[] { 0.49f });
var adjacentCutDirectResult = guard.Apply(
    adjacentCutDirectProvider,
    new[] { new FaceTrackFilledFace(402, adjacentCutGhost, size, 0.49f, 400) },
    static (source, target) =>
    {
        if (source == 401 && target == 402)
            return 0.50;
        if (source == 400 && target == 402)
            return 0.70;
        return 0.10;
    },
    directDifferenceThreshold: 0.36,
    directDifferenceMaxChecks: 1);

if (adjacentCutDirectResult.Removed != 1 || string.Join(",", adjacentCutDirectResult.CutFramePairs) != "401->402")
    throw new InvalidOperationException($"Expected adjacent cut to remove without direct check, got removed={adjacentCutDirectResult.Removed}, cutPairs={string.Join(",", adjacentCutDirectResult.CutFramePairs)}.");
if (adjacentCutDirectResult.DirectDifferenceChecks != 0 || adjacentCutDirectResult.DirectDifferenceSkipped != 0)
    throw new InvalidOperationException($"Expected adjacent cut not to spend direct budget, got checked={adjacentCutDirectResult.DirectDifferenceChecks}, skipped={adjacentCutDirectResult.DirectDifferenceSkipped}.");

var directBudgetProvider = new FrameMaskProvider();
var directBudgetSource = new Rect(380, 260, 84, 86);
var directBudgetGhostA = new Rect(384, 264, 84, 86);
var directBudgetGhostB = new Rect(388, 266, 84, 86);
var directBudgetGhostC = new Rect(392, 268, 84, 86);
directBudgetProvider.SetFaceRects(300, new[] { directBudgetSource }, size, 0.92f, new[] { 0.92f });
directBudgetProvider.SetFaceRects(302, new[] { directBudgetGhostA }, size, 0.51f, new[] { 0.51f });
directBudgetProvider.SetFaceRects(303, new[] { directBudgetGhostB }, size, 0.52f, new[] { 0.52f });
directBudgetProvider.SetFaceRects(304, new[] { directBudgetGhostC }, size, 0.53f, new[] { 0.53f });
var directBudgetCandidates = new[]
{
    new FaceTrackFilledFace(302, directBudgetGhostA, size, 0.51f, 300),
    new FaceTrackFilledFace(303, directBudgetGhostB, size, 0.52f, 300),
    new FaceTrackFilledFace(304, directBudgetGhostC, size, 0.53f, 300)
};

var directBudgetResult = guard.Apply(
    directBudgetProvider,
    directBudgetCandidates,
    static (source, target) =>
    {
        if (source == 300 && target >= 302 && target <= 304)
            return 0.50;
        return 0.10;
    },
    directDifferenceThreshold: 0.36,
    directDifferenceMaxChecks: 1);

if (directBudgetResult.DirectDifferenceChecks != 1 || directBudgetResult.DirectDifferenceSkipped != 2)
    throw new InvalidOperationException($"Expected direct budget checked=1 skipped=2, got checked={directBudgetResult.DirectDifferenceChecks}, skipped={directBudgetResult.DirectDifferenceSkipped}.");
if (directBudgetResult.Removed != 1 || string.Join(",", directBudgetResult.RemovedFrameIndices) != "302")
    throw new InvalidOperationException($"Expected direct budget to remove only first checked frame 302, got removed={directBudgetResult.Removed}, frames={string.Join(",", directBudgetResult.RemovedFrameIndices)}.");
if (directBudgetProvider.TryGetFaceMaskData(302, out var directBudgetRemovedFrame) && directBudgetRemovedFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected direct budget checked frame 302 to be removed.");
if (!directBudgetProvider.TryGetFaceMaskData(303, out var directBudgetSkippedFrameA) || directBudgetSkippedFrameA.Faces.Count != 1)
    throw new InvalidOperationException("Expected direct budget skipped frame 303 to remain.");
if (!directBudgetProvider.TryGetFaceMaskData(304, out var directBudgetSkippedFrameB) || directBudgetSkippedFrameB.Faces.Count != 1)
    throw new InvalidOperationException("Expected direct budget skipped frame 304 to remain.");

var directReuseProvider = new FrameMaskProvider();
var directReuseSource = new Rect(460, 260, 82, 84);
var directReuseGhostA = new Rect(466, 264, 82, 84);
var directReuseGhostB = new Rect(560, 264, 82, 84);
directReuseProvider.SetFaceRects(320, new[] { directReuseSource }, size, 0.91f, new[] { 0.91f });
directReuseProvider.SetFaceRects(323, new[] { directReuseGhostA, directReuseGhostB }, size, 0.49f, new[] { 0.49f, 0.48f });
var directReuseResult = guard.Apply(
    directReuseProvider,
    new[]
    {
        new FaceTrackFilledFace(323, directReuseGhostA, size, 0.49f, 320),
        new FaceTrackFilledFace(323, directReuseGhostB, size, 0.48f, 320)
    },
    static (source, target) => source == 320 && target == 323 ? 0.50 : 0.10,
    directDifferenceThreshold: 0.36,
    directDifferenceMaxChecks: 1);

if (directReuseResult.DirectDifferenceChecks != 1 || directReuseResult.DirectDifferenceSkipped != 0)
    throw new InvalidOperationException($"Expected cached direct pair reuse to spend one check and skip none, got checked={directReuseResult.DirectDifferenceChecks}, skipped={directReuseResult.DirectDifferenceSkipped}.");
if (directReuseResult.Removed != 2)
    throw new InvalidOperationException($"Expected cached direct pair reuse to remove both target boxes, got removed={directReuseResult.Removed}.");
if (directReuseProvider.TryGetFaceMaskData(323, out var directReuseFrame) && directReuseFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected cached direct pair reuse to remove both frame 323 boxes.");

var weakSourceDirectProvider = new FrameMaskProvider();
var weakSourcePrevious = new Rect(430, 180, 78, 80);
var weakSourceCurrent = new Rect(436, 184, 78, 80);
weakSourceDirectProvider.SetFaceRects(150, new[] { weakSourcePrevious }, size, 0.52f, new[] { 0.52f });
weakSourceDirectProvider.SetFaceRects(151, new[] { weakSourceCurrent }, size, 0.49f, new[] { 0.49f });
var weakSourceDirectCandidates = guard.BuildWeakTrackTransitionCandidates(
    weakSourceDirectProvider,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 3,
        MaxFillGap = 3,
        WeakConfidence = 0.38f,
        StrongConfidence = 0.58f,
        MinTrackIou = 0.08,
        MaxCenterShiftRatio = 0.72,
        MaxAreaChangeRatio = 4.0,
        DuplicateIou = 0.35
    },
    maxTargetConfidence: 0.90f,
    maxTransitionGap: 3,
    minConfidenceDrop: 0.02f,
    maxPostCutCarryFrames: 5,
    minSourceConfidence: 0.58f);

if (weakSourceDirectCandidates.Count != 0)
    throw new InvalidOperationException($"Expected weak-source direct transition to stay out of scene-cut candidates, got {weakSourceDirectCandidates.Count}.");

var mediumDirectProvider = new FrameMaskProvider();
var mediumDirectPrevious = new Rect(560, 210, 92, 94);
var mediumDirectGhostA = new Rect(566, 214, 92, 94);
var mediumDirectGhostB = new Rect(570, 216, 92, 94);
var mediumDirectGhostC = new Rect(574, 218, 92, 94);
mediumDirectProvider.SetFaceRects(200, new[] { mediumDirectPrevious }, size, 0.96f, new[] { 0.96f });
mediumDirectProvider.SetFaceRects(201, new[] { mediumDirectGhostA }, size, 0.86f, new[] { 0.86f });
mediumDirectProvider.SetFaceRects(202, new[] { mediumDirectGhostB }, size, 0.87f, new[] { 0.87f });
mediumDirectProvider.SetFaceRects(203, new[] { mediumDirectGhostC }, size, 0.89f, new[] { 0.89f });

var mediumDirectCandidates = guard.BuildWeakTrackTransitionCandidates(
    mediumDirectProvider,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 3,
        MaxFillGap = 3,
        WeakConfidence = 0.38f,
        StrongConfidence = 0.58f,
        MinTrackIou = 0.08,
        MaxCenterShiftRatio = 0.72,
        MaxAreaChangeRatio = 4.0,
        DuplicateIou = 0.35
    },
    maxTargetConfidence: 0.90f,
    maxTransitionGap: 3,
    minConfidenceDrop: 0.06f,
    maxPostCutCarryFrames: 5);

var mediumDirectResult = guard.Apply(
    mediumDirectProvider,
    mediumDirectCandidates,
    static (source, target) => source == 200 && target >= 201 && target <= 203 ? 0.50 : 0.0);

if (mediumDirectCandidates.Count != 3)
    throw new InvalidOperationException($"Expected three medium-confidence direct transition carry candidates, got {mediumDirectCandidates.Count}.");
if (mediumDirectResult.Removed != 3 || string.Join(",", mediumDirectResult.RemovedFrameIndices) != "201,202,203")
    throw new InvalidOperationException($"Expected medium-confidence direct transition carry to be removed from frames 201-203, got removed={mediumDirectResult.Removed}, frames={string.Join(",", mediumDirectResult.RemovedFrameIndices)}.");
if (mediumDirectProvider.TryGetFaceMaskData(201, out var mediumDirectFrameA) && mediumDirectFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected medium-confidence direct carry frame 201 after a hard cut to be removed.");
if (mediumDirectProvider.TryGetFaceMaskData(202, out var mediumDirectFrameB) && mediumDirectFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected medium-confidence direct carry frame 202 after a hard cut to be removed.");
if (mediumDirectProvider.TryGetFaceMaskData(203, out var mediumDirectFrameC) && mediumDirectFrameC.Faces.Count != 0)
    throw new InvalidOperationException("Expected medium-confidence direct carry frame 203 after a hard cut to be removed.");

var noDropDirectProvider = new FrameMaskProvider();
var noDropDirectPrevious = new Rect(610, 240, 88, 90);
var noDropDirectGhostA = new Rect(614, 242, 88, 90);
var noDropDirectGhostB = new Rect(618, 244, 88, 90);
noDropDirectProvider.SetFaceRects(220, new[] { noDropDirectPrevious }, size, 0.88f, new[] { 0.88f });
noDropDirectProvider.SetFaceRects(221, new[] { noDropDirectGhostA }, size, 0.93f, new[] { 0.93f });
noDropDirectProvider.SetFaceRects(222, new[] { noDropDirectGhostB }, size, 0.92f, new[] { 0.92f });
var noDropDirectCandidates = guard.BuildWeakTrackTransitionCandidates(
    noDropDirectProvider,
    new FaceTrackPostProcessOptions
    {
        MaxTrackGap = 3,
        MaxFillGap = 3,
        WeakConfidence = 0.38f,
        StrongConfidence = 0.58f,
        MinTrackIou = 0.08,
        MaxCenterShiftRatio = 0.72,
        MaxAreaChangeRatio = 4.0,
        DuplicateIou = 0.35
    },
    maxTargetConfidence: 0.95f,
    maxTransitionGap: 3,
    minConfidenceDrop: 0.0f,
    maxPostCutCarryFrames: 5,
    minSourceConfidence: 0.58f);
var noDropDirectResult = guard.Apply(
    noDropDirectProvider,
    noDropDirectCandidates,
    static (source, target) => source == 220 && target >= 221 && target <= 222 ? 0.50 : 0.0);

if (noDropDirectCandidates.Count != 2)
    throw new InvalidOperationException($"Expected two no-drop direct transition carry candidates, got {noDropDirectCandidates.Count}.");
if (noDropDirectResult.Removed != 2 || string.Join(",", noDropDirectResult.RemovedFrameIndices) != "221,222")
    throw new InvalidOperationException($"Expected no-drop direct transition carry to be removed from frames 221-222, got removed={noDropDirectResult.Removed}, frames={string.Join(",", noDropDirectResult.RemovedFrameIndices)}.");
if (noDropDirectProvider.TryGetFaceMaskData(221, out var noDropDirectFrameA) && noDropDirectFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected no-drop direct carry frame 221 after a hard cut to be removed.");
if (noDropDirectProvider.TryGetFaceMaskData(222, out var noDropDirectFrameB) && noDropDirectFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected no-drop direct carry frame 222 after a hard cut to be removed.");

var weakTailProvider = new FrameMaskProvider();
var weakTailSource = new Rect(340, 240, 80, 82);
var weakTailFirst = new Rect(346, 244, 82, 84);
var weakTailSecond = new Rect(350, 246, 82, 84);
var weakTailThird = new Rect(354, 248, 82, 84);
var weakTailStrong = new Rect(358, 250, 82, 84);
weakTailProvider.SetFaceRects(70, new[] { weakTailSource }, size, 0.86f, new[] { 0.86f });
weakTailProvider.SetFaceRects(71, new[] { weakTailFirst }, size, 0.46f, new[] { 0.46f });
weakTailProvider.SetFaceRects(72, new[] { weakTailSecond }, size, 0.47f, new[] { 0.47f });
weakTailProvider.SetFaceRects(73, new[] { weakTailThird }, size, 0.48f, new[] { 0.48f });
weakTailProvider.SetFaceRects(74, new[] { weakTailStrong }, size, 0.91f, new[] { 0.91f });
var weakTailResult = guard.Apply(
    weakTailProvider,
    new[] { new FaceTrackFilledFace(71, weakTailFirst, size, 0.46f, 70) },
    static (source, target) => source == 70 && target == 71 ? 0.50 : 0.0,
    removeMatchingTailFrames: 5,
    removeMatchingTailMaxConfidence: 0.78f);

if (weakTailResult.Removed != 3 || string.Join(",", weakTailResult.RemovedFrameIndices) != "71,72,73")
    throw new InvalidOperationException($"Expected cut removal to also remove weak matching tail frames 72-73, got removed={weakTailResult.Removed}, frames={string.Join(",", weakTailResult.RemovedFrameIndices)}.");
if (weakTailProvider.TryGetFaceMaskData(71, out var weakTailFrameA) && weakTailFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected first weak tail frame after a cut to be removed.");
if (weakTailProvider.TryGetFaceMaskData(72, out var weakTailFrameB) && weakTailFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected matching weak tail frame after a cut to be removed.");
if (weakTailProvider.TryGetFaceMaskData(73, out var weakTailFrameC) && weakTailFrameC.Faces.Count != 0)
    throw new InvalidOperationException("Expected second matching weak tail frame after a cut to be removed.");
if (!weakTailProvider.TryGetFaceMaskData(74, out var weakTailStrongFrame) || weakTailStrongFrame.Faces.Count != 1)
    throw new InvalidOperationException("Expected strong matching detection after the weak tail to remain.");

var highTailProvider = new FrameMaskProvider();
var highTailSource = new Rect(240, 220, 82, 84);
var highTailFirst = new Rect(246, 224, 82, 84);
var highTailSecond = new Rect(250, 226, 82, 84);
highTailProvider.SetFaceRects(150, new[] { highTailSource }, size, 0.90f, new[] { 0.90f });
highTailProvider.SetFaceRects(151, new[] { highTailFirst }, size, 0.82f, new[] { 0.82f });
highTailProvider.SetFaceRects(152, new[] { highTailSecond }, size, 0.93f, new[] { 0.93f });
var highTailResult = guard.Apply(
    highTailProvider,
    new[] { new FaceTrackFilledFace(151, highTailFirst, size, 0.82f, 150) },
    static (source, target) => source == 150 && target == 151 ? 0.50 : 0.0,
    removeMatchingTailFrames: 3,
    removeMatchingTailMaxConfidence: 0.95f);

if (highTailResult.Removed != 2 || string.Join(",", highTailResult.RemovedFrameIndices) != "151,152")
    throw new InvalidOperationException($"Expected high-confidence carried tail to be removed after a confirmed cut, got removed={highTailResult.Removed}, frames={string.Join(",", highTailResult.RemovedFrameIndices)}.");
if (highTailProvider.TryGetFaceMaskData(152, out var highTailFrame) && highTailFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected high-confidence matching tail after a cut to be removed.");

var smoothedProvider = new FrameMaskProvider();
var smoothedCandidate = new Rect(460, 210, 90, 92);
var smoothedMask = new Rect(472, 218, 90, 92);
smoothedProvider.SetFaceRects(181, new[] { smoothedMask }, size, 0.60f, new[] { 0.60f });
var smoothedResult = guard.Apply(
    smoothedProvider,
    new[] { new FaceTrackFilledFace(181, smoothedCandidate, size, 0.60f, 180) },
    static (source, target) => source == 180 && target == 181 ? 0.50 : 0.0,
    candidateMatchMinIou: 0.55,
    candidateMatchMaxCenterShiftRatio: 0.45,
    candidateMatchMaxAreaChangeRatio: 2.0);

if (smoothedResult.Removed != 1)
    throw new InvalidOperationException($"Expected smoothed post-cut candidate to be removed by relaxed scene-cut matching, got {smoothedResult.Removed}.");
if (smoothedProvider.TryGetFaceMaskData(181, out var smoothedFrame) && smoothedFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected shifted smoothed post-cut candidate to be removed.");

var postCutProvider = new FrameMaskProvider();
var postCutPrevious = new Rect(718, 258, 84, 86);
var postCutGhostA = new Rect(720, 260, 84, 86);
var postCutGhostB = new Rect(724, 262, 84, 86);
var postCutPersistentA = new Rect(420, 300, 70, 72);
var postCutContinuousA = new Rect(860, 260, 80, 82);
var postCutContinuousB = new Rect(864, 262, 80, 82);
var postCutContinuousC = new Rect(868, 264, 80, 82);
postCutProvider.SetFaceRects(30, new[] { postCutPrevious }, size, 0.88f, new[] { 0.88f });
postCutProvider.SetFaceRects(31, new[] { postCutGhostA }, size, 0.41f, new[] { 0.41f });
postCutProvider.SetFaceRects(32, new[] { postCutGhostB }, size, 0.42f, new[] { 0.42f });
postCutProvider.SetFaceRects(40, new[] { postCutPersistentA }, size, 0.43f, new[] { 0.43f });
for (int frame = 41; frame <= 46; frame++)
    postCutProvider.SetFaceRects(frame, new[] { new Rect(postCutPersistentA.X + frame - 40, postCutPersistentA.Y, postCutPersistentA.Width, postCutPersistentA.Height) }, size, 0.43f, new[] { 0.43f });
postCutProvider.SetFaceRects(50, new[] { postCutContinuousA }, size, 0.88f, new[] { 0.88f });
postCutProvider.SetFaceRects(51, new[] { postCutContinuousB }, size, 0.42f, new[] { 0.42f });
postCutProvider.SetFaceRects(52, new[] { postCutContinuousC }, size, 0.41f, new[] { 0.41f });

var postCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    postCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 6);
var postCutResult = guard.Apply(
    postCutProvider,
    postCutCandidates,
    static (source, target) => source == 30 && target >= 31 && target <= 32 || source == 50 && target >= 51 && target <= 52 ? 0.52 : 0.05);

if (postCutCandidates.Count != 4)
    throw new InvalidOperationException($"Expected four weak post-cut carry candidates with stronger matching source masks, got {postCutCandidates.Count}.");

if (postCutResult.Removed != 4 || string.Join(",", postCutResult.RemovedFrameIndices) != "31,32,51,52")
    throw new InvalidOperationException($"Expected weak post-cut carry at frames 31,32,51,52 to be removed, got removed={postCutResult.Removed}, frames={string.Join(",", postCutResult.RemovedFrameIndices)}.");

if (postCutProvider.TryGetFaceMaskData(31, out var postCutFrameA) && postCutFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected first weak post-cut carry frame to be removed.");
if (postCutProvider.TryGetFaceMaskData(32, out var postCutFrameB) && postCutFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected second weak post-cut carry frame to be removed.");
if (!postCutProvider.TryGetFaceMaskData(50, out var postCutContinuousSource) || postCutContinuousSource.Faces.Count != 1)
    throw new InvalidOperationException("Expected continuous weak carry source before the hard cut to remain.");
if (postCutProvider.TryGetFaceMaskData(51, out var postCutContinuousFrameA) && postCutContinuousFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected continuous weak carry after a hard cut to be removed.");
if (postCutProvider.TryGetFaceMaskData(52, out var postCutContinuousFrameB) && postCutContinuousFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected continuous weak carry tail after a hard cut to be removed.");
if (!postCutProvider.TryGetFaceMaskData(40, out var persistentFrame) || persistentFrame.Faces.Count != 1)
    throw new InvalidOperationException("Expected persistent weak run with no stronger source to remain.");

var edgePostCutProvider = new FrameMaskProvider();
var edgePostCutSource = new Rect(0, 22, 72, 76);
var edgePostCutA = new Rect(0, 24, 72, 76);
var edgePostCutB = new Rect(2, 26, 72, 76);
edgePostCutProvider.SetFaceRects(59, new[] { edgePostCutSource }, size, 0.88f, new[] { 0.88f });
edgePostCutProvider.SetFaceRects(60, new[] { edgePostCutA }, size, 0.47f, new[] { 0.47f });
edgePostCutProvider.SetFaceRects(61, new[] { edgePostCutB }, size, 0.48f, new[] { 0.48f });
var edgePostCutDefaultCandidates = guard.BuildWeakPostCutCarryCandidates(
    edgePostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4);
if (edgePostCutDefaultCandidates.Count != 0)
    throw new InvalidOperationException($"Expected edge post-cut carry candidates to stay excluded by default, got {edgePostCutDefaultCandidates.Count}.");

var edgePostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    edgePostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4,
    includeEdgeCandidates: true);
var edgePostCutResult = guard.Apply(
    edgePostCutProvider,
    edgePostCutCandidates,
    static (source, target) => source == 59 && target >= 60 && target <= 61 ? 0.52 : 0.05,
    removeMatchingTailFrames: 4,
    removeMatchingTailMaxConfidence: 0.78f);

if (edgePostCutCandidates.Count != 2)
    throw new InvalidOperationException($"Expected edge post-cut carry candidates to be checked when explicitly enabled, got {edgePostCutCandidates.Count}.");
if (edgePostCutResult.Removed != 2 || string.Join(",", edgePostCutResult.RemovedFrameIndices) != "60,61")
    throw new InvalidOperationException($"Expected edge post-cut carry to be removed from frames 60-61, got removed={edgePostCutResult.Removed}, frames={string.Join(",", edgePostCutResult.RemovedFrameIndices)}.");
if (edgePostCutProvider.TryGetFaceMaskData(60, out var edgePostCutFrameA) && edgePostCutFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected edge weak carry frame 60 after a hard cut to be removed.");
if (edgePostCutProvider.TryGetFaceMaskData(61, out var edgePostCutFrameB) && edgePostCutFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected edge weak carry frame 61 after a hard cut to be removed.");

var longPostCutProvider = new FrameMaskProvider();
longPostCutProvider.SetFaceRects(79, new[] { new Rect(358, 238, 82, 84) }, size, 0.86f, new[] { 0.86f });
for (int frame = 80; frame <= 86; frame++)
{
    var face = new Rect(360 + frame - 80, 240, 82, 84);
    longPostCutProvider.SetFaceRects(frame, new[] { face }, size, 0.44f, new[] { 0.44f });
}

var longPostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    longPostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 5);
var longPostCutResult = guard.Apply(
    longPostCutProvider,
    longPostCutCandidates,
    static (source, target) => source == 79 && target == 80 ? 0.52 : 0.05,
    removeMatchingTailFrames: 6,
    removeMatchingTailMaxConfidence: 0.78f);

if (longPostCutCandidates.Count < 5)
    throw new InvalidOperationException($"Expected long weak post-cut run to remain checkable, got {longPostCutCandidates.Count} candidates.");
if (longPostCutResult.Removed != 7 || string.Join(",", longPostCutResult.RemovedFrameIndices) != "80,81,82,83,84,85,86")
    throw new InvalidOperationException($"Expected long weak post-cut run to be removed from frames 80-86, got removed={longPostCutResult.Removed}, frames={string.Join(",", longPostCutResult.RemovedFrameIndices)}.");
if (longPostCutProvider.TryGetFaceMaskData(86, out var longPostCutTail) && longPostCutTail.Faces.Count != 0)
    throw new InvalidOperationException("Expected long weak post-cut tail to be removed.");

var sameStrongContinuationProvider = new FrameMaskProvider();
sameStrongContinuationProvider.SetFaceRects(899, new[] { new Rect(318, 218, 82, 84) }, size, 0.88f, new[] { 0.88f });
sameStrongContinuationProvider.SetFaceRects(900, new[] { new Rect(320, 220, 82, 84) }, size, 0.44f, new[] { 0.44f });
sameStrongContinuationProvider.SetFaceRects(901, new[] { new Rect(321, 221, 82, 84) }, size, 0.45f, new[] { 0.45f });
sameStrongContinuationProvider.SetFaceRects(902, new[] { new Rect(322, 222, 82, 84) }, size, 0.46f, new[] { 0.46f });
sameStrongContinuationProvider.SetFaceRects(903, new[] { new Rect(323, 223, 82, 84) }, size, 0.88f, new[] { 0.88f });
var sameStrongContinuationCandidates = guard.BuildWeakPostCutCarryCandidates(
    sameStrongContinuationProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4);
var sameStrongContinuationResult = guard.Apply(
    sameStrongContinuationProvider,
    sameStrongContinuationCandidates,
    static (source, target) => source == 899 && target == 900 ? 0.52 : 0.05,
    removeMatchingTailFrames: 4,
    removeMatchingTailMaxConfidence: 0.98f);

if (sameStrongContinuationCandidates.Count != 3)
    throw new InvalidOperationException($"Expected same-position strong continuation to stay checkable as carry residue, got {sameStrongContinuationCandidates.Count} candidates.");
if (sameStrongContinuationResult.Removed != 4 || string.Join(",", sameStrongContinuationResult.RemovedFrameIndices) != "900,901,902,903")
    throw new InvalidOperationException($"Expected same-position strong continuation carry to be removed through the tail, got removed={sameStrongContinuationResult.Removed}, frames={string.Join(",", sameStrongContinuationResult.RemovedFrameIndices)}.");
if (sameStrongContinuationProvider.TryGetFaceMaskData(903, out var sameStrongContinuationTail) && sameStrongContinuationTail.Faces.Count != 0)
    throw new InvalidOperationException("Expected same-position strong continuation after a hard cut to be removed as carry residue.");

var independentStrongContinuationProvider = new FrameMaskProvider();
independentStrongContinuationProvider.SetFaceRects(949, new[] { new Rect(318, 218, 82, 84) }, size, 0.88f, new[] { 0.88f });
independentStrongContinuationProvider.SetFaceRects(950, new[] { new Rect(320, 220, 82, 84) }, size, 0.44f, new[] { 0.44f });
independentStrongContinuationProvider.SetFaceRects(951, new[] { new Rect(350, 220, 82, 84) }, size, 0.88f, new[] { 0.88f });
var independentStrongContinuationCandidates = guard.BuildWeakPostCutCarryCandidates(
    independentStrongContinuationProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4);
var independentStrongContinuationProbeCandidates = guard.BuildWeakPostCutCarryCandidates(
    independentStrongContinuationProvider,
    maxTargetConfidence: 0.995f,
    maxCarryFrames: 4,
    minSourceConfidence: 0.80f,
    minTargetConfidence: 0.40f,
    includeIndependentStrongContinuation: true);
var independentStrongContinuationProbeResult = guard.Apply(
    independentStrongContinuationProvider,
    independentStrongContinuationProbeCandidates,
    static (source, target) => source == 949 && target == 950 ? 0.52 : 0.05,
    removeCandidates: false);

if (independentStrongContinuationCandidates.Count != 0)
    throw new InvalidOperationException($"Expected shifted strong continuation to protect an independent post-cut face, got {independentStrongContinuationCandidates.Count} candidates.");
if (independentStrongContinuationProbeCandidates.Count != 2)
    throw new InvalidOperationException($"Expected probe-only mode to keep shifted strong continuation checkable as evidence, got {independentStrongContinuationProbeCandidates.Count} candidates.");
if (independentStrongContinuationProbeResult.Removed != 0 || string.Join(",", independentStrongContinuationProbeResult.CutFramePairs) != "949->950")
    throw new InvalidOperationException($"Expected probe-only shifted strong continuation evidence without removal, got removed={independentStrongContinuationProbeResult.Removed}, cutPairs={string.Join(",", independentStrongContinuationProbeResult.CutFramePairs)}.");
if (!independentStrongContinuationProvider.TryGetFaceMaskData(951, out var independentStrongContinuationTail) || independentStrongContinuationTail.Faces.Count != 1)
    throw new InvalidOperationException("Expected independent shifted strong continuation after a hard cut to remain.");

var extendedWindowPostCutProvider = new FrameMaskProvider();
extendedWindowPostCutProvider.SetFaceRects(599, new[] { new Rect(458, 238, 82, 84) }, size, 0.86f, new[] { 0.86f });
for (int frame = 600; frame <= 608; frame++)
{
    var face = new Rect(460 + frame - 600, 240, 82, 84);
    extendedWindowPostCutProvider.SetFaceRects(frame, new[] { face }, size, 0.44f, new[] { 0.44f });
}

var extendedWindowPostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    extendedWindowPostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 8);
var extendedWindowPostCutResult = guard.Apply(
    extendedWindowPostCutProvider,
    extendedWindowPostCutCandidates,
    static (source, target) => source == 599 && target == 600 ? 0.52 : 0.05,
    removeMatchingTailFrames: 8,
    removeMatchingTailMaxConfidence: 0.78f);

if (extendedWindowPostCutCandidates.Count < 8)
    throw new InvalidOperationException($"Expected extended weak post-cut window to keep eight carry candidates, got {extendedWindowPostCutCandidates.Count}.");
if (extendedWindowPostCutResult.Removed != 9 || string.Join(",", extendedWindowPostCutResult.RemovedFrameIndices) != "600,601,602,603,604,605,606,607,608")
    throw new InvalidOperationException($"Expected extended weak post-cut window to be removed from frames 600-608, got removed={extendedWindowPostCutResult.Removed}, frames={string.Join(",", extendedWindowPostCutResult.RemovedFrameIndices)}.");
if (extendedWindowPostCutProvider.TryGetFaceMaskData(608, out var extendedWindowPostCutTail) && extendedWindowPostCutTail.Faces.Count != 0)
    throw new InvalidOperationException("Expected extended weak post-cut tail to be removed through the 8-frame carry window.");

var delayedPostCutProvider = new FrameMaskProvider();
var delayedSource = new Rect(606, 206, 76, 78);
var delayedGhostA = new Rect(610, 210, 76, 78);
var delayedGhostB = new Rect(614, 212, 76, 78);
delayedPostCutProvider.SetFaceRects(100, new[] { delayedSource }, size, 0.86f, new[] { 0.86f });
delayedPostCutProvider.SetFaceRects(101, new[] { new Rect(608, 208, 76, 78) }, size, 0.84f, new[] { 0.84f });
delayedPostCutProvider.SetFaceRects(102, new[] { new Rect(609, 209, 76, 78) }, size, 0.82f, new[] { 0.82f });
delayedPostCutProvider.SetFaceRects(103, new[] { delayedGhostA }, size, 0.47f, new[] { 0.47f });
delayedPostCutProvider.SetFaceRects(104, new[] { delayedGhostB }, size, 0.48f, new[] { 0.48f });
var delayedPostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    delayedPostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4,
    sourceLookbackFrames: 3);
var delayedPostCutResult = guard.Apply(
    delayedPostCutProvider,
    delayedPostCutCandidates,
    static (source, target) => source == 100 && target >= 103 && target <= 104 ? 0.54 : 0.04);

if (delayedPostCutCandidates.Count != 2)
    throw new InvalidOperationException($"Expected delayed weak post-cut run to keep one matching source candidate per target, got {delayedPostCutCandidates.Count} candidates.");
if (delayedPostCutResult.Removed != 2 || string.Join(",", delayedPostCutResult.RemovedFrameIndices) != "103,104")
    throw new InvalidOperationException($"Expected delayed weak post-cut run to be removed from frames 103-104, got removed={delayedPostCutResult.Removed}, frames={string.Join(",", delayedPostCutResult.RemovedFrameIndices)}.");
if (delayedPostCutProvider.TryGetFaceMaskData(103, out var delayedPostCutFrameA) && delayedPostCutFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected delayed weak post-cut frame 103 to be removed.");
if (delayedPostCutProvider.TryGetFaceMaskData(104, out var delayedPostCutFrameB) && delayedPostCutFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected delayed weak post-cut frame 104 to be removed.");

var extendedLookbackPostCutProvider = new FrameMaskProvider();
var extendedLookbackSource = new Rect(506, 206, 78, 80);
var extendedLookbackGhostA = new Rect(510, 210, 78, 80);
var extendedLookbackGhostB = new Rect(514, 212, 78, 80);
extendedLookbackPostCutProvider.SetFaceRects(200, new[] { extendedLookbackSource }, size, 0.87f, new[] { 0.87f });
extendedLookbackPostCutProvider.SetFaceRects(205, new[] { extendedLookbackGhostA }, size, 0.46f, new[] { 0.46f });
extendedLookbackPostCutProvider.SetFaceRects(206, new[] { extendedLookbackGhostB }, size, 0.47f, new[] { 0.47f });
var shortLookbackPostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    extendedLookbackPostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4,
    sourceLookbackFrames: 3);
if (shortLookbackPostCutCandidates.Count != 0)
    throw new InvalidOperationException($"Expected 3-frame source lookback to miss the delayed post-cut carry source, got {shortLookbackPostCutCandidates.Count} candidates.");
var extendedLookbackPostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    extendedLookbackPostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4,
    sourceLookbackFrames: 5);
var extendedLookbackPostCutResult = guard.Apply(
    extendedLookbackPostCutProvider,
    extendedLookbackPostCutCandidates,
    static (source, target) => source == 200 && target >= 205 && target <= 206 ? 0.54 : 0.04);

if (extendedLookbackPostCutCandidates.Count != 2)
    throw new InvalidOperationException($"Expected 5-frame source lookback to catch delayed post-cut carry frames 205-206, got {extendedLookbackPostCutCandidates.Count} candidates.");
if (extendedLookbackPostCutResult.Removed != 2 || string.Join(",", extendedLookbackPostCutResult.RemovedFrameIndices) != "205,206")
    throw new InvalidOperationException($"Expected extended-lookback weak post-cut carry to be removed from frames 205-206, got removed={extendedLookbackPostCutResult.Removed}, frames={string.Join(",", extendedLookbackPostCutResult.RemovedFrameIndices)}.");
if (extendedLookbackPostCutProvider.TryGetFaceMaskData(205, out var extendedLookbackPostCutFrameA) && extendedLookbackPostCutFrameA.Faces.Count != 0)
    throw new InvalidOperationException("Expected extended-lookback weak post-cut frame 205 to be removed.");
if (extendedLookbackPostCutProvider.TryGetFaceMaskData(206, out var extendedLookbackPostCutFrameB) && extendedLookbackPostCutFrameB.Faces.Count != 0)
    throw new InvalidOperationException("Expected extended-lookback weak post-cut frame 206 to be removed.");

var noSourcePostCutProvider = new FrameMaskProvider();
var noSourceFaceA = new Rect(710, 250, 82, 84);
var noSourceFaceB = new Rect(714, 252, 82, 84);
noSourcePostCutProvider.SetFaceRects(130, new[] { noSourceFaceA }, size, 0.44f, new[] { 0.44f });
noSourcePostCutProvider.SetFaceRects(131, new[] { noSourceFaceB }, size, 0.45f, new[] { 0.45f });
var noSourcePostCutCandidates = guard.BuildWeakPostCutCarryCandidates(
    noSourcePostCutProvider,
    maxTargetConfidence: 0.50f,
    maxCarryFrames: 4,
    sourceLookbackFrames: 3);

if (noSourcePostCutCandidates.Count != 0)
    throw new InvalidOperationException($"Expected weak post-cut run with no matching source mask to stay out of scene-cut candidates, got {noSourcePostCutCandidates.Count}.");
if (!noSourcePostCutProvider.TryGetFaceMaskData(130, out var noSourcePostCutFrameA) || noSourcePostCutFrameA.Faces.Count != 1 ||
    !noSourcePostCutProvider.TryGetFaceMaskData(131, out var noSourcePostCutFrameB) || noSourcePostCutFrameB.Faces.Count != 1)
{
    throw new InvalidOperationException("Expected no-source weak post-cut frames to remain for visual review instead of being removed as carry residue.");
}

var cacheProvider = new FrameMaskProvider();
var cacheFaceA = new Rect(100, 100, 40, 42);
var cacheFaceB = new Rect(300, 100, 44, 46);
cacheProvider.SetFaceRects(21, new[] { cacheFaceA, cacheFaceB }, size, 0.39f, new[] { 0.39f, 0.38f });
var cacheCandidates = new[]
{
    new FaceTrackFilledFace(21, cacheFaceA, size, 0.39f, 20),
    new FaceTrackFilledFace(21, cacheFaceA, size, 0.39f, 20),
    new FaceTrackFilledFace(21, cacheFaceB, size, 0.38f, 20)
};
int diffCalls = 0;
var cacheResult = guard.Apply(
    cacheProvider,
    cacheCandidates,
    (source, target) =>
    {
        diffCalls++;
        return 0.50;
    });

if (cacheResult.Checked != 2 || cacheResult.Removed != 2)
    throw new InvalidOperationException($"Expected exact duplicate candidate to be skipped while distinct boxes stay checked, got checked={cacheResult.Checked} removed={cacheResult.Removed}.");

if (diffCalls != 1)
    throw new InvalidOperationException($"Expected duplicate pair difference to be computed once, got {diffCalls}.");

var gradualProvider = new FrameMaskProvider();
var gradualGhost = new Rect(180, 160, 74, 76);
gradualProvider.SetFaceRects(3, new[] { gradualGhost }, size, 0.58f, new[] { 0.58f });
var gradualCandidates = new[]
{
    new FaceTrackFilledFace(3, gradualGhost, size, 0.58f, 0)
};

var gradualResult = guard.Apply(
    gradualProvider,
    gradualCandidates,
    static (source, target) => source == 0 && target == 3 ? 0.46 : 0.18);

if (gradualResult.Removed != 1 || string.Join(",", gradualResult.CutFramePairs) != "0->3")
    throw new InvalidOperationException($"Expected cumulative scene change to remove gradual carry at frame 3, got removed={gradualResult.Removed}, cutPairs={string.Join(",", gradualResult.CutFramePairs)}.");

if (gradualProvider.TryGetFaceMaskData(3, out var gradualFrame) && gradualFrame.Faces.Count != 0)
    throw new InvalidOperationException("Expected gradual-transition track-fill candidate to be removed.");

var mildGradualProvider = new FrameMaskProvider();
var mildGradualGhost = new Rect(220, 180, 76, 78);
mildGradualProvider.SetFaceRects(4, new[] { mildGradualGhost }, size, 0.64f, new[] { 0.64f });
var mildGradualResult = guard.Apply(
    mildGradualProvider,
    new[] { new FaceTrackFilledFace(4, mildGradualGhost, size, 0.64f, 0) },
    static (source, target) => source == 0 && target == 4 ? 0.37 : 0.18);

if (mildGradualResult.Removed != 1 || string.Join(",", mildGradualResult.CutFramePairs) != "0->4")
    throw new InvalidOperationException($"Expected lower direct scene-change threshold to remove mild gradual carry at frame 4, got removed={mildGradualResult.Removed}, cutPairs={string.Join(",", mildGradualResult.CutFramePairs)}.");

var probeProvider = new FrameMaskProvider();
var probeSource = new Rect(760, 300, 96, 98);
var probeCarry = new Rect(762, 302, 96, 98);
probeProvider.SetFaceRects(100, new[] { probeSource }, size, 0.93f, new[] { 0.93f });
probeProvider.SetFaceRects(101, new[] { probeCarry }, size, 0.94f, new[] { 0.94f });
var probeCandidates = guard.BuildWeakPostCutCarryCandidates(
    probeProvider,
    maxTargetConfidence: 0.995f,
    maxCarryFrames: 5,
    sourceLookbackFrames: 3,
    minSourceConfidence: 0.80f,
    minTargetConfidence: 0.78f,
    includeEdgeCandidates: true);
var probeResult = guard.Apply(
    probeProvider,
    probeCandidates,
    static (source, target) => source == 100 && target == 101 ? 0.50 : 0.0,
    removeCandidates: false);

if (probeCandidates.Count != 1)
    throw new InvalidOperationException($"Expected one strong carry scene-cut probe candidate, got {probeCandidates.Count}.");
if (probeResult.Checked != 1 || probeResult.Removed != 0 || string.Join(",", probeResult.CutFramePairs) != "100->101")
    throw new InvalidOperationException($"Expected probe-only scene cut evidence without removal, got checked={probeResult.Checked}, removed={probeResult.Removed}, cutPairs={string.Join(",", probeResult.CutFramePairs)}.");
if (!probeProvider.TryGetFaceMaskData(101, out var probeFrame) || probeFrame.Faces.Count != 1)
    throw new InvalidOperationException("Expected probe-only scene cut guard to keep the high-confidence carry candidate for the final carry cleanup stage.");

Console.WriteLine($"[FaceTrackSceneCutGuardVerify] checked={result.Checked}, checkedPairs={string.Join(",", result.CheckedFramePairs)}, maxDiff={result.MaxDifference:0.000}, cutPairs={string.Join(",", result.CutFramePairs)}, removed={result.Removed}, removedFrames={string.Join(",", result.RemovedFrameIndices)}, threshold={result.Threshold:0.000}, hardCutRemoved=True, sameSceneKept=True, reverseChecked={reverseResult.Checked}, reverseRemoved={reverseResult.Removed}, reversePairs={string.Join(",", reverseResult.CheckedFramePairs)}, directCandidates={directCandidates.Count}, directRemoved={directResult.Removed}, adjacentCutDirectChecked={adjacentCutDirectResult.DirectDifferenceChecks}, directBudgetChecked={directBudgetResult.DirectDifferenceChecks}, directBudgetSkipped={directBudgetResult.DirectDifferenceSkipped}, directReuseRemoved={directReuseResult.Removed}, directReuseChecked={directReuseResult.DirectDifferenceChecks}, directReuseSkipped={directReuseResult.DirectDifferenceSkipped}, weakSourceDirectCandidates={weakSourceDirectCandidates.Count}, mediumDirectCandidates={mediumDirectCandidates.Count}, mediumDirectRemoved={mediumDirectResult.Removed}, noDropDirectCandidates={noDropDirectCandidates.Count}, noDropDirectRemoved={noDropDirectResult.Removed}, weakTailRemoved={weakTailResult.Removed}, highTailRemoved={highTailResult.Removed}, smoothedRemoved={smoothedResult.Removed}, postCutCandidates={postCutCandidates.Count}, postCutRemoved={postCutResult.Removed}, edgePostCutDefaultCandidates={edgePostCutDefaultCandidates.Count}, edgePostCutCandidates={edgePostCutCandidates.Count}, edgePostCutRemoved={edgePostCutResult.Removed}, longPostCutRemoved={longPostCutResult.Removed}, sameStrongContinuationCandidates={sameStrongContinuationCandidates.Count}, sameStrongContinuationRemoved={sameStrongContinuationResult.Removed}, independentStrongContinuationCandidates={independentStrongContinuationCandidates.Count}, independentStrongContinuationProbeCandidates={independentStrongContinuationProbeCandidates.Count}, independentStrongContinuationProbeCutPairs={string.Join(",", independentStrongContinuationProbeResult.CutFramePairs)}, independentStrongContinuationProbeRemoved={independentStrongContinuationProbeResult.Removed}, extendedWindowPostCutCandidates={extendedWindowPostCutCandidates.Count}, extendedWindowPostCutRemoved={extendedWindowPostCutResult.Removed}, delayedPostCutCandidates={delayedPostCutCandidates.Count}, delayedPostCutRemoved={delayedPostCutResult.Removed}, extendedLookbackPostCutCandidates={extendedLookbackPostCutCandidates.Count}, extendedLookbackPostCutRemoved={extendedLookbackPostCutResult.Removed}, noSourcePostCutCandidates={noSourcePostCutCandidates.Count}, gradualRemoved={gradualResult.Removed}, gradualCutPairs={string.Join(",", gradualResult.CutFramePairs)}, mildGradualRemoved={mildGradualResult.Removed}, probeCandidates={probeCandidates.Count}, probeCutPairs={string.Join(",", probeResult.CutFramePairs)}, probeRemoved={probeResult.Removed}, diffCacheCalls={diffCalls}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
