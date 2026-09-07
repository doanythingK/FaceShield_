using FaceShield.Services.Video;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Serializes workspace persistence and keeps bitmap pixel work off the caller
    /// thread. A queued request is published before its worker starts so FlushAsync
    /// and Dispose always observe a complete request/task pair.
    /// </summary>
    public sealed class WorkspacePersistenceCoordinator : IDisposable
    {
        private readonly WorkspaceStateStore _store;
        private readonly FrameMaskProvider _maskProvider;
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private readonly object _taskGate = new();
        private Task _latestTask = Task.CompletedTask;
        private long _latestRequestId;
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

            ThrowIfDisposed();
            FrameMaskProvider.PersistenceSnapshot maskSnapshot =
                _maskProvider.CreatePersistenceSnapshot();

            PendingSave pending;
            lock (_taskGate)
            {
                if (_disposed)
                {
                    maskSnapshot.Dispose();
                    throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));
                }

                pending = new PendingSave(
                    ++_latestRequestId,
                    snapshot,
                    maskSnapshot);

                // Publish before starting the worker. This makes the request visible
                // to FlushAsync/Dispose as one atomic (id, task) pair.
                _latestTask = pending.Completion.Task;
            }

            _ = ExecutePendingSaveAsync(pending);
            return pending.Completion.Task;
        }

        private async Task ExecutePendingSaveAsync(PendingSave pending)
        {
            bool entered = false;
            Exception? failure = null;
            try
            {
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
        /// Used during application shutdown when persistence must finish before
        /// workspace resources are disposed and the process exits.
        /// </summary>
        public void SaveNow(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            QueueSaveAsync(snapshot).GetAwaiter().GetResult();
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
