using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FaceShield.ViewModels.Workspace;

public partial class FrameListViewModel : ViewModelBase, IDisposable
{
    public string VideoPath { get; }

    // ─────────────────────────────
    // Timeline bind targets
    // ─────────────────────────────

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
    private bool isPlaybackEnabled = true;

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
    private CancellationTokenSource? _timelineNavigationCts;
    private CancellationTokenSource? _selectedTimestampCts;

    // ─────────────────────────────
    // ScrollBar 파생 프로퍼티
    // ─────────────────────────────
    public double TotalDurationSeconds { get; private set; }

    // ─────────────────────────────
    // ctor
    // ─────────────────────────────
    public FrameListViewModel(string videoPath)
    {
        VideoPath = videoPath;

        LoadVideoInfo(videoPath);

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

            int videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(fmt);
            AVStream* videoStream = videoStreamIndex >= 0 ? fmt->streams[videoStreamIndex] : null;

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

            int frames = videoStream->nb_frames > 0
                ? (int)Math.Min(videoStream->nb_frames, int.MaxValue)
                : (int)Math.Floor(durationSeconds * fpsValue);
            TotalFrames = Math.Max(frames, 0);

            TotalDurationSeconds = durationSeconds > 0
                ? durationSeconds
                : Fps > 0 && TotalFrames > 0
                    ? TotalFrames / Fps
                    : 0;
            OnPropertyChanged(nameof(TotalDurationSeconds));

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

    public void SetThumbnailProvider(TimelineThumbnailProvider? provider)
    {
        CancelTimelineNavigation();
        CancelSelectedTimestampResolution();
        ThumbnailProvider = provider;
        OnPropertyChanged(nameof(TimelineTimeText));
        ResolveSelectedTimestampInBackground();
    }

    public void UpdateActualTotalFrames(int actualTotalFrames)
    {
        int normalized = Math.Max(0, actualTotalFrames);
        if (normalized == TotalFrames)
            return;

        int selected = SelectedFrameIndex;
        TotalFrames = normalized;
        SelectedFrameIndex = normalized > 0
            ? Math.Clamp(selected, 0, normalized - 1)
            : -1;
        ClampView();
    }

    private void ClampView()
    {
        if (SecondsPerScreen <= 0) return;

        double maxStart =
            Math.Max(0, TotalDurationSeconds - SecondsPerScreen);

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
            if (TotalFrames <= 0 || SelectedFrameIndex < 0)
                return "--:-- / --:--";

            var total = TimeSpan.FromSeconds(
                Math.Max(0, TotalDurationSeconds));
            if (ThumbnailProvider?.TryGetFrameTimestampSeconds(
                    SelectedFrameIndex,
                    out double currentSeconds) != true)
            {
                return $"--:-- / {FormatTime(total)}";
            }

            var current = TimeSpan.FromSeconds(
                Math.Clamp(
                    currentSeconds,
                    0,
                    Math.Max(0, TotalDurationSeconds)));
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
        CancelTimelineNavigation();
        SelectedFrameIndexChanged?.Invoke(value);
        OnPropertyChanged(nameof(FramePositionText));
        OnPropertyChanged(nameof(TimelineTimeText));
        ResolveSelectedTimestampInBackground();
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
        if (!IsPlaying)
            return;

        IsPlaying = false;
        PlaybackStateChanged?.Invoke(false);
        PlaybackStopped?.Invoke();
    }

    public void NotifyPlaybackStarted()
    {
        if (IsPlaying || !IsPlaybackEnabled)
            return;

        IsPlaying = true;
        PlaybackStateChanged?.Invoke(true);
    }

    public void SetPlaybackEnabled(bool enabled)
    {
        IsPlaybackEnabled = enabled;
        if (enabled || !IsPlaying)
            return;

        IsPlaying = false;
        PlaybackStateChanged?.Invoke(false);
    }

    public void SetPlaybackFrameIndex(int frameIndex)
    {
        if (TotalFrames <= 0)
            return;

        SelectedFrameIndex = Math.Clamp(frameIndex, 0, TotalFrames - 1);
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
        if (SelectedFrameIndex < 0 || TotalFrames <= 0)
            return;

        var provider = ThumbnailProvider;
        if (provider == null)
            return;

        int sourceFrameIndex = SelectedFrameIndex;
        if (provider.TryGetFrameTimestampSeconds(
                sourceFrameIndex,
                out double currentSeconds))
        {
            double targetSeconds = Math.Clamp(
                currentSeconds + seconds,
                0,
                Math.Max(0, TotalDurationSeconds));
            if (provider.TryGetFrameIndexAtTimestamp(
                    targetSeconds,
                    out int cachedIndex))
            {
                SelectedFrameIndex = Math.Clamp(
                    cachedIndex,
                    0,
                    TotalFrames - 1);
                return;
            }
        }

        CancelTimelineNavigation();
        var cts = new CancellationTokenSource();
        _timelineNavigationCts = cts;
        _ = ResolveTimelineNavigationAsync(
            provider,
            sourceFrameIndex,
            seconds,
            cts);
    }

    private async Task ResolveTimelineNavigationAsync(
        TimelineThumbnailProvider provider,
        int sourceFrameIndex,
        int secondsDelta,
        CancellationTokenSource cts)
    {
        try
        {
            (bool resolved, int frameIndex) = await Task.Run(() =>
            {
                if (!provider.TryResolveFrameTimestampSeconds(
                        sourceFrameIndex,
                        cts.Token,
                        out double currentSeconds))
                {
                    return (false, -1);
                }

                double targetSeconds = Math.Clamp(
                    currentSeconds + secondsDelta,
                    0,
                    Math.Max(0, TotalDurationSeconds));
                bool ok = provider.TryResolveFrameIndexAtTimestamp(
                    targetSeconds,
                    cts.Token,
                    out int resolvedIndex);
                return (ok, resolvedIndex);
            }, cts.Token);

            if (!resolved || cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed ||
                    cts.IsCancellationRequested ||
                    !ReferenceEquals(_timelineNavigationCts, cts) ||
                    !ReferenceEquals(ThumbnailProvider, provider) ||
                    SelectedFrameIndex != sourceFrameIndex ||
                    TotalFrames <= 0)
                {
                    return;
                }

                SelectedFrameIndex = Math.Clamp(
                    frameIndex,
                    0,
                    TotalFrames - 1);
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_timelineNavigationCts, cts))
                _timelineNavigationCts = null;
            cts.Dispose();
        }
    }

    private void ResolveSelectedTimestampInBackground()
    {
        CancelSelectedTimestampResolution();

        int frameIndex = SelectedFrameIndex;
        var provider = ThumbnailProvider;
        if (_disposed ||
            provider == null ||
            frameIndex < 0 ||
            provider.TryGetFrameTimestampSeconds(frameIndex, out _))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _selectedTimestampCts = cts;
        _ = ResolveSelectedTimestampAsync(provider, frameIndex, cts);
    }

    private async Task ResolveSelectedTimestampAsync(
        TimelineThumbnailProvider provider,
        int frameIndex,
        CancellationTokenSource cts)
    {
        try
        {
            bool resolved = await Task.Run(
                () => provider.TryResolveFrameTimestampSeconds(
                    frameIndex,
                    cts.Token,
                    out _),
                cts.Token);

            if (!resolved || cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed ||
                    cts.IsCancellationRequested ||
                    !ReferenceEquals(_selectedTimestampCts, cts) ||
                    !ReferenceEquals(ThumbnailProvider, provider) ||
                    SelectedFrameIndex != frameIndex)
                {
                    return;
                }

                OnPropertyChanged(nameof(TimelineTimeText));
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_selectedTimestampCts, cts))
                _selectedTimestampCts = null;
            cts.Dispose();
        }
    }

    private void CancelTimelineNavigation()
    {
        var cts = _timelineNavigationCts;
        _timelineNavigationCts = null;
        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void CancelSelectedTimestampResolution()
    {
        var cts = _selectedTimestampCts;
        _selectedTimestampCts = null;
        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task SuspendTimelineOperationsAndWaitAsync()
    {
        CancelTimelineNavigation();
        CancelSelectedTimestampResolution();

        var provider = ThumbnailProvider;
        if (provider != null)
            await provider.SuspendOperationsAndWaitAsync();
    }

    public void ResumeTimelineOperations()
    {
        var provider = ThumbnailProvider;
        if (provider == null)
            return;

        provider.ResumeOperations();
        OnPropertyChanged(nameof(TimelineTimeText));
        ResolveSelectedTimestampInBackground();
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlay();
            return;
        }

        if (!IsPlaybackEnabled)
            return;

        NotifyPlaybackStarted();
    }

    private void StopPlay()
    {
        NotifyPlaybackStopped();
    }
    // ─────────────────────────────
    // Dispose
    // ─────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelTimelineNavigation();
        CancelSelectedTimestampResolution();

        // VideoSession owns the shared thumbnail provider and decoder.
        ThumbnailProvider = null;
    }
}
