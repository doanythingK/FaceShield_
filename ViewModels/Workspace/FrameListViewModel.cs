using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FrameListViewModel : ViewModelBase, IDisposable
{
    public string VideoPath { get; }

    // ─────────────────────────────
    // Timeline bind targets
    // ─────────────────────────────
    [ObservableProperty]
    private IReadOnlyList<FrameItemViewModel> items = Array.Empty<FrameItemViewModel>();

    [ObservableProperty]
    private int totalFrames;

    [ObservableProperty]
    private double fps;

    [ObservableProperty]
    private int selectedFrameIndex = -1;

    [ObservableProperty]
    private double secondsPerScreen = 10.0;

    [ObservableProperty]
    private double viewStartSeconds = 0.0;

    // ─────────────────────────────
    // Thumbnail Provider
    // ─────────────────────────────
    [ObservableProperty]
    private TimelineThumbnailProvider? thumbnailProvider;

    private bool _disposed;

    // ─────────────────────────────
    // ScrollBar 파생 프로퍼티
    // ─────────────────────────────
    public double TotalDurationSeconds
    {
        get
        {
            if (Fps <= 0 || TotalFrames <= 0)
                return 0;

            return TotalFrames / Fps;
        }
    }

    // ─────────────────────────────
    // ctor
    // ─────────────────────────────
    public FrameListViewModel(string videoPath)
    {
        VideoPath = videoPath;

        LoadVideoInfo(videoPath);

        Items = Enumerable
            .Range(0, TotalFrames)
            .Select(i =>
                new FrameItemViewModel(
                    index: i,
                    hasFace: true,
                    time: TimeSpan.FromSeconds(Fps > 0 ? i / Fps : 0)))
            .ToArray();

        ThumbnailProvider = new TimelineThumbnailProvider(
            videoPath,
            thumbWidth: 160,
            thumbHeight: 90);
    }

    // ─────────────────────────────
    // FFmpeg metadata load
    // ─────────────────────────────
    private unsafe void LoadVideoInfo(string path)
    {
        AVFormatContext* fmt = null;

        try
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0)
                throw new InvalidOperationException("Failed to open video.");

            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                throw new InvalidOperationException("Failed to read stream info.");

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
                throw new InvalidOperationException("Video stream not found.");

            double fpsValue =
                videoStream->avg_frame_rate.num != 0
                    ? ffmpeg.av_q2d(videoStream->avg_frame_rate)
                    : videoStream->r_frame_rate.num != 0
                        ? ffmpeg.av_q2d(videoStream->r_frame_rate)
                        : 30.0;

            Fps = fpsValue;

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
                durationSeconds = 0;
            }

            int frames = (int)Math.Floor(durationSeconds * fpsValue);
            TotalFrames = Math.Max(frames, 0);

            // 전체 영상 길이(초)
            double totalDurationSec =
                Fps > 0 && TotalFrames > 0
                    ? TotalFrames / Fps
                    : 0;

            // 🔑 초기에는 전체 영상이 한 화면에 보이도록
            SecondsPerScreen = Math.Max(0.1, TotalDurationSeconds);

            // 시작은 항상 0초
            ViewStartSeconds = 0;

            // 첫 프레임 선택
            SelectedFrameIndex = TotalFrames > 0 ? 0 : -1;
        }
        finally
        {
            if (fmt != null)
                ffmpeg.avformat_close_input(&fmt);
        }
    }

    // ─────────────────────────────
    // Timeline helper
    // ─────────────────────────────
    public double FrameIndexToSeconds(int frameIndex)
        => frameIndex < 0 ? 0 : frameIndex / Fps;

    private void ClampView()
    {
        if (SecondsPerScreen <= 0) return;

        double maxStart =
            Math.Max(0, FrameIndexToSeconds(TotalFrames) - SecondsPerScreen);

        if (ViewStartSeconds < 0)
            ViewStartSeconds = 0;
        else if (ViewStartSeconds > maxStart)
            ViewStartSeconds = maxStart;
    }

    public string FramePositionText
    {
        get
        {
            if (TotalFrames <= 0 || SelectedFrameIndex < 0)
                return "- / -";

            // 사용자 표시용이므로 1-based
            return $"{SelectedFrameIndex + 1} / {TotalFrames}";
        }
    }

    // ─────────────────────────────
    // 🔑 **여기가 핵심**
    // Zoom / 메타 변경 시 자동 보정
    // ─────────────────────────────
    partial void OnSecondsPerScreenChanged(double value)
    {
        ClampView();
    }

    partial void OnFpsChanged(double value)
    {
        ClampView();
        OnPropertyChanged(nameof(TotalDurationSeconds));
    }

    partial void OnSelectedFrameIndexChanged(int value)
    {
        SelectedFrameIndexChanged?.Invoke(value);
        OnPropertyChanged(nameof(FramePositionText));
    }

    public event Action<int>? SelectedFrameIndexChanged;
    partial void OnTotalFramesChanged(int value)
    {
        ClampView();
        OnPropertyChanged(nameof(TotalDurationSeconds));
        OnPropertyChanged(nameof(FramePositionText));

    }

    public void SetPropertyChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
    }
    // ─────────────────────────────
    // Dispose
    // ─────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (ThumbnailProvider != null)
        {
            try { ThumbnailProvider.Dispose(); }
            catch { }
            ThumbnailProvider = null;
        }
    }
}
