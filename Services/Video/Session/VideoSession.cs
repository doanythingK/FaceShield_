// FILE: Services/Video/Session/VideoSession.cs
using FaceShield.Services.Video;
using System;

namespace FaceShield.Services.Video.Session;

public sealed class VideoSession : IDisposable
{
    public readonly ExactFrameProvider ExactProvider;
    public readonly TimelineController Timeline;
    private readonly TimelineThumbnailProvider _thumbsProvider;
    private bool _disposed;

    public VideoSession(
        string videoPath,
        int thumbWidth = 240,
        int thumbHeight = 135,
        IProgress<int>? progress = null,
        int maxThumbnailCacheEntries = 256)
    {
        var extractor = new FfFrameExtractor(videoPath, enableHardware: false);
        ExactProvider = new ExactFrameProvider(extractor);

        try
        {
            _thumbsProvider = new TimelineThumbnailProvider(
                videoPath,
                thumbWidth,
                thumbHeight,
                maxThumbnailCacheEntries);
            Timeline = new TimelineController(ExactProvider, _thumbsProvider);
            progress?.Report(100);
        }
        catch
        {
            ExactProvider.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ExactProvider.Dispose();
        _thumbsProvider.Dispose();
    }
}
