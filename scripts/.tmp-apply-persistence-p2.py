from pathlib import Path

coord_path = Path('Services/Workspace/WorkspacePersistenceCoordinator.cs')
coord = coord_path.read_text(encoding='utf-8')
old_save_now = '''        public void SaveNow(WorkspaceSnapshot snapshot)
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
'''
new_save_now = '''        public void SaveNow(WorkspaceSnapshot snapshot)
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
                FrameMaskProvider.PersistenceSnapshot? maskSnapshot = null;
                try
                {
                    // Metadata/lease capture may be proportional to the number of masks.
                    // Keep it outside _taskGate after the final boundary is published.
                    maskSnapshot = _maskProvider.CreatePersistenceSnapshot();
                    var pending = new PendingSave(
                        finalRequestId,
                        snapshot,
                        maskSnapshot,
                        finalCompletion!);
                    maskSnapshot = null; // PendingSave owns the persistence lease.
                    _ = ExecutePendingSaveAsync(pending, predecessor);
                }
                catch (Exception ex)
                {
                    try
                    {
                        maskSnapshot?.Dispose();
                    }
                    catch
                    {
                        // Preserve the snapshot-capture failure as the terminal error.
                    }

                    finalCompletion!.TrySetException(ex);
                }
            }

            finalTask.GetAwaiter().GetResult();
        }
'''
if coord.count(old_save_now) != 1:
    raise SystemExit(f'SaveNow anchor count={coord.count(old_save_now)}')
coord = coord.replace(old_save_now, new_save_now)
old_pending = '''            internal PendingSave(
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
'''
new_pending = '''            internal PendingSave(
                long requestId,
                WorkspaceSnapshot snapshot,
                FrameMaskProvider.PersistenceSnapshot maskSnapshot,
                TaskCompletionSource<object?>? completion = null)
            {
                RequestId = requestId;
                Snapshot = snapshot;
                MaskSnapshot = maskSnapshot;
                Completion = completion ?? new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal long RequestId { get; }
            internal WorkspaceSnapshot Snapshot { get; }
            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }
            internal TaskCompletionSource<object?> Completion { get; }
'''
if coord.count(old_pending) != 1:
    raise SystemExit(f'PendingSave anchor count={coord.count(old_pending)}')
coord = coord.replace(old_pending, new_pending)
coord_path.write_text(coord, encoding='utf-8', newline='')

store_path = Path('Services/Workspace/WorkspaceStateStore.cs')
text = store_path.read_text(encoding='utf-8')
old_active_decl = '''        private static readonly HashSet<string> ActiveWorkspacePreparationDirectories =
            new(FilePathComparer);
'''
new_active_decl = '''        // A directory can be reserved by more than one reader/save concurrently.
        // Reference counts prevent one load from releasing another load's protection.
        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =
            new(FilePathComparer);
'''
if text.count(old_active_decl) != 1:
    raise SystemExit(f'active declaration anchor count={text.count(old_active_decl)}')
text = text.replace(old_active_decl, new_active_decl)

