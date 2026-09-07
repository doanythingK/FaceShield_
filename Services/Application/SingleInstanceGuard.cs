using System;
using System.Threading;

namespace FaceShield.Services.Application;

internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "FaceShield_SingleInstance_8D52D4C9EAB34AF0";
    private static int _processLeaseHeld;

    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    internal static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        guard = null;
        if (Interlocked.CompareExchange(ref _processLeaseHeld, 1, 0) != 0)
            return false;

        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(
                initiallyOwned: true,
                name: MutexName,
                createdNew: out bool createdNew);
            ownsMutex = createdNew;

            if (!ownsMutex)
            {
                try
                {
                    ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // The previous process died while owning the mutex. WaitOne grants
                    // ownership to this process in the abandoned-mutex case.
                    ownsMutex = true;
                }
            }

            if (!ownsMutex)
            {
                mutex.Dispose();
                mutex = null;
                Interlocked.Exchange(ref _processLeaseHeld, 0);
                return false;
            }

            guard = new SingleInstanceGuard(mutex);
            mutex = null;
            return true;
        }
        catch
        {
            if (ownsMutex && mutex != null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Ownership was already lost; disposal below is sufficient.
                }
            }

            mutex?.Dispose();
            Interlocked.Exchange(ref _processLeaseHeld, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // If ownership was lost during shutdown, disposing the handle is enough.
            }
        }
        finally
        {
            _mutex.Dispose();
            Interlocked.Exchange(ref _processLeaseHeld, 0);
        }
    }
}
