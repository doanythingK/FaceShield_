from pathlib import Path

coordinator_path = Path('Services/Workspace/WorkspacePersistenceCoordinator.cs')
coordinator = '''using FaceShield.Services.Video;
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
'''
coordinator_path.write_text(coordinator, encoding='utf-8', newline='')

store_path = Path('Services/Workspace/WorkspaceStateStore.cs')
text = store_path.read_text(encoding='utf-8')
old = '''                foreach (string candidate in candidates)
                {
                    string fullCandidate = Path.GetFullPath(candidate);
                    if (IsWorkspaceDirectoryReferenced(videoPath, mode, fullCandidate))
                        continue;

                    TryDeleteDirectoryIfInactive(fullCandidate);
                }
            }
            catch
            {
                // Cleanup is best-effort; never invalidate a successful save.
            }
        }

        private bool IsWorkspaceDirectoryReferenced(
            string videoPath,
            WorkspaceMode mode,
            string directory)
        {
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                return IsWorkspaceDirectoryReferencedByState(_state, videoPath, mode, directory) ||
                    IsWorkspaceDirectoryReferencedByState(
                        TryLoadStateFile(_stateBackupFile),
                        videoPath,
                        mode,
                        directory);
            }
        }
'''
new = '''                foreach (string candidate in candidates)
                {
                    string fullCandidate = Path.GetFullPath(candidate);
                    TryDeleteWorkspaceDirectoryIfUnreferenced(
                        videoPath,
                        mode,
                        fullCandidate);
                }
            }
            catch
            {
                // Cleanup is best-effort; never invalidate a successful save.
            }
        }

        private bool TryDeleteWorkspaceDirectoryIfUnreferenced(
            string videoPath,
            WorkspaceMode mode,
            string directory)
        {
            // WorkspaceDirectoryGate acts as a deletion reservation. A preparing save
            // must remain registered until after commit, so holding this gate across
            // the final state-reference check prevents the old check/delete TOCTOU.
            lock (WorkspaceDirectoryGate)
            {
                if (HasActiveWorkspaceDirectoryUnderLocked(directory))
                    return false;

                bool referenced;
                lock (GlobalStateGate)
                {
                    RefreshStateLocked();
                    referenced =
                        IsWorkspaceDirectoryReferencedByState(
                            _state,
                            videoPath,
                            mode,
                            directory) ||
                        IsWorkspaceDirectoryReferencedByState(
                            TryLoadStateFile(_stateBackupFile),
                            videoPath,
                            mode,
                            directory);
                }

                if (referenced)
                    return false;

                if (!Directory.Exists(directory))
                    return true;

                try
                {
                    // Do not hold GlobalStateGate during filesystem deletion. The
                    // directory lifecycle gate alone keeps save prepare/unregister
                    // transitions from crossing this deletion boundary.
                    Directory.Delete(directory, recursive: true);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WorkspaceStateStore] orphan workspace cleanup deferred: {ex.Message}");
                    return false;
                }
            }
        }
'''
if text.count(old) != 1:
    raise SystemExit(f'WorkspaceStateStore cleanup anchor count={text.count(old)}')
text = text.replace(old, new)
store_path.write_text(text, encoding='utf-8', newline='')

guard_path = Path('Services/Application/SingleInstanceGuard.cs')
guard = '''using System;
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
'''
guard_path.write_text(guard, encoding='utf-8', newline='')

# Structural invariants for the P1 fixes and cross-platform single-instance guard.
coord = coordinator_path.read_text(encoding='utf-8')
if 'QueueSaveAsync(snapshot).GetAwaiter().GetResult();' in coord:
    raise SystemExit('SaveNow still delegates directly to QueueSaveAsync')
for token in [
    'private bool _finalizing;',
    'await predecessor.ConfigureAwait(false);',
    '_finalizing = true;',
    'new InvalidOperationException(',
    'finalTask.GetAwaiter().GetResult();',
]:
    if token not in coord:
        raise SystemExit(f'missing coordinator invariant: {token}')

store = store_path.read_text(encoding='utf-8')
for token in [
    'TryDeleteWorkspaceDirectoryIfUnreferenced(',
    'lock (WorkspaceDirectoryGate)',
    'lock (GlobalStateGate)',
    'if (HasActiveWorkspaceDirectoryUnderLocked(directory))',
    'Directory.Delete(directory, recursive: true);',
]:
    if token not in store:
        raise SystemExit(f'missing store invariant: {token}')

guard_text = guard_path.read_text(encoding='utf-8')
for forbidden in ['FileStream.Lock(', 'FileStream.Unlock(', '.Lock(0, 1)', '.Unlock(0, 1)']:
    if forbidden in guard_text:
        raise SystemExit(f'unsupported single-instance primitive remains: {forbidden}')
for token in ['new Mutex(', 'WaitOne(0)', 'ReleaseMutex()', '_processLeaseHeld']:
    if token not in guard_text:
        raise SystemExit(f'missing single-instance invariant: {token}')

print('[PersistenceP1Patch] PASS')