old_load = '''        public bool TryLoadWorkspace(
            string videoPath,
            WorkspaceMode mode,
            FrameMaskProvider maskProvider,
            out WorkspaceSnapshot? snapshot)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));

            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                snapshot = null;
                WorkspaceState? primaryState = FindWorkspaceState(_state, videoPath, mode);
                if (primaryState == null)
                    return false;

                WorkspaceState stateToUse = primaryState;
                bool loadedComplete = TryLoadWorkspacePayload(
                    videoPath,
                    mode,
                    primaryState,
                    maskProvider,
                    requireComplete: true);

                if (!loadedComplete)
                {
                    AppState? backupAppState = TryLoadStateFile(_stateBackupFile);
                    WorkspaceState? backupState = FindWorkspaceState(backupAppState, videoPath, mode);
                    if (backupState != null &&
                        TryLoadWorkspacePayload(
                            videoPath,
                            mode,
                            backupState,
                            maskProvider,
                            requireComplete: true))
                    {
                        stateToUse = backupState;
                        System.Diagnostics.Debug.WriteLine(
                            $"[WorkspaceStateStore] recovered workspace payload from backup generation " +
                            $"'{backupState.StorageGeneration ?? "legacy"}'.");
                    }
                    else
                    {
                        // Preserve the old best-effort behavior when neither generation is complete,
                        // but never delete unreadable payload files while attempting recovery.
                        TryLoadWorkspacePayload(
                            videoPath,
                            mode,
                            primaryState,
                            maskProvider,
                            requireComplete: false);
                    }
                }

                snapshot = CreateWorkspaceSnapshot(stateToUse, mode);
                return true;
            }
        }
'''
new_load = '''        public bool TryLoadWorkspace(
            string videoPath,
            WorkspaceMode mode,
            FrameMaskProvider maskProvider,
            out WorkspaceSnapshot? snapshot)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));

            snapshot = null;
            WorkspaceState primaryState;
            WorkspaceState? backupState;

            // Capture immutable metadata while the state gate is held, but keep PNG
            // existence checks, reads, and decoding outside the process-wide gate.
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                WorkspaceState? primary = FindWorkspaceState(_state, videoPath, mode);
                if (primary == null)
                    return false;

                primaryState = CloneWorkspaceState(primary);
                AppState? backupAppState = TryLoadStateFile(_stateBackupFile);
                WorkspaceState? backup = FindWorkspaceState(backupAppState, videoPath, mode);
                backupState = backup == null ? null : CloneWorkspaceState(backup);
            }

            WorkspaceState stateToUse = primaryState;
            bool loadedComplete = TryLoadWorkspacePayload(
                videoPath,
                mode,
                primaryState,
                maskProvider,
                requireComplete: true);

            if (!loadedComplete)
            {
                if (backupState != null &&
                    TryLoadWorkspacePayload(
                        videoPath,
                        mode,
                        backupState,
                        maskProvider,
                        requireComplete: true))
                {
                    stateToUse = backupState;
                    System.Diagnostics.Debug.WriteLine(
                        $"[WorkspaceStateStore] recovered workspace payload from backup generation " +
                        $"'{backupState.StorageGeneration ?? "legacy"}'.");
                }
                else
                {
                    // Preserve the old best-effort behavior when neither captured
                    // generation is complete. Directory reservations below prevent
                    // cleanup/removal from deleting a payload while it is being read.
                    TryLoadWorkspacePayload(
                        videoPath,
                        mode,
                        primaryState,
                        maskProvider,
                        requireComplete: false);
                }
            }

            snapshot = CreateWorkspaceSnapshot(stateToUse, mode);
            return true;
        }
'''
if text.count(old_load) != 1:
    raise SystemExit(f'TryLoadWorkspace anchor count={text.count(old_load)}')
text = text.replace(old_load, new_load)

find_anchor = '''        private static WorkspaceState? FindWorkspaceState(
            AppState? appState,
            string videoPath,
            WorkspaceMode mode)
        {
            if (appState == null)
                return null;

            return appState.Workspaces.FirstOrDefault(w =>
                string.Equals(w.VideoPath, videoPath, FilePathComparison) &&
                string.Equals(w.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase));
        }
'''
clone_method = find_anchor + '''
        private static WorkspaceState CloneWorkspaceState(WorkspaceState source)
        {
            return new WorkspaceState
            {
                VideoPath = source.VideoPath,
                Mode = source.Mode,
                StorageGeneration = source.StorageGeneration,
                SelectedFrameIndex = source.SelectedFrameIndex,
                ViewStartSeconds = source.ViewStartSeconds,
                SecondsPerScreen = source.SecondsPerScreen,
                TimelineExtentSeconds = source.TimelineExtentSeconds,
                LastOpened = source.LastOpened,
                MaskIndices = source.MaskIndices?.ToList() ?? new List<int>(),
                FaceMasks = source.FaceMasks?
                    .Where(face => face != null)
                    .Select(face => new FaceMaskState
                    {
                        FrameIndex = face.FrameIndex,
                        Width = face.Width,
                        Height = face.Height,
                        MinConfidence = face.MinConfidence,
                        Faces = face.Faces?
                            .Where(rect => rect != null)
                            .Select(rect => new RectState
                            {
                                X = rect.X,
                                Y = rect.Y,
                                Width = rect.Width,
                                Height = rect.Height
                            })
                            .ToList() ?? new List<RectState>(),
                        Confidences = face.Confidences?.ToList() ?? new List<float>()
                    })
                    .ToList() ?? new List<FaceMaskState>(),
                AutoResumeIndex = source.AutoResumeIndex,
                AutoCompleted = source.AutoCompleted,
                AutoRunSignature = source.AutoRunSignature,
                AutoExecutionSignature = source.AutoExecutionSignature,
                AutoExportGateRequired = source.AutoExportGateRequired,
                AutoExportGatePassed = source.AutoExportGatePassed,
                AutoExportGateFailure = source.AutoExportGateFailure,
                AutoExportHybridPolicyAvailable = source.AutoExportHybridPolicyAvailable,
                AutoExportAllowHybridCopy = source.AutoExportAllowHybridCopy,
                AutoExportHybridDisableReasons = source.AutoExportHybridDisableReasons
            };
        }
'''
if text.count(find_anchor) != 1:
    raise SystemExit(f'FindWorkspaceState anchor count={text.count(find_anchor)}')
text = text.replace(find_anchor, clone_method)

