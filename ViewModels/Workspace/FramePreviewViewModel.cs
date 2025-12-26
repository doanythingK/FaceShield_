// FILE: D:\WorkSpace\FaceShield\ViewModels\Workspace\FramePreviewViewModel.cs
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel : ViewModelBase
{
    private readonly ToolPanelViewModel _toolPanel;

    private WriteableBitmap? _frameBitmap;
    private WriteableBitmap? _maskBitmap;
    private WriteableBitmap? _previewBitmap;

    private VideoSession? _session;

    private bool _isDrawing;
    private readonly Stack<byte[]> _maskUndo = new();

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

    public Cursor CurrentCursor =>
        CurrentMode switch
        {
            EditMode.Brush => Cursor.Parse("Cross"),
            EditMode.Eraser => Cursor.Parse("No"),
            EditMode.Auto => Cursor.Parse("Hand"),
            _ => Cursor.Default
        };

    public int PreviewBlurRadius { get; set; } = 6;

    public FramePreviewViewModel(ToolPanelViewModel toolPanel)
    {
        _toolPanel = toolPanel;
        _toolPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolPanelViewModel.CurrentMode))
                OnPropertyChanged(nameof(CurrentCursor));
        };
    }

    public void Undo()
    {
        if (_maskBitmap == null) return;
        if (_maskUndo.Count == 0) return;

        var bytes = _maskUndo.Pop();
        RestoreMaskBytes(_maskBitmap, bytes);

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

            int radius = 8;

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

    /// <summary>
    /// 타임라인 / 재생 / 키 이동으로 프레임 인덱스가 바뀔 때 호출.
    /// - 즉시: 썸네일 캐시에서 저화질 프레임 표시
    /// - 디바운스 후: 고화질 정확 프레임 1장만 디코딩
    /// </summary>
    public async void OnFrameIndexChanged(int index)
    {
        if (_session == null)
            return;
        if (index < 0)
            return;

        // 1) 항상 저화질 썸네일 즉시 표시 (재생 중에도 매 프레임 갱신)
        try
        {
            var low = _session.Timeline.OnFrameChanging(index);
            if (low != null)
                PreviewBitmap = low;
        }
        catch
        {
            // 썸네일 없으면 무시
        }

        // 2) 입력이 멈췄다고 판단되는 시점에만 고화질 프레임 1장 로딩
        var exact = await _session.Timeline.OnFrameChangedAsync(index);
        if (exact == null)
            return;

        // 고화질 원본 보관 (마스크 기반 블러용)
        FrameBitmap = exact;

        // 마스크 크기 맞추기 (없거나 사이즈 다르면 리셋)
        if (_maskBitmap == null ||
            _maskBitmap.PixelSize.Width != exact.PixelSize.Width ||
            _maskBitmap.PixelSize.Height != exact.PixelSize.Height)
        {
            MaskBitmap = CreateEmptyMask(exact.PixelSize.Width, exact.PixelSize.Height);
            _maskUndo.Clear();
        }

        // 고화질 + 마스크로 프리뷰 생성
        RefreshPreview();
    }
}
