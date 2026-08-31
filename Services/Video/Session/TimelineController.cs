using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class TimelineController
{
    private readonly ThumbnailCache _thumbs;
    private readonly ExactFrameProvider _exact;
    private readonly TimelineThumbnailProvider _thumbProvider;

    private readonly int _debounceMs = 80; // 반응 속도 개선
    private int _exactRequestId;
    private int _thumbRequestId;

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
        return _thumbs.GetNearest(frameIndex);
    }

    // 🔹 선택된 프레임에 대한 정확한 썸네일 로드 (저화질이지만 프레임 일치)
    public async Task<WriteableBitmap?> OnFrameChangingExactAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _thumbRequestId);

        try
        {
            var thumbnail = await Task.Run(() => _thumbProvider.GetThumbnail(frameIndex));
            return requestId == Volatile.Read(ref _thumbRequestId)
                ? thumbnail
                : null;
        }
        catch
        {
            return null;
        }
    }

    // 🔹 드래그 종료 판단 → 고화질 로드
    public async Task<WriteableBitmap?> OnFrameChangedAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _exactRequestId);

        // 사용자가 손을 떼었다고 판단하는 지연
        await Task.Delay(_debounceMs);
        if (requestId != Volatile.Read(ref _exactRequestId))
            return null;

        // 🔥 선택한 프레임에 대해 정확히 고화질 1장 로딩
        try
        {
            var exact = await _exact.GetExactAsync(frameIndex, CancellationToken.None);
            if (requestId == Volatile.Read(ref _exactRequestId))
                return exact;

            exact?.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    // 🔹 재생 중지 시 즉시 고화질 로드 (디바운스 없음)
    public async Task<WriteableBitmap?> GetExactNowAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _exactRequestId);

        try
        {
            var exact = await _exact.GetExactAsync(frameIndex, CancellationToken.None);
            if (requestId == Volatile.Read(ref _exactRequestId))
                return exact;

            exact?.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
