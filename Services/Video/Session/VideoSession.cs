// FILE: Services/Video/Session/VideoSession.cs
using FaceShield.Services.Video;
using System;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        try
        {
            ManualPlayer?.Dispose();
        }
        finally
        {
            DisposeSessionResources();
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        try
        {
            if (ManualPlayer != null)
                await ManualPlayer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeSessionResources();
        }

        GC.SuppressFinalize(this);
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
