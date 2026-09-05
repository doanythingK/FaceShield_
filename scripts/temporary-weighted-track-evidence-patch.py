from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if old not in text:
        raise SystemExit(f'missing patch anchor in {path}: {old[:120]!r}')
    if text.count(old) != 1:
        raise SystemExit(f'non-unique patch anchor in {path}: count={text.count(old)}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

builder = 'Services/Analysis/FaceTrackBuilder.cs'
replace_once(
    builder,
    '''    public sealed record FaceTrackPostProcessOptions\n    {''',
    '''    internal readonly record struct FaceTrackEvidence(\n        double Score,\n        float MeanConfidence,\n        double DetectionDensity,\n        double Continuity,\n        double Persistence);\n\n    internal static class FaceTrackEvidenceScorer\n    {\n        internal static FaceTrackEvidence Evaluate(\n            FaceTrack track,\n            FaceTrackPostProcessOptions options)\n        {\n            if (track.DetectionCount <= 0)\n                return default;\n\n            double confidenceSum = 0.0;\n            foreach (var detection in track.Detections)\n                confidenceSum += Math.Clamp(detection.Confidence, 0.0f, 1.0f);\n\n            float meanConfidence = (float)(confidenceSum / track.DetectionCount);\n            int span = Math.Max(\n                1,\n                track.Detections[^1].FrameIndex -\n                track.Detections[0].FrameIndex + 1);\n            double density = Math.Clamp(\n                track.DetectionCount / (double)span,\n                0.0,\n                1.0);\n            double persistence = Math.Clamp(\n                track.DetectionCount /\n                (double)Math.Max(1, options.EvidencePersistenceDetections),\n                0.0,\n                1.0);\n            double continuity = ComputeContinuity(track, options);\n\n            // Privacy-oriented evidence: detector confidence is the largest term,\n            // while temporal stability must still support weak/moderate detections.\n            double score = Math.Clamp(\n                meanConfidence * 0.50 +\n                density * 0.15 +\n                continuity * 0.20 +\n                persistence * 0.15,\n                0.0,\n                1.0);\n\n            return new FaceTrackEvidence(\n                score,\n                meanConfidence,\n                density,\n                continuity,\n                persistence);\n        }\n\n        private static double ComputeContinuity(\n            FaceTrack track,\n            FaceTrackPostProcessOptions options)\n        {\n            if (track.DetectionCount < 2)\n                return 0.0;\n\n            double sum = 0.0;\n            int transitions = 0;\n            double areaLimit = Math.Max(1.01, options.MaxAreaChangeRatio);\n            double areaLogLimit = Math.Log(areaLimit);\n            double iouTarget = Math.Max(0.20, options.MinTrackIou * 2.0);\n\n            for (int i = 1; i < track.Detections.Count; i++)\n            {\n                var previous = track.Detections[i - 1];\n                var current = track.Detections[i];\n                int gap = Math.Max(1, current.FrameIndex - previous.FrameIndex);\n                double allowedShift = Math.Max(\n                    0.001,\n                    options.MaxCenterShiftRatio * gap);\n                double centerShift = FaceTrackBuilder.GetNormalizedCenterShift(\n                    previous.Bounds,\n                    current.Bounds);\n                double centerScore = Math.Clamp(\n                    1.0 - centerShift / allowedShift,\n                    0.0,\n                    1.0);\n\n                double rawAreaRatio = FaceTrackBuilder.GetAreaRatio(\n                    previous.Bounds,\n                    current.Bounds);\n                double symmetricAreaRatio = rawAreaRatio <= 0.0\n                    ? areaLimit\n                    : Math.Max(rawAreaRatio, 1.0 / rawAreaRatio);\n                double areaScore = Math.Clamp(\n                    1.0 - Math.Log(Math.Max(1.0, symmetricAreaRatio)) / areaLogLimit,\n                    0.0,\n                    1.0);\n\n                double iouScore = Math.Clamp(\n                    FaceTrackBuilder.IoU(previous.Bounds, current.Bounds) / iouTarget,\n                    0.0,\n                    1.0);\n                double gapWeight = 0.75 + 0.25 / gap;\n                sum += (\n                    centerScore * 0.45 +\n                    areaScore * 0.30 +\n                    iouScore * 0.25) * gapWeight;\n                transitions++;\n            }\n\n            return transitions == 0\n                ? 0.0\n                : Math.Clamp(sum / transitions, 0.0, 1.0);\n        }\n    }\n\n    public sealed record FaceTrackPostProcessOptions\n    {''')

replace_once(
    builder,
    '''        public int ConfirmedTrackMinDetections { get; init; } = 3;\n        public bool AllowSmallTrackLostFill { get; init; } = false;''',
    '''        public int ConfirmedTrackMinDetections { get; init; } = 3;\n        public bool EnableWeightedTrackEvidence { get; init; } = false;\n        public double MinConfirmedTrackEvidenceScore { get; init; } = 0.0;\n        public double LowEvidenceRejectScore { get; init; } = 0.0;\n        public float LowEvidenceRejectMaxConfidence { get; init; } = 0.0f;\n        public int LowEvidenceRejectMaxDetections { get; init; } = 0;\n        public int EvidencePersistenceDetections { get; init; } = 5;\n        public double StrongTrackEvidenceScore { get; init; } = 1.01;\n        public int MaxStrongTrackLostFillFrames { get; init; } = 0;\n        public bool AllowSmallTrackLostFill { get; init; } = false;''')

interpolator = 'Services/Analysis/FaceTrackInterpolator.cs'
replace_once(
    interpolator,
    '''using System.Collections.Generic;\nusing System.Linq;''',
    '''using System.Collections.Generic;\nusing System.Diagnostics;\nusing System.Linq;''')

replace_once(
    interpolator,
    '''            int removedLowerFrameFaces = RemoveLowerFrameLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);\n            int removedSparseFaces = RemoveSparseLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);\n            int removedFaces = RemoveShortLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);''',
    '''            int removedLowerFrameFaces = RemoveLowerFrameLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);\n            int removedLowEvidenceFaces = RemoveLowEvidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);\n            int removedSparseFaces = RemoveSparseLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);\n            int removedFaces = removedLowEvidenceFaces + RemoveShortLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);''')

replace_once(
    interpolator,
    '''        private static int RemoveShortLowConfidenceTracks(\n            IReadOnlyList<FaceTrack> tracks,''',
    '''        private static int RemoveLowEvidenceTracks(\n            IReadOnlyList<FaceTrack> tracks,\n            SparseFrameMap<List<Rect>?> facesByFrame,\n            SparseFrameMap<List<float>?> confByFrame,\n            FaceTrackPostProcessOptions options,\n            ISet<int> removedTrackIds,\n            CancellationToken cancellationToken)\n        {\n            if (!options.EnableWeightedTrackEvidence ||\n                options.LowEvidenceRejectMaxDetections <= 0 ||\n                options.LowEvidenceRejectMaxConfidence <= 0 ||\n                options.LowEvidenceRejectScore <= 0)\n            {\n                return 0;\n            }\n\n            int removed = 0;\n            foreach (var track in tracks)\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                if (removedTrackIds.Contains(track.Id) ||\n                    track.DetectionCount <= 0 ||\n                    track.DetectionCount > options.LowEvidenceRejectMaxDetections ||\n                    track.MaxConfidence > options.LowEvidenceRejectMaxConfidence ||\n                    CouldBePartialFace(track, tracks, options))\n                {\n                    continue;\n                }\n\n                var evidence = FaceTrackEvidenceScorer.Evaluate(track, options);\n                if (evidence.Score >= options.LowEvidenceRejectScore)\n                    continue;\n\n                removedTrackIds.Add(track.Id);\n                int removedOnTrack = RemoveTrackDetections(\n                    track,\n                    facesByFrame,\n                    confByFrame,\n                    cancellationToken);\n                removed += removedOnTrack;\n                Debug.WriteLine(\n                    $\"[FaceTrackEvidence] track={track.Id} action=reject detections={track.DetectionCount} score={evidence.Score:0.000} meanConfidence={evidence.MeanConfidence:0.000} density={evidence.DetectionDensity:0.000} continuity={evidence.Continuity:0.000} persistence={evidence.Persistence:0.000} removedFaces={removedOnTrack}\");\n            }\n\n            return removed;\n        }\n\n        private static int RemoveShortLowConfidenceTracks(\n            IReadOnlyList<FaceTrack> tracks,''')

replace_once(
    interpolator,
    '''            foreach (var track in tracks)\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                bool couldBePartialFace = CouldBePartialFace(track, tracks, options);''',
    '''            foreach (var track in tracks)\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                if (removedTrackIds.Contains(track.Id))\n                    continue;\n\n                bool couldBePartialFace = CouldBePartialFace(track, tracks, options);''')

replace_once(
    interpolator,
    '''                var detections = track.Detections;\n                var last = detections[^1];''',
    '''                int maxLostFillFrames = ResolveLostFillFrames(track, options);\n                if (maxLostFillFrames <= 0)\n                    continue;\n\n                var detections = track.Detections;\n                var last = detections[^1];''')

replace_once(
    interpolator,
    '''                for (int offset = 1; offset <= options.MaxLostFillFrames; offset++)''',
    '''                for (int offset = 1; offset <= maxLostFillFrames; offset++)''')

replace_once(
    interpolator,
    '''        private static bool IsConfirmedTrack(FaceTrack track, FaceTrackPostProcessOptions options)\n            => track.DetectionCount >= options.ConfirmedTrackMinDetections &&\n                track.MaxConfidence >= options.StrongConfidence;''',
    '''        private static bool IsConfirmedTrack(FaceTrack track, FaceTrackPostProcessOptions options)\n        {\n            if (track.DetectionCount < options.ConfirmedTrackMinDetections ||\n                track.MaxConfidence < options.StrongConfidence)\n            {\n                return false;\n            }\n\n            if (!options.EnableWeightedTrackEvidence ||\n                options.MinConfirmedTrackEvidenceScore <= 0)\n            {\n                return true;\n            }\n\n            return FaceTrackEvidenceScorer.Evaluate(track, options).Score >=\n                options.MinConfirmedTrackEvidenceScore;\n        }\n\n        private static int ResolveLostFillFrames(\n            FaceTrack track,\n            FaceTrackPostProcessOptions options)\n        {\n            int baseFrames = Math.Max(0, options.MaxLostFillFrames);\n            if (!options.EnableWeightedTrackEvidence ||\n                options.MaxStrongTrackLostFillFrames <= baseFrames ||\n                options.StrongTrackEvidenceScore <= 0)\n            {\n                return baseFrames;\n            }\n\n            var evidence = FaceTrackEvidenceScorer.Evaluate(track, options);\n            if (evidence.Score < options.StrongTrackEvidenceScore)\n                return baseFrames;\n\n            Debug.WriteLine(\n                $\"[FaceTrackEvidence] track={track.Id} action=extend-hold score={evidence.Score:0.000} baseFrames={baseFrames} strongFrames={options.MaxStrongTrackLostFillFrames}\");\n            return options.MaxStrongTrackLostFillFrames;\n        }''')

post = 'Services/Analysis/AutoMaskTemporalPostProcessor.cs'
replace_once(
    post,
    '''                    EdgePartialFaceMarginRatio = 0.06,\n                    ConfirmedTrackMinDetections = 3\n                };''',
    '''                    EdgePartialFaceMarginRatio = 0.06,\n                    ConfirmedTrackMinDetections = 3,\n                    EnableWeightedTrackEvidence = cleanupYoloFalsePositives,\n                    MinConfirmedTrackEvidenceScore = cleanupYoloFalsePositives ? 0.66 : 0.0,\n                    LowEvidenceRejectScore = cleanupYoloFalsePositives ? 0.64 : 0.0,\n                    LowEvidenceRejectMaxConfidence = cleanupYoloFalsePositives ? 0.60f : 0f,\n                    LowEvidenceRejectMaxDetections = cleanupYoloFalsePositives ? 3 : 0,\n                    EvidencePersistenceDetections = 5,\n                    StrongTrackEvidenceScore = cleanupYoloFalsePositives ? 0.80 : 1.01,\n                    MaxStrongTrackLostFillFrames = cleanupYoloFalsePositives ? 12 : 0\n                };''')

replace_once(
    post,
    '''                    LowerFrameTrackMaxAreaRatio = 0.045\n                };''',
    '''                    LowerFrameTrackMaxAreaRatio = 0.045,\n                    ConfirmedTrackMinDetections = 3,\n                    EnableWeightedTrackEvidence = true,\n                    MinConfirmedTrackEvidenceScore = 0.66,\n                    LowEvidenceRejectScore = 0.64,\n                    LowEvidenceRejectMaxConfidence = 0.60f,\n                    LowEvidenceRejectMaxDetections = 3,\n                    EvidencePersistenceDetections = 5,\n                    StrongTrackEvidenceScore = 0.80,\n                    MaxStrongTrackLostFillFrames = 10\n                };''')

# Add a retained regression harness rather than relying only on one-off CI assertions.
test = Path('scripts/verify-yolo-weighted-track-evidence.ps1')
test.write_text(r'''param()

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
''', encoding='utf-8')

print('weighted track evidence patch applied')
