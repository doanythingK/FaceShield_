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
        private readonly object _captureGate = new();
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

        // Compatibility overload for callers that already own an immutable snapshot.
        // WorkspaceViewModel uses the factory overload to include scalar capture in
        // the same ordering boundary as mask capture and request publication.
        public Task QueueSaveAsync(WorkspaceSnapshot snapshot)
            => snapshot == null ? Task.CompletedTask : QueueSaveAsync(() => snapshot);

        public Task QueueSaveAsync(Func<WorkspaceSnapshot> snapshotFactory)
        {
            if (snapshotFactory == null)
                throw new ArgumentNullException(nameof(snapshotFactory));

            lock (_captureGate)
            {
                ThrowIfQueueClosed();
                WorkspaceSnapshot snapshot = snapshotFactory()
                    ?? throw new InvalidOperationException("Workspace snapshot capture returned null.");
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
            if (snapshot != null)
                SaveNow(() => snapshot);
        }

        public void SaveNow(Func<WorkspaceSnapshot> snapshotFactory)
        {
            if (snapshotFactory == null)
                throw new ArgumentNullException(nameof(snapshotFactory));

            Task finalTask;
            // Capture both scalar and bitmap states under the same gate, including
            // the terminal request's publication. An earlier QueueSaveAsync cannot
            // slip its mask snapshot between those two captures.
            lock (_captureGate)
            {
                Task predecessor;
                TaskCompletionSource<object?>? finalCompletion = null;
                long finalRequestId = 0;
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
                        _finalizing = true;
                        predecessor = _latestTask;
                        finalRequestId = ++_latestRequestId;
                        finalCompletion = new TaskCompletionSource<object?>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _latestTask = finalCompletion.Task;
                        finalTask = _latestTask;
                    }
                }

                if (finalCompletion != null)
                {
                    WorkspaceStateStore.SaveLease? saveLease = null;
                    FrameMaskProvider.PersistenceSnapshot? maskSnapshot = null;
                    try
                    {
                        WorkspaceSnapshot snapshot = snapshotFactory()
                            ?? throw new InvalidOperationException("Workspace snapshot capture returned null.");
                        saveLease = _store.AcquireWorkspaceSaveLease(snapshot.VideoPath);
                        maskSnapshot = _maskProvider.CreatePersistenceSnapshot();
                        var pending = new PendingSave(
                            finalRequestId,
                            snapshot,
                            maskSnapshot,
                            saveLease,
                            finalCompletion);
                        maskSnapshot = null;
                        saveLease = null;
                        // The worker awaits the previously published completion tail.
                        // It cannot complete synchronously while the capture gate is held.
                        _ = ExecutePendingSaveAsync(pending, predecessor!);
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
                            // Preserve the original capture failure.
                        }

                        finalCompletion.TrySetException(ex);
                    }
                }
            }

            finalTask.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Task latestTask;
            // As with SaveNow, wait for any in-progress QueueSaveAsync capture to
            // publish (or fail) before taking the completion tail. Otherwise Dispose
            // could return while a request still owns a provider snapshot that was not
            // yet visible through _latestTask.
            lock (_captureGate)
            {
                lock (_taskGate)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    latestTask = _latestTask;
                }
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
