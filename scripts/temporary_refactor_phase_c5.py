from pathlib import Path


def read_exact(path: str) -> str:
    with Path(path).open('r', encoding='utf-8', newline='') as f:
        return f.read()


def write_exact(path: str, text: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('w', encoding='utf-8', newline='') as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read_exact(path)
    candidates = [(old, new)]
    if '\n' in old:
        candidates.append((old.replace('\n', '\r\n'), new.replace('\n', '\r\n')))
    for before, after in candidates:
        count = text.count(before)
        if count == 1:
            write_exact(path, text.replace(before, after, 1))
            return
        if count > 1:
            raise RuntimeError(f'Expected one match in {path}, found {count}: {old[:120]!r}')
    raise RuntimeError(f'Patch target not found in {path}: {old[:160]!r}')


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    text = read_exact(path)
    sep = '\r\n' if '\r\n' in text else '\n'
    start_key = start_marker.replace('\n', sep)
    end_key = end_marker.replace('\n', sep)
    start = text.find(start_key)
    if start < 0:
        raise RuntimeError(f'Start marker not found in {path}: {start_marker!r}')
    end = text.find(end_key, start)
    if end < 0:
        raise RuntimeError(f'End marker not found in {path}: {end_marker!r}')
    write_exact(path, text[:start] + replacement.replace('\n', sep) + text[end:])


write_exact('ViewModels/Workspace/WorkspaceSessionPlaybackCoordinator.cs', '''using Avalonia.Threading;
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
''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''using FaceShield.Services.Video.Session;\n''',
    '')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private readonly WorkspaceExportCoordinator _exportCoordinator;\n        private readonly AutoMaskRunCoordinator _autoRunCoordinator;\n        private bool _sessionInitialized;\n        // 프레임별 최종 마스크 저장소\n        private readonly FrameMaskProvider _maskProvider = new();\n\n        private CancellationTokenSource? _sessionInitCts;\n''',
    '''        private readonly WorkspaceExportCoordinator _exportCoordinator;\n        private readonly AutoMaskRunCoordinator _autoRunCoordinator;\n        private readonly WorkspaceSessionPlaybackCoordinator _sessionPlaybackCoordinator;\n        // 프레임별 최종 마스크 저장소\n        private readonly FrameMaskProvider _maskProvider = new();\n\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                EndLifetimeOperation,\n                PersistWorkspaceState);\n            if (!deferSessionInit)\n                InitializeSession(loadProgress, initializationToken);\n\n            // 🔹 자동/최종 마스크 provider 주입\n''',
    '''                EndLifetimeOperation,\n                PersistWorkspaceState);\n            _sessionPlaybackCoordinator = new WorkspaceSessionPlaybackCoordinator(\n                Mode,\n                FrameList,\n                FramePreview,\n                () => _autoRunCoordinator.IsRunning,\n                _autoRunCoordinator.MarkPreviewNeedsExactRefresh,\n                TryBeginLifetimeOperation,\n                EndLifetimeOperation,\n                message => ShowErrorDialogAsync("재생 실패", message));\n            if (!deferSessionInit)\n                _sessionPlaybackCoordinator.Initialize(loadProgress, initializationToken);\n\n            // 🔹 자동/최종 마스크 provider 주입\n''')

replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            FrameList.SelectedFrameIndexChanged += index =>\n''',
    '''            ToolPanel.UndoRequested += () => FramePreview.Undo();\n''',
    '')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            if (_sessionInitialized && FrameList.SelectedFrameIndex >= 0)\n            {\n                Dispatcher.UIThread.Post(() =>\n                {\n                    if (_sessionInitialized && FrameList.SelectedFrameIndex >= 0)\n                        FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);\n                });\n            }\n''',
    '''            _sessionPlaybackCoordinator.ScheduleInitialPreview();\n''')

replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        public async Task EnsureSessionInitializedAsync(\n''',
    '''        private Task<bool> SaveVideoAsync(\n''',
    '''        public Task EnsureSessionInitializedAsync(\n            IProgress<int>? loadProgress,\n            CancellationToken cancellationToken = default)\n            => _sessionPlaybackCoordinator.EnsureInitializedAsync(\n                loadProgress,\n                cancellationToken);\n\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private void DisposeOwnedResources()\n        {\n            _autoRunCoordinator.Dispose();\n''',
    '''        private void DisposeOwnedResources()\n        {\n            _sessionPlaybackCoordinator.Dispose();\n            _autoRunCoordinator.Dispose();\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            _autoRunCoordinator.Cancel();\n            _exportCoordinator.Cancel();\n            try { _sessionInitCts?.Cancel(); }\n            catch { }\n            CancelIssueTimeRefresh();\n''',
    '''            _autoRunCoordinator.Cancel();\n            _exportCoordinator.Cancel();\n            _sessionPlaybackCoordinator.CancelInitialization();\n            CancelIssueTimeRefresh();\n''')

print('Phase C5 session/playback coordinator extraction applied.')
