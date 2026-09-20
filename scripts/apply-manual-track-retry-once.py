#!/usr/bin/env python3
"""Apply and test a guarded tracking-retry fix in a one-shot CI job."""
from pathlib import Path
import os


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding='utf-8')
    matches = text.count(old)
    if matches != 1:
        raise RuntimeError(f'{path}: expected one matching patch anchor, got {matches}')
    file.write_text(text.replace(old, new, 1), encoding='utf-8')


path = 'Services/Video/ManualMaskKeyframeTimeline.cs'
replace_once(path,
    'internal static void SetTrackSegment(FrameMaskProvider provider, ManualMaskTrackSegment segment)',
    'internal static bool SetTrackSegment(FrameMaskProvider provider, ManualMaskTrackSegment segment)')
replace_once(path,
    '''        lock (state.Gate)
        {
            var next = state.Segments.Where(existing => existing.SourceKeyframe != segment.SourceKeyframe)
                .Select(static existing => existing.Clone()).ToList();
            next.Add(segment.Clone());
            next.Sort(static (a, b) => a.SourceKeyframe.CompareTo(b.SourceKeyframe));
            PersistSegmentsLocked(state, next);
            state.Segments = next;
            state.SegmentValidity[segment.SourceKeyframe] = true;
        }
    }

    internal static bool TryCloneEffectiveKeyframeMask''',
    '''        lock (state.Gate)
        {
            ManualMaskTrackSegment? existing = state.Segments.FirstOrDefault(candidate =>
                candidate.SourceKeyframe == segment.SourceKeyframe);
            // A shorter retry (including a one-frame check or an early failure)
            // cannot erase a longer, still-current verified interval. A changed
            // source fingerprint never qualifies for preservation. An equally
            // long failed retry cannot downgrade an already successful interval.
            if (existing != null &&
                string.Equals(existing.SourceMaskFingerprint,
                    segment.SourceMaskFingerprint, StringComparison.Ordinal) &&
                IsSegmentCurrentLocked(provider, state, existing) &&
                (existing.EndExclusive > segment.EndExclusive ||
                 (existing.EndExclusive == segment.EndExclusive &&
                  !existing.StoppedByFailure && segment.StoppedByFailure)))
                return true;

            var next = state.Segments.Where(candidate => candidate.SourceKeyframe != segment.SourceKeyframe)
                .Select(static candidate => candidate.Clone()).ToList();
            next.Add(segment.Clone());
            next.Sort(static (a, b) => a.SourceKeyframe.CompareTo(b.SourceKeyframe));
            PersistSegmentsLocked(state, next);
            state.Segments = next;
            state.SegmentValidity[segment.SourceKeyframe] = true;
            return false;
        }
    }

    internal static bool TryCloneEffectiveKeyframeMask''')

path = 'ViewModels/Workspace/FramePreviewViewModel.ManualTracking.cs'
replace_once(path,
    '''                bool commitStarted = false;
                try
                {''',
    '''                bool commitStarted = false;
                bool retainedExistingTrack = false;
                try
                {''')
replace_once(path,
    '''                    ManualMaskKeyframeTimeline.SetTrackSegment(
                        provider,
                        result.Segment);
                }
                finally''',
    '''                    retainedExistingTrack = ManualMaskKeyframeTimeline.SetTrackSegment(
                        provider,
                        result.Segment);
                    if (retainedExistingTrack)
                        ManualTrackingStatusText =
                            "재추적이 기존 검증 구간보다 짧아 이전 추적 결과를 유지했습니다.";
                }
                finally''')
# The final completion/failure status is assigned after the commit; append the
# retention warning there too, so it is not overwritten by a generic message.
replace_once(path,
    '''            if (result.Segment.StoppedByFailure)
            {''',
    '''            bool retainedPreviousCoverage = result.ProcessedFrames > 0 &&
                ManualMaskKeyframeTimeline.HasLongerCurrentSegment(
                    provider, result.Segment);
            if (result.Segment.StoppedByFailure)
            {''')
