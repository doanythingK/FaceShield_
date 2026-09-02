// FILE: D:\WorkSpace\FaceShield\ViewModels\Workspace\FramePreviewViewModel.cs
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel : ViewModelBase, IDisposable
{
    private readonly ToolPanelViewModel _toolPanel;
    private IFrameMaskProvider? _maskProvider;

    private WriteableBitmap? _frameBitmap;
    private WriteableBitmap? _maskBitmap;
    private WriteableBitmap? _previewBitmap;
    private WriteableBitmap? _blurredFrame;
    private WriteableBitmap? _blurredSource;
    private int _blurredRadius;

    private VideoSession? _session;

    private bool _isDrawing;
    private readonly Stack<byte[]> _maskUndo = new();
    private int _changeStamp;
    private bool _isPlaying;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private int _playbackRunId;
    private int _currentFrameIndex = -1;
    private bool _maskDirty;
    private Point? _lastDrawPoint;
    private long _lastPreviewTick;
    private const int PreviewThrottleMs = 40;
    private bool _hasDirtyRegion;
    private int _dirtyX0;
    private int _dirtyY0;
    private int _dirtyX1;
    private int _dirtyY1;
    private IReadOnlyList<Rect> _detectionRects = Array.Empty<Rect>();
    private bool _showDetectionOverlay;
    private bool _ownsPreviewBitmap;
    private bool _disposed;

    public WriteableBitmap? FrameBitmap
    {
        get => _frameBitmap;
        private set
        {
            if (ReferenceEquals(_frameBitmap, value))
                return;

            var previous = _frameBitmap;
            _frameBitmap = value;
            OnPropertyChanged(nameof(FrameBitmap));

            if (previous != null &&
                !ReferenceEquals(previous, _previewBitmap))
            {
                previous.Dispose();
            }
        }
    }

    public WriteableBitmap? MaskBitmap
    {
        get => _maskBitmap;
        private set
        {
            if (ReferenceEquals(_maskBitmap, value))
                return;

            var previous = _maskBitmap;
            _maskBitmap = value;
            OnPropertyChanged(nameof(MaskBitmap));
            previous?.Dispose();
        }
    }

    public WriteableBitmap? PreviewBitmap => _previewBitmap;

    private void SetPreviewBitmap(WriteableBitmap? value, bool ownsBitmap)
    {
        if (ReferenceEquals(_previewBitmap, value))
        {
            _ownsPreviewBitmap = ownsBitmap;
            OnPropertyChanged(nameof(PreviewBitmap));
            return;
        }

        var previous = _previewBitmap;
        bool disposePrevious = _ownsPreviewBitmap &&
            previous != null &&
            !ReferenceEquals(previous, _frameBitmap) &&
            !ReferenceEquals(previous, _maskBitmap) &&
            !ReferenceEquals(previous, _blurredFrame);

        _previewBitmap = value;
        _ownsPreviewBitmap = ownsBitmap;
        OnPropertyChanged(nameof(PreviewBitmap));

        if (disposePrevious)
            previous!.Dispose();
    }

    private void ResetBlurredFrame()
    {
        var previous = _blurredFrame;
        _blurredFrame = null;
        _blurredSource = null;
        _blurredRadius = 0;
        previous?.Dispose();
    }

    private void PrepareFrameReplacement()
    {
        if (ReferenceEquals(_previewBitmap, _frameBitmap))
            SetPreviewBitmap(null, ownsBitmap: false);

        ResetBlurredFrame();
    }

    public EditMode CurrentMode => _toolPanel.CurrentMode;

    public int BrushDiameter => _toolPanel.BrushDiameter;

    public bool ShowBrushCursor => _toolPanel.ShowBrushSize;

    public IReadOnlyList<Rect> DetectionRects
    {
        get => _detectionRects;
        private set
        {
            _detectionRects = value ?? Array.Empty<Rect>();
            OnPropertyChanged(nameof(DetectionRects));
        }
    }

    public bool ShowDetectionOverlay
    {
        get => _showDetectionOverlay;
        set
        {
            if (_showDetectionOverlay == value)
                return;
            _showDetectionOverlay = value;
            OnPropertyChanged(nameof(ShowDetectionOverlay));
        }
    }

    public event Action<int>? MaskEdited;

    public Cursor CurrentCursor =>
        CurrentMode switch
        {
            EditMode.Brush => Cursor.Parse("Cross"),
            EditMode.Eraser => Cursor.Parse("No"),
            EditMode.Auto => Cursor.Parse("Hand"),
            _ => Cursor.Default
        };

    public int PreviewBlurRadius { get; set; } = 28;

    public FramePreviewViewModel(ToolPanelViewModel toolPanel, IFrameMaskProvider maskProvider)
    {
        _toolPanel = toolPanel;
        _maskProvider = maskProvider;
        PreviewBlurRadius = toolPanel.BlurRadius;

        _toolPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolPanelViewModel.CurrentMode))
            {
                OnPropertyChanged(nameof(CurrentCursor));
                OnPropertyChanged(nameof(ShowBrushCursor));
            }
            else if (e.PropertyName == nameof(ToolPanelViewModel.BrushDiameter))
            {
                OnPropertyChanged(nameof(BrushDiameter));
            }
            else if (e.PropertyName == nameof(ToolPanelViewModel.BlurRadius))
            {
                PreviewBlurRadius = _toolPanel.BlurRadius;
                ResetBlurredFrame();
                RefreshPreview(force: true);
            }
        };
    }

    public void Undo()
    {
        if (_maskBitmap == null) return;
        if (_maskUndo.Count == 0) return;

        var bytes = _maskUndo.Pop();
        RestoreMaskBytes(_maskBitmap, bytes);
        _maskDirty = true;

        RefreshPreview(force: true);
    }

    public void OnPointerPressed(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_maskBitmap == null || _frameBitmap == null) return;

        // ✅ 인스턴스 오버로드 사용 (인수 1개)
        PushUndoSnapshot(_maskBitmap);
        _isDrawing = true;
        _lastDrawPoint = point;
        DrawStroke(point, point);
    }

    public void OnPointerMoved(Point point)
    {
        if (!_isDrawing) return;
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_lastDrawPoint == null)
        {
            _lastDrawPoint = point;
            DrawStroke(point, point);
            return;
        }

        DrawStroke(_lastDrawPoint.Value, point);
        _lastDrawPoint = point;
    }

    public void OnPointerReleased(Point point)
    {
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        _isDrawing = false;
        _lastDrawPoint = null;
        RefreshPreview(force: true);
        if (_maskDirty && _currentFrameIndex >= 0)
            MaskEdited?.Invoke(_currentFrameIndex);
    }

    private void DrawStroke(Point from, Point to)
    {
        if (_maskBitmap is null) return;

        using var fb = _maskBitmap.Lock();
        unsafe
        {
            int radius = Math.Max(1, _toolPanel.BrushDiameter / 2);
            MarkDirty(from, to, radius, fb.Size.Width, fb.Size.Height);

            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;

            void DrawCircleAt(int x, int y)
            {
                if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height)
                    return;

                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(fb.Size.Width - 1, x + radius);
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(fb.Size.Height - 1, y + radius);

                for (int yy = y0; yy <= y1; yy++)
                {
                    byte* row = basePtr + yy * stride;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        int dx = xx - x;
                        int dy = yy - y;
                        if (dx * dx + dy * dy > radius * radius) continue;

                        byte* p = row + xx * 4;

                        if (CurrentMode == EditMode.Brush)
                        {
                            p[0] = 255; p[1] = 255; p[2] = 255; p[3] = 255;
                        }
                        else
                        {
                            p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 0;
                        }
                    }
                }
            }

            double dxLine = to.X - from.X;
            double dyLine = to.Y - from.Y;
            double dist = Math.Sqrt(dxLine * dxLine + dyLine * dyLine);
            if (dist < 0.001)
            {
                DrawCircleAt((int)Math.Round(to.X), (int)Math.Round(to.Y));
            }
            else
            {
                double step = Math.Max(1.0, radius * 0.5);
                int steps = Math.Max(1, (int)Math.Ceiling(dist / step));
                double stepX = dxLine / steps;
                double stepY = dyLine / steps;

                double x = from.X;
                double y = from.Y;
                for (int i = 0; i <= steps; i++)
                {
                    DrawCircleAt((int)Math.Round(x), (int)Math.Round(y));
                    x += stepX;
                    y += stepY;
                }
            }
        }

        OnPropertyChanged(nameof(MaskBitmap));
        RefreshPreview();
        _maskDirty = true;
    }

    private void RefreshPreview(bool force = false)
    {
        if (_frameBitmap == null || _maskBitmap == null) return;
        if (!force)
        {
            long now = Environment.TickCount64;
            if (now - _lastPreviewTick < PreviewThrottleMs)
                return;
            _lastPreviewTick = now;
        }
        else
        {
            _lastPreviewTick = Environment.TickCount64;
        }
        IReadOnlyList<Rect>? faces = null;
        if (_maskProvider is FrameMaskProvider provider &&
            _currentFrameIndex >= 0 &&
            provider.TryGetFaceMaskData(_currentFrameIndex, out var data))
        {
            faces = data.Faces;
        }

        if (faces == null || faces.Count == 0 || _isDrawing)
        {
            EnsureBlurredFrame();
            Rect? dirtyRect = null;
            if (!force && _hasDirtyRegion)
            {
                dirtyRect = new Rect(
                    _dirtyX0,
                    _dirtyY0,
                    Math.Max(0, _dirtyX1 - _dirtyX0 + 1),
                    Math.Max(0, _dirtyY1 - _dirtyY0 + 1));
            }

            var preview = PreviewBlurProcessor.ComposeMaskedPreview(
                _frameBitmap,
                _blurredFrame!,
                _maskBitmap,
                _ownsPreviewBitmap ? _previewBitmap : null,
                dirtyRect);
            SetPreviewBitmap(preview, ownsBitmap: true);
        }
        else
        {
            SetPreviewBitmap(
                PreviewBlurProcessor.CreateBlurPreview(_frameBitmap, _maskBitmap, PreviewBlurRadius, faces),
                ownsBitmap: true);
        }

        _hasDirtyRegion = false;
    }

    private void EnsureBlurredFrame()
    {
        if (_frameBitmap == null)
            return;

        if (_blurredFrame == null ||
            _blurredRadius != PreviewBlurRadius ||
            !ReferenceEquals(_blurredSource, _frameBitmap))
        {
            ResetBlurredFrame();
            _blurredFrame = PreviewBlurProcessor.CreateBlurredFrame(_frameBitmap, PreviewBlurRadius);
            _blurredRadius = PreviewBlurRadius;
            _blurredSource = _frameBitmap;
        }
    }


    private static WriteableBitmap CreateEmptyMask(int w, int h)
    {
        return new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
    }

    private static WriteableBitmap CloneBitmap(WriteableBitmap src)
    {
        var dst = new WriteableBitmap(
            src.PixelSize,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using var sfb = src.Lock();
        using var dfb = dst.Lock();

        unsafe
        {
            int h = src.PixelSize.Height;
            int copy = Math.Min(sfb.RowBytes, dfb.RowBytes);

            byte* s = (byte*)sfb.Address;
            byte* d = (byte*)dfb.Address;

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    s + y * sfb.RowBytes,
                    d + y * dfb.RowBytes,
                    dfb.RowBytes,
                    copy);
            }
        }

        return dst;
    }

    private static void PushUndoSnapshot(WriteableBitmap mask, Stack<byte[]> stack)
    {
        using var fb = mask.Lock();
        unsafe
        {
            int bytes = fb.RowBytes * fb.Size.Height;
            var arr = new byte[bytes];
            MarshalCopyToArray((byte*)fb.Address, arr);
            stack.Push(arr);
        }
    }

    // ✅ 인스턴스용 오버로드: 호출부는 이 메서드 사용
    private void PushUndoSnapshot(WriteableBitmap mask)
        => PushUndoSnapshot(mask, _maskUndo);

    private static void RestoreMaskBytes(WriteableBitmap mask, byte[] bytes)
    {
        using var fb = mask.Lock();
        unsafe
        {
            int len = Math.Min(bytes.Length, fb.RowBytes * fb.Size.Height);
            MarshalCopyFromArray(bytes, (byte*)fb.Address, len);
        }
    }

    private static unsafe void MarshalCopyToArray(byte* src, byte[] dst)
    {
        for (int i = 0; i < dst.Length; i++)
            dst[i] = src[i];
    }

    private static unsafe void MarshalCopyFromArray(byte[] src, byte* dst, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] = src[i];
    }

    // WorkspaceViewModel에서 FramePreview 초기화 시 세션 주입
    public void InitializeSession(VideoSession session)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));
        if (_disposed)
        {
            session.Dispose();
            return;
        }

        PreviewBlurProcessor.ReleaseCachedRenderer();
        _session?.Dispose();
        _session = session;
    }
    public void SetMaskProvider(IFrameMaskProvider maskProvider)
    {
        _maskProvider = maskProvider;
    }
    /// <summary>
    /// 타임라인 / 재생 / 키 이동으로 프레임 인덱스가 바뀔 때 호출.
    /// - 즉시: 썸네일 기반 저화질 프리뷰
    /// - 디바운스 후: 고화질 프레임 + 프레임 인덱스에 맞는 마스크 적용
    /// </summary>
    public async void OnFrameIndexChanged(int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnFrameIndexChanged(index));
            return;
        }

        var session = _session;
        if (_disposed || session == null)
            return;
        if (index < 0)
            return;

        PersistCurrentMask();
        _currentFrameIndex = index;

        if (_isPlaying)
        {
            return;
        }

        int stamp = Interlocked.Increment(ref _changeStamp);

        // 1) 선택 프레임 저화질 프리뷰.
        // 캐시 원본이 아니라 독립 복사본을 받아 eviction과 화면 수명을 분리합니다.
        var exactThumb = await session.Timeline.OnFrameChangingExactAsync(index);
        if (_disposed || !ReferenceEquals(_session, session))
        {
            exactThumb?.Dispose();
            return;
        }

        if (exactThumb != null)
        {
            if (stamp == _changeStamp)
                SetPreviewBitmap(exactThumb, ownsBitmap: true);
            else
                exactThumb.Dispose();
        }

        // 2) 고화질 프레임
        var exact = await session.Timeline.OnFrameChangedAsync(index);
        if (_disposed || !ReferenceEquals(_session, session))
        {
            exact?.Dispose();
            return;
        }

        if (exact == null || stamp != _changeStamp)
        {
            if (exact != null)
                exact.Dispose();
            if (!_isPlaying && stamp == _changeStamp)
                await TryLoadExactFallbackAsync(session, index, stamp);
            Debug.WriteLine($"[FramePreview] exact frame not available (frame={index}, stamp={stamp}).");
            return;
        }

        ApplyExactFrame(exact, index, stamp);
    }

    public async void OnPlaybackStopped(int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPlaybackStopped(index));
            return;
        }

        var session = _session;
        if (_disposed || session == null)
            return;
        if (index < 0)
            return;

        int stamp = Interlocked.Increment(ref _changeStamp);

        await TryLoadExactFallbackAsync(session, index, stamp);
    }

    public void StartPlayback(
        string videoPath,
        int startFrameIndex,
        double fps,
        int totalFrames,
        Action<int> onFrameAdvanced,
        Action onPlaybackEnded,
        Action<string>? onPlaybackFailed = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => StartPlayback(
                videoPath,
                startFrameIndex,
                fps,
                totalFrames,
                onFrameAdvanced,
                onPlaybackEnded,
                onPlaybackFailed));
            return;
        }

        if (string.IsNullOrWhiteSpace(videoPath) || startFrameIndex < 0)
        {
            onPlaybackEnded();
            return;
        }

        PersistCurrentMask();
        Interlocked.Increment(ref _changeStamp);

        Task? previousPlaybackTask = _playbackTask;
        CancellationTokenSource? previousCts = _playbackCts;
        if (previousCts != null)
        {
            try { previousCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        var playbackCts = new CancellationTokenSource();
        _playbackCts = playbackCts;

        _isPlaying = true;
        int runId = Interlocked.Increment(ref _playbackRunId);
        int safeStart = totalFrames > 0
            ? Math.Clamp(startFrameIndex, 0, totalFrames - 1)
            : Math.Max(0, startFrameIndex);
        _currentFrameIndex = safeStart;

        _playbackTask = StartPlaybackAfterPreviousAsync(
            previousPlaybackTask,
            runId,
            videoPath,
            safeStart,
            fps,
            totalFrames,
            playbackCts,
            onFrameAdvanced,
            onPlaybackEnded,
            onPlaybackFailed);
    }

    private async Task StartPlaybackAfterPreviousAsync(
        Task? previousPlaybackTask,
        int runId,
        string videoPath,
        int startFrameIndex,
        double fps,
        int totalFrames,
        CancellationTokenSource playbackCts,
        Action<int> onFrameAdvanced,
        Action onPlaybackEnded,
        Action<string>? onPlaybackFailed)
    {
        try
        {
            if (previousPlaybackTask != null)
            {
                try
                {
                    await previousPlaybackTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[FramePreview] previous playback shutdown completed with error: {ex.Message}");
                }
            }

            if (playbackCts.IsCancellationRequested ||
                runId != Volatile.Read(ref _playbackRunId) ||
                _disposed)
            {
                return;
            }

            await RunSequentialPlaybackAsync(
                runId,
                videoPath,
                startFrameIndex,
                fps,
                totalFrames,
                playbackCts.Token,
                onFrameAdvanced,
                onPlaybackEnded,
                onPlaybackFailed).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_playbackCts, playbackCts))
                _playbackCts = null;
            playbackCts.Dispose();
        }
    }

    public void StopPlayback()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StopPlayback);
            return;
        }

        _isPlaying = false;
        Interlocked.Increment(ref _playbackRunId);

        CancellationTokenSource? playbackCts = _playbackCts;
        _playbackCts = null;
        if (playbackCts != null)
        {
            try { playbackCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task SuspendExactFrameOperationsAndWaitAsync()
    {
        var session = _session;
        if (_disposed || session == null)
            return;

        await session.ExactProvider.SuspendOperationsAndWaitAsync();
    }

    public void ResumeExactFrameOperations()
    {
        if (_disposed)
            return;

        _session?.ExactProvider.ResumeOperations();
    }

    public async Task StopPlaybackAndWaitAsync()
    {
        Task? playbackTask = null;
        if (Dispatcher.UIThread.CheckAccess())
        {
            playbackTask = _playbackTask;
            StopPlayback();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                playbackTask = _playbackTask;
                StopPlayback();
            });
        }

        if (playbackTask == null)
            return;

        try
        {
            await playbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FramePreview] playback shutdown completed with error: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_playbackTask, playbackTask))
                _playbackTask = null;
        }
    }

    private Task RunSequentialPlaybackAsync(
        int runId,
        string videoPath,
        int startFrameIndex,
        double fps,
        int totalFrames,
        CancellationToken ct,
        Action<int> onFrameAdvanced,
        Action onPlaybackEnded,
        Action<string>? onPlaybackFailed)
    {
        return Task.Run(async () =>
        {
            bool endedNaturally = false;
            string? playbackError = null;
            double frameMs = fps > 0 ? 1000.0 / fps : 33.333;
            double? playbackOriginTimestampSeconds = null;
            double lastTargetMs = 0;
            var clock = Stopwatch.StartNew();

            try
            {
                using var extractor = new FfFrameExtractor(
                    videoPath,
                    cancellationToken: ct);
                extractor.StartSequentialRead(startFrameIndex, ct);

                while (!ct.IsCancellationRequested &&
                       extractor.TryGetNextFrame(ct, out var frame, out int frameIndex))
                {
                    if (frame == null)
                        continue;
                    if (frameIndex < startFrameIndex)
                    {
                        frame.Dispose();
                        continue;
                    }

                    if (totalFrames > 0 && frameIndex >= totalFrames)
                    {
                        frame.Dispose();
                        endedNaturally = true;
                        break;
                    }

                    double decodedTimestampSeconds =
                        extractor.LastDecodedTimestampSeconds;
                    double targetMs;
                    if (double.IsFinite(decodedTimestampSeconds))
                    {
                        playbackOriginTimestampSeconds ??= decodedTimestampSeconds;
                        targetMs = Math.Max(
                            0,
                            (decodedTimestampSeconds -
                             playbackOriginTimestampSeconds.Value) * 1000.0);
                    }
                    else
                    {
                        targetMs =
                            Math.Max(0, frameIndex - startFrameIndex) * frameMs;
                    }

                    targetMs = Math.Max(lastTargetMs, targetMs);
                    lastTargetMs = targetMs;
                    double delayMs = targetMs - clock.Elapsed.TotalMilliseconds;
                    if (delayMs > 1)
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);

                    bool frameAccepted = false;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || runId != _playbackRunId || !_isPlaying)
                            return;

                        ApplyPlaybackFrame(frame, frameIndex);
                        frameAccepted = true;
                        onFrameAdvanced(frameIndex);
                    });

                    if (!frameAccepted)
                        frame.Dispose();

                    if (totalFrames > 0 && frameIndex >= totalFrames - 1)
                    {
                        endedNaturally = true;
                        break;
                    }
                }

                if (!ct.IsCancellationRequested && !extractor.SequentialReadCancelled)
                {
                    if (!string.IsNullOrWhiteSpace(extractor.SequentialDecodeError))
                    {
                        playbackError = extractor.SequentialDecodeError;
                    }
                    else if (extractor.SequentialReachedEndOfStream)
                    {
                        endedNaturally = true;
                    }
                    else if (!endedNaturally)
                    {
                        playbackError = "재생 디코더가 EOF에 도달하지 않은 상태에서 중단되었습니다.";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                endedNaturally = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FramePreview] sequential playback failed: {ex.Message}");
                playbackError = ex.Message;
                endedNaturally = false;
            }

            if (ct.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (runId != _playbackRunId || !_isPlaying)
                    return;

                if (!string.IsNullOrWhiteSpace(playbackError))
                {
                    Debug.WriteLine($"[FramePreview] sequential playback stopped by decode error: {playbackError}");
                    if (onPlaybackFailed != null)
                        onPlaybackFailed(playbackError);
                    else
                        onPlaybackEnded();
                    return;
                }

                if (endedNaturally)
                    onPlaybackEnded();
            });
        }, CancellationToken.None);
    }

    private void ApplyExactFrame(WriteableBitmap exact, int index, int? expectedStamp = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyExactFrame(exact, index, expectedStamp));
            return;
        }

        if (expectedStamp.HasValue && expectedStamp.Value != _changeStamp)
        {
            exact.Dispose();
            return;
        }

        _currentFrameIndex = index;
        PrepareFrameReplacement();
        FrameBitmap = exact;

        // 🔹 2-1) 저장 마스크는 복제하고, 얼굴 rect는 바로 편집용 마스크로 렌더링합니다.
        // FrameMaskProvider.GetFinalMask()의 임시 full-resolution bitmap 생성을 피합니다.
        MaskBitmap = CreateEditableMask(index, exact)
            ?? CreateEmptyMask(exact.PixelSize.Width, exact.PixelSize.Height);
        _maskUndo.Clear();
        _maskDirty = false;

        if (_maskProvider is FrameMaskProvider faceProvider &&
            faceProvider.TryGetFaceMaskData(index, out var faceData))
        {
            DetectionRects = faceData.Faces;
        }
        else
        {
            DetectionRects = Array.Empty<Rect>();
        }

        // 3) 프리뷰 갱신
        RefreshPreview(force: true);
    }

    private void ApplyPlaybackFrame(WriteableBitmap exact, int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyPlaybackFrame(exact, index));
            return;
        }

        _currentFrameIndex = index;
        PrepareFrameReplacement();
        FrameBitmap = exact;
        _maskUndo.Clear();
        _maskDirty = false;

        MaskBitmap = CreateEditableMask(index, exact);

        if (MaskBitmap == null)
        {
            DetectionRects = Array.Empty<Rect>();
            SetPreviewBitmap(exact, ownsBitmap: false);
            return;
        }

        if (_maskProvider is FrameMaskProvider faceProvider &&
            faceProvider.TryGetFaceMaskData(index, out var faceData))
        {
            DetectionRects = faceData.Faces;
        }
        else
        {
            DetectionRects = Array.Empty<Rect>();
        }

        RefreshPreview(force: true);
    }

    private WriteableBitmap? CreateEditableMask(int frameIndex, WriteableBitmap frame)
    {
        if (_maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryGetStoredMask(frameIndex, out var stored))
            {
                return stored.PixelSize.Width == frame.PixelSize.Width &&
                       stored.PixelSize.Height == frame.PixelSize.Height
                    ? CloneBitmap(stored)
                    : null;
            }

            if (provider.TryGetFaceMaskData(frameIndex, out var faceData))
            {
                if (faceData.Size.Width != frame.PixelSize.Width ||
                    faceData.Size.Height != frame.PixelSize.Height)
                {
                    return null;
                }

                return FrameMaskProvider.CreateMaskFromFaceRects(faceData.Size, faceData.Faces);
            }

            return null;
        }

        var providerMask = _maskProvider?.GetFinalMask(frameIndex);
        if (providerMask == null ||
            providerMask.PixelSize.Width != frame.PixelSize.Width ||
            providerMask.PixelSize.Height != frame.PixelSize.Height)
        {
            return null;
        }

        return CloneBitmap(providerMask);
    }

    public void PersistCurrentMask()
    {
        if (!_maskDirty)
            return;
        if (_maskProvider == null || _currentFrameIndex < 0 || _maskBitmap == null)
            return;

        _maskProvider.SetMask(_currentFrameIndex, CloneBitmap(_maskBitmap));
        _maskDirty = false;
    }

    private async Task TryLoadExactFallbackAsync(
        VideoSession session,
        int index,
        int stamp)
    {
        if (_disposed || !ReferenceEquals(_session, session))
            return;

        var exact = await session.Timeline.GetExactNowAsync(index);
        if (_disposed ||
            !ReferenceEquals(_session, session) ||
            stamp != _changeStamp)
        {
            exact?.Dispose();
            return;
        }

        if (exact == null)
        {
            await Task.Delay(120);
            if (_disposed ||
                !ReferenceEquals(_session, session) ||
                stamp != _changeStamp)
            {
                return;
            }

            exact = await session.Timeline.GetExactNowAsync(index);
        }

        if (exact == null ||
            _disposed ||
            !ReferenceEquals(_session, session) ||
            stamp != _changeStamp)
        {
            exact?.Dispose();
            return;
        }

        ApplyExactFrame(exact, index, stamp);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Increment(ref _changeStamp);
        Interlocked.Increment(ref _playbackRunId);
        _isPlaying = false;

        CancellationTokenSource? playbackCts = _playbackCts;
        _playbackCts = null;
        if (playbackCts != null)
        {
            try { playbackCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        SetPreviewBitmap(null, ownsBitmap: false);
        ResetBlurredFrame();
        FrameBitmap = null;
        MaskBitmap = null;

        var session = _session;
        _session = null;
        session?.Dispose();
        PreviewBlurProcessor.ReleaseCachedRenderer();
    }

    private void MarkDirty(Point from, Point to, int radius, int width, int height)
    {
        int x0 = (int)Math.Floor(Math.Min(from.X, to.X) - radius);
        int y0 = (int)Math.Floor(Math.Min(from.Y, to.Y) - radius);
        int x1 = (int)Math.Ceiling(Math.Max(from.X, to.X) + radius);
        int y1 = (int)Math.Ceiling(Math.Max(from.Y, to.Y) + radius);

        x0 = Math.Clamp(x0, 0, Math.Max(0, width - 1));
        y0 = Math.Clamp(y0, 0, Math.Max(0, height - 1));
        x1 = Math.Clamp(x1, 0, Math.Max(0, width - 1));
        y1 = Math.Clamp(y1, 0, Math.Max(0, height - 1));

        if (!_hasDirtyRegion)
        {
            _dirtyX0 = x0;
            _dirtyY0 = y0;
            _dirtyX1 = x1;
            _dirtyY1 = y1;
            _hasDirtyRegion = true;
            return;
        }

        _dirtyX0 = Math.Min(_dirtyX0, x0);
        _dirtyY0 = Math.Min(_dirtyY0, y0);
        _dirtyX1 = Math.Max(_dirtyX1, x1);
        _dirtyY1 = Math.Max(_dirtyY1, y1);
    }

}
