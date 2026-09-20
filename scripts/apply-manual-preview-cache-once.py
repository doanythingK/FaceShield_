#!/usr/bin/env python3
"""One-shot guarded changes; workflow deletes this file only after tests pass."""
from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected one anchor, found {count}')
    return text.replace(old, new, 1)

cache_path = Path('Services/Video/ManualPreviewCompositeCache.cs')
if cache_path.exists():
    raise RuntimeError('preview cache already exists; refusing repeat patch')
cache_path.write_text('''using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

/// <summary>
/// UI-thread-owned cache for the current frame's preview ONLY. Auto pixels are
/// never written into the editable manual bitmap. The caller must not dispose
/// a returned composite; Reset/Dispose own the cached bitmaps.
/// </summary>
internal sealed class ManualPreviewCompositeCache : IDisposable
{
    private WriteableBitmap? _automaticMask;
    private WriteableBitmap? _composite;
    private WriteableBitmap? _manualSource;
    private IReadOnlyList<Rect>? _faceRects;
    private int _frameIndex = -1;

    internal WriteableBitmap Compose(
        WriteableBitmap manual,
        FrameMaskProvider.FaceMaskData automatic,
        int frameIndex,
        Rect? dirtyRect = null)
    {
        ArgumentNullException.ThrowIfNull(manual);
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (manual.PixelSize != automatic.Size || automatic.Faces.Count == 0)
            throw new InvalidOperationException("Preview Auto and manual mask dimensions or faces are invalid.");

        bool rebuild = _automaticMask == null || _composite == null ||
            _frameIndex != frameIndex ||
            !ReferenceEquals(_faceRects, automatic.Faces) ||
            _composite.PixelSize != manual.PixelSize;
        try
        {
            if (rebuild)
            {
                Reset();
                _automaticMask = FrameMaskProvider.CreateMaskFromFaceRects(
                    automatic.Size, automatic.Faces);
                _composite = new WriteableBitmap(manual.PixelSize, manual.Dpi,
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
                _frameIndex = frameIndex;
                _faceRects = automatic.Faces;
            }

            bool fullUpdate = rebuild || !ReferenceEquals(_manualSource, manual) ||
                !dirtyRect.HasValue;
            _manualSource = manual;
            int width = manual.PixelSize.Width, height = manual.PixelSize.Height;
            int x0 = 0, y0 = 0, x1 = width, y1 = height;
            if (!fullUpdate)
            {
                Rect rect = dirtyRect!.Value;
                x0 = Math.Clamp((int)Math.Floor(rect.X), 0, width);
                y0 = Math.Clamp((int)Math.Floor(rect.Y), 0, height);
                x1 = Math.Clamp((int)Math.Ceiling(rect.Right), 0, width);
                y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, height);
                if (x1 <= x0 || y1 <= y0)
                    return _composite!;
            }

            using var source = manual.Lock();
            using var auto = _automaticMask!.Lock();
            using var result = _composite!.Lock();
            unsafe
            {
                byte* manualBase = (byte*)source.Address;
                byte* autoBase = (byte*)auto.Address;
                byte* resultBase = (byte*)result.Address;
                for (int y = y0; y < y1; y++)
                {
                    byte* manualRow = manualBase + y * source.RowBytes;
                    byte* autoRow = autoBase + y * auto.RowBytes;
                    byte* outputRow = resultBase + y * result.RowBytes;
                    for (int x = x0; x < x1; x++)
                    {
                        int p = x * 4;
                        byte alpha = Math.Max(manualRow[p + 3], autoRow[p + 3]);
                        outputRow[p] = alpha;
                        outputRow[p + 1] = alpha;
                        outputRow[p + 2] = alpha;
                        outputRow[p + 3] = alpha;
                    }
                }
            }
            return _composite!;
        }
        catch
        {
            Reset();
            throw;
        }
    }

    internal void Reset()
    {
        _composite?.Dispose();
        _composite = null;
        _automaticMask?.Dispose();
        _automaticMask = null;
        _manualSource = null;
        _faceRects = null;
        _frameIndex = -1;
    }

    public void Dispose() => Reset();
}
''', encoding='utf-8')

vm_path = Path('ViewModels/Workspace/FramePreviewViewModel.cs')
vm = vm_path.read_text(encoding='utf-8-sig')
vm = replace_once(vm,
    '    private int _blurredRadius;\n',
    '    private int _blurredRadius;\n    private readonly ManualPreviewCompositeCache _manualPreviewCache = new();\n',
    'preview cache field')
vm = replace_once(vm,
    '        ResetBlurredFrame();\n    }\n\n    public EditMode CurrentMode',
    '        _manualPreviewCache.Reset();\n        ResetBlurredFrame();\n    }\n\n    public EditMode CurrentMode',
    'frame replacement cleanup')
vm = replace_once(vm,
    '        CancelManualFrameLoad();\n        PreviewBlurProcessor.ReleaseCachedRenderer();',
    '        CancelManualFrameLoad();\n        _manualPreviewCache.Reset();\n        PreviewBlurProcessor.ReleaseCachedRenderer();',
    'session switch cleanup')
vm = replace_once(vm,
    '        SetPreviewBitmap(null, ownsBitmap: false);\n        ResetBlurredFrame();\n        FrameBitmap = null;',
    '        SetPreviewBitmap(null, ownsBitmap: false);\n        _manualPreviewCache.Reset();\n        ResetBlurredFrame();\n        FrameBitmap = null;',
    'dispose cleanup')
start_marker = '        // The editable mask is manual-only in manual-keyframe workspaces.'
end_marker = '\n    private void EnsureBlurredFrame()'
if vm.count(start_marker) != 1 or vm.count(end_marker) != 1:
    raise RuntimeError('preview method anchors changed')
