// FILE: D:\WorkSpace\FaceShield\ViewModels\Workspace\FramePreviewViewModel.cs
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel : ViewModelBase
{
    private readonly ToolPanelViewModel _toolPanel;
    private IFrameMaskProvider? _maskProvider;

    private WriteableBitmap? _frameBitmap;
    private WriteableBitmap? _maskBitmap;
    private WriteableBitmap? _previewBitmap;
    private WriteableBitmap? _blurredFrame;
    private WriteableBitmap? _blurredSource;
    private int _blurredRadius;

    private VideoSession? _session;

    private bool _isDrawing;
    private readonly Stack<byte[]> _maskUndo = new();
    private int _changeStamp;
    private bool _isPlaying;
    private bool _playbackDecodeActive;
    private int _queuedPlaybackFrameIndex = -1;
    private int _lastAppliedPlaybackFrameIndex = -1;
    private int _currentFrameIndex = -1;
    private bool _maskDirty;
    private Point? _lastDrawPoint;
    private long _lastPreviewTick;
    private const int PreviewThrottleMs = 40;
    private bool _hasDirtyRegion;
    private int _dirtyX0;
    private int _dirtyY0;
    private int _dirtyX1;
    private int _dirtyY1;
    private IReadOnlyList<Rect> _detectionRects = Array.Empty<Rect>();
    private bool _showDetectionOverlay;

    public WriteableBitmap? FrameBitmap
    {
        get => _frameBitmap;
        private set
        {
            _frameBitmap = value;
            OnPropertyChanged(nameof(FrameBitmap));
        }
    }

    public WriteableBitmap? MaskBitmap
    {
        get => _maskBitmap;
        private set
        {
            _maskBitmap = value;
            OnPropertyChanged(nameof(MaskBitmap));
        }
    }

    public WriteableBitmap? PreviewBitmap
    {
        get => _previewBitmap;
        private set
        {
            _previewBitmap = value;
            OnPropertyChanged(nameof(PreviewBitmap));
        }
    }

    public EditMode CurrentMode => _toolPanel.CurrentMode;

    public int BrushDiameter => _toolPanel.BrushDiameter;

    public bool ShowBrushCursor => _toolPanel.ShowBrushSize;

    public IReadOnlyList<Rect> DetectionRects
    {
        get => _detectionRects;
        private set
        {
            _detectionRects = value ?? Array.Empty<Rect>();
            OnPropertyChanged(nameof(DetectionRects));
        }
    }

    public bool ShowDetectionOverlay
    {
        get => _showDetectionOverlay;
        set
        {
            if (_showDetectionOverlay == value)
                return;
            _showDetectionOverlay = value;
            OnPropertyChanged(nameof(ShowDetectionOverlay));
        }
    }

    public event Action<int>? MaskEdited;

    public Cursor CurrentCursor =>
        CurrentMode switch
        {
            EditMode.Brush => Cursor.Parse("Cross"),
            EditMode.Eraser => Cursor.Parse("No"),
            EditMode.Auto => Cursor.Parse("Hand"),
            _ => Cursor.Default
        };

    public int PreviewBlurRadius { get; set; } = 28;

    public FramePreviewViewModel(ToolPanelViewModel toolPanel, IFrameMaskProvider maskProvider)
    {
        _toolPanel = toolPanel;
        _maskProvider = maskProvider;
        PreviewBlurRadius = toolPanel.BlurRadius;

        _toolPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolPanelViewModel.CurrentMode))
            {
                OnPropertyChanged(nameof(CurrentCursor));
                OnPropertyChanged(nameof(ShowBrushCursor));
            }
            else if (e.PropertyName == nameof(ToolPanelViewModel.BrushDiameter))
            {
                OnPropertyChanged(nameof(BrushDiameter));
            }
            else if (e.PropertyName == nameof(ToolPanelViewModel.BlurRadius))
            {
                PreviewBlurRadius = _toolPanel.BlurRadius;
                _blurredFrame = null;
                _blurredSource = null;
                RefreshPreview(force: true);
            }
        };
    }

    public void Undo()
    {
        if (_maskBitmap == null) return;
        if (_maskUndo.Count == 0) return;

        var bytes = _maskUndo.Pop();
        RestoreMaskBytes(_maskBitmap, bytes);
        _maskDirty = true;

        RefreshPreview(force: true);
    }

    public void OnPointerPressed(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_maskBitmap == null || _frameBitmap == null) return;

        // ✅ 인스턴스 오버로드 사용 (인수 1개)
        PushUndoSnapshot(_maskBitmap);
        _isDrawing = true;
        _lastDrawPoint = point;
        DrawStroke(point, point);
    }

    public void OnPointerMoved(Point point)
    {
        if (!_isDrawing) return;
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_lastDrawPoint == null)
        {
            _lastDrawPoint = point;
            DrawStroke(point, point);
            return;
        }

        DrawStroke(_lastDrawPoint.Value, point);
        _lastDrawPoint = point;
    }

    public void OnPointerReleased(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        _isDrawing = false;
        _lastDrawPoint = null;
        RefreshPreview(force: true);
        if (_maskDirty && _currentFrameIndex >= 0)
            MaskEdited?.Invoke(_currentFrameIndex);
    }

    private void DrawStroke(Point from, Point to)
    {
        if (_maskBitmap is null) return;

        using var fb = _maskBitmap.Lock();
        unsafe
        {
            int radius = Math.Max(1, _toolPanel.BrushDiameter / 2);
            MarkDirty(from, to, radius, fb.Size.Width, fb.Size.Height);

            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;

            void DrawCircleAt(int x, int y)
            {
                if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height)
                    return;

                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(fb.Size.Width - 1, x + radius);
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(fb.Size.Height - 1, y + radius);

                for (int yy = y0; yy <= y1; yy++)
                {
                    byte* row = basePtr + yy * stride;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        int dx = xx - x;
                        int dy = yy - y;
                        if (dx * dx + dy * dy > radius * radius) continue;

                        byte* p = row + xx * 4;

                        if (CurrentMode == EditMode.Brush)
                        {
                            p[0] = 255; p[1] = 255; p[2] = 255; p[3] = 255;
                        }
                        else
                        {
                            p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 0;
                        }
                    }
                }
            }

            double dxLine = to.X - from.X;
            double dyLine = to.Y - from.Y;
            double dist = Math.Sqrt(dxLine * dxLine + dyLine * dyLine);
            if (dist < 0.001)
            {
                DrawCircleAt((int)Math.Round(to.X), (int)Math.Round(to.Y));
            }
            else
            {
                double step = Math.Max(1.0, radius * 0.5);
                int steps = Math.Max(1, (int)Math.Ceiling(dist / step));
                double stepX = dxLine / steps;
                double stepY = dyLine / steps;

                double x = from.X;
                double y = from.Y;
                for (int i = 0; i <= steps; i++)
                {
                    DrawCircleAt((int)Math.Round(x), (int)Math.Round(y));
                    x += stepX;
                    y += stepY;
                }
            }
        }

        OnPropertyChanged(nameof(MaskBitmap));
        RefreshPreview();
        _maskDirty = true;
    }

    private void RefreshPreview(bool force = false)
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
        {
            faces = data.Faces;
        }

        if (faces == null || faces.Count == 0 || _isDrawing)
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

            PreviewBitmap = PreviewBlurProcessor.ComposeMaskedPreview(
                _frameBitmap,
                _blurredFrame!,
                _maskBitmap,
                PreviewBitmap,
                dirtyRect);
        }
        else
        {
            PreviewBitmap = PreviewBlurProcessor.CreateBlurPreview(_frameBitmap, _maskBitmap, PreviewBlurRadius, faces);
        }

        _hasDirtyRegion = false;
    }

    private void EnsureBlurredFrame()
    {
        if (_frameBitmap == null)
            return;

        if (_blurredFrame == null ||
            _blurredRadius != PreviewBlurRadius ||
            !ReferenceEquals(_blurredSource, _frameBitmap))
        {
            _blurredFrame = PreviewBlurProcessor.CreateBlurredFrame(_frameBitmap, PreviewBlurRadius);
            _blurredRadius = PreviewBlurRadius;
            _blurredSource = _frameBitmap;
        }
    }


    private static WriteableBitmap CreateEmptyMask(int w, int h)
    {
        return new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
    }

    private static WriteableBitmap CloneBitmap(WriteableBitmap src)
    {
        var dst = new WriteableBitmap(
            src.PixelSize,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using var sfb = src.Lock();
        using var dfb = dst.Lock();

        unsafe
        {
            int h = src.PixelSize.Height;
            int copy = Math.Min(sfb.RowBytes, dfb.RowBytes);

            byte* s = (byte*)sfb.Address;
            byte* d = (byte*)dfb.Address;

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    s + y * sfb.RowBytes,
                    d + y * dfb.RowBytes,
                    dfb.RowBytes,
                    copy);
            }
        }

        return dst;
    }

    private static void PushUndoSnapshot(WriteableBitmap mask, Stack<byte[]> stack)
    {
        using var fb = mask.Lock();
        unsafe
        {
            int bytes = fb.RowBytes * fb.Size.Height;
            var arr = new byte[bytes];
            MarshalCopyToArray((byte*)fb.Address, arr);
            stack.Push(arr);
        }
    }

    // ✅ 인스턴스용 오버로드: 호출부는 이 메서드 사용
    private void PushUndoSnapshot(WriteableBitmap mask)
        => PushUndoSnapshot(mask, _maskUndo);

    private static void RestoreMaskBytes(WriteableBitmap mask, byte[] bytes)
    {
        using var fb = mask.Lock();
        unsafe
        {
            int len = Math.Min(bytes.Length, fb.RowBytes * fb.Size.Height);
            MarshalCopyFromArray(bytes, (byte*)fb.Address, len);
        }
    }

    private static unsafe void MarshalCopyToArray(byte* src, byte[] dst)
    {
        for (int i = 0; i < dst.Length; i++)
            dst[i] = src[i];
    }

    private static unsafe void MarshalCopyFromArray(byte[] src, byte* dst, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] = src[i];
    }

    // WorkspaceViewModel에서 FramePreview 초기화 시 세션 주입
    public void InitializeSession(VideoSession session)
    {
        _session = session;
    }
    public void SetMaskProvider(IFrameMaskProvider maskProvider)
    {
        _maskProvider = maskProvider;
    }
    /// <summary>
    /// 타임라인 / 재생 / 키 이동으로 프레임 인덱스가 바뀔 때 호출.
    /// - 즉시: 썸네일 기반 저화질 프리뷰
    /// - 디바운스 후: 고화질 프레임 + 프레임 인덱스에 맞는 마스크 적용
    /// </summary>
    public async void OnFrameIndexChanged(int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnFrameIndexChanged(index));
            return;
        }

        if (_session == null)
            return;
        if (index < 0)
            return;

        PersistCurrentMask();
        _currentFrameIndex = index;

        if (_isPlaying)
        {
            QueuePlaybackFrame(index);
            return;
        }

        int stamp = Interlocked.Increment(ref _changeStamp);

        // 1) 저화질 썸네일
        try
        {
            var low = _session.Timeline.OnFrameChanging(index);
            if (low != null && stamp == _changeStamp)
                PreviewBitmap = low;
        }
        catch
        {
            // 썸네일 없으면 무시
        }

        // 1-1) 선택 프레임과 동일한 저화질 썸네일 (정확도 우선)
        var exactThumb = await _session.Timeline.OnFrameChangingExactAsync(index);
        if (exactThumb != null && stamp == _changeStamp)
            PreviewBitmap = exactThumb;

        // 2) 고화질 프레임
        var exact = await _session.Timeline.OnFrameChangedAsync(index);
        if (exact == null || stamp != _changeStamp)
        {
            if (!_isPlaying && stamp == _changeStamp)
                await TryLoadExactFallbackAsync(index, stamp);
            Debug.WriteLine($"[FramePreview] exact frame not available (frame={index}, stamp={stamp}).");
            return;
        }

        ApplyExactFrame(exact, index, stamp);
    }

    public async void OnPlaybackStopped(int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPlaybackStopped(index));
            return;
        }

        if (_session == null)
            return;
        if (index < 0)
            return;

        int stamp = Interlocked.Increment(ref _changeStamp);

        await TryLoadExactFallbackAsync(index, stamp);
    }

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        if (!isPlaying)
        {
            _queuedPlaybackFrameIndex = -1;
            _lastAppliedPlaybackFrameIndex = -1;
        }
    }

    private void QueuePlaybackFrame(int index)
    {
        _queuedPlaybackFrameIndex = index;

        if (_playbackDecodeActive)
            return;

        _playbackDecodeActive = true;
        _ = RunPlaybackDecodeLoopAsync();
    }

    private async Task RunPlaybackDecodeLoopAsync()
    {
        try
        {
            while (_isPlaying && _session != null)
            {
                int index = _queuedPlaybackFrameIndex;
                if (index < 0)
                    break;

                _queuedPlaybackFrameIndex = -1;

                var exact = await _session.Timeline.GetExactNowAsync(index);
                if (exact != null &&
                    _isPlaying &&
                    index >= _lastAppliedPlaybackFrameIndex)
                {
                    _lastAppliedPlaybackFrameIndex = index;
                    ApplyExactFrame(exact, index);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Playback naturally cancels outstanding frame loads while stopping or seeking.
        }
        finally
        {
            _playbackDecodeActive = false;

            if (_isPlaying && _queuedPlaybackFrameIndex >= 0)
                QueuePlaybackFrame(_queuedPlaybackFrameIndex);
        }
    }

    private void ApplyExactFrame(WriteableBitmap exact, int index, int? expectedStamp = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyExactFrame(exact, index, expectedStamp));
            return;
        }

        if (expectedStamp.HasValue && expectedStamp.Value != _changeStamp)
            return;

        FrameBitmap = exact;
        _blurredFrame = null;
        _blurredSource = null;

        // 🔹 2-1) 자동/최종 마스크가 이미 있는지 provider에서 먼저 조회
        WriteableBitmap? providerMask = null;
        if (_maskProvider != null)
        {
            providerMask = _maskProvider.GetFinalMask(index);
        }

        if (providerMask != null &&
            providerMask.PixelSize.Width == exact.PixelSize.Width &&
            providerMask.PixelSize.Height == exact.PixelSize.Height)
        {
            // provider 마스크를 직접 수정하면 안 되니 복제해서 사용
            MaskBitmap = CloneBitmap(providerMask);
            _maskUndo.Clear();
            _maskDirty = false;
        }
        else
        {
            // 없으면 프레임별로 새 빈 마스크 생성
            MaskBitmap = CreateEmptyMask(exact.PixelSize.Width, exact.PixelSize.Height);
            _maskUndo.Clear();
            _maskDirty = false;
        }

        if (_maskProvider is FrameMaskProvider faceProvider &&
            faceProvider.TryGetFaceMaskData(index, out var faceData))
        {
            DetectionRects = faceData.Faces;
        }
        else
        {
            DetectionRects = Array.Empty<Rect>();
        }

        // 3) 프리뷰 갱신
        RefreshPreview(force: true);
    }

    public void PersistCurrentMask()
    {
        if (!_maskDirty)
            return;
        if (_maskProvider == null || _currentFrameIndex < 0 || _maskBitmap == null)
            return;

        _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));
        _maskDirty = false;
    }

    private async Task TryLoadExactFallbackAsync(int index, int stamp)
    {
        if (_session == null)
            return;

        var exact = await _session.Timeline.GetExactNowAsync(index);
        if (exact == null || stamp != _changeStamp)
        {
            await Task.Delay(120);
            if (stamp != _changeStamp)
                return;
            exact = await _session.Timeline.GetExactNowAsync(index);
        }

        if (exact == null || stamp != _changeStamp)
            return;

        ApplyExactFrame(exact, index, stamp);
    }

    private void MarkDirty(Point from, Point to, int radius, int width, int height)
    {
        int x0 = (int)Math.Floor(Math.Min(from.X, to.X) - radius);
        int y0 = (int)Math.Floor(Math.Min(from.Y, to.Y) - radius);
        int x1 = (int)Math.Ceiling(Math.Max(from.X, to.X) + radius);
        int y1 = (int)Math.Ceiling(Math.Max(from.Y, to.Y) + radius);

        x0 = Math.Clamp(x0, 0, Math.Max(0, width - 1));
        y0 = Math.Clamp(y0, 0, Math.Max(0, height - 1));
        x1 = Math.Clamp(x1, 0, Math.Max(0, width - 1));
        y1 = Math.Clamp(y1, 0, Math.Max(0, height - 1));

        if (!_hasDirtyRegion)
        {
            _dirtyX0 = x0;
            _dirtyY0 = y0;
            _dirtyX1 = x1;
            _dirtyY1 = y1;
            _hasDirtyRegion = true;
            return;
        }

        _dirtyX0 = Math.Min(_dirtyX0, x0);
        _dirtyY0 = Math.Min(_dirtyY0, y0);
        _dirtyX1 = Math.Max(_dirtyX1, x1);
        _dirtyY1 = Math.Max(_dirtyY1, y1);
    }

}
