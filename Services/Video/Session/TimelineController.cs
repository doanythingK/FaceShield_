using Avalonia.Media.Imaging;
using System;
using System.Diagnostics;
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
        CancellationTokenSource requestCts = BeginRequest(ref _thumbCts);
        CancellationToken token = requestCts.Token;
        try
        {
            WriteableBitmap? thumbnail = await Task.Run(
                () => _thumbProvider.GetThumbnailCopy(frameIndex, token),
                token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _thumbRequestId))
            {
                return thumbnail;
            }

            thumbnail?.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ReportRequestFailure("thumbnail", frameIndex, ex);
            return null;
        }
        finally
        {
            CompleteRequest(ref _thumbCts, requestCts);
        }
    }

    public async Task<WriteableBitmap?> OnFrameChangedAsync(int frameIndex)
    {
        int requestId = Interlocked.Increment(ref _exactRequestId);
        CancellationTokenSource requestCts = BeginRequest(ref _exactCts);
        CancellationToken token = requestCts.Token;
        try
        {
            await Task.Delay(_debounceMs, token);
            WriteableBitmap? exact = await _exact.GetExactAsync(frameIndex, token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _exactRequestId))
            {
                return exact;
            }

            exact?.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ReportRequestFailure("exact-debounced", frameIndex, ex);
            return null;
        }
        finally
        {
            CompleteRequest(ref _exactCts, requestCts);
        }
    }

    public async Task<WriteableBitmap?> GetExactNowAsync(
        int frameIndex,
        CancellationToken cancellationToken = default)
    {
        int requestId = Interlocked.Increment(ref _exactRequestId);
        CancellationTokenSource requestCts = BeginRequest(ref _exactCts);
        CancellationTokenSource? linkedCts = null;
        CancellationToken token = requestCts.Token;
        try
        {
            // The request owns requestCts until this method completes. A newer
            // request may cancel it, but cannot dispose it while linked-token
            // registration or exact decoding is still using the token.
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                requestCts.Token,
                cancellationToken);
            token = linkedCts.Token;

            WriteableBitmap? exact = await _exact.GetExactAsync(frameIndex, token);
            if (!token.IsCancellationRequested &&
                requestId == Volatile.Read(ref _exactRequestId))
            {
                return exact;
            }

            exact?.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ReportRequestFailure("exact-now", frameIndex, ex);
            return null;
        }
        finally
        {
            linkedCts?.Dispose();
            CompleteRequest(ref _exactCts, requestCts);
        }
    }

    private static void ReportRequestFailure(
        string operation,
        int frameIndex,
        Exception exception)
    {
        Debug.WriteLine(
            $"[TimelineController] operation={operation} frame={frameIndex} error={exception.GetType().Name}: {exception.Message}");
    }

    private CancellationTokenSource BeginRequest(
        ref CancellationTokenSource? slot)
    {
        var current = new CancellationTokenSource();
        CancellationTokenSource? previous;
        bool reject;

        lock (_requestSync)
        {
            reject = _disposed;
            previous = reject ? null : slot;
            if (!reject)
                slot = current;
        }

        if (reject)
        {
            current.Cancel();
            return current;
        }

        CancelRequest(previous);
        return current;
    }

    private void CompleteRequest(
        ref CancellationTokenSource? slot,
        CancellationTokenSource requestCts)
    {
        lock (_requestSync)
        {
            if (ReferenceEquals(slot, requestCts))
                slot = null;
        }

        requestCts.Dispose();
    }

    private static void CancelRequest(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request owner may have completed between replacement and cancel.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? exact;
        CancellationTokenSource? thumb;

        lock (_requestSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            exact = _exactCts;
            thumb = _thumbCts;
            _exactCts = null;
            _thumbCts = null;
        }

        // Active requests own disposal of their CTS. Controller shutdown only
        // cancels them so no source is disposed while an in-flight request is
        // still registering callbacks or consuming its token.
        CancelRequest(exact);
        CancelRequest(thumb);
    }
}
