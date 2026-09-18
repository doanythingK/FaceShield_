#!/usr/bin/env python3
"""One-time guarded source patch. Run only on refactor/manual-overlay-workflow.
Every expected anchor must occur exactly once; abort instead of guessing.
"""
from pathlib import Path


def replace(path, old, new, count=1):
    p = Path(path)
    s = p.read_text(encoding='utf-8')
    found = s.count(old)
    if found != count:
        raise RuntimeError(f'{path}: expected {count} anchors, found {found}: {old[:90]!r}')
    p.write_text(s.replace(old, new), encoding='utf-8')

p = 'Services/Video/FrameMaskProvider.cs'
replace(p, '            _masks[frameIndex] = mask;\n            _faceMasks.TryRemove(frameIndex, out _);',
           '            // Stored manual and automatically detected masks are separate layers.\n            _masks[frameIndex] = mask;')
replace(p, '    {\n        RemoveStoredMaskLocked(frameIndex);\n\n        if (faces.Length == 0',
           '    {\n        // A new Auto detection must not erase the manually confirmed layer.\n        if (faces.Length == 0')
replace(p, '''            if (_masks.TryGetValue(frameIndex, out var mask))
                return CloneBitmap(mask, CancellationToken.None);

            if (_faceMasks.TryGetValue(frameIndex, out var faces))
                return CreateMaskFromFaceRects(faces.Size, faces.Faces);''', '''            if (_masks.TryGetValue(frameIndex, out var mask))
            {
                WriteableBitmap result = CloneBitmap(mask, CancellationToken.None);
                try
                {
                    if (_faceMasks.TryGetValue(frameIndex, out var overlay))
                        ManualMaskKeyframeTimeline.UnionAutoInto(result, overlay);
                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }

            if (_faceMasks.TryGetValue(frameIndex, out var faces))
                return CreateMaskFromFaceRects(faces.Size, faces.Faces);''')
replace(p, '''            if (_storedMaskFrames.Contains(frameIndex))
                return;

            Rect[] faceArray''', '''            // Auto work is independent of manual stored masks.
            Rect[] faceArray''')
replace(p, '''                // Stored/manual bitmap entries remain authoritative.
                if (_masks.ContainsKey(entry.Key))
                    continue;

                _faceMasks[entry.Key] = entry.Value;''', '''                // Preserve both layers; composition never mutates either.
                _faceMasks[entry.Key] = entry.Value;''')
replace(p, '''                // A manually stored bitmap remains authoritative if an unexpected
                // working-copy mutation attempted to replace it with face rectangles.
                if (_masks.ContainsKey(entry.Key))
                    continue;

                _faceMasks[entry.Key] = entry.Value;''', '''                // Auto and manual are independently retained at the same ordinal.
                _faceMasks[entry.Key] = entry.Value;''')

p = 'Services/Video/ManualMaskKeyframeTimeline.cs'
replace(p, '''    internal static bool TryCloneEffectiveKeyframeMask(FrameMaskProvider provider, int frameIndex,
        out WriteableBitmap mask, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();''', '''    internal static bool TryCloneEffectiveKeyframeMask(FrameMaskProvider provider, int frameIndex,
        out WriteableBitmap mask, CancellationToken cancellationToken = default)
        => TryCloneEffectiveMaskCore(provider, frameIndex, out mask, cancellationToken, includeAuto: true);

    // The editable/trackable layer must never include another face's Auto mask.
    internal static bool TryCloneEffectiveManualMask(FrameMaskProvider provider, int frameIndex,
        out WriteableBitmap mask, CancellationToken cancellationToken = default)
        => TryCloneEffectiveMaskCore(provider, frameIndex, out mask, cancellationToken, includeAuto: false);

    private static bool TryCloneEffectiveMaskCore(FrameMaskProvider provider, int frameIndex,
        out WriteableBitmap mask, CancellationToken cancellationToken, bool includeAuto)
    {
        cancellationToken.ThrowIfCancellationRequested();''')
replace(p, '''        bool independentManual = stored.Length > 0;
        int sourceKeyframe = FindFloorKeyframe(''', '''        bool independentManual = stored.Length > 0;
        if (!includeAuto && !independentManual) return false;
        int sourceKeyframe = FindFloorKeyframe(''')