start = vm.index(start_marker)
end = vm.index(end_marker, start)
vm = vm[:start] + '''        // Editable pixels remain manual-only. Reuse this frame's Auto raster and
        // preview union; brush updates touch only the changed rectangle.
        Rect? dirtyRect = null;
        if (!force && _hasDirtyRegion)
            dirtyRect = new Rect(_dirtyX0, _dirtyY0,
                Math.Max(0, _dirtyX1 - _dirtyX0 + 1),
                Math.Max(0, _dirtyY1 - _dirtyY0 + 1));

        WriteableBitmap previewMask = _maskBitmap;
        if (_manualMaskKeyframesEnabled &&
            _maskProvider is FrameMaskProvider manualProvider &&
            _currentFrameIndex >= 0 &&
            manualProvider.TryGetFaceMaskData(_currentFrameIndex, out var automatic) &&
            automatic.Faces.Count > 0)
        {
            previewMask = _manualPreviewCache.Compose(
                _maskBitmap, automatic, _currentFrameIndex, dirtyRect);
        }
        else
        {
            _manualPreviewCache.Reset();
        }

        if (_manualMaskKeyframesEnabled || faces == null || faces.Count == 0 || _isDrawing)
        {
            EnsureBlurredFrame();
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
''' + vm[end:]
vm_path.write_text(vm, encoding='utf-8')

regression_path = Path('scripts/frame-mask-layer-regression.cs.txt')
regression = regression_path.read_text(encoding='utf-8')
marker = 'Console.WriteLine("PASS: legacy residual and manual-only editor retain Auto/manual alpha, including overlap and Auto relocation");'
addition = '''// The production preview cache must preserve an overlapping manual stroke,
// reuse the same bitmap during brush movement, and invalidate on Auto changes.
const int cachedFrame = 27;
provider.SetFaceRects(cachedFrame, new[] { autoFace }, size);
using (var editable = ManualMaskEditorLayer.CreateEditableMask(provider, cachedFrame, size))
using (var cache = new ManualPreviewCompositeCache())
{
    PaintRect(editable, 112, 28, 24, 24, 255);
    Assert(provider.TryGetFaceMaskData(cachedFrame, out var originalAuto),
        "Missing Auto layer for preview-cache test");
    var initial = cache.Compose(editable, originalAuto, cachedFrame);
    Assert(AlphaAt(initial, 40, 38) > 0 && AlphaAt(initial, 120, 36) == 255,
        "Initial cached preview lost Auto or manual pixels");
    PaintRect(editable, 32, 36, 6, 6, 255);
    var reused = cache.Compose(editable, originalAuto, cachedFrame,
        new Rect(32, 36, 6, 6));
    Assert(ReferenceEquals(initial, reused),
        "Brush movement allocated a new preview composite");
    Assert(AlphaAt(reused, 34, 38) == 255 && AlphaAt(reused, 120, 36) == 255,
        "Dirty preview update lost an overlapping or unchanged manual pixel");
    provider.SetFaceRects(cachedFrame, new[] { new Rect(54, 20, 26, 26) }, size);
    Assert(provider.TryGetFaceMaskData(cachedFrame, out var movedAuto),
        "Missing updated Auto layer for preview-cache test");
    var rebuilt = cache.Compose(editable, movedAuto, cachedFrame,
        new Rect(32, 36, 6, 6));
    Assert(!ReferenceEquals(initial, rebuilt),
        "Changing Auto did not invalidate the cached preview composite");
    Assert(AlphaAt(rebuilt, 34, 38) == 255 && AlphaAt(rebuilt, 40, 38) == 0 &&
           AlphaAt(rebuilt, 66, 32) > 0 && AlphaAt(rebuilt, 120, 36) == 255,
        "Auto relocation lost an overlapping manual pixel or retained stale Auto pixels");
    Assert(AlphaAt(editable, 40, 38) == 0 && AlphaAt(editable, 34, 38) == 255,
        "Preview cache mutated the editable manual layer");
    var nextFrame = cache.Compose(editable, movedAuto, cachedFrame + 1,
        new Rect(32, 36, 6, 6));
    Assert(!ReferenceEquals(rebuilt, nextFrame),
        "Switching frames retained a stale cached bitmap");
}

Console.WriteLine("PASS: cached preview reuses dirty regions and invalidates on Auto/frame change");

'''
regression = replace_once(regression, marker, addition + marker, 'headless regression insertion')
regression_path.write_text(regression, encoding='utf-8')

verify_path = Path('scripts/verify-manual-overlay.sh')
verify = verify_path.read_text(encoding='utf-8')
verify = replace_once(verify,
    "    'ManualMaskEditorLayer.ComposeWithAutomatic(' \\\n    'manualProvider.SetIndependentManualMask('; do",
    "    'ManualMaskEditorLayer.CreateEditableMask(' \\\n    'manualProvider.SetIndependentManualMask('; do",
    'remove stale direct composite guard') if False else verify
# Keep the existing old compositor as a compatibility API, but ensure the
# real view-model calls the new cached compositor instead of that API.
anchor = "echo 'PASS: editor stores manual-only alpha and composites Auto only for preview'\n"
verify = replace_once(verify, anchor,
    "if ! grep -Fq '_manualPreviewCache.Compose(' \"$editor_view\"; then\n"
    "    echo 'ERROR: manual preview cache is not wired to the editor' >&2\n"
    "    exit 1\nfi\n" + anchor,
    'cache wiring guard')
verify_path.write_text(verify, encoding='utf-8')
print('PASS: staged preview-cache class, UI lifecycle, dirty-region regression and wiring guard')
