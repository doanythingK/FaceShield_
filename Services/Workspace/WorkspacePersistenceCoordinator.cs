using FaceShield.Services.Video;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Serializes workspace persistence and moves bitmap cloning / PNG encoding / file I/O
    /// off the UI thread. Queued stale requests are skipped before expensive work starts.
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

            long requestId = Interlocked.Increment(ref _latestRequestId);
            Task task = SaveQueuedAsync(snapshot, requestId);
            lock (_taskGate)
                _latestTask = task;
            return task;
        }

        private async Task SaveQueuedAsync(
            WorkspaceSnapshot snapshot,
            long requestId)
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (requestId != Volatile.Read(ref _latestRequestId))
                    return;

                await Task.Run(() => _store.SaveWorkspace(snapshot, _maskProvider))
                    .ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
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
            ThrowIfDisposed();

            try
            {
                FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // A fresh synchronous save below supersedes a failed queued save.
            }

            _saveGate.Wait();
            try
            {
                _store.SaveWorkspace(snapshot, _maskProvider);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Persistence errors are observed by the owning view model.
            }

            _disposed = true;
            _saveGate.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));
        }
    }
}