replace(p, '''            if (!independentManual) return false;
            WriteableBitmap? automatic''', '''            if (!independentManual || !includeAuto) return false;
            WriteableBitmap? automatic''')
replace(p, '''                if (independentManual && provider.TryGetFaceMaskData(frameIndex, out var faces))
                    UnionAutoInto(manual, faces);''', '''                if (includeAuto && independentManual &&
                    provider.TryGetFaceMaskData(frameIndex, out var faces))
                    UnionAutoInto(manual, faces);''')
replace(p, '''        using WriteableBitmap? sourceMask = provider.GetFinalMask(segment.SourceKeyframe);
        if (sourceMask != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))''', '''        using WriteableBitmap? sourceMask = TryCloneSourceMask(provider,
            segment.SourceKeyframe, out WriteableBitmap storedOrAuto) ? storedOrAuto : null;
        if (sourceMask != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))''')
replace(p, '    private static void UnionAutoInto(WriteableBitmap manual, FrameMaskProvider.FaceMaskData data)',
           '    internal static void UnionAutoInto(WriteableBitmap manual, FrameMaskProvider.FaceMaskData data)')
replace(p, '''            using WriteableBitmap? source = _snapshot.GetFinalMask(segment.SourceKeyframe);
            if (source != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))''', '''            using WriteableBitmap? source = _snapshot.TryCloneStoredMask(
                segment.SourceKeyframe, out WriteableBitmap manual)
                ? manual : _snapshot.GetFinalMask(segment.SourceKeyframe);
            if (source != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))''')

p = 'ViewModels/Workspace/FramePreviewViewModel.cs'
replace(p, '        if (faces == null || faces.Count == 0 || _isDrawing)',
           '        if (faces == null || faces.Count == 0)')
replace(p, '''                return FrameMaskProvider.CreateMaskFromFaceRects(faceData.Size, faceData.Faces);
            }

            return null;''', '''                // In manual mode the editor owns only manual alpha. Auto is
                // displayed separately and must not become part of the track source.
                return _manualMaskKeyframesEnabled
                    ? CreateEmptyMask(faceData.Size.Width, faceData.Size.Height)
                    : FrameMaskProvider.CreateMaskFromFaceRects(faceData.Size, faceData.Faces);
            }

            return null;''')
replace(p, '''        _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));''', '''        _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));''')  # guarded presence

p = 'ViewModels/Workspace/FramePreviewViewModel.ManualKeyframes.cs'
replace(p, '''        if (provider.HasStoredMask(_currentFrameIndex) ||
            provider.TryGetFaceMaskData(_currentFrameIndex, out _))''',
           '''        if (provider.HasStoredMask(_currentFrameIndex))''')
replace(p, '''        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider,
                _currentFrameIndex,
                out var inherited))''', '''        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                provider,
                _currentFrameIndex,
                out var inherited))''')
replace(p, '''        if (ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider,
                _currentFrameIndex,
                out var baseline))''', '''        if (ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                provider,
                _currentFrameIndex,
                out var baseline))''')

p = 'ViewModels/Workspace/FramePreviewViewModel.ManualTracking.cs'
replace(p, '''        WriteableBitmap? sourceMask = sourceWasExplicit
            ? provider.GetFinalMask(sourceFrame)
            : CloneBitmap(_maskBitmap);''', '''        // An explicit manual source is never the Auto+manual display composite.
        WriteableBitmap? sourceMask = provider.TryCloneStoredMask(
            sourceFrame, out WriteableBitmap explicitManual)
            ? explicitManual
            : sourceWasExplicit ? provider.GetFinalMask(sourceFrame) : CloneBitmap(_maskBitmap);''')

p = 'ViewModels/Workspace/WorkspaceExportCoordinator.cs'
replace(p, '''            using var sourceMask = _maskProvider.GetFinalMask(segment.SourceKeyframe);''', '''            using var sourceMask = _maskProvider.TryCloneStoredMask(
                segment.SourceKeyframe, out WriteableBitmap explicitManual)
                ? explicitManual : _maskProvider.GetFinalMask(segment.SourceKeyframe);''')
# WorkspaceExportCoordinator already imports FaceShield.Services.Video, but
# WriteableBitmap requires Avalonia.Media.Imaging.
replace(p, 'using FaceShield.Services.Analysis;\n', 'using Avalonia.Media.Imaging;\nusing FaceShield.Services.Analysis;\n')

