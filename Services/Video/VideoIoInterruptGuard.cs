using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video;

internal unsafe sealed class VideoIoInterruptGuard : IDisposable
{
    private static readonly AVIOInterruptCB_callback Callback = HandleInterrupt;

    private GCHandle _handle;
    private int _interruptRequested;
    private int _disposed;

    internal VideoIoInterruptGuard()
    {
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    internal void Configure(AVFormatContext* format)
    {
        if (format == null || !_handle.IsAllocated)
            return;

        format->interrupt_callback.callback = Callback;
        format->interrupt_callback.opaque =
            (void*)GCHandle.ToIntPtr(_handle);
    }

    internal InterruptScope Begin(CancellationToken cancellationToken)
        => new(this, cancellationToken);

    internal readonly struct InterruptScope : IDisposable
    {
        private readonly VideoIoInterruptGuard _owner;
        private readonly CancellationTokenRegistration _registration;

        internal InterruptScope(
            VideoIoInterruptGuard owner,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            Volatile.Write(ref owner._interruptRequested, 0);
            _registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(
                    static state =>
                    {
                        if (state is VideoIoInterruptGuard guard)
                            Volatile.Write(ref guard._interruptRequested, 1);
                    },
                    owner)
                : default;

            if (cancellationToken.IsCancellationRequested)
                Volatile.Write(ref owner._interruptRequested, 1);
        }

        public void Dispose()
        {
            _registration.Dispose();
            if (Volatile.Read(ref _owner._disposed) == 0)
                Volatile.Write(ref _owner._interruptRequested, 0);
        }
    }

    private static int HandleInterrupt(void* opaque)
    {
        if (opaque == null)
            return 0;

        try
        {
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)opaque);
            if (handle.Target is not VideoIoInterruptGuard guard)
                return 1;

            return Volatile.Read(ref guard._interruptRequested) != 0 ||
                   Volatile.Read(ref guard._disposed) != 0
                ? 1
                : 0;
        }
        catch
        {
            return 1;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _interruptRequested, 1);
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
