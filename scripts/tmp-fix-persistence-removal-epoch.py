from pathlib import Path


def load_text(path: Path):
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), bom, newline


def save_text(path: Path, text: str, bom: bool, newline: str):
    if newline == "\r\n":
        text = text.replace("\n", "\r\n")
    payload = text.encode("utf-8")
    if bom:
        payload = b"\xef\xbb\xbf" + payload
    path.write_bytes(payload)


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


root = Path(__file__).resolve().parents[1]
store_path = root / "Services" / "Workspace" / "WorkspaceStateStore.cs"
coord_path = root / "Services" / "Workspace" / "WorkspacePersistenceCoordinator.cs"

store, store_bom, store_newline = load_text(store_path)
store = replace_once(
    store,
    """        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =
            new(FilePathComparer);
        private readonly string _rootDir;
""",
    """        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =
            new(FilePathComparer);
        // Logical deletion epochs are process-local because pending saves are also
        // process-local. A save captures the epoch when it is requested and may commit
        // only if no removal for that video path has happened since then.
        private static readonly Dictionary<string, long> WorkspaceRemovalEpochs =
            new(FilePathComparer);
        private readonly string _rootDir;
""",
    "add removal epoch registry")

store = replace_once(
    store,
    """        public void RemoveWorkspacesForPath(string videoPath)
""",
    """        internal long CaptureWorkspaceRemovalEpoch(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("A workspace video path is required.", nameof(videoPath));

            lock (GlobalStateGate)
                return GetWorkspaceRemovalEpochLocked(videoPath);
        }

        private static long GetWorkspaceRemovalEpochLocked(string videoPath)
            => WorkspaceRemovalEpochs.TryGetValue(videoPath, out long epoch)
                ? epoch
                : 0L;

        private static void RestoreWorkspaceRemovalEpochLocked(
            string videoPath,
            long previousEpoch)
        {
            if (previousEpoch == 0)
                WorkspaceRemovalEpochs.Remove(videoPath);
            else
                WorkspaceRemovalEpochs[videoPath] = previousEpoch;
        }

        public void RemoveWorkspacesForPath(string videoPath)
""",
    "add removal epoch helpers")

store = replace_once(
    store,
    """                RefreshStateLocked();
                var previousWorkspaces = _state.Workspaces.ToList();
                try
                {
                    _state.Workspaces.RemoveAll(w =>
                        string.Equals(
                            w.VideoPath,
                            videoPath,
                            StringComparison.OrdinalIgnoreCase));

                    // Commit the reference removal first. The old state remains in the
                    // backup until we explicitly synchronize it below.
                    SaveState();
                }
                catch
                {
                    _state.Workspaces = previousWorkspaces;
                    throw;
                }
""",
    """                RefreshStateLocked();
                var previousWorkspaces = _state.Workspaces.ToList();
                long previousRemovalEpoch = GetWorkspaceRemovalEpochLocked(videoPath);
                try
                {
                    // Publish logical removal under the same state gate used by save
                    // commit. Any save requested before this point keeps the previous
                    // epoch and is rejected if it reaches commit later.
                    WorkspaceRemovalEpochs[videoPath] = checked(previousRemovalEpoch + 1);

                    _state.Workspaces.RemoveAll(w =>
                        string.Equals(
                            w.VideoPath,
                            videoPath,
                            StringComparison.OrdinalIgnoreCase));

                    // Commit the reference removal first. The old state remains in the
                    // backup until we explicitly synchronize it below.
                    SaveState();
                }
                catch
                {
                    _state.Workspaces = previousWorkspaces;
                    RestoreWorkspaceRemovalEpochLocked(videoPath, previousRemovalEpoch);
                    throw;
                }
""",
    "advance removal epoch with state removal")

store = replace_once(
    store,
    """            using FrameMaskProvider.PersistenceSnapshot persistenceSnapshot =
                maskProvider.CreatePersistenceSnapshot();
            SaveWorkspaceSnapshot(snapshot, persistenceSnapshot);
        }

        internal void SaveWorkspaceSnapshot(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot)
            => SaveWorkspaceCore(snapshot, persistenceSnapshot);

        private void SaveWorkspaceCore(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot)
""",
    """            long removalEpoch = CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);
            using FrameMaskProvider.PersistenceSnapshot persistenceSnapshot =
                maskProvider.CreatePersistenceSnapshot();
            SaveWorkspaceSnapshot(snapshot, persistenceSnapshot, removalEpoch);
        }

        internal void SaveWorkspaceSnapshot(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,
            long removalEpoch)
            => SaveWorkspaceCore(snapshot, persistenceSnapshot, removalEpoch);

        private void SaveWorkspaceCore(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,
            long removalEpoch)
""",
    "thread removal epoch through save core")

store = replace_once(
    store,
    """            RegisterActiveWorkspaceDirectory(fullDir);
            bool committed = false;

            try
""",
    """            RegisterActiveWorkspaceDirectory(fullDir);
            bool committed = false;
            bool invalidatedByRemoval = false;

            try
""",
    "track removal invalidation")

