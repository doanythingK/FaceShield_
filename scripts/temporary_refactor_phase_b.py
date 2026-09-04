from pathlib import Path


def read_exact(p: Path) -> str:
    with p.open("r", encoding="utf-8", newline="") as f:
        return f.read()


def write_exact(p: Path, text: str) -> None:
    with p.open("w", encoding="utf-8", newline="") as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = read_exact(p)
    candidates = [(old, new)]
    if "\n" in old:
        candidates.append((old.replace("\n", "\r\n"), new.replace("\n", "\r\n")))
    for old_candidate, new_candidate in candidates:
        count = text.count(old_candidate)
        if count == 1:
            write_exact(p, text.replace(old_candidate, new_candidate, 1))
            return
        if count > 1:
            raise RuntimeError(f"Expected one match in {path}, found {count}")
    raise RuntimeError(f"Patch target not found in {path}: {old[:120]!r}")


coordinator = Path("Services/Workspace/WorkspacePersistenceCoordinator.cs")
write_exact(coordinator, """using FaceShield.Services.Video;
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
""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """        private readonly WorkspaceStateStore? _stateStore;\n        private int[] _autoAnomalies = Array.Empty<int>();\n""",
    """        private readonly WorkspaceStateStore? _stateStore;\n        private readonly WorkspacePersistenceCoordinator? _workspacePersistence;\n        private int[] _autoAnomalies = Array.Empty<int>();\n""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """            _stateStore = stateStore;\n            initializationToken.ThrowIfCancellationRequested();\n""",
    """            _stateStore = stateStore;\n            _workspacePersistence = stateStore == null\n                ? null\n                : new WorkspacePersistenceCoordinator(stateStore, _maskProvider);\n            initializationToken.ThrowIfCancellationRequested();\n""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """        [RelayCommand]\n        private void GoBack()\n        {\n            FramePreview.PersistCurrentMask();\n            PersistWorkspaceState();\n            _onBack?.Invoke();\n        }\n""",
    """        [RelayCommand]\n        private async Task GoBack()\n        {\n            FramePreview.PersistCurrentMask();\n            PersistWorkspaceState(includePreviewMask: false);\n\n            if (_workspacePersistence != null)\n            {\n                try\n                {\n                    await _workspacePersistence.FlushAsync();\n                }\n                catch (Exception ex)\n                {\n                    await ShowErrorDialogAsync(\"워크스페이스 저장 실패\", ex.Message);\n                    return;\n                }\n            }\n\n            _onBack?.Invoke();\n        }\n""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """        public void PersistWorkspaceState()\n        {\n            PersistWorkspaceState(includePreviewMask: true);\n        }\n\n        private void PersistWorkspaceState(bool includePreviewMask)\n        {\n            if (_stateStore == null)\n                return;\n\n            if (includePreviewMask)\n                FramePreview.PersistCurrentMask();\n            var snapshot = BuildSnapshot();\n            _stateStore.SaveWorkspace(snapshot, _maskProvider);\n        }\n""",
    """        public void PersistWorkspaceState()\n        {\n            PersistWorkspaceState(includePreviewMask: true);\n        }\n\n        public void PersistWorkspaceStateImmediate()\n        {\n            if (_stateStore == null)\n                return;\n\n            FramePreview.PersistCurrentMask();\n            WorkspaceSnapshot snapshot = BuildSnapshot();\n            if (_workspacePersistence != null)\n                _workspacePersistence.SaveNow(snapshot);\n            else\n                _stateStore.SaveWorkspace(snapshot, _maskProvider);\n        }\n\n        private void PersistWorkspaceState(bool includePreviewMask)\n        {\n            if (_workspacePersistence == null)\n                return;\n\n            if (includePreviewMask)\n                FramePreview.PersistCurrentMask();\n\n            WorkspaceSnapshot snapshot = BuildSnapshot();\n            if (!TryBeginLifetimeOperation())\n                return;\n\n            Task saveTask;\n            try\n            {\n                saveTask = _workspacePersistence.QueueSaveAsync(snapshot);\n            }\n            catch\n            {\n                EndLifetimeOperation();\n                throw;\n            }\n\n            _ = ObserveWorkspacePersistenceAsync(saveTask);\n        }\n\n        private async Task ObserveWorkspacePersistenceAsync(Task saveTask)\n        {\n            try\n            {\n                await saveTask.ConfigureAwait(false);\n            }\n            catch (Exception ex)\n            {\n                System.Diagnostics.Debug.WriteLine(\n                    $\"[WorkspacePersistence] background save failed: {ex.Message}\");\n            }\n            finally\n            {\n                EndLifetimeOperation();\n            }\n        }\n""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """            _maskProvider.Dispose();\n            _exportGate.Dispose();\n""",
    """            _workspacePersistence?.Dispose();\n            _maskProvider.Dispose();\n            _exportGate.Dispose();\n""")

replace_once(
    "ViewModels/Pages/HomePageViewModel.cs",
    """        public void PersistAllWorkspaces()\n        {\n            foreach (var vm in _workspaceCache.Values)\n                vm.PersistWorkspaceState();\n\n            _stateStore.SaveRecents(Recents);\n        }\n""",
    """        public void PersistAllWorkspaces()\n        {\n            foreach (var vm in _workspaceCache.Values)\n                vm.PersistWorkspaceStateImmediate();\n\n            _stateStore.SaveRecents(Recents);\n        }\n""")

print("Phase B workspace persistence patch applied.")
