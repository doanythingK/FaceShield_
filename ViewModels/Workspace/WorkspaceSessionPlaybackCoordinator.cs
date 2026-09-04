using Avalonia.Threading;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video.Session;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

internal sealed class WorkspaceSessionPlaybackCoordinator : IDisposable
{
    private readonly WorkspaceMode _mode;
    private readonly FrameListViewModel _frameList;
    private readonly FramePreviewViewModel _framePreview;
    private readonly Func<bool> _isAutoRunning;
    private readonly Action _markAutoPreviewNeedsExactRefresh;
    private readonly Func<bool> _tryBeginLifetimeOperation;
    private readonly Action _endLifetimeOperation;
    private readonly Func<string, Task> _showPlaybackErrorAsync;
    private readonly object _stateGate = new();

    private CancellationTokenSource? _sessionInitCts;
    private bool _initialized;
    private bool _disposed;

    internal WorkspaceSessionPlaybackCoordinator(
        WorkspaceMode mode,
        FrameListViewModel frameList,
        FramePreviewViewModel framePreview,
        Func<bool> isAutoRunning,
        Action markAutoPreviewNeedsExactRefresh,
        Func<bool> tryBeginLifetimeOperation,
        Action endLifetimeOperation,
        Func<string, Task> showPlaybackErrorAsync)
    {
        _mode = mode;
        _frameList = frameList ?? throw new ArgumentNullException(nameof(frameList));
        _framePreview = framePreview ?? throw new ArgumentNullException(nameof(framePreview));
        _isAutoRunning = isAutoRunning ?? throw new ArgumentNullException(nameof(isAutoRunning));
        _markAutoPreviewNeedsExactRefresh = markAutoPreviewNeedsExactRefresh ?? throw new ArgumentNullException(nameof(markAutoPreviewNeedsExactRefresh));
        _tryBeginLifetimeOperation = tryBeginLifetimeOperation ?? throw new ArgumentNullException(nameof(tryBeginLifetimeOperation));
        _endLifetimeOperation = endLifetimeOperation ?? throw new ArgumentNullException(nameof(endLifetimeOperation));
        _showPlaybackErrorAsync = showPlaybackErrorAsync ?? throw new ArgumentNullException(nameof(showPlaybackErrorAsync));

        _frameList.SelectedFrameIndexChanged += OnSelectedFrameIndexChanged;
        _frameList.PlaybackStopped += OnPlaybackStopped;
        _frameList.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    internal bool IsInitialized
    {
        get
        {
            lock (_stateGate)
                return _initialized;
        }
    }

    internal void Initialize(
        IProgress<int>? loadProgress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsInitialized)
            return;

        var session = new VideoSession(
            _frameList.VideoPath,
            progress: loadProgress,
            cancellationToken: cancellationToken);
        AdoptSession(session);
    }

    internal async Task EnsureInitializedAsync(
        IProgress<int>? loadProgress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsInitialized || !_tryBeginLifetimeOperation())
            return;

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _sessionInitCts, sessionCts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            if (IsInitialized)
                return;

            VideoSession session = await Task.Run(
                () => new VideoSession(
                    _frameList.VideoPath,
                    progress: loadProgress,
                    cancellationToken: sessionCts.Token),
                sessionCts.Token);

            if (sessionCts.IsCancellationRequested ||
                !ReferenceEquals(Volatile.Read(ref _sessionInitCts), sessionCts))
            {
                session.Dispose();
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            lock (_stateGate)
            {
                if (_disposed || _initialized)
                {
                    session.Dispose();
                    return;
                }
            }

            AdoptSession(session);
            if (_frameList.SelectedFrameIndex >= 0)
                _framePreview.OnFrameIndexChanged(_frameList.SelectedFrameIndex);
        }
        catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _sessionInitCts, null, sessionCts);
            sessionCts.Dispose();
            _endLifetimeOperation();
        }
    }

    internal void ScheduleInitialPreview()
    {
        if (!IsInitialized || _frameList.SelectedFrameIndex < 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !IsInitialized || _frameList.SelectedFrameIndex < 0)
                return;
            _framePreview.OnFrameIndexChanged(_frameList.SelectedFrameIndex);
        });
    }

    internal void CancelInitialization()
    {
        CancellationTokenSource? cts = Volatile.Read(ref _sessionInitCts);
        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void AdoptSession(VideoSession session)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(WorkspaceSessionPlaybackCoordinator));
            }
            if (_initialized)
            {
                session.Dispose();
                return;
            }
        }

        _framePreview.InitializeSession(session);
        _frameList.SetThumbnailProvider(session.ThumbnailProvider);
        lock (_stateGate)
            _initialized = true;
    }

    private void OnSelectedFrameIndexChanged(int frameIndex)
    {
        if (_frameList.IsPlaying)
            return;

        if (_isAutoRunning() && _mode == WorkspaceMode.Auto)
        {
            _markAutoPreviewNeedsExactRefresh();
            return;
        }

        _framePreview.OnFrameIndexChanged(frameIndex);
    }

    private void OnPlaybackStopped()
    {
        if (_frameList.SelectedFrameIndex >= 0)
            _framePreview.OnPlaybackStopped(_frameList.SelectedFrameIndex);
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        if (!isPlaying)
        {
            _framePreview.StopPlayback();
            return;
        }

        int playbackTotalFrames = _frameList.IsTotalFramesEstimated
            ? 0
            : _frameList.TotalFrames;
        bool playbackDecodedFrame = false;
        _framePreview.StartPlayback(
            _frameList.VideoPath,
            _frameList.SelectedFrameIndex,
            _frameList.Fps,
            playbackTotalFrames,
            frameIndex =>
            {
                playbackDecodedFrame = true;
                _frameList.SetPlaybackFrameIndex(frameIndex);
            },
            () =>
            {
                if (_frameList.IsTotalFramesEstimated &&
                    playbackDecodedFrame &&
                    _frameList.SelectedFrameIndex >= 0)
                {
                    _frameList.UpdateActualTotalFrames(
                        _frameList.SelectedFrameIndex + 1);
                }

                _frameList.NotifyPlaybackStopped();
            },
            message =>
            {
                _frameList.NotifyPlaybackStopped();
                _ = _showPlaybackErrorAsync(message);
            });
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WorkspaceSessionPlaybackCoordinator));
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        CancelInitialization();
        _frameList.SelectedFrameIndexChanged -= OnSelectedFrameIndexChanged;
        _frameList.PlaybackStopped -= OnPlaybackStopped;
        _frameList.PlaybackStateChanged -= OnPlaybackStateChanged;
    }
}
