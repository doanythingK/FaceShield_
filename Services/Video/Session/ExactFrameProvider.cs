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
        if (_disposed || Volatile.Read(ref _operationsSuspended) != 0)
            return null;

        CancellationToken operationToken;
        lock (_operationStateSync)
            operationToken = _operationCts.Token;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _lifetimeCts.Token,
            operationToken);
        CancellationToken token = linked.Token;

        try
        {
            await _decodeGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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
        catch (OperationCanceledException)
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
            if (_disposed)
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
        if (_disposed)
            return;

        Volatile.Write(ref _operationsSuspended, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        lock (_operationStateSync)
        {
            Volatile.Write(ref _operationsSuspended, 1);
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
            _operationCts.Dispose();
            _lifetimeCts.Dispose();
        }
    }
}
