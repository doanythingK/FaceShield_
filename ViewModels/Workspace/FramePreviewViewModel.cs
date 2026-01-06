// FILE: D:\WorkSpace\FaceShield\ViewModels\Workspace\FramePreviewViewModel.cs
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
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

    private VideoSession? _session;

    private bool _isDrawing;
    private readonly Stack<byte[]> _maskUndo = new();
    private int _changeStamp;
    private bool _isPlaying;
    private int _currentFrameIndex = -1;
    private bool _maskDirty;

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

    public Cursor CurrentCursor =>
        CurrentMode switch
        {
            EditMode.Brush => Cursor.Parse("Cross"),
            EditMode.Eraser => Cursor.Parse("No"),
            EditMode.Auto => Cursor.Parse("Hand"),
            _ => Cursor.Default
        };

    public int PreviewBlurRadius { get; set; } = 20;

    public FramePreviewViewModel(ToolPanelViewModel toolPanel, IFrameMaskProvider maskProvider)
    {
        _toolPanel = toolPanel;
        _maskProvider = maskProvider;

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
        };
    }

    public void Undo()
    {
        if (_maskBitmap == null) return;
        if (_maskUndo.Count == 0) return;

        var bytes = _maskUndo.Pop();
        RestoreMaskBytes(_maskBitmap, bytes);
        _maskDirty = true;

        RefreshPreview();
    }

    public void OnPointerPressed(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_maskBitmap == null || _frameBitmap == null) return;

        // ✅ 인스턴스 오버로드 사용 (인수 1개)
        PushUndoSnapshot(_maskBitmap);
        _isDrawing = true;
        DrawPoint(point);
    }

    public void OnPointerMoved(Point point)
    {
        if (!_isDrawing) return;
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        DrawPoint(point);
    }

    public void OnPointerReleased(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        _isDrawing = false;
    }

    private void DrawPoint(Point point)
    {
        if (_maskBitmap is null) return;

        using var fb = _maskBitmap.Lock();
        unsafe
        {
            int x = (int)point.X;
            int y = (int)point.Y;

            if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height)
                return;

            int radius = Math.Max(1, _toolPanel.BrushDiameter / 2);

            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;

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

        OnPropertyChanged(nameof(MaskBitmap));
        RefreshPreview();
        _maskDirty = true;
    }

    private void RefreshPreview()
    {
        if (_frameBitmap == null || _maskBitmap == null) return;
        PreviewBitmap = PreviewBlurProcessor.CreateBlurPreview(_frameBitmap, _maskBitmap, PreviewBlurRadius);
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
        if (_session == null)
            return;
        if (index < 0)
            return;

        PersistCurrentMask();
        _currentFrameIndex = index;

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

        ApplyExactFrame(exact, index);
    }

    public async void OnPlaybackStopped(int index)
    {
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
    }

    private void ApplyExactFrame(WriteableBitmap exact, int index)
    {
        FrameBitmap = exact;

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

        // 3) 프리뷰 갱신
        RefreshPreview();
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

        ApplyExactFrame(exact, index);
    }

}
