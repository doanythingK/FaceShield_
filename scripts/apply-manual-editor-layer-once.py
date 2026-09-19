#!/usr/bin/env python3
"""One-shot guarded source edit; the verifier commits only after build and tests."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
view = root / 'ViewModels/Workspace/FramePreviewViewModel.cs'
text = view.read_text(encoding='utf-8')

start = text.index('    private void RefreshPreview(bool force = false)\n')
end = text.index('    private void EnsureBlurredFrame()\n', start)
old_preview = text[start:end]
assert old_preview.count('PreviewBlurProcessor.ComposeMaskedPreview(') == 1
assert old_preview.count('PreviewBlurProcessor.CreateBlurPreview(') == 1
new_preview = '''    private void RefreshPreview(bool force = false)
    {
        if (_frameBitmap == null || _maskBitmap == null) return;
        if (!force)
        {
            long now = Environment.TickCount64;
            if (now - _lastPreviewTick < PreviewThrottleMs)
                return;
            _lastPreviewTick = now;
        }
        else
        {
            _lastPreviewTick = Environment.TickCount64;
        }

        IReadOnlyList<Rect>? faces = null;
        if (_maskProvider is FrameMaskProvider provider &&
            _currentFrameIndex >= 0 &&
            provider.TryGetFaceMaskData(_currentFrameIndex, out var data))
            faces = data.Faces;

        // The editable mask is manual-only in manual-keyframe workspaces. Auto
        // is merged into a disposable PREVIEW bitmap, never into editable state.
        // This preserves a user-painted manual pixel even if Auto overlaps it.
        WriteableBitmap? previewComposite = null;
        try
        {
            WriteableBitmap previewMask = _maskBitmap;
            if (_manualMaskKeyframesEnabled &&
                _maskProvider is FrameMaskProvider manualProvider &&
                _currentFrameIndex >= 0 &&
                manualProvider.TryGetFaceMaskData(_currentFrameIndex, out var automatic) &&
                automatic.Faces.Count > 0)
            {
                previewComposite = ManualMaskEditorLayer.ComposeWithAutomatic(
                    _maskBitmap, automatic);
                previewMask = previewComposite;
            }

            if (_manualMaskKeyframesEnabled || faces == null || faces.Count == 0 || _isDrawing)
            {
                EnsureBlurredFrame();
                Rect? dirtyRect = null;
                if (!force && _hasDirtyRegion)
                {
                    dirtyRect = new Rect(
                        _dirtyX0,
                        _dirtyY0,
                        Math.Max(0, _dirtyX1 - _dirtyX0 + 1),
                        Math.Max(0, _dirtyY1 - _dirtyY0 + 1));
                }

                var preview = PreviewBlurProcessor.ComposeMaskedPreview(
                    _frameBitmap,
                    _blurredFrame!,
                    previewMask,
                    _ownsPreviewBitmap ? _previewBitmap : null,
                    dirtyRect);
                SetPreviewBitmap(preview, ownsBitmap: true);
            }
            else
            {
                SetPreviewBitmap(
                    PreviewBlurProcessor.CreateBlurPreview(
                        _frameBitmap, previewMask, PreviewBlurRadius, faces),
                    ownsBitmap: true);
            }

            _hasDirtyRegion = false;
        }
        finally
        {
            previewComposite?.Dispose();
        }
    }

'''
text = text[:start] + new_preview + text[end:]

start = text.index('    private WriteableBitmap? CreateEditableMask(int frameIndex, WriteableBitmap frame)\n')
end = text.index('    public void PersistCurrentMask()\n', start)
old_create = text[start:end]
anchor = '''    {
        if (_maskProvider is FrameMaskProvider provider)
'''
replacement = '''    {
        // Never make Auto pixels editable manual pixels: the union cannot
        // distinguish a new brush stroke inside an already-opaque Auto mask.
        if (_manualMaskKeyframesEnabled &&
            _maskProvider is FrameMaskProvider manualProvider)
            return ManualMaskEditorLayer.CreateEditableMask(
                manualProvider, frameIndex, frame.PixelSize);

        if (_maskProvider is FrameMaskProvider provider)
'''
if old_create.count(anchor) != 1:
    raise RuntimeError('CreateEditableMask anchor changed')
text = text[:start] + old_create.replace(anchor, replacement, 1) + text[end:]

old_persist = '''        _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));
        _maskDirty = false;
'''
new_persist = '''        // Manual-keyframe edits already contain ONLY the user's manual layer.
        // SetMask would strip any pixels overlapped by Auto and lose coverage.
        if (_manualMaskKeyframesEnabled && _maskProvider is FrameMaskProvider manualProvider)
            manualProvider.SetIndependentManualMask(_currentFrameIndex, CloneBitmap(_maskBitmap));
        else
            _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));
        _maskDirty = false;
'''
if text.count(old_persist) != 1:
    raise RuntimeError('PersistCurrentMask anchor changed')
text = text.replace(old_persist, new_persist, 1)
view.write_text(text, encoding='utf-8')

keyframes = root / 'ViewModels/Workspace/FramePreviewViewModel.ManualKeyframes.cs'
text = keyframes.read_text(encoding='utf-8')
start = text.index('        // Undo can return a tracked/inherited frame exactly to the effective mask it\n')
end = text.index('        NotifyManualMaskEditedForTracking(_currentFrameIndex);\n', start)
new_undo = '''        // Compare manual-only baselines. A composite Auto mask would make an
        // Undo result look like a new manual correction, or hide an overlap edit.
        using (var baseline = ManualMaskEditorLayer.CreateEditableMask(
                   provider, _currentFrameIndex, _maskBitmap.PixelSize))
        {
            if (string.Equals(
                    ManualMaskFingerprint.Compute(baseline),
                    ManualMaskFingerprint.Compute(_maskBitmap),
                    StringComparison.Ordinal))
            {
                _maskDirty = false;
                _manualTrackingPendingSourceValidationFrame = -1;
                ManualTrackingStatusText =
                    "되돌리기로 원래 마스크 상태가 복원되어 기존 추적을 유지합니다.";
                OnPropertyChanged(nameof(CanTrackForward));
                return;
            }
        }

'''
text = text[:start] + new_undo + text[end:]
old_inherited = '''        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider,
                _currentFrameIndex,
                out var inherited))
'''
new_inherited = '''        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                provider,
                _currentFrameIndex,
                out var inherited))
'''
if text.count(old_inherited) != 1:
    raise RuntimeError('inherited manual-only mask anchor changed')
text = text.replace(old_inherited, new_inherited, 1)
keyframes.write_text(text, encoding='utf-8')

regression = root / 'scripts/frame-mask-layer-regression.cs.txt'
text = regression.read_text(encoding='utf-8')
old_end = 'Console.WriteLine("PASS: same-frame editor composite stores manual residual; Auto/manual layers remain independent and compose");'
new_end = '''// New manual-keyframe editor path: Auto is visible but NOT writable. Paint
// inside an opaque Auto face, persist manual-only, then move Auto away: the
// overlapping user stroke must remain. This was lost by alpha subtraction.
const int overlappingFrame = frame + 1;
provider.SetFaceRects(overlappingFrame, new[] { autoFace }, size, 0.9f, new[] { 0.9f });
using (var manualEditor = ManualMaskEditorLayer.CreateEditableMask(provider, overlappingFrame, size))
{
    Assert(AlphaAt(manualEditor, 32, 38) == 0 && AlphaAt(manualEditor, 40, 38) == 0,
        "Auto pixels leaked into the writable manual layer");
    PaintRect(manualEditor, 32, 36, 6, 6, 255);
    PaintRect(manualEditor, 112, 28, 24, 24, 255);
    Assert(provider.TryGetFaceMaskData(overlappingFrame, out var automatic),
        "Overlapping Auto face missing");
    using (var preview = ManualMaskEditorLayer.ComposeWithAutomatic(manualEditor, automatic))
    {
        Assert(AlphaAt(preview, 40, 38) > 0, "Preview lost Auto-only pixels");
        Assert(AlphaAt(preview, 34, 38) == 255, "Preview lost overlapped manual pixels");
        Assert(AlphaAt(preview, 120, 36) == 255, "Preview lost independent manual pixels");
    }
    provider.SetIndependentManualMask(overlappingFrame, Clone(manualEditor));
}
using (var reopened = ManualMaskEditorLayer.CreateEditableMask(provider, overlappingFrame, size))
{
    Assert(AlphaAt(reopened, 40, 38) == 0, "Auto-only pixels were persisted as manual");
    Assert(AlphaAt(reopened, 34, 38) == 255, "Overlapping manual stroke was lost on reload");
}
provider.SetFaceRects(overlappingFrame, new[] { new Rect(54, 20, 26, 26) }, size);
using (var finalOverlap = provider.GetFinalMask(overlappingFrame)
       ?? throw new InvalidOperationException("Overlapping final mask missing"))
{
    Assert(AlphaAt(finalOverlap, 34, 38) == 255,
        "Manual stroke vanished when Auto moved off the overlapping pixels");
    Assert(AlphaAt(finalOverlap, 66, 32) > 0,
        "Updated Auto face vanished when manual overlap was preserved");
}

Console.WriteLine("PASS: legacy residual and manual-only editor retain Auto/manual alpha, including overlap and Auto relocation");'''
if text.count(old_end) != 1:
    raise RuntimeError('mask layer regression anchor changed')
regression.write_text(text.replace(old_end, new_end, 1), encoding='utf-8')

# Fail if the three production connections are lost during later refactoring.
assert 'ManualMaskEditorLayer.ComposeWithAutomatic(' in view.read_text()
assert 'manualProvider.SetIndependentManualMask(' in view.read_text()
assert 'ManualMaskEditorLayer.CreateEditableMask(' in view.read_text()
assert 'ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(' in keyframes.read_text()
print('PASS: staged manual-only editor, preview composition, Undo and overlapping Auto regression')
