using Avalonia.Media.Imaging;
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

    internal void InvalidateSequentialPosition()
    {
        using OperationLease operation = EnterOperation();
        _decodeGate.Wait();
        try
        {
            // A decoded frame is not considered committed until the UI accepts it.
            // If the UI cancels before that point, force the next read to seek from
            // the caller's visible frame index instead of continuing past it.
            _hasSequentialPosition = false;
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
