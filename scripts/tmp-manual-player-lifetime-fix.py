from pathlib import Path

ROOT = Path('.')
_newlines = {}


def read(path):
    data = (ROOT / path).read_bytes()
    _newlines[path] = '\r\n' if b'\r\n' in data else '\n'
    return data.decode('utf-8').replace('\r\n', '\n')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    newline = _newlines.get(path, '\n')
    if newline == '\r\n':
        text = text.replace('\n', '\r\n')
    p.write_bytes(text.encode('utf-8'))


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)


# 1) ManualFramePlayer owns operation lifetime. Admitted waiters/runners are
# counted before touching the decode gate, so the gate cannot be disposed while
# any operation can still Release() or wake from Wait().
path = 'Services/Video/Session/ManualFramePlayer.cs'
read(path)  # preserve generated newline style
write(path, '''using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

internal readonly record struct ManualFrameIdentity(
    int FrameOrdinal,
    long? PresentationTimestamp,
    int TimestampOccurrence,
    double TimelineSeconds,
    string TimelineSource)
{
    internal bool HasPresentationTimestamp =>
        PresentationTimestamp.HasValue && TimestampOccurrence > 0;
}

internal sealed class ManualPlayerFrame
{
    internal ManualPlayerFrame(
        WriteableBitmap bitmap,
        ManualFrameIdentity identity)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Identity = identity;
    }

    internal WriteableBitmap Bitmap { get; }
    internal ManualFrameIdentity Identity { get; }
}

internal sealed class ManualFramePlayer : IDisposable, IAsyncDisposable
{
    private sealed class OperationLease : IDisposable
    {
        private ManualFramePlayer? _owner;

        internal OperationLease(ManualFramePlayer owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitOperation();
        }
    }

    private readonly FfFrameExtractor _extractor;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly object _lifetimeSync = new();
    private TaskCompletionSource<bool>? _drainedTcs;
    private int _activeOperations;
    private bool _closing;
    private int _resourcesDisposed;
    private bool _hasSequentialPosition;
    private int _nextFrameIndex;

    internal ManualFramePlayer(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        _extractor = new FfFrameExtractor(
            videoPath,
            enableHardware: true,
            cancellationToken: cancellationToken);
    }

    internal bool ReachedEndOfStream => _extractor.SequentialReachedEndOfStream;
    internal bool ReadCancelled => _extractor.SequentialReadCancelled;
    internal string? DecodeError => _extractor.SequentialDecodeError;

    internal ManualPlayerFrame? LoadFrame(
        int frameIndex,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        int requested = Math.Max(0, frameIndex);
        _decodeGate.Wait(cancellationToken);
        try
        {
            if (!_hasSequentialPosition || requested != _nextFrameIndex)
            {
                _extractor.StartSequentialRead(requested, cancellationToken);
                _hasSequentialPosition = true;
                _nextFrameIndex = requested;
            }

            return ReadNextLocked(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _hasSequentialPosition = false;
            throw;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    internal ManualPlayerFrame? ReadNext(
        int fallbackStartFrameIndex,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        _decodeGate.Wait(cancellationToken);
        try
        {
            if (!_hasSequentialPosition)
            {
                int start = Math.Max(0, fallbackStartFrameIndex);
                _extractor.StartSequentialRead(start, cancellationToken);
                _hasSequentialPosition = true;
                _nextFrameIndex = start;
            }

            return ReadNextLocked(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _hasSequentialPosition = false;
            throw;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private ManualPlayerFrame? ReadNextLocked(
        CancellationToken cancellationToken)
    {
        if (!_extractor.TryGetNextFrame(
                cancellationToken,
                out WriteableBitmap? bitmap,
                out int frameIndex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _hasSequentialPosition = false;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_extractor.SequentialReadCancelled)
                _hasSequentialPosition = false;
            return null;
        }

        if (bitmap == null)
            return null;

        long? presentationTimestamp = null;
        int timestampOccurrence = 0;
        if (_extractor.TryGetLastDecodedPresentationIdentity(
                out long rawTimestamp,
                out int occurrence))
        {
            presentationTimestamp = rawTimestamp;
            timestampOccurrence = occurrence;
        }

        _nextFrameIndex = frameIndex == int.MaxValue
            ? int.MaxValue
            : frameIndex + 1;
        _hasSequentialPosition = true;

        return new ManualPlayerFrame(
            bitmap,
            new ManualFrameIdentity(
                frameIndex,
                presentationTimestamp,
                timestampOccurrence,
                _extractor.LastDecodedTimestampSeconds,
                _extractor.LastDecodedTimestampSource));
    }

    private OperationLease EnterOperation()
    {
        lock (_lifetimeSync)
        {
            if (_closing || Volatile.Read(ref _resourcesDisposed) != 0)
                throw new ObjectDisposedException(nameof(ManualFramePlayer));

            checked { _activeOperations++; }
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_lifetimeSync)
        {
            if (_activeOperations <= 0)
                throw new InvalidOperationException("Manual player operation lifetime underflow.");

            _activeOperations--;
            if (_closing && _activeOperations == 0)
                drained = _drainedTcs;
        }

        drained?.TrySetResult(true);
    }

    private Task BeginClose()
    {
        lock (_lifetimeSync)
        {
            if (Volatile.Read(ref _resourcesDisposed) != 0)
                return Task.CompletedTask;

            _closing = true;
            if (_activeOperations == 0)
                return Task.CompletedTask;

            _drainedTcs ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _drainedTcs.Task;
        }
    }

    internal async ValueTask StopAndWaitAsync()
    {
        Task drainTask = BeginClose();
        await drainTask.ConfigureAwait(false);
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        _extractor.Dispose();
        _decodeGate.Dispose();
    }

    public void Dispose()
    {
        BeginClose().GetAwaiter().GetResult();
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAndWaitAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
''')