old_payload_head = '''        private bool TryLoadWorkspacePayload(
            string videoPath,
            WorkspaceMode mode,
            WorkspaceState state,
            FrameMaskProvider maskProvider,
            bool requireComplete)
        {
            string dir = ResolveWorkspaceDirForRead(
                videoPath,
                mode,
                state.StorageGeneration);
            var loadedMasks = new List<KeyValuePair<int, WriteableBitmap>>();

            try
            {
'''
new_payload_head = '''        private bool TryLoadWorkspacePayload(
            string videoPath,
            WorkspaceMode mode,
            WorkspaceState state,
            FrameMaskProvider maskProvider,
            bool requireComplete)
        {
            string dir = ResolveWorkspaceDirForRead(
                videoPath,
                mode,
                state.StorageGeneration);
            string fullDir = Path.GetFullPath(dir);
            var loadedMasks = new List<KeyValuePair<int, WriteableBitmap>>();
            RegisterActiveWorkspaceDirectory(fullDir);

            try
            {
'''
if text.count(old_payload_head) != 1:
    raise SystemExit(f'payload head anchor count={text.count(old_payload_head)}')
text = text.replace(old_payload_head, new_payload_head)
old_payload_finally = '''            finally
            {
                foreach (var entry in loadedMasks)
                    entry.Value.Dispose();
            }
        }
'''
new_payload_finally = '''            finally
            {
                foreach (var entry in loadedMasks)
                    entry.Value.Dispose();
                UnregisterActiveWorkspaceDirectory(fullDir);
            }
        }
'''
# This block should identify the payload method's finally uniquely in this file.
if text.count(old_payload_finally) != 1:
    raise SystemExit(f'payload finally anchor count={text.count(old_payload_finally)}')
text = text.replace(old_payload_finally, new_payload_finally)

old_reservation_methods = '''        private static void RegisterActiveWorkspaceDirectory(string directory)
        {
            lock (WorkspaceDirectoryGate)
                ActiveWorkspacePreparationDirectories.Add(directory);
        }

        private static void UnregisterActiveWorkspaceDirectory(string directory)
        {
            lock (WorkspaceDirectoryGate)
                ActiveWorkspacePreparationDirectories.Remove(directory);
        }
'''
new_reservation_methods = '''        private static void RegisterActiveWorkspaceDirectory(string directory)
        {
            lock (WorkspaceDirectoryGate)
            {
                ActiveWorkspacePreparationDirectories.TryGetValue(directory, out int count);
                ActiveWorkspacePreparationDirectories[directory] = checked(count + 1);
            }
        }

        private static void UnregisterActiveWorkspaceDirectory(string directory)
        {
            lock (WorkspaceDirectoryGate)
            {
                if (!ActiveWorkspacePreparationDirectories.TryGetValue(directory, out int count))
                    return;

                if (count <= 1)
                    ActiveWorkspacePreparationDirectories.Remove(directory);
                else
                    ActiveWorkspacePreparationDirectories[directory] = count - 1;
            }
        }
'''
if text.count(old_reservation_methods) != 1:
    raise SystemExit(f'reservation methods anchor count={text.count(old_reservation_methods)}')
text = text.replace(old_reservation_methods, new_reservation_methods)
old_has_active = '''        private static bool HasActiveWorkspaceDirectoryUnderLocked(string directory)
        {
            if (ActiveWorkspacePreparationDirectories.Contains(directory))
                return true;

            string prefix = directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return ActiveWorkspacePreparationDirectories.Any(path =>
                path.StartsWith(prefix, FilePathComparison));
        }
'''
new_has_active = '''        private static bool HasActiveWorkspaceDirectoryUnderLocked(string directory)
        {
            if (ActiveWorkspacePreparationDirectories.TryGetValue(directory, out int count) &&
                count > 0)
            {
                return true;
            }

            string prefix = directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return ActiveWorkspacePreparationDirectories.Keys.Any(path =>
                path.StartsWith(prefix, FilePathComparison));
        }
'''
if text.count(old_has_active) != 1:
    raise SystemExit(f'HasActive anchor count={text.count(old_has_active)}')
text = text.replace(old_has_active, new_has_active)
store_path.write_text(text, encoding='utf-8', newline='')

# Product-level structural invariants.
coord = coord_path.read_text(encoding='utf-8')
store = store_path.read_text(encoding='utf-8')
checks = {
    'final placeholder': '_latestTask = finalCompletion.Task;' in coord,
    'snapshot outside task lock intent': 'Keep it outside _taskGate after the final boundary is published.' in coord,
    'load metadata clone': 'primaryState = CloneWorkspaceState(primary);' in store,
    'payload read reservation': 'RegisterActiveWorkspaceDirectory(fullDir);' in store and 'UnregisterActiveWorkspaceDirectory(fullDir);' in store,
    'ref counted directory reservation': 'Dictionary<string, int> ActiveWorkspacePreparationDirectories' in store,
}
missing = [name for name, ok in checks.items() if not ok]
if missing:
    raise SystemExit('missing invariants: ' + ', '.join(missing))
print('[PersistenceP2Patch] PASS')