p = 'Services/Video/MaskedVideoExporter.cs'
replace(p, '''            var (rx0, ry0, rx1, ry1) = faces != null && faces.Count > 0
                ? GetFaceBounds(faces, w, h)
                : GetMaskBounds(maskData, maskStride, w, h);
            if (rx1 <= rx0 || ry1 <= ry0)''', '''            // A manual region may be outside every detected face. The bounds
            // must cover both, otherwise BGRA blur silently drops manual pixels.
            var (rx0, ry0, rx1, ry1) = GetMaskBounds(maskData, maskStride, w, h);
            if (faces != null && faces.Count > 0)
            {
                var (fx0, fy0, fx1, fy1) = GetFaceBounds(faces, w, h);
                if (fx1 > fx0 && fy1 > fy0)
                {
                    if (rx1 <= rx0 || ry1 <= ry0)
                        (rx0, ry0, rx1, ry1) = (fx0, fy0, fx1, fy1);
                    else
                        (rx0, ry0, rx1, ry1) =
                            (Math.Min(rx0, fx0), Math.Min(ry0, fy0),
                             Math.Max(rx1, fx1), Math.Max(ry1, fy1));
                }
            }
            if (rx1 <= rx0 || ry1 <= ry0)''')

p = 'scripts/manual-auto-overlap-integration.cs.txt'
replace(p, '''provider.SetMask(sourceFrame, sourceMask);
provider.SetFaceRects(sourceFrame - 1,''', '''provider.SetMask(sourceFrame, sourceMask);
// Auto at the very same frame must not overwrite the manually selected face.
provider.SetFaceRects(sourceFrame, new[] { new Rect(9, 10, 14, 14) },
    new PixelSize(width, height));
Check(provider.HasEntry(sourceFrame) && provider.TryGetFaceMaskData(sourceFrame, out _),
    "Auto and manual cannot coexist on source frame");
using (var exactComposite = provider.GetFinalMask(sourceFrame))
{
    Check(exactComposite != null, "Source frame lost the union mask");
    AssertBoth(exactComposite!);
}
provider.SetFaceRects(sourceFrame - 1,''')
replace(p, '''timeline.GetMethod("SetTrackSegment", internalStatic)!.Invoke(null,
    new object[] { provider, segment });

MethodInfo preview''', '''timeline.GetMethod("SetTrackSegment", internalStatic)!.Invoke(null,
    new object[] { provider, segment });
// Re-running Auto on the source must neither destroy the manual source nor
// invalidate its independently fingerprinted tracking samples.
provider.SetFaceRects(sourceFrame, new[] { new Rect(9, 10, 14, 14) },
    new PixelSize(width, height));
Check(provider.TryCloneStoredMask(sourceFrame, out var manualAfterAuto),
    "Auto refresh removed explicit manual source");
manualAfterAuto.Dispose();

MethodInfo preview''')

# Record the exact remaining boundary. This is not per-target tracking yet.
p = 'docs/MANUAL_OVERLAY_WORKFLOW.md'
s = Path(p).read_text(encoding='utf-8')
s += '''\n## Same-frame legacy-layer preservation (this change)\n\n- `FrameMaskProvider` retains manual stored bitmaps and Auto face rectangles at the same frame; `GetFinalMask` unions only current-frame alpha. Auto working-copy commits no longer discard Auto solely because manual coverage exists. Legacy workspace state still owns both entries.\n- Manual mode edits/tracks manual-only alpha; exact Auto faces are displayed independently. Source fingerprints and export failure validation use the manual source instead of the display union. Preview and export overlay the current-frame Auto result. BGRA rendering now includes both mask and face bounds.\n- Synthetic regression covers Auto+manual at one source frame, Auto refresh, two later Auto frames crossed by a continuous manual track, preview/export union and missing-sample rejection. **Not yet covered:** independently editing/tracking multiple manual target IDs, GUI usability and real-face blur accuracy. Current generic eraser does not suppress a separate Auto result; a dedicated Auto correction/disable operation is still needed.\n'''
Path(p).write_text(s, encoding='utf-8')
print('PASS: guarded same-frame production patches and cross-frame regression staged')
