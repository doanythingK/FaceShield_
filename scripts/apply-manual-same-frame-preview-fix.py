#!/usr/bin/env python3
from pathlib import Path

def patch(path, before, after):
    file = Path(path)
    content = file.read_text(encoding='utf-8')
    if content.count(before) != 1:
        raise RuntimeError(f'{path}: expected one preview patch anchor, found {content.count(before)}')
    file.write_text(content.replace(before, after), encoding='utf-8')

patch('ViewModels/Workspace/FramePreviewViewModel.cs', '''        else
        {
            SetPreviewBitmap(
                PreviewBlurProcessor.CreateBlurPreview(_frameBitmap, _maskBitmap, PreviewBlurRadius, faces),
                ownsBitmap: true);
        }

        _hasDirtyRegion = false;''', '''        else
        {
            // Manual editing owns its own alpha; do not mutate the edit bitmap
            // while composing Auto for the displayed preview (including strokes).
            WriteableBitmap? composed = null;
            try
            {
                if (_manualMaskKeyframesEnabled &&
                    _maskProvider is FrameMaskProvider automaticProvider &&
                    automaticProvider.TryGetFaceMaskData(_currentFrameIndex, out var automatic))
                {
                    composed = CloneBitmap(_maskBitmap);
                    ManualMaskKeyframeTimeline.UnionAutoInto(composed, automatic);
                }
                SetPreviewBitmap(
                    PreviewBlurProcessor.CreateBlurPreview(
                        _frameBitmap, composed ?? _maskBitmap, PreviewBlurRadius, faces),
                    ownsBitmap: true);
            }
            finally { composed?.Dispose(); }
        }

        _hasDirtyRegion = false;''')

p = 'scripts/manual-auto-overlap-integration.cs.txt'
patch(p, '''    AssertBoth(exactComposite!);
}
provider.SetFaceRects(sourceFrame - 1,''', '''    AssertBoth(exactComposite!);
    // Exercise the real BGRA preview renderer, not only a mask alpha union.
    using var image = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
        PixelFormat.Bgra8888, AlphaFormat.Premul);
    using (var pixels = image.Lock())
    {
        unsafe
        {
            byte* start = (byte*)pixels.Address;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = start + y * pixels.RowBytes + x * 4;
                    byte level = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
                    pixel[0] = pixel[1] = pixel[2] = level;
                    pixel[3] = 255;
                }
        }
    }
    using var rendered = PreviewBlurProcessor.CreateBlurPreview(
        image, exactComposite!, blurRadius: 7,
        faces: new[] { new Rect(9, 10, 14, 14) });
    Check(BlueAt(rendered, 130, 95) != BlueAt(image, 130, 95),
        "BGRA preview skipped manual region outside Auto face bounds");
    Check(BlueAt(rendered, 16, 17) != BlueAt(image, 16, 17),
        "BGRA preview skipped Auto region");
}
provider.SetFaceRects(sourceFrame - 1,''')
patch(p, '''static byte AlphaAt(WriteableBitmap bitmap, int x, int y)
{''', '''static byte BlueAt(WriteableBitmap bitmap, int x, int y)
{
    using var framebuffer = bitmap.Lock();
    unsafe
    {
        byte* row = (byte*)framebuffer.Address + y * framebuffer.RowBytes;
        return row[x * 4];
    }
}

static byte AlphaAt(WriteableBitmap bitmap, int x, int y)
{''')
print('PASS: preview uses immutable manual edit layer plus Auto; BGRA pixel regression staged')
