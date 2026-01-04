// FILE: Services/Video/Session/VideoSession.cs
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video.Session;

public sealed class VideoSession
{
    public readonly ThumbnailCache ThumbnailCache;
    public readonly ExactFrameProvider ExactProvider;
    public readonly TimelineController Timeline;

    public VideoSession(
        string videoPath,
        int thumbStep = 20,          // 4K 20분 기준: 20프레임마다 썸네일 하나 (메모리 부담 줄이기)
        int thumbWidth = 240,
        int thumbHeight = 135,
        IProgress<int>? progress = null)
    {
        // 1) 고화질 정확 프레임용 Extractor
        var extractor = new FfFrameExtractor(videoPath, enableHardware: false);
        ExactProvider = new ExactFrameProvider(extractor);

        // 2) 썸네일 캐시 생성
        var thumbsProvider = new TimelineThumbnailProvider(videoPath, thumbWidth, thumbHeight);

        var map = new Dictionary<int, WriteableBitmap>();
        int totalFrames = GetTotalFrames(videoPath);

        if (totalFrames <= 0)
            totalFrames = 300; // 방어용 최소값

        int totalThumbs = (int)Math.Ceiling(totalFrames / (double)Math.Max(1, thumbStep));
        int done = 0;

        for (int i = 0; i < totalFrames; i += thumbStep)
        {
            var bmp = thumbsProvider.GetThumbnail(i);
            if (bmp != null)
                map[i] = bmp;

            done++;
            if (progress != null)
            {
                int percent = (int)Math.Round(done * 100.0 / Math.Max(1, totalThumbs));
                if (percent > 100) percent = 100;
                progress.Report(percent);
            }
        }

        progress?.Report(100);

        ThumbnailCache = new ThumbnailCache(map, thumbStep);

        // 3) UX 컨트롤러 (드래그 중/멈췄을 때 분리)
        Timeline = new TimelineController(ThumbnailCache, ExactProvider, thumbsProvider);
    }

    /// <summary>
    /// FFmpeg 메타데이터 기반으로 총 프레임 수 추정
    /// FrameListViewModel.LoadVideoInfo와 같은 로직
    /// </summary>
    private unsafe int GetTotalFrames(string videoPath)
    {
        AVFormatContext* fmt = null;

        try
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

            if (ffmpeg.avformat_open_input(&fmt, videoPath, null, null) < 0)
                return 0;

            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                return 0;

            AVStream* videoStream = null;

            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStream = fmt->streams[i];
                    break;
                }
            }

            if (videoStream == null)
                return 0;

            double fpsValue =
                videoStream->avg_frame_rate.num != 0
                    ? ffmpeg.av_q2d(videoStream->avg_frame_rate)
                    : videoStream->r_frame_rate.num != 0
                        ? ffmpeg.av_q2d(videoStream->r_frame_rate)
                        : 30.0;

            double durationSeconds;

            if (videoStream->duration > 0)
            {
                durationSeconds =
                    videoStream->duration * ffmpeg.av_q2d(videoStream->time_base);
            }
            else if (fmt->duration > 0)
            {
                durationSeconds =
                    fmt->duration / (double)ffmpeg.AV_TIME_BASE;
            }
            else
            {
                return 0;
            }

            int frames = (int)Math.Floor(durationSeconds * fpsValue);
            return Math.Max(frames, 0);
        }
        finally
        {
            if (fmt != null)
                ffmpeg.avformat_close_input(&fmt);
        }
    }
}