# 2) VideoSession exposes an async disposal path while keeping a safe synchronous
# fallback for legacy callers. Manual player resources are drained before the
# rest of the session is released.
path = 'Services/Video/Session/VideoSession.cs'
text = read(path)
text = replace_once(
    text,
    'using System.Threading;\n',
    'using System.Threading;\nusing System.Threading.Tasks;\n',
    'video session async using')
text = replace_once(
    text,
    'public sealed class VideoSession : IDisposable\n',
    'public sealed class VideoSession : IDisposable, IAsyncDisposable\n',
    'video session async disposable')
text = replace_once(
    text,
    '    private bool _disposed;\n',
    '    private int _disposeState;\n',
    'video session dispose state')
old_dispose = '''    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ManualPlayer?.Dispose();
        Timeline.Dispose();
        ExactProvider.Dispose();
        ThumbnailProvider.Dispose();
        _extractor.Dispose();
    }
'''
new_dispose = '''    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        try
        {
            ManualPlayer?.Dispose();
        }
        finally
        {
            DisposeSessionResources();
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        try
        {
            if (ManualPlayer != null)
                await ManualPlayer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeSessionResources();
        }

        GC.SuppressFinalize(this);
    }

    private void DisposeSessionResources()
    {
        Timeline.Dispose();
        ExactProvider.Dispose();
        ThumbnailProvider.Dispose();
        _extractor.Dispose();
    }
'''
text = replace_once(text, old_dispose, new_dispose, 'video session safe dispose')
write(path, text)

# 3) Track the actual manual frame load task. New direct loads wait for the
# previous load to observe cancellation instead of overlapping it indefinitely.
path = 'ViewModels/Workspace/FramePreviewViewModel.cs'
text = read(path)
text = replace_once(
    text,
    '''    private CancellationTokenSource? _manualFrameLoadCts;
    private int _manualFrameLoadGeneration;
''',
    '''    private CancellationTokenSource? _manualFrameLoadCts;
    private Task? _manualFrameLoadTask;
    private int _manualFrameLoadGeneration;
''',
    'manual load task field')
text = replace_once(
    text,
    '''        int generation = Interlocked.Increment(ref _manualFrameLoadGeneration);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _manualFrameLoadCts, requestCts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        IsFrameLoading = true;
''',
    '''        await CancelManualFrameLoadAndWaitAsync();

        int generation = Interlocked.Increment(ref _manualFrameLoadGeneration);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Interlocked.Exchange(ref _manualFrameLoadCts, requestCts);

        IsFrameLoading = true;
''',
    'manual load awaits previous')
text = replace_once(
    text,
    '''            ManualPlayerFrame? loaded = await Task.Run(
                () => player.LoadFrame(index, requestCts.Token),
                requestCts.Token);
''',
    '''            Task<ManualPlayerFrame?> loadTask = Task.Run(
                () => player.LoadFrame(index, requestCts.Token),
                requestCts.Token);
            Volatile.Write(ref _manualFrameLoadTask, loadTask);
            ManualPlayerFrame? loaded = await loadTask;
''',
    'manual load task tracking')
text = replace_once(
    text,
    '''            requestCts.Dispose();
        }
    }

    private void CancelManualFrameLoad()
''',
    '''            Task? currentLoadTask = Volatile.Read(ref _manualFrameLoadTask);
            if (currentLoadTask?.IsCompleted == true)
            {
                Interlocked.CompareExchange(
                    ref _manualFrameLoadTask,
                    null,
                    currentLoadTask);
            }
            requestCts.Dispose();
        }
    }

    private async Task CancelManualFrameLoadAndWaitAsync()
    {
        Interlocked.Increment(ref _manualFrameLoadGeneration);
        CancellationTokenSource? cts =
            Interlocked.Exchange(ref _manualFrameLoadCts, null);
        Task? loadTask = Volatile.Read(ref _manualFrameLoadTask);

        if (cts != null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

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
            Interlocked.CompareExchange(ref _manualFrameLoadTask, null, loadTask);
        }
    }

    internal async Task StopManualOperationsAndWaitAsync()
    {
        await StopPlaybackAndWaitAsync().ConfigureAwait(false);
        await CancelManualFrameLoadAndWaitAsync().ConfigureAwait(false);
    }

    private void CancelManualFrameLoad()
''',
    'manual load async shutdown')
write(path, text)

# 4) Back navigation is already asynchronous: use it to drain playback/load
# before the workspace hands control back and starts resource disposal.
path = 'ViewModels/Pages/WorkspaceViewModel.cs'
text = read(path)
text = replace_once(
    text,
    '''            _onBack?.Invoke();
        }
''',
    '''            await FramePreview.StopManualOperationsAndWaitAsync();
            _onBack?.Invoke();
        }
''',
    'workspace back drains manual operations')
write(path, text)

# 5) Record the lifetime invariant in the architecture note.
path = 'MANUAL_BLUR_PLAYER_ARCHITECTURE.md'
text = read(path)
anchor = '''- 남은 단계: byte-budget ring buffer, far-seek pending playhead 세분화, background prefetch priority, export identity 검증
'''
replacement = anchor + '''- lifetime invariant:
  - 새 manual decode 작업은 closing 이후 진입 불가
  - admitted load/playback decode operation이 모두 종료된 뒤에만 extractor와 decode gate를 Dispose
  - 일반 뒤로가기 경로는 playback/load cancellation 후 비동기 종료 대기
  - 동기 Dispose는 legacy/app-shutdown fallback이며 동일 drain invariant를 지킴
'''
text = replace_once(text, anchor, replacement, 'manual architecture lifetime invariant')
write(path, text)

print('manual player lifetime hardening applied')
