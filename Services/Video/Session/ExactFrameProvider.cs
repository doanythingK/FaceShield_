// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider : IDisposable
{
    private readonly FfFrameExtractor _extractor;
    private readonly bool _ownsExtractor;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _operationStateSync = new();
    private CancellationTokenSource _operationCts = new();
    private int _operationsSuspended;
    private int _disposeStarted;
    private bool _disposed;

    public ExactFrameProvider(FfFrameExtractor extractor, bool ownsExtractor = true)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _ownsExtractor = ownsExtractor;
    }

    public async Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        CancellationTokenSource linked;
        lock (_operationStateSync)
        {
            if (_disposed ||
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _operationsSuspended) != 0)
            {
                return null;
            }

            // Admission includes linked-token registration. Dispose uses the same
            // lock before cancelling and eventually disposing these sources, so an
            // admitted request can never register against an already-disposed source.
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                _lifetimeCts.Token,
                _operationCts.Token);
        }

        using var linkedScope = linked;
        CancellationToken token = linked.Token;

        try
        {
            await _decodeGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            if (_disposed ||
                Volatile.Read(ref _operationsSuspended) != 0 ||
                token.IsCancellationRequested)
            {
                return null;
            }

            var frame = await Task.Run(
                () => _extractor.GetFrameByIndex(frameIndex, token),
                token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                frame?.Dispose();
                return null;
            }

            return frame;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public async Task SuspendOperationsAndWaitAsync()
    {
        CancellationTokenSource previous;
        lock (_operationStateSync)
        {
            if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
                return;

            Volatile.Write(ref _operationsSuspended, 1);
            previous = _operationCts;
            _operationCts = new CancellationTokenSource();
        }

        try
        {
            previous.Cancel();
            await _decodeGate.WaitAsync().ConfigureAwait(false);
            _decodeGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            previous.Dispose();
        }
    }

    public void ResumeOperations()
    {
        if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
            return;

        Volatile.Write(ref _operationsSuspended, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        lock (_operationStateSync)
        {
            _disposed = true;
            Volatile.Write(ref _operationsSuspended, 1);
            _lifetimeCts.Cancel();
            _operationCts.Cancel();
        }

        _decodeGate.Wait();
        try
        {
            if (_ownsExtractor)
                _extractor.Dispose();
        }
        finally
        {
            _decodeGate.Release();
            _decodeGate.Dispose();
            lock (_operationStateSync)
            {
                _operationCts.Dispose();
                _lifetimeCts.Dispose();
            }
        }
    }
}