store = replace_once(
    store,
    """                lock (GlobalStateGate)
                {
                    RefreshStateLocked();
                    WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>
                        string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                        string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));

                    _state.Workspaces.RemoveAll(w =>
                        string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                        string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));
                    _state.Workspaces.Add(newState);

                    try
                    {
                        SaveState();
                        committed = true;
                    }
                    catch
                    {
                        _state.Workspaces.Remove(newState);
                        if (previousState != null)
                            _state.Workspaces.Add(previousState);
                        throw;
                    }
                }
""",
    """                lock (GlobalStateGate)
                {
                    RefreshStateLocked();
                    if (GetWorkspaceRemovalEpochLocked(snapshot.VideoPath) != removalEpoch)
                    {
                        // The user removed this workspace after the save was requested.
                        // Do not let an older pending save recreate the deleted state.
                        invalidatedByRemoval = true;
                    }
                    else
                    {
                        WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>
                            string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));

                        _state.Workspaces.RemoveAll(w =>
                            string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));
                        _state.Workspaces.Add(newState);

                        try
                        {
                            SaveState();
                            committed = true;
                        }
                        catch
                        {
                            _state.Workspaces.Remove(newState);
                            if (previousState != null)
                                _state.Workspaces.Add(previousState);
                            throw;
                        }
                    }
                }
""",
    "reject pre-removal save at commit")

store = replace_once(
    store,
    """            finally
            {
                UnregisterActiveWorkspaceDirectory(fullDir);
            }

            CleanupUnreferencedWorkspaceDirectories(snapshot.VideoPath, snapshot.Mode);
""",
    """            finally
            {
                UnregisterActiveWorkspaceDirectory(fullDir);
            }

            if (invalidatedByRemoval)
            {
                DeleteDirectoryBestEffort(dir);
                // Removal may have deferred base cleanup while this generation was
                // reserved. Retry after unregistering the stale preparation directory.
                TryDeleteWorkspaceBaseDirectory(snapshot.VideoPath);
                return;
            }

            CleanupUnreferencedWorkspaceDirectories(snapshot.VideoPath, snapshot.Mode);
""",
    "cleanup rejected save payload")

save_text(store_path, store, store_bom, store_newline)

coord, coord_bom, coord_newline = load_text(coord_path)
coord = replace_once(
    coord,
    """            ThrowIfQueueClosed();
            FrameMaskProvider.PersistenceSnapshot maskSnapshot =
                _maskProvider.CreatePersistenceSnapshot();
""",
    """            ThrowIfQueueClosed();
            long removalEpoch = _store.CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);
            FrameMaskProvider.PersistenceSnapshot maskSnapshot =
                _maskProvider.CreatePersistenceSnapshot();
""",
    "capture queue removal epoch")

coord = replace_once(
    coord,
    """                pending = new PendingSave(
                    ++_latestRequestId,
                    snapshot,
                    maskSnapshot);
""",
    """                pending = new PendingSave(
                    ++_latestRequestId,
                    snapshot,
                    maskSnapshot,
                    removalEpoch);
""",
    "attach queue removal epoch")

coord = replace_once(
    coord,
    """                    await Task.Run(() => _store.SaveWorkspaceSnapshot(
                        pending.Snapshot,
                        pending.MaskSnapshot)).ConfigureAwait(false);
""",
    """                    await Task.Run(() => _store.SaveWorkspaceSnapshot(
                        pending.Snapshot,
                        pending.MaskSnapshot,
                        pending.RemovalEpoch)).ConfigureAwait(false);
""",
    "pass removal epoch to store")

coord = replace_once(
    coord,
    """            if (snapshot == null)
                return;

            Task predecessor = Task.CompletedTask;
""",
    """            if (snapshot == null)
                return;

            long removalEpoch = _store.CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);
            Task predecessor = Task.CompletedTask;
""",
    "capture SaveNow removal epoch")

coord = replace_once(
    coord,
    """                    var pending = new PendingSave(
                        finalRequestId,
                        snapshot,
                        maskSnapshot,
                        finalCompletion!);
""",
    """                    var pending = new PendingSave(
                        finalRequestId,
                        snapshot,
                        maskSnapshot,
                        removalEpoch,
                        finalCompletion!);
""",
    "attach SaveNow removal epoch")

coord = replace_once(
    coord,
    """                long requestId,
                WorkspaceSnapshot snapshot,
                FrameMaskProvider.PersistenceSnapshot maskSnapshot,
                TaskCompletionSource<object?>? completion = null)
            {
                RequestId = requestId;
                Snapshot = snapshot;
                MaskSnapshot = maskSnapshot;
                Completion = completion ?? new TaskCompletionSource<object?>(
""",
    """                long requestId,
                WorkspaceSnapshot snapshot,
                FrameMaskProvider.PersistenceSnapshot maskSnapshot,
                long removalEpoch,
                TaskCompletionSource<object?>? completion = null)
            {
                RequestId = requestId;
                Snapshot = snapshot;
                MaskSnapshot = maskSnapshot;
                RemovalEpoch = removalEpoch;
                Completion = completion ?? new TaskCompletionSource<object?>(
""",
    "extend PendingSave constructor")

coord = replace_once(
    coord,
    """            internal WorkspaceSnapshot Snapshot { get; }
            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }
            internal TaskCompletionSource<object?> Completion { get; }
""",
    """            internal WorkspaceSnapshot Snapshot { get; }
            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }
            internal long RemovalEpoch { get; }
            internal TaskCompletionSource<object?> Completion { get; }
""",
    "add PendingSave removal epoch property")

save_text(coord_path, coord, coord_bom, coord_newline)
print("[PersistenceRemovalEpochPatch] applied")
