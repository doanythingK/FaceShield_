using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FaceShield.Services.Video;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FaceShield.Controls;

/// <summary>
/// A single, independent PTS indexer handles the most recent click without waiting
/// for thumbnail decoding. Never infer VFR ordinals from average FPS.
/// </summary>
public sealed class ManualTimelineFrameStrip : TimelineFrameStrip
{
    public static readonly StyledProperty<string?> VideoPathProperty =
        AvaloniaProperty.Register<ManualTimelineFrameStrip, string?>(nameof(VideoPath));

    public string? VideoPath
    {
        get => GetValue(VideoPathProperty);
        set => SetValue(VideoPathProperty, value);
    }

    public static readonly StyledProperty<string?> NavigationStatusProperty =
        AvaloniaProperty.Register<ManualTimelineFrameStrip, string?>(
            nameof(NavigationStatus), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public string? NavigationStatus
    {
        get => GetValue(NavigationStatusProperty);
        set => SetCurrentValue(NavigationStatusProperty, value);
    }

    public static readonly StyledProperty<bool> NavigationEnabledProperty =
        AvaloniaProperty.Register<ManualTimelineFrameStrip, bool>(nameof(NavigationEnabled), true);

    public bool NavigationEnabled
    {
        get => GetValue(NavigationEnabledProperty);
        set => SetValue(NavigationEnabledProperty, value);
    }

    public static readonly StyledProperty<ICommand?> FrameSelectionCommandProperty =
        AvaloniaProperty.Register<ManualTimelineFrameStrip, ICommand?>(nameof(FrameSelectionCommand));

    public ICommand? FrameSelectionCommand
    {
        get => GetValue(FrameSelectionCommandProperty);
        set => SetValue(FrameSelectionCommandProperty, value);
    }

    private static readonly IBrush ThumbnailReadyBrush =
        new SolidColorBrush(Color.FromRgb(73, 194, 123));
    private static readonly IBrush TimestampReadyBrush =
        new SolidColorBrush(Color.FromRgb(74, 156, 231));

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private CancellationTokenSource? _selectionCts;
    private FfFrameExtractor? _indexer;
    private string? _indexerPath;
    private int _attachmentGeneration;

    public ManualTimelineFrameStrip()
    {
        DetachedFromVisualTree += (_, _) =>
        {
            Interlocked.Increment(ref _attachmentGeneration);
            CancelSelection();
            // A running decode owns the indexer until it releases the gate.
            _ = Task.Run(async () =>
            {
                await _indexGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _indexer?.Dispose();
                    _indexer = null;
                    _indexerPath = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ManualTimeline] indexer shutdown failed: {ex}");
                }
                finally
                {
                    _indexGate.Release();
                }
            });
        };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            !NavigationEnabled || TotalFrames <= 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.X < 0 || position.X > Bounds.Width ||
            position.Y < 0 || position.Y > Bounds.Height)
        {
            return;
        }

        e.Handled = true;
        ICommand? command = FrameSelectionCommand;
        if (command == null || !command.CanExecute(Math.Max(0, SelectedFrameIndex)))
        {
            NavigationStatus = "현재 작업 중에는 타임라인을 이동할 수 없습니다.";
            return;
        }

        TimelineThumbnailProvider? provider = ThumbnailProvider;
        if (provider == null || string.IsNullOrWhiteSpace(VideoPath))
        {
            NavigationStatus = "영상 프레임 탐색이 아직 준비되지 않았습니다.";
            return;
        }

        // Latest click wins, including a click immediately resolved from cache.
        CancelSelection();
        double seconds = Math.Max(0, ViewStartSeconds) +
            Math.Max(0.05, SecondsPerScreen) *
            Math.Clamp(position.X / Math.Max(1, Bounds.Width), 0, 1);
        if (provider.TryGetFrameIndexAtTimestamp(seconds, out int cachedFrame))
        {
            SelectFrame(cachedFrame);
            return;
        }

        string path = VideoPath!;
        int generation = Volatile.Read(ref _attachmentGeneration);
        int baseline = SelectedFrameIndex;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _selectionCts, cts);
        NavigationStatus = "클릭한 위치의 정확한 프레임을 확인하는 중...";
        _ = ResolveSelectionAsync(provider, path, seconds, baseline, generation, cts);
    }

    private async Task ResolveSelectionAsync(
        TimelineThumbnailProvider provider, string path, double seconds,
        int baseline, int attachmentGeneration, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            (bool found, int frame) = await Task.Run(async () =>
            {
                await _indexGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_indexer == null ||
                        !string.Equals(_indexerPath, path, StringComparison.Ordinal))
                    {
                        _indexer?.Dispose();
                        _indexer = new FfFrameExtractor(
                            path, enableHardware: false, cancellationToken: token);
                        _indexerPath = path;
                    }

                    bool ok = _indexer.TryResolveFrameIndexAtTimestamp(
                        seconds, token, out int index);
                    return (ok, index);
                }
                finally
                {
                    _indexGate.Release();
                }
            }, token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    !ReferenceEquals(Volatile.Read(ref _selectionCts), cts) ||
                    !ReferenceEquals(ThumbnailProvider, provider) ||
                    !string.Equals(VideoPath, path, StringComparison.Ordinal) ||
                    attachmentGeneration != Volatile.Read(ref _attachmentGeneration) ||
                    SelectedFrameIndex != baseline)
                {
                    return;
                }

                if (found && frame >= 0)
                    SelectFrame(frame);
                else
                    NavigationStatus = "정확한 프레임 위치를 확인하지 못했습니다. 영상의 시간 정보 또는 디코딩 상태를 확인하세요.";
                InvalidateVisual();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManualTimeline] priority selection failed: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested &&
                    ReferenceEquals(Volatile.Read(ref _selectionCts), cts) &&
                    attachmentGeneration == Volatile.Read(ref _attachmentGeneration) &&
                    SelectedFrameIndex == baseline)
                {
                    NavigationStatus = "해당 위치의 프레임을 불러오지 못했습니다. 영상의 디코딩 상태를 확인하세요.";
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _selectionCts, null, cts);
            cts.Dispose();
        }
    }

    private void SelectFrame(int frame)
    {
        if (frame < 0)
            return;
        int selected = IsTotalFramesEstimated
            ? frame
            : Math.Clamp(frame, 0, Math.Max(0, TotalFrames - 1));
        ICommand? command = FrameSelectionCommand;
        if (command == null || !command.CanExecute(selected))
        {
            NavigationStatus = "현재 작업 중에는 타임라인을 이동할 수 없습니다.";
            return;
        }
        command.Execute(selected);
        NavigationStatus = null;
        InvalidateVisual();
    }

    private void CancelSelection()
    {
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _selectionCts, null);
        if (previous == null)
            return;
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        // ResolveSelectionAsync owns disposal after its decoder has drained.
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        TimelineThumbnailProvider? provider = ThumbnailProvider;
        double width = Bounds.Width;
        if (provider == null || width <= 1 || Bounds.Height <= 24)
            return;

        double stripHeight = Math.Max(24, Bounds.Height - 22);
        double span = Math.Max(0.05, SecondsPerScreen);
        int slots = Math.Clamp((int)Math.Floor(width / 100), 1, 300);
        double slotWidth = width / slots;
        for (int slot = 0; slot < slots; slot++)
        {
            double sampleTime = ViewStartSeconds +
                span * ((slot + 0.5) / slots);
            double x = slot * slotWidth;
            // Each band describes one sampled point, not every frame in a slot.
            if (provider.TryGetCachedThumbnailAtTime(sampleTime, out _))
            {
                context.FillRectangle(ThumbnailReadyBrush,
                    new Rect(x + 1, stripHeight - 8, Math.Max(1, slotWidth - 2), 3));
            }
            if (provider.TryGetFrameIndexAtTimestamp(sampleTime, out _))
            {
                context.FillRectangle(TimestampReadyBrush,
                    new Rect(x + 1, stripHeight - 4, Math.Max(1, slotWidth - 2), 3));
            }
        }
    }
}
