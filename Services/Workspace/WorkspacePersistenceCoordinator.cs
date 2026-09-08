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
            WorkspaceStateStore.SaveLease saveLease =
                _store.AcquireWorkspaceSaveLease(snapshot.VideoPath);
            FrameMaskProvider.PersistenceSnapshot maskSnapshot;
            try
            {
                maskSnapshot = _maskProvider.CreatePersistenceSnapshot();
            }
            catch
            {
                saveLease.Dispose();
                throw;
            }

            PendingSave pending;
            Task predecessor;
            lock (_taskGate)
            {
                if (_disposed || _finalizing)
                {
                    maskSnapshot.Dispose();
                    saveLease.Dispose();
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(WorkspacePersistenceCoordinator));

                    throw new InvalidOperationException(
                        "Workspace persistence is finalizing and no longer accepts queued saves.");
                }

                predecessor = _latestTask;
                pending = new PendingSave(
                    ++_latestRequestId,
                    snapshot,
                    maskSnapshot,
                    saveLease);

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
                        pending.MaskSnapshot,
                        pending.SaveLease)).ConfigureAwait(false);
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

                try
                {
                    pending.SaveLease.Dispose();
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

            Task predecessor = Task.CompletedTask;
            Task finalTask;
            TaskCompletionSource<object?>? finalCompletion = null;
            long finalRequestId = 0;
            bool ownsFinalization = false;

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
                    // Publish a terminal placeholder before releasing the task gate.
                    // QueueSaveAsync observes _finalizing and cannot cross this boundary,
                    // while Dispose/another SaveNow can already wait on the placeholder.
                    _finalizing = true;
                    predecessor = _latestTask;
                    finalRequestId = ++_latestRequestId;
                    finalCompletion = new TaskCompletionSource<object?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _latestTask = finalCompletion.Task;
                    finalTask = _latestTask;
                    ownsFinalization = true;
                }
            }

            if (ownsFinalization)
            {
                WorkspaceStateStore.SaveLease? saveLease = null;
                FrameMaskProvider.PersistenceSnapshot? maskSnapshot = null;
                try
                {
                    // The save lease starts before snapshot capture and remains owned by
                    // PendingSave through commit, stale skip, or failure.
                    saveLease = _store.AcquireWorkspaceSaveLease(snapshot.VideoPath);
                    maskSnapshot = _maskProvider.CreatePersistenceSnapshot();
                    var pending = new PendingSave(
                        finalRequestId,
                        snapshot,
                        maskSnapshot,
                        saveLease,
                        finalCompletion!);
                    maskSnapshot = null;
                    saveLease = null;
                    _ = ExecutePendingSaveAsync(pending, predecessor);
                }
                catch (Exception ex)
                {
                    try
                    {
                        maskSnapshot?.Dispose();
                        saveLease?.Dispose();
                    }
                    catch
                    {
                        // Preserve the lease/snapshot-capture failure as the terminal error.
                    }

                    finalCompletion!.TrySetException(ex);
                }
            }

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
                FrameMaskProvider.PersistenceSnapshot maskSnapshot,
                WorkspaceStateStore.SaveLease saveLease,
                TaskCompletionSource<object?>? completion = null)
            {
                RequestId = requestId;
                Snapshot = snapshot;
                MaskSnapshot = maskSnapshot;
                SaveLease = saveLease;
                Completion = completion ?? new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal long RequestId { get; }
            internal WorkspaceSnapshot Snapshot { get; }
            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }
            internal WorkspaceStateStore.SaveLease SaveLease { get; }
            internal TaskCompletionSource<object?> Completion { get; }
        }
    }
}
