using System;
using System.Threading;

namespace FaceShield.ViewModels.Workspace;

/// <summary>
/// Publishes a manual-tracking cancellation source before its workspace lifetime
/// is admitted. Uses the same gate as Cancel and manual-track commit: a close
/// can never observe an admitted, uncancellable tracking operation.
/// </summary>
internal static class ManualTrackingCancellationGate
{
    internal static bool TryBegin(
        object gate,
        ref CancellationTokenSource? active,
        CancellationTokenSource source,
        Func<bool>? tryBeginLifetime)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(source);

        lock (gate)
        {
            if (active != null)
                return false;

            Volatile.Write(ref active, source);
            bool admitted = false;
            try
            {
                // WorkspaceOperationLifetime releases its lock before invoking
                // AdmissionClosed. Its cancel subscriber can therefore wait for
                // this gate without reversing the lock order.
                admitted = tryBeginLifetime?.Invoke() == true;
                return admitted;
            }
            finally
            {
                if (!admitted && ReferenceEquals(active, source))
                    Volatile.Write(ref active, null);
            }
        }
    }

    internal static void Finish(
        object gate,
        ref CancellationTokenSource? active,
        CancellationTokenSource source)
    {
        lock (gate)
        {
            if (ReferenceEquals(active, source))
                Volatile.Write(ref active, null);
            // Coordinate disposal with Cancel to avoid Cancel after Dispose.
            source.Dispose();
        }
    }
}