replace_once(path,
    '''            }
        }
        catch (OperationCanceledException) when (trackingCts.IsCancellationRequested)''',
    '''            }
            if (retainedPreviousCoverage)
                ManualTrackingStatusText += " 이전에 검증된 더 긴 추적 구간은 유지됩니다.";
        }
        catch (OperationCanceledException) when (trackingCts.IsCancellationRequested)''')
# The UI queries the retained interval under the timeline gate after committing.
replace_once('Services/Video/ManualMaskKeyframeTimeline.cs',
    '''    internal static bool TryCloneEffectiveKeyframeMask(FrameMaskProvider provider, int frameIndex,''',
    '''    internal static bool HasLongerCurrentSegment(
        FrameMaskProvider provider, ManualMaskTrackSegment candidate)
    {
        if (!States.TryGetValue(provider, out TimelineState? state)) return false;
        lock (state.Gate)
        {
            ManualMaskTrackSegment? existing = state.Segments.FirstOrDefault(segment =>
                segment.SourceKeyframe == candidate.SourceKeyframe);
            return existing != null &&
                existing.EndExclusive > candidate.EndExclusive &&
                string.Equals(existing.SourceMaskFingerprint,
                    candidate.SourceMaskFingerprint, StringComparison.Ordinal) &&
                IsSegmentCurrentLocked(provider, state, existing);
        }
    }

    internal static bool TryCloneEffectiveKeyframeMask(FrameMaskProvider provider, int frameIndex,''')

path = 'scripts/frame-mask-layer-regression.cs.txt'
replace_once(path,
    'using System;\n',
    'using System;\nusing System.Collections.Generic;\nusing System.IO;\n')
