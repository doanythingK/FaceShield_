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
using System.ComponentModel;
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
    private bool _useManualPlayer;
    private CancellationTokenSource? _manualFrameLoadCts;
    private Task? _manualFrameLoadTask;
    private int _manualFrameLoadGeneration;
    private ManualFrameIdentity? _currentManualFrameIdentity;
    private bool _isFrameLoading;
    private string? _frameLoadingMessage;

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

    public bool IsFrameLoading
    {
        get => _isFrameLoading;
        private set
        {
            if (_isFrameLoading == value)
                return;
            _isFrameLoading = value;
            OnPropertyChanged(nameof(IsFrameLoading));
        }
    }

    public string? FrameLoadingMessage
    {
        get => _frameLoadingMessage;
        private set
        {
            if (string.Equals(_frameLoadingMessage, value, StringComparison.Ordinal))
                return;
            _frameLoadingMessage = value;
            OnPropertyChanged(nameof(FrameLoadingMessage));
        }
    }

    internal ManualFrameIdentity? CurrentManualFrameIdentity =>
        _currentManualFrameIdentity;

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

    private bool CanMutateCurrentMask()
        => _toolPanel.CanEditWorkspace &&
           !_isPlaying &&
           !IsFrameLoading &&
           _currentFrameIndex >= 0 &&
           _frameBitmap != null &&
           _maskBitmap != null;

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

        _toolPanel.PropertyChanged += OnToolPanelPropertyChanged;
    }

    internal void SetSessionReady(bool ready)
    {
        void Apply()
        {
            if (!_disposed)
                _toolPanel.IsSessionReady = ready;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void OnToolPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

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
    }

    public void Undo()
    {
        if (!CanMutateCurrentMask()) return;
        if (_maskBitmap == null) return;
        if (_maskUndo.Count == 0) return;

        var bytes = _maskUndo.Pop();
        RestoreMaskBytes(_maskBitmap, bytes);
        _maskDirty = true;

        RefreshPreview(force: true);
    }

    public void OnPointerPressed(Point point)
    {
        if (!CanMutateCurrentMask()) return;
        if (CurrentMode is not EditMode.Brush and not EditMode.Eraser) return;
        if (_maskBitmap == null || _frameBitmap == null) return;

        PushUndoSnapshot(_maskBitmap);
        _isDrawing = true;
        _lastDrawPoint = point;
        DrawStroke(point, point);
    }

    public void OnPointerMoved(Point point)
    {
        if (!CanMutateCurrentMask())
        {
            _isDrawing = false;
            _lastDrawPoint = null;
            return;
        }
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
        if (!CanMutateCurrentMask())
        {
            _isDrawing = false;
            _lastDrawPoint = null;
            return;
        }
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

    public void InitializeSession(
        VideoSession session,
        bool useManualPlayer = false)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));
        if (_disposed)
        {
            session.Dispose();
            return;
        }

        CancelManualFrameLoad();
        PreviewBlurProcessor.ReleaseCachedRenderer();
        _session?.Dispose();
        _session = session;
        _useManualPlayer = useManualPlayer && session.ManualPlayer != null;
        _currentManualFrameIdentity = null;
    }

    public void SetMaskProvider(IFrameMaskProvider maskProvider)
    {
        _maskProvider = maskProvider;
    }

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

        if (_useManualPlayer)
        {
            await LoadManualFrameAsync(index);
            return;
        }

        PersistCurrentMask();
        InvalidateEditableFrameState();

        if (_isPlaying)
            return;

        int stamp = Interlocked.Increment(ref _changeStamp);

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

        var exact = await session.Timeline.OnFrameChangedAsync(index);
        if (_disposed || !ReferenceEquals(_session, session))
        {
            exact?.Dispose();
            return;
        }

        if (exact == null || stamp != _changeStamp)
        {
            exact?.Dispose();
            if (!_isPlaying && stamp == _changeStamp)
                await TryLoadExactFallbackAsync(session, index, stamp);
            Debug.WriteLine($"[FramePreview] exact frame not available (frame={index}, stamp={stamp}).");
            return;
        }

        ApplyExactFrame(exact, index, stamp);
    }

    internal async Task LoadManualFrameAsync(
        int index,
        CancellationToken cancellationToken = default)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await LoadManualFrameAsync(index, cancellationToken);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            await completion.Task.ConfigureAwait(false);
            return;
        }

        var session = _session;
        var player = session?.ManualPlayer;
        if (_disposed || !_useManualPlayer || session == null || player == null)
            return;
        if (index < 0 || _isPlaying)
            return;

        if (_currentFrameIndex == index &&
            _frameBitmap != null &&
            _currentManualFrameIdentity?.FrameOrdinal == index)
        {
            EnsureCurrentManualFrameEditable(index);
            return;
        }

        PersistCurrentMask();
        _isDrawing = false;
        _lastDrawPoint = null;
        _maskUndo.Clear();

        await CancelManualFrameLoadAndWaitAsync();

        // Track the entire manual-load operation, not only the decoder Task.Run.
        // Shutdown must also wait for fallback exact-load and UI ownership transfer.
        var operationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task operationTask = operationCompletion.Task;
        Volatile.Write(ref _manualFrameLoadTask, operationTask);

        int generation = Interlocked.Increment(ref _manualFrameLoadGeneration);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Interlocked.Exchange(ref _manualFrameLoadCts, requestCts);

        IsFrameLoading = true;
        FrameLoadingMessage = "프레임 불러오는 중...";

        try
        {
            Task<ManualPlayerFrame?> decodeTask = Task.Run(
                () => player.LoadFrame(index, requestCts.Token),
                requestCts.Token);
            ManualPlayerFrame? loaded = await decodeTask;

            if (loaded == null)
            {
                if (!requestCts.IsCancellationRequested &&
                    generation == _manualFrameLoadGeneration &&
                    ReferenceEquals(_session, session))
                {
                    int stamp = Interlocked.Increment(ref _changeStamp);
                    await TryLoadExactFallbackAsync(
                        session,
                        index,
                        stamp,
                        requestCts.Token);
                }
                return;
            }

            WriteableBitmap bitmap = loaded.Bitmap;
            if (_disposed ||
                requestCts.IsCancellationRequested ||
                generation != _manualFrameLoadGeneration ||
                !ReferenceEquals(_session, session))
            {
                bitmap.Dispose();
                return;
            }

            bool frameOwnershipTransferred = false;
            try
            {
                ApplyManualFrame(
                    bitmap,
                    loaded.Identity,
                    out frameOwnershipTransferred);
            }
            finally
            {
                if (!frameOwnershipTransferred)
                    bitmap.Dispose();
            }
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FramePreview] manual player frame load failed (frame={index}): {ex}");
            if (!_disposed &&
                !requestCts.IsCancellationRequested &&
                generation == _manualFrameLoadGeneration &&
                ReferenceEquals(_session, session))
            {
                int stamp = Interlocked.Increment(ref _changeStamp);
                await TryLoadExactFallbackAsync(
                    session,
                    index,
                    stamp,
                    requestCts.Token);
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _manualFrameLoadCts,
                null,
                requestCts);
            ClearManualFrameLoadingState(generation);

            operationCompletion.TrySetResult(true);
            _ = Interlocked.CompareExchange(
                ref _manualFrameLoadTask,
                null,
                operationTask);
            requestCts.Dispose();
        }
    }

    private void ClearManualFrameLoadingState(int generation)
    {
        void Clear()
        {
            if (_disposed || generation != _manualFrameLoadGeneration)
                return;

            IsFrameLoading = false;
            FrameLoadingMessage = null;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Clear();
        else
            Dispatcher.UIThread.Post(Clear);
    }

    private async Task CancelManualFrameLoadAndWaitAsync()
    {
        int generation = Interlocked.Increment(ref _manualFrameLoadGeneration);
        CancellationTokenSource? cts =
            Interlocked.Exchange(ref _manualFrameLoadCts, null);
        Task? loadTask = Volatile.Read(ref _manualFrameLoadTask);

        if (cts != null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        ClearManualFrameLoadingState(generation);

        if (loadTask == null)
            return;

        try
        {
            await loadTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FramePreview] manual frame load shutdown completed with error: {ex.Message}");
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _manualFrameLoadTask,
                null,
                loadTask);
            ClearManualFrameLoadingState(generation);
        }
    }

    internal async Task StopManualOperationsAndWaitAsync()
    {
        await StopPlaybackAndWaitAsync().ConfigureAwait(false);
        await CancelManualFrameLoadAndWaitAsync().ConfigureAwait(false);
    }

    private void CancelManualFrameLoad()
    {
        int generation = Interlocked.Increment(ref _manualFrameLoadGeneration);
        CancellationTokenSource? cts =
            Interlocked.Exchange(ref _manualFrameLoadCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        ClearManualFrameLoadingState(generation);
    }

    private void EnsureCurrentManualFrameEditable(int index)
    {
        if (_frameBitmap == null || _currentFrameIndex != index)
            return;

        if (_maskBitmap == null)
        {
            MaskBitmap = CreateEditableMask(index, _frameBitmap)
                ?? CreateEmptyMask(
                    _frameBitmap.PixelSize.Width,
                    _frameBitmap.PixelSize.Height);
        }

        UpdateDetectionRects(index);
        _maskUndo.Clear();
        _maskDirty = false;
        RefreshPreview(force: true);
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

        if (_useManualPlayer)
        {
            if (_currentFrameIndex == index && _frameBitmap != null)
            {
                EnsureCurrentManualFrameEditable(index);
                return;
            }

            await LoadManualFrameAsync(index);
            return;
        }

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

        if (_disposed)
            return;

        if (string.IsNullOrWhiteSpace(videoPath) || startFrameIndex < 0)
        {
            onPlaybackEnded();
            return;
        }

        var playbackSession = _session;
        if (playbackSession == null)
        {
            const string message = "영상 세션이 아직 준비되지 않았습니다.";
            if (onPlaybackFailed != null)
                onPlaybackFailed(message);
            else
                onPlaybackEnded();
            return;
        }

        PersistCurrentMask();
        bool useManualPlayback =
            _useManualPlayer && playbackSession.ManualPlayer != null;
        bool continueManualFromCurrentFrame =
            useManualPlayback &&
            _frameBitmap != null &&
            _currentFrameIndex == startFrameIndex &&
            _currentManualFrameIdentity?.FrameOrdinal == startFrameIndex;
        double? manualOriginTimestampSeconds =
            continueManualFromCurrentFrame &&
            _currentManualFrameIdentity.HasValue &&
            double.IsFinite(_currentManualFrameIdentity.Value.TimelineSeconds)
                ? _currentManualFrameIdentity.Value.TimelineSeconds
                : null;

        CancelManualFrameLoad();
        if (useManualPlayback)
        {
            _isDrawing = false;
            _lastDrawPoint = null;
            _maskUndo.Clear();
            _hasDirtyRegion = false;
        }
        else
        {
            InvalidateEditableFrameState();
        }
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
            onPlaybackFailed,
            useManualPlayback,
            continueManualFromCurrentFrame,
            manualOriginTimestampSeconds);
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
        Action<string>? onPlaybackFailed,
        bool useManualPlayback,
        bool continueManualFromCurrentFrame,
        double? manualOriginTimestampSeconds)
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

            if (useManualPlayback)
            {
                await RunManualSequentialPlaybackAsync(
                    runId,
                    startFrameIndex,
                    fps,
                    totalFrames,
                    playbackCts.Token,
                    onFrameAdvanced,
                    onPlaybackEnded,
                    onPlaybackFailed,
                    continueManualFromCurrentFrame,
                    manualOriginTimestampSeconds).ConfigureAwait(false);
            }
            else
            {
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

        if (_disposed)
            return;

        _isPlaying = false;
        if (_useManualPlayer)
        {
            _isDrawing = false;
            _lastDrawPoint = null;
            _maskUndo.Clear();
            _hasDirtyRegion = false;
        }
        else
        {
            InvalidateEditableFrameState();
        }
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

    private Task RunManualSequentialPlaybackAsync(
        int runId,
        int startFrameIndex,
        double fps,
        int totalFrames,
        CancellationToken ct,
        Action<int> onFrameAdvanced,
        Action onPlaybackEnded,
        Action<string>? onPlaybackFailed,
        bool continueFromCurrentFrame,
        double? originTimestampSeconds)
    {
        var session = _session;
        var player = session?.ManualPlayer;
        if (player == null)
        {
            return Task.Run(() =>
            {
                Dispatcher.UIThread.Post(() =>
                    onPlaybackFailed?.Invoke(
                        "수동 재생 디코더가 준비되지 않았습니다."));
            });
        }

        return Task.Run(async () =>
        {
            bool endedNaturally = false;
            string? playbackError = null;
            double frameMs = fps > 0 ? 1000.0 / fps : 33.333;
            double? playbackOriginTimestampSeconds = originTimestampSeconds;
            double lastTargetMs = 0;
            int fallbackIndex = continueFromCurrentFrame
                ? startFrameIndex == int.MaxValue
                    ? int.MaxValue
                    : startFrameIndex + 1
                : startFrameIndex;
            bool firstRead = true;
            var clock = Stopwatch.StartNew();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ManualPlayerFrame? decoded = firstRead && !continueFromCurrentFrame
                        ? player.LoadFrame(fallbackIndex, ct)
                        : player.ReadNext(fallbackIndex, ct);
                    firstRead = false;
                    if (decoded == null)
                    {
                        if (!ct.IsCancellationRequested && !player.ReadCancelled)
                        {
                            playbackError = player.DecodeError;
                            endedNaturally =
                                string.IsNullOrWhiteSpace(playbackError) &&
                                player.ReachedEndOfStream;
                        }
                        break;
                    }

                    WriteableBitmap? frame = decoded.Bitmap;
                    bool frameCommittedToPreview = false;
                    try
                    {
                        int frameIndex = decoded.Identity.FrameOrdinal;
                        fallbackIndex = frameIndex == int.MaxValue
                            ? int.MaxValue
                            : frameIndex + 1;

                        if (totalFrames > 0 && frameIndex >= totalFrames)
                        {
                            endedNaturally = true;
                            break;
                        }

                        double decodedTimestampSeconds =
                            decoded.Identity.TimelineSeconds;
                        double targetMs;
                        if (double.IsFinite(decodedTimestampSeconds))
                        {
                            playbackOriginTimestampSeconds ??=
                                decodedTimestampSeconds;
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
                        double delayMs =
                            targetMs - clock.Elapsed.TotalMilliseconds;
                        if (delayMs > 1)
                        {
                            await Task.Delay(
                                TimeSpan.FromMilliseconds(delayMs),
                                ct).ConfigureAwait(false);
                        }

                        WriteableBitmap frameToApply = frame;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested ||
                                runId != _playbackRunId ||
                                !_isPlaying)
                            {
                                return;
                            }

                            ApplyPlaybackFrame(
                                frameToApply,
                                frameIndex,
                                decoded.Identity,
                                out frameCommittedToPreview);
                            if (frameCommittedToPreview)
                                onFrameAdvanced(frameIndex);
                        });

                        if (!frameCommittedToPreview)
                            break;

                        frame = null;
                        if (totalFrames > 0 &&
                            frameIndex >= totalFrames - 1)
                        {
                            endedNaturally = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!frameCommittedToPreview)
                        {
                            frame?.Dispose();
                            TryInvalidateManualSequentialPosition(player);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                endedNaturally = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[FramePreview] manual sequential playback failed: {ex}");
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

    private static void TryInvalidateManualSequentialPosition(
        ManualFramePlayer player)
    {
        try
        {
            player.InvalidateSequentialPosition();
        }
        catch (ObjectDisposedException)
        {
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
                       extractor.TryGetNextFrame(
                           ct,
                           out WriteableBitmap? decodedFrame,
                           out int frameIndex))
                {
                    if (decodedFrame == null)
                        continue;

                    WriteableBitmap? frame = decodedFrame;
                    bool frameCommittedToPreview = false;
                    try
                    {
                        if (frameIndex < startFrameIndex)
                            continue;

                        if (totalFrames > 0 && frameIndex >= totalFrames)
                        {
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

                        WriteableBitmap frameToApply = frame;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested ||
                                runId != _playbackRunId ||
                                !_isPlaying)
                            {
                                return;
                            }

                            ApplyPlaybackFrame(
                                frameToApply,
                                frameIndex,
                                manualIdentity: null,
                                out frameCommittedToPreview);
                            if (frameCommittedToPreview)
                                onFrameAdvanced(frameIndex);
                        });

                        if (!frameCommittedToPreview)
                            break;

                        frame = null;
                        if (totalFrames > 0 && frameIndex >= totalFrames - 1)
                        {
                            endedNaturally = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!frameCommittedToPreview)
                            frame?.Dispose();
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

        if (_disposed ||
            (expectedStamp.HasValue && expectedStamp.Value != _changeStamp))
        {
            exact.Dispose();
            return;
        }

        _currentManualFrameIdentity = null;
        _currentFrameIndex = index;
        PrepareFrameReplacement();
        FrameBitmap = exact;

        MaskBitmap = CreateEditableMask(index, exact)
            ?? CreateEmptyMask(exact.PixelSize.Width, exact.PixelSize.Height);
        _maskUndo.Clear();
        _maskDirty = false;

        UpdateDetectionRects(index);
        RefreshPreview(force: true);
    }

    private void ApplyPlaybackFrame(
        WriteableBitmap exact,
        int index,
        ManualFrameIdentity? manualIdentity,
        out bool ownershipTransferred)
    {
        ownershipTransferred = false;
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Playback frame ownership can only transfer on the UI thread.");

        if (_disposed)
            return;

        _currentManualFrameIdentity = manualIdentity;
        _currentFrameIndex = index;
        PrepareFrameReplacement();
        FrameBitmap = exact;
        ownershipTransferred = true;
        _maskUndo.Clear();
        _maskDirty = false;

        MaskBitmap = CreateEditableMask(index, exact);

        if (MaskBitmap == null)
        {
            DetectionRects = Array.Empty<Rect>();
            SetPreviewBitmap(exact, ownsBitmap: false);
            return;
        }

        UpdateDetectionRects(index);
        RefreshPreview(force: true);
    }

    private void ApplyManualFrame(
        WriteableBitmap frame,
        ManualFrameIdentity identity,
        out bool ownershipTransferred)
    {
        ownershipTransferred = false;
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Manual frame ownership can only transfer on the UI thread.");

        if (_disposed)
            return;

        _currentManualFrameIdentity = identity;
        _currentFrameIndex = identity.FrameOrdinal;
        PrepareFrameReplacement();
        FrameBitmap = frame;
        ownershipTransferred = true;
        MaskBitmap = CreateEditableMask(identity.FrameOrdinal, frame)
            ?? CreateEmptyMask(
                frame.PixelSize.Width,
                frame.PixelSize.Height);
        _maskUndo.Clear();
        _maskDirty = false;
        UpdateDetectionRects(identity.FrameOrdinal);
        RefreshPreview(force: true);
    }

    private void UpdateDetectionRects(int frameIndex)
    {
        if (_maskProvider is FrameMaskProvider faceProvider &&
            faceProvider.TryGetFaceMaskData(frameIndex, out var faceData))
        {
            DetectionRects = faceData.Faces;
        }
        else
        {
            DetectionRects = Array.Empty<Rect>();
        }
    }

    private WriteableBitmap? CreateEditableMask(int frameIndex, WriteableBitmap frame)
    {
        if (_maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryCloneStoredMask(frameIndex, out var stored))
            {
                if (stored.PixelSize.Width == frame.PixelSize.Width &&
                    stored.PixelSize.Height == frame.PixelSize.Height)
                {
                    return stored;
                }

                stored.Dispose();
                return null;
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
        if (providerMask == null)
            return null;

        if (providerMask.PixelSize.Width != frame.PixelSize.Width ||
            providerMask.PixelSize.Height != frame.PixelSize.Height)
        {
            providerMask.Dispose();
            return null;
        }

        return providerMask;
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

    private void InvalidateEditableFrameState()
    {
        _isDrawing = false;
        _lastDrawPoint = null;
        _maskUndo.Clear();
        _maskDirty = false;
        _hasDirtyRegion = false;
        _currentManualFrameIdentity = null;
        _currentFrameIndex = -1;
        MaskBitmap = null;
        DetectionRects = Array.Empty<Rect>();
    }

    private async Task TryLoadExactFallbackAsync(
        VideoSession session,
        int index,
        int stamp,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !ReferenceEquals(_session, session))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var exact = await session.Timeline.GetExactNowAsync(
            index,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed ||
            !ReferenceEquals(_session, session) ||
            stamp != _changeStamp)
        {
            exact?.Dispose();
            return;
        }

        if (exact == null)
        {
            await Task.Delay(120, cancellationToken);
            if (_disposed ||
                !ReferenceEquals(_session, session) ||
                stamp != _changeStamp)
            {
                return;
            }

            exact = await session.Timeline.GetExactNowAsync(
                index,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
        CancelManualFrameLoad();
        _toolPanel.PropertyChanged -= OnToolPanelPropertyChanged;
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
