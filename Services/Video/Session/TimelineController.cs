using Avalonia.Media.Imaging;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class TimelineController : IDisposable
{
    private readonly ThumbnailCache _thumbs;
    private readonly ExactFrameProvider _exact;
    private readonly TimelineThumbnailProvider _thumbProvider;

    private CancellationTokenSource? _cts;
    private readonly int _debounceMs = 80; // 반응 속도 개선
    private CancellationTokenSource? _thumbCts;
    private bool _disposed;

    public TimelineController(
        ThumbnailCache thumbs,
        ExactFrameProvider exact,
        TimelineThumbnailProvider thumbProvider)
    {
        _thumbs = thumbs;
        _exact = exact;
        _thumbProvider = thumbProvider;
    }

    // 🔹 드래그 중 즉시 썸네일 표시
    public WriteableBitmap OnFrameChanging(int frameIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TimelineController));
        return _thumbs.GetNearest(frameIndex);
    }

    // 🔹 선택된 프레임에 대한 정확한 썸네일 로드 (저화질이지만 프레임 일치)
    public async Task<WriteableBitmap?> OnFrameChangingExactAsync(int frameIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TimelineController));
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        try
        {
            var bmp = await Task.Run(() => _thumbProvider.GetThumbnail(frameIndex), ct);
            return bmp == null ? null : ThumbnailCache.CloneBitmap(bmp);
        }
        catch
        {
            return null;
        }
    }

    // 🔹 드래그 종료 판단 → 고화질 로드
    public async Task<WriteableBitmap?> OnFrameChangedAsync(int frameIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TimelineController));
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // 사용자가 손을 떼었다고 판단하는 지연
            await Task.Delay(_debounceMs, ct);
        }
        catch
        {
            return null; // 드래그 계속 중
        }

        // 🔥 선택한 프레임에 대해 정확히 고화질 1장 로딩
        try
        {
            return await _exact.GetExactAsync(frameIndex, ct);
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    // 🔹 재생 중지 시 즉시 고화질 로드 (디바운스 없음)
    public async Task<WriteableBitmap?> GetExactNowAsync(int frameIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TimelineController));
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            return await _exact.GetExactAsync(frameIndex, ct);
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = null;
    }
}
