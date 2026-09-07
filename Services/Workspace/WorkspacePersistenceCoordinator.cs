using FaceShield.Services.Video;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Serializes workspace persistence and keeps bitmap pixel work off the caller
    /// thread. Published requests form an ordered completion chain so FlushAsync,
    /// SaveNow, and Dispose observe all prior persistence work.
    /// </summary>
    public sealed class WorkspacePersistenceCoordinator : IDisposable
    {
        private readonly WorkspaceStateStore _store;
        private readonly FrameMaskProvider _maskProvider;
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private readonly object _taskGate = new();
        private Task _latestTask = Task.CompletedTask;
        private long _latestRequestId;
        private bool _finalizing;
        private bool _disposed;

        public WorkspacePersistenceCoordinator(
            WorkspaceStateStore store,
            FrameMaskProvider maskProvider)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
        }

        public Task QueueSaveAsync(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return Task.CompletedTask;

            ThrowIfQueueClosed();
            FrameMaskProvider.PersistenceSnapshot maskSnapshot =
                _maskProvider.CreatePersistenceSnapshot();

            PendingSave pending;
            Task predecessor;
            lock (_taskGate)
            {
                if (_disposed || _finalizing)
                {
                    maskSnapshot.Dispose();
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));

                    throw new InvalidOperationException(
                        "Workspace persistence is finalizing and no longer accepts queued saves.");
                }

                predecessor = _latestTask;
                pending = new PendingSave(
                    ++_latestRequestId,
                    snapshot,
                    maskSnapshot);

                // Publish the completion tail before starting this worker. The worker
                // also awaits its predecessor, so the tail drains every prior request.
                _latestTask = pending.Completion.Task;
            }

            _ = ExecutePendingSaveAsync(pending, predecessor);
            return pending.Completion.Task;
        }

        private async Task ExecutePendingSaveAsync(
            PendingSave pending,
            Task predecessor)
        {
            bool entered = false;
            Exception? failure = null;
            try
            {
                try
                {
                    await predecessor.ConfigureAwait(false);
                }
                catch
                {
                    // A newer request is still allowed to persist after an older
                    // request failed; callers awaiting the older task observe its error.
                }

                await _saveGate.WaitAsync().ConfigureAwait(false);
                entered = true;

                bool stale;
                lock (_taskGate)
                    stale = pending.RequestId != _latestRequestId;

                if (!stale)
                {
                    await Task.Run(() => _store.SaveWorkspaceSnapshot(
                        pending.Snapshot,
                        pending.MaskSnapshot)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    if (entered)
                        _saveGate.Release();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                try
                {
                    pending.MaskSnapshot.Dispose();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                if (failure == null)
                    pending.Completion.TrySetResult(null);
                else
                    pending.Completion.TrySetException(failure);
            }
        }

        public async Task FlushAsync()
        {
            ThrowIfDisposed();
            while (true)
            {
                Task task;
                long requestId;
                lock (_taskGate)
                {
                    task = _latestTask;
                    requestId = _latestRequestId;
                }

                await task.ConfigureAwait(false);

                lock (_taskGate)
                {
                    if (requestId == _latestRequestId &&
                        ReferenceEquals(task, _latestTask))
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Performs the terminal application-shutdown save. Once finalization starts,
        /// new queued saves are rejected. The returned boundary drains all previously
        /// published work and commits this final snapshot before returning.
        /// </summary>
        public void SaveNow(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            PendingSave? pending = null;
            Task predecessor = Task.CompletedTask;
            Task finalTask;

            lock (_taskGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));

                if (_finalizing)
                {
                    finalTask = _latestTask;
                }
                else
                {
                    // Capture the persistence lease while publication is blocked so a
                    // concurrent QueueSaveAsync cannot slip in after the final boundary.
                    FrameMaskProvider.PersistenceSnapshot maskSnapshot =
                        _maskProvider.CreatePersistenceSnapshot();

                    _finalizing = true;
                    predecessor = _latestTask;
                    pending = new PendingSave(
                        ++_latestRequestId,
                        snapshot,
                        maskSnapshot);
                    _latestTask = pending.Completion.Task;
                    finalTask = _latestTask;
                }
            }

            if (pending != null)
                _ = ExecutePendingSaveAsync(pending, predecessor);

            finalTask.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Task latestTask;
            lock (_taskGate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                latestTask = _latestTask;
            }

            try
            {
                latestTask.GetAwaiter().GetResult();
            }
            catch
            {
                // Persistence errors are observed by the owning view model.
            }

            _saveGate.Dispose();
        }

        private void ThrowIfQueueClosed()
        {
            lock (_taskGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));
                if (_finalizing)
                {
                    throw new InvalidOperationException(
                        "Workspace persistence is finalizing and no longer accepts queued saves.");
                }
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_taskGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));
            }
        }

        private sealed class PendingSave
        {
            internal PendingSave(
                long requestId,
                WorkspaceSnapshot snapshot,
                FrameMaskProvider.PersistenceSnapshot maskSnapshot)
            {
                RequestId = requestId;
                Snapshot = snapshot;
                MaskSnapshot = maskSnapshot;
            }

            internal long RequestId { get; }
            internal WorkspaceSnapshot Snapshot { get; }
            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }
            internal TaskCompletionSource<object?> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
