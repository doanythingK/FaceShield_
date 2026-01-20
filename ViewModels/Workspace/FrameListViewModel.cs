using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FrameListViewModel : ViewModelBase, IDisposable
{
    private const int MaxFrameItems = 5000;
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

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool showNoFaceIssues = true;

    [ObservableProperty]
    private bool showLowConfidenceIssues = true;

    [ObservableProperty]
    private bool showFlickerIssues = true;

    [ObservableProperty]
    private IReadOnlyList<int> noFaceIssueFrames = Array.Empty<int>();

    [ObservableProperty]
    private IReadOnlyList<int> lowConfidenceIssueFrames = Array.Empty<int>();

    [ObservableProperty]
    private IReadOnlyList<int> flickerIssueFrames = Array.Empty<int>();

    [ObservableProperty]
    private int noFaceIssueCount;

    [ObservableProperty]
    private int lowConfidenceIssueCount;

    [ObservableProperty]
    private int flickerIssueCount;

    // ─────────────────────────────
    // Thumbnail Provider
    // ─────────────────────────────
    [ObservableProperty]
    private TimelineThumbnailProvider? thumbnailProvider;

    private bool _disposed;
    private DispatcherTimer? _playTimer;

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

        if (TotalFrames > 0 && TotalFrames <= MaxFrameItems)
        {
            Items = Enumerable
                .Range(0, TotalFrames)
                .Select(i =>
                    new FrameItemViewModel(
                        index: i,
                        hasFace: true,
                        time: TimeSpan.FromSeconds(Fps > 0 ? i / Fps : 0)))
                .ToArray();
        }
        else
        {
            Items = Array.Empty<FrameItemViewModel>();
        }

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

            int openResult = ffmpeg.avformat_open_input(&fmt, path, null, null);
            FFmpegErrorHelper.ThrowIfError(openResult, $"Failed to open video: {path}");

            int streamInfo = ffmpeg.avformat_find_stream_info(fmt, null);
            FFmpegErrorHelper.ThrowIfError(streamInfo, $"Failed to read stream info: {path}");

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

            long nbFrames = videoStream->nb_frames;
            int frames = (int)Math.Floor(durationSeconds * fpsValue);
            if (nbFrames > 0 && nbFrames < int.MaxValue)
            {
                if (frames <= 0 || nbFrames > frames)
                    frames = (int)nbFrames;
            }

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

    public string TimelineTimeText
    {
        get
        {
            if (Fps <= 0 || TotalFrames <= 0 || SelectedFrameIndex < 0)
                return "--:-- / --:--";

            var current = TimeSpan.FromSeconds(SelectedFrameIndex / Fps);
            var total = TimeSpan.FromSeconds(TotalFrames / Fps);
            return $"{FormatTime(current)} / {FormatTime(total)}";
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return time.ToString(@"hh\:mm\:ss");

        return time.ToString(@"mm\:ss");
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
        OnPropertyChanged(nameof(TimelineTimeText));
    }

    partial void OnSelectedFrameIndexChanged(int value)
    {
        SelectedFrameIndexChanged?.Invoke(value);
        OnPropertyChanged(nameof(FramePositionText));
        OnPropertyChanged(nameof(TimelineTimeText));
    }

    partial void OnNoFaceIssueFramesChanged(IReadOnlyList<int> value)
    {
        NoFaceIssueCount = value?.Count ?? 0;
    }

    partial void OnLowConfidenceIssueFramesChanged(IReadOnlyList<int> value)
    {
        LowConfidenceIssueCount = value?.Count ?? 0;
    }

    partial void OnFlickerIssueFramesChanged(IReadOnlyList<int> value)
    {
        FlickerIssueCount = value?.Count ?? 0;
    }

    public event Action<int>? SelectedFrameIndexChanged;
    public event Action? PlaybackStopped;
    public event Action<bool>? PlaybackStateChanged;
    partial void OnTotalFramesChanged(int value)
    {
        ClampView();
        OnPropertyChanged(nameof(TotalDurationSeconds));
        OnPropertyChanged(nameof(FramePositionText));
        OnPropertyChanged(nameof(TimelineTimeText));

    }

    public void SetPropertyChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
    }

    public void NotifyPlaybackStopped()
    {
        IsPlaying = false;
        PlaybackStopped?.Invoke();
        PlaybackStateChanged?.Invoke(false);
    }

    public void NotifyPlaybackStarted()
    {
        IsPlaying = true;
        PlaybackStateChanged?.Invoke(true);
    }

    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (TotalFrames <= 0)
            return false;

        switch (key)
        {
            case Key.Left:
            case Key.Right:
                MoveFrame(forward: key == Key.Right, modifiers);
                return true;

            case Key.Up:
                MoveBySeconds(+1);
                return true;

            case Key.Down:
                MoveBySeconds(-1);
                return true;

            case Key.Home:
                SelectedFrameIndex = 0;
                return true;

            case Key.End:
                SelectedFrameIndex = TotalFrames - 1;
                return true;

            case Key.Space:
                TogglePlay();
                return true;
        }

        return false;
    }

    private void MoveFrame(bool forward, KeyModifiers mods)
    {
        int step = mods.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        int delta = forward ? step : -step;

        int next = Math.Clamp(
            SelectedFrameIndex + delta,
            0,
            TotalFrames - 1);

        SelectedFrameIndex = next;
    }

    private void MoveBySeconds(int seconds)
    {
        if (Fps <= 0) return;

        int deltaFrames = (int)Math.Round(seconds * Fps);

        int next = Math.Clamp(
            SelectedFrameIndex + deltaFrames,
            0,
            TotalFrames - 1);

        SelectedFrameIndex = next;
    }

    private void TogglePlay()
    {
        EnsurePlayTimer();

        if (_playTimer != null && _playTimer.IsEnabled)
        {
            StopPlay();
            return;
        }

        _playTimer?.Start();
        NotifyPlaybackStarted();
    }

    private void StopPlay()
    {
        if (_playTimer != null)
            _playTimer.Stop();

        NotifyPlaybackStopped();
    }

    private void EnsurePlayTimer()
    {
        if (_playTimer != null)
            return;

        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, Fps))
        };

        _playTimer.Tick += (_, _) =>
        {
            if (SelectedFrameIndex >= TotalFrames - 1)
            {
                StopPlay();
                return;
            }

            SelectedFrameIndex++;
        };
    }
    // ─────────────────────────────
    // Dispose
    // ─────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_playTimer != null)
        {
            try { _playTimer.Stop(); }
            catch { }
            _playTimer = null;
        }

        if (ThumbnailProvider != null)
        {
            try { ThumbnailProvider.Dispose(); }
            catch { }
            ThumbnailProvider = null;
        }
    }
}
