// FILE: Services/Video/Session/VideoSession.cs
using FaceShield.Services.Video;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class VideoSession : IDisposable, IAsyncDisposable
{
    public readonly ExactFrameProvider ExactProvider;
    public readonly TimelineController Timeline;
    public TimelineThumbnailProvider ThumbnailProvider { get; }
    internal ManualFramePlayer? ManualPlayer { get; }

    private readonly FfFrameExtractor _extractor;
    private readonly object _disposeGate = new();
    private Task? _disposeAfter;
    private int _disposeState;

    public VideoSession(
        string videoPath,
        int thumbWidth = 240,
        int thumbHeight = 135,
        IProgress<int>? progress = null,
        int maxThumbnailCacheEntries = 256,
        CancellationToken cancellationToken = default,
        bool enableManualPlayer = false)
    {
        _extractor = new FfFrameExtractor(
            videoPath,
            enableHardware: false,
            cancellationToken: cancellationToken);
        ExactProvider = new ExactFrameProvider(_extractor, ownsExtractor: false);
        ManualPlayer = null;

        TimelineThumbnailProvider? thumbnailProvider = null;
        try
        {
            if (enableManualPlayer)
            {
                ManualPlayer = new ManualFramePlayer(
                    videoPath,
                    cancellationToken);
            }

            thumbnailProvider = new TimelineThumbnailProvider(
                _extractor,
                thumbWidth,
                thumbHeight,
                maxThumbnailCacheEntries,
                ownsExtractor: false);
            ThumbnailProvider = thumbnailProvider;
            Timeline = new TimelineController(ExactProvider, ThumbnailProvider);
            progress?.Report(100);
        }
        catch
        {
            // Construction is an ownership transaction: every resource created
            // before the failure must be released even when an earlier Dispose
            // itself faults. Cleanup failures must not replace the construction fault.
            TryDispose(ManualPlayer);
            TryDispose(thumbnailProvider);
            TryDispose(ExactProvider);
            TryDispose(_extractor);
            throw;
        }
    }

    /// <summary>
    /// A directly disposed preview may still have a tracking operation waiting for
    /// this session. Register its completion before calling Dispose; the synchronous
    /// Dispose returns without blocking the UI, and releases resources after drain.
    /// </summary>
    internal void DeferDisposeUntil(Task operation)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        lock (_disposeGate)
        {
            if (_disposeState != 0)
                throw new ObjectDisposedException(nameof(VideoSession));

            _disposeAfter = _disposeAfter == null
                ? operation
                : Task.WhenAll(_disposeAfter, operation);
        }
    }

    private bool TryBeginDispose(out Task? pending)
    {
        lock (_disposeGate)
        {
            pending = null;
            if (_disposeState != 0)
                return false;

            _disposeState = 1;
            pending = _disposeAfter;
            return true;
        }
    }

    public void Dispose()
    {
        if (!TryBeginDispose(out Task? pending))
            return;

        if (pending != null && !pending.IsCompleted)
        {
            _ = DisposeAfterAsync(pending);
            return;
        }

        DisposeCore();
    }

    private async Task DisposeAfterAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed tracking operation must not keep the native session alive.
            Debug.WriteLine($"[VideoSession] tracking drain failed: {ex.Message}");
        }

        try
        {
            DisposeCore();
        }
        catch (Exception ex)
        {
            // A deferred Dispose cannot report back through its original caller.
            Debug.WriteLine($"[VideoSession] deferred disposal failed: {ex.Message}");
        }
    }

    private void DisposeCore()
    {
        try
        {
            ManualPlayer?.Dispose();
        }
        finally
        {
            DisposeSessionResources();
            GC.SuppressFinalize(this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose(out Task? pending))
            return;

        if (pending != null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[VideoSession] tracking drain failed: {ex.Message}"); }
        }

        try
        {
            if (ManualPlayer != null)
                await ManualPlayer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeSessionResources();
            GC.SuppressFinalize(this);
        }
    }

    private void DisposeSessionResources()
    {
        try
        {
            Timeline.Dispose();
        }
        finally
        {
            try
            {
                ExactProvider.Dispose();
            }
            finally
            {
                try
                {
                    ThumbnailProvider.Dispose();
                }
                finally
                {
                    _extractor.Dispose();
                }
            }
        }
    }

    private static void TryDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
        }
    }
}
