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

        try
        {
            if (enableManualPlayer)
            {
                ManualPlayer = new ManualFramePlayer(
                    videoPath,
                    cancellationToken);
            }

            ThumbnailProvider = new TimelineThumbnailProvider(
                _extractor,
                thumbWidth,
                thumbHeight,
                maxThumbnailCacheEntries,
                ownsExtractor: false);
            Timeline = new TimelineController(ExactProvider, ThumbnailProvider);
            progress?.Report(100);
        }
        catch
        {
            ManualPlayer?.Dispose();
            ExactProvider.Dispose();
            _extractor.Dispose();
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
        Timeline.Dispose();
        ExactProvider.Dispose();
        ThumbnailProvider.Dispose();
        _extractor.Dispose();
    }
}
