using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class TimelineController : IDisposable
{
    private readonly ExactFrameProvider _exact;
    private readonly TimelineThumbnailProvider _thumbProvider;
    private readonly object _requestSync = new();
    private readonly int _debounceMs = 80;
    private int _exactRequestId;
    private int _thumbRequestId;
    private CancellationTokenSource? _exactCts;
    private CancellationTokenSource? _thumbCts;
    private bool _disposed;

    public TimelineController(ExactFrameProvider exact, TimelineThumbnailProvider thumbProvider)
    {
        _exact = exact;
        _thumbProvider = thumbProvider;
    }

    public async Task<WriteableBitmap?> OnFrameChangingExactAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _thumbRequestId);
        CancellationToken token = ReplaceRequestToken(ref _thumbCts);
        try
        {
            WriteableBitmap? thumbnail = await Task.Run(
                () => _thumbProvider.GetThumbnailCopy(frameIndex, token),
                token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _thumbRequestId))
                return thumbnail;

            thumbnail?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
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
        CancellationToken token = ReplaceRequestToken(ref _exactCts);
        try
        {
            await Task.Delay(_debounceMs, token);
            var exact = await _exact.GetExactAsync(frameIndex, token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _exactRequestId))
                return exact;

            exact?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
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
        CancellationToken token = ReplaceRequestToken(ref _exactCts);
        try
        {
            var exact = await _exact.GetExactAsync(frameIndex, token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _exactRequestId))
                return exact;

            exact?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private CancellationToken ReplaceRequestToken(ref CancellationTokenSource? slot)
    {
        lock (_requestSync)
        {
            if (_disposed)
                return new CancellationToken(canceled: true);

            slot?.Cancel();
            slot?.Dispose();
            slot = new CancellationTokenSource();
            return slot.Token;
        }
    }

    public void Dispose()
    {
        lock (_requestSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _exactCts?.Cancel();
            _thumbCts?.Cancel();
            _exactCts?.Dispose();
            _thumbCts?.Dispose();
            _exactCts = null;
            _thumbCts = null;
        }
    }
}
