using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class TimelineController
{
    private readonly ThumbnailCache _thumbs;
    private readonly ExactFrameProvider _exact;

    private CancellationTokenSource? _cts;
    private readonly int _debounceMs = 140; // 조금 더 안정적

    public TimelineController(ThumbnailCache thumbs, ExactFrameProvider exact)
    {
        _thumbs = thumbs;
        _exact = exact;
    }

    // 🔹 드래그 중 즉시 썸네일 표시
    public WriteableBitmap OnFrameChanging(int frameIndex)
    {
        return _thumbs.GetNearest(frameIndex);
    }

    // 🔹 드래그 종료 판단 → 고화질 로드
    public async Task<WriteableBitmap?> OnFrameChangedAsync(int frameIndex)
    {
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
        return await _exact.GetExactAsync(frameIndex, ct);
    }
}
