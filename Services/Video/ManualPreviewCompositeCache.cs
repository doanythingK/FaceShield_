using Avalonia;
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
    internal bool LastComposeWasFullUpdate { get; private set; }

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
            LastComposeWasFullUpdate = fullUpdate;
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
        LastComposeWasFullUpdate = false;
    }

    public void Dispose() => Reset();
}
