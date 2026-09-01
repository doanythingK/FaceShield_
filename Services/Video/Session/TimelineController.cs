using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class TimelineController
{
    private readonly ExactFrameProvider _exact;
    private readonly TimelineThumbnailProvider _thumbProvider;

    private readonly int _debounceMs = 80;
    private int _exactRequestId;
    private int _thumbRequestId;

    public TimelineController(
        ExactFrameProvider exact,
        TimelineThumbnailProvider thumbProvider)
    {
        _exact = exact;
        _thumbProvider = thumbProvider;
    }

    public async Task<WriteableBitmap?> OnFrameChangingExactAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _thumbRequestId);

        try
        {
            WriteableBitmap? thumbnail =
                await Task.Run(() => _thumbProvider.GetThumbnailCopy(frameIndex));
            if (requestId == Volatile.Read(ref _thumbRequestId))
                return thumbnail;

            thumbnail?.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<WriteableBitmap?> OnFrameChangedAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _exactRequestId);

        await Task.Delay(_debounceMs);
        if (requestId != Volatile.Read(ref _exactRequestId))
            return null;

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
