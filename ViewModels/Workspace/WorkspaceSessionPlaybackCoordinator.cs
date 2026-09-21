using Avalonia.Threading;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video.Session;
using System;
using System.Diagnostics;
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
    private VideoSession? _adoptedSession;
    private bool _adoptionInProgress;
    private bool _initialized;
    private bool _disposed;
    private bool _revertingFailedTargetNavigation;
    private bool _suppressStoppedAfterFailedTargetSave;

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

        _frameList.SetPlaybackEnabled(false);
        _framePreview.SetSessionReady(false);
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
            cancellationToken: cancellationToken,
            enableManualPlayer: _mode == WorkspaceMode.Manual);
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

        var startupClock = Stopwatch.StartNew();
        try
        {
            if (IsInitialized)
                return;

            VideoSession session = await Task.Run(
                () => new VideoSession(
                    _frameList.VideoPath,
                    progress: loadProgress,
                    cancellationToken: sessionCts.Token,
                    enableManualPlayer: _mode == WorkspaceMode.Manual),
                sessionCts.Token);

            Debug.WriteLine($"[WorkspaceStartup] session opened in {startupClock.ElapsedMilliseconds} ms (mode={_mode}).");
            if (sessionCts.IsCancellationRequested ||
                !ReferenceEquals(Volatile.Read(ref _sessionInitCts), sessionCts))
            {
                session.Dispose();
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            bool prioritizeManualFrame = _mode == WorkspaceMode.Manual;
            AdoptSession(session, deferTimeline: prioritizeManualFrame);
            if (!ReferenceEquals(_adoptedSession, session))
                return;

            try
            {
                if (_frameList.SelectedFrameIndex >= 0 && IsInitialized)
                {
                    if (prioritizeManualFrame)
                    {
                        await _framePreview.LoadManualFrameAsync(
                            _frameList.SelectedFrameIndex,
                            sessionCts.Token);
                    }
                    else
                    {
                        _framePreview.OnFrameIndexChanged(
                            _frameList.SelectedFrameIndex);
                    }
                }
            }
            finally
            {
                if (prioritizeManualFrame && !_disposed &&
                    ReferenceEquals(_adoptedSession, session))
                {
                    _frameList.SetThumbnailProvider(session.ThumbnailProvider);
                    _frameList.SetPlaybackEnabled(true);
                    _framePreview.SetSessionReady(true);
                    Debug.WriteLine($"[WorkspaceStartup] initial manual frame and session ready in {startupClock.ElapsedMilliseconds} ms.");
                }
            }
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

    private void AdoptSession(VideoSession session, bool deferTimeline = false)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));

        lock (_stateGate)
        {
            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(WorkspaceSessionPlaybackCoordinator));
            }

            if (_initialized || _adoptionInProgress)
            {
                session.Dispose();
                return;
            }

            _adoptionInProgress = true;
        }

        bool adopted = false;
        try
        {
            _framePreview.InitializeSession(
                session,
                useManualPlayer: _mode == WorkspaceMode.Manual);
            if (!deferTimeline)
                _frameList.SetThumbnailProvider(session.ThumbnailProvider);

            lock (_stateGate)
            {
                if (!_disposed)
                {
                    _adoptedSession = session;
                    _initialized = true;
                    adopted = true;
                }
            }

            if (!adopted)
            {
                _frameList.SetPlaybackEnabled(false);
                _framePreview.SetSessionReady(false);
                return;
            }

            if (!deferTimeline)
            {
                _frameList.SetPlaybackEnabled(true);
                _framePreview.SetSessionReady(true);
            }
        }
        finally
        {
            lock (_stateGate)
                _adoptionInProgress = false;
        }
    }

    /// <summary>
    /// Handles both GUI and programmatic FrameList selection/playback. A failed
    /// target save must not fall through to the legacy global mask writer.
    /// </summary>
    private bool TryCommitTargetBeforeTransition()
    {
        if (_mode != WorkspaceMode.Manual)
            return true;
        try
        {
            _framePreview.CommitPendingManualTargetEdit();
            _framePreview.PreserveManualTargetUndo();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManualTarget] Navigation blocked by target save failure: {ex}");
            _ = _showPlaybackErrorAsync(
                $"수동 얼굴 마스크 저장에 실패하여 프레임 이동을 취소했습니다: {ex.Message}");
            return false;
        }
    }

    private void OnSelectedFrameIndexChanged(int frameIndex)
    {
        if (_revertingFailedTargetNavigation || _frameList.IsPlaying)
            return;

        if (_isAutoRunning() && _mode == WorkspaceMode.Auto)
        {
            _markAutoPreviewNeedsExactRefresh();
            return;
        }

        if (!TryCommitTargetBeforeTransition())
        {
            int previousFrame = _framePreview.ManualTargetEditableFrameIndex;
            if (previousFrame >= 0 && previousFrame != frameIndex)
            {
                _revertingFailedTargetNavigation = true;
                try { _frameList.SelectedFrameIndex = previousFrame; }
                finally { _revertingFailedTargetNavigation = false; }
            }
            return;
        }

        _framePreview.OnFrameIndexChanged(frameIndex);
    }

    private void OnPlaybackStopped()
    {
        if (_suppressStoppedAfterFailedTargetSave)
            return;
        if (_frameList.SelectedFrameIndex >= 0)
            _framePreview.OnPlaybackStopped(_frameList.SelectedFrameIndex);
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        if (!isPlaying)
        {
            // NotifyPlaybackStopped is also used to revert a rejected play
            // request. The player never started, so StopPlayback would clear
            // this face's unsaved Undo stack and editable state needlessly.
            if (!_suppressStoppedAfterFailedTargetSave)
                _framePreview.StopPlayback();
            _framePreview.NotifyManualTrackingPlaybackStateChanged();
            return;
        }

        if (!IsInitialized)
        {
            _frameList.NotifyPlaybackStopped();
            return;
        }

        if (!TryCommitTargetBeforeTransition())
        {
            _suppressStoppedAfterFailedTargetSave = true;
            try { _frameList.NotifyPlaybackStopped(); }
            finally { _suppressStoppedAfterFailedTargetSave = false; }
            _framePreview.NotifyManualTrackingPlaybackStateChanged();
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
        _framePreview.NotifyManualTrackingPlaybackStateChanged();
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
            _adoptedSession = null;
        }

        CancelInitialization();
        _framePreview.DetachManualTrackingContext();
        _frameList.SetPlaybackEnabled(false);
        _framePreview.SetSessionReady(false);
        _frameList.SelectedFrameIndexChanged -= OnSelectedFrameIndexChanged;
        _frameList.PlaybackStopped -= OnPlaybackStopped;
        _frameList.PlaybackStateChanged -= OnPlaybackStateChanged;
    }
}