replace_once(path,
    'Console.WriteLine("PASS: cached preview reuses dirty regions and invalidates on Auto/frame change");',
    '''// A new retry must never truncate already verified frames from the same
// unchanged manual source, including in the export snapshot and on disk.
string retryVideo = Path.GetTempFileName();
try
{
    using var retryProvider = new FrameMaskProvider();
    var source = new WriteableBitmap(size, new Vector(96, 96),
        PixelFormat.Bgra8888, AlphaFormat.Premul);
    PaintRect(source, 60, 30, 12, 12, 255);
    retryProvider.SetIndependentManualMask(42, source);
    ManualMaskKeyframeTimeline.Configure(retryProvider, enabled: true, videoPath: retryVideo);
    Assert(retryProvider.TryCloneStoredMask(42, out var sourceCopy),
        "Missing manual source for retry regression");
    string fingerprint;
    using (sourceCopy)
        fingerprint = ManualMaskFingerprint.Compute(sourceCopy);

    ManualMaskTrackSegment MakeSegment(int frames, double offsetX = 0, bool failed = false)
    {
        var component = new ManualMaskTrackComponent
        {
            ComponentIndex = 0,
            SourceBoundsX = 60, SourceBoundsY = 30,
            SourceBoundsWidth = 12, SourceBoundsHeight = 12,
            Samples = new List<ManualMaskTrackSample>()
        };
        for (int frameIndex = 43; frameIndex <= 42 + frames; frameIndex++)
            component.Samples.Add(new ManualMaskTrackSample
            {
                FrameIndex = frameIndex,
                OffsetX = offsetX, OffsetY = 0,
                Scale = 1, Confidence = 0.9
            });
        return new ManualMaskTrackSegment
        {
            SourceKeyframe = 42,
            SourceMaskFingerprint = fingerprint,
            EndExclusive = 43 + frames,
            StoppedByFailure = failed,
            StopFrame = failed ? 43 + frames : null,
            StopReason = failed ? "synthetic retry failure" : null,
            Components = new List<ManualMaskTrackComponent> { component }
        };
    }

    Assert(!ManualMaskKeyframeTimeline.SetTrackSegment(retryProvider, MakeSegment(3)),
        "Initial validated interval was not saved");
    Assert(ManualMaskKeyframeTimeline.SetTrackSegment(retryProvider,
            MakeSegment(1, failed: true)),
        "An early-failing retry erased an existing longer track");
    Assert(ManualMaskKeyframeTimeline.HasLongerCurrentSegment(retryProvider, MakeSegment(1)),
        "UI did not recognize preserved verified coverage");
    Assert(ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(retryProvider, 45,
            out var retainedMask), "Preview lost the previously verified tail");
    using (retainedMask)
        Assert(AlphaAt(retainedMask, 64, 34) == 255,
            "Previously verified tail was replaced by a shorter retry");
    using (var exportLease = ManualMaskKeyframeTimeline.CreateExportMaskLease(retryProvider))
    using (var exported = exportLease.Provider.GetFinalMask(45)
           ?? throw new InvalidOperationException("Export lost verified tail"))
        Assert(AlphaAt(exported, 64, 34) == 255,
            "Export snapshot lost the previously verified tail");
    Assert(ManualMaskTrackStore.LoadForExport(retryVideo).Single().EndExclusive == 46,
        "Short retry truncated persisted verified tail");
    Assert(ManualMaskKeyframeTimeline.SetTrackSegment(retryProvider,
            MakeSegment(3, failed: true)),
        "An equally long failed retry downgraded a successful track");
    Assert(!ManualMaskKeyframeTimeline.SetTrackSegment(retryProvider,
            MakeSegment(3, offsetX: 20)),
        "An equally long successful retry could not replace its predecessor");
    Assert(ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(retryProvider, 45,
            out var updatedMask), "Successful retrack was not applied");
    using (updatedMask)
        Assert(AlphaAt(updatedMask, 84, 34) == 255 && AlphaAt(updatedMask, 64, 34) == 0,
            "Successful full-length retrack did not replace the previous motion");
    Assert(ManualMaskTrackStore.LoadForExport(retryVideo).Single()
        .Components[0].Samples[^1].OffsetX == 20,
        "Successful full-length retrack was not persisted");
}
finally
{
    File.Delete(retryVideo);
}
Console.WriteLine("PASS: shorter/failed retracks preserve verified preview/export/disk tail; full retry replaces it");

Console.WriteLine("PASS: cached preview reuses dirty regions and invalidates on Auto/frame change");''')

path = 'docs/MANUAL_OVERLAY_WORKFLOW.md'
file = Path(path)
file.write_text(file.read_text(encoding='utf-8') + f'''

## Change log — 2026-09-20: preserve verified tracking on retry

- **Why:** `ManualMaskKeyframeTimeline.SetTrackSegment` previously unconditionally replaced a segment with the same source frame. A shorter or early-failing retry could silently erase already verified later frames in preview, export and the persisted tracking JSON.
- **What:** Preserve a longer, still-current track when the new candidate has the same source fingerprint but a shorter interval; also preserve a successful interval over an equally long failed retry. Different-source or equally long successful retries may replace the prior track. Surface a retention message in the tracking status. This protects the existing single-global-keyframe workflow; it does not add per-target IDs.
- **Tests:** Headless application regression now checks early failure, shorter interval, preview, export snapshot, persisted state, an equally long failed retry and a successful full-length retrack. Run: https://github.com/doanythingK/FaceShield_/actions/runs/{os.environ.get('GITHUB_RUN_ID', 'unknown')} (the one-shot integration; complete cross-platform Quality Gate must be verified separately).
- **Remaining:** No per-face target identity/selection or independent same-target correction boundaries, no real-face tracking accuracy benchmark and no packaged GUI/export parity test.
''', encoding='utf-8')

print('PASS: guarded retry retention, UI feedback, headless preview/export/persistence regression and changelog staged')
