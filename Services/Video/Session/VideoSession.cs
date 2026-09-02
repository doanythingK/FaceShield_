// FILE: Services/Video/Session/VideoSession.cs
using FaceShield.Services.Video;
using System;
using System.Threading;

namespace FaceShield.Services.Video.Session;

public sealed class VideoSession : IDisposable
{
    public readonly ExactFrameProvider ExactProvider;
    public readonly TimelineController Timeline;
    public TimelineThumbnailProvider ThumbnailProvider { get; }

    private readonly FfFrameExtractor _extractor;
    private bool _disposed;

    public VideoSession(
        string videoPath,
        int thumbWidth = 240,
        int thumbHeight = 135,
        IProgress<int>? progress = null,
        int maxThumbnailCacheEntries = 256,
        CancellationToken cancellationToken = default)
    {
        _extractor = new FfFrameExtractor(
            videoPath,
            enableHardware: false,
            cancellationToken: cancellationToken);
        ExactProvider = new ExactFrameProvider(_extractor, ownsExtractor: false);

        try
        {
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
            ExactProvider.Dispose();
            _extractor.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Timeline.Dispose();
        ExactProvider.Dispose();
        ThumbnailProvider.Dispose();
        _extractor.Dispose();
    }
}
