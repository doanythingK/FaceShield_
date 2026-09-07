from pathlib import Path


def read_text(path):
    return Path(path).read_bytes().decode('utf-8')


def write_text(path, text):
    Path(path).write_bytes(text.encode('utf-8'))


def local_newline(text):
    return '\r\n' if text.count('\r\n') > 0 else '\n'


def adapt(text, nl):
    return text.replace('\r\n', '\n').replace('\n', nl)


def replace_once(path, old, new, label):
    text = read_text(path)
    nl = local_newline(text)
    old2 = adapt(old, nl)
    new2 = adapt(new, nl)
    count = text.count(old2)
    if count != 1:
        raise SystemExit(f'{label}: expected one match, found {count}')
    write_text(path, text.replace(old2, new2, 1))


def replace_between(path, start_marker, end_marker, replacement, label):
    text = read_text(path)
    start = text.find(start_marker)
    if start < 0:
        raise SystemExit(f'{label}: start marker not found')
    end = text.find(end_marker, start)
    if end < 0:
        raise SystemExit(f'{label}: end marker not found')
    nl = local_newline(text[start:end])
    write_text(path, text[:start] + adapt(replacement, nl) + text[end:])


# 1) Detached snapshot may expose stable borrowed stored-mask entries to persistence.
replace_once(
    'Services/Video/FrameMaskProvider.cs',
    '''    internal SparseFaceMaskWorkingCopy CreateSparseFaceMaskWorkingCopy(
''',
    '''    internal IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetStoredMaskBorrowedSnapshot()
    {
        if (!_allowsBorrowedBitmapReads)
        {
            throw new InvalidOperationException(
                "Borrowed stored-mask snapshots are only allowed on detached provider snapshots.");
        }

        lock (_stateGate)
            return _masks.ToArray();
    }

    internal SparseFaceMaskWorkingCopy CreateSparseFaceMaskWorkingCopy(
''',
    'frame-mask-borrowed-snapshot')


# 2) WorkspaceStateStore: process-wide state/file serialization and stale-state refresh.
replace_once(
    'Services/Workspace/WorkspaceStateStore.cs',
    '''        private readonly string _rootDir;
''',
    '''        private static readonly object GlobalStateGate = new();
        private readonly string _rootDir;
''',
    'state-global-gate')

replace_once(
    'Services/Workspace/WorkspaceStateStore.cs',
    '''            _state = LoadState();
''',
    '''            lock (GlobalStateGate)
                _state = LoadState();
''',
    'state-constructor-lock')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public IReadOnlyList<RecentItem> GetRecents()',
    '        public AutoSettingsState? GetAutoSettings()',
    '''        public IReadOnlyList<RecentItem> GetRecents()
        {
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                return _state.Recents
                    .Select(r => new RecentItem(r.Title, r.Path, r.LastOpened))
                    .ToList();
            }
        }

''',
    'state-get-recents')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public AutoSettingsState? GetAutoSettings()',
    '        public void SaveAutoSettings(AutoSettingsState settings)',
    '''        public AutoSettingsState? GetAutoSettings()
        {
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                return _state.AutoSettings;
            }
        }

''',
    'state-get-auto-settings')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public void SaveAutoSettings(AutoSettingsState settings)',
    '        public void SaveRecents(IEnumerable<RecentItem> recents)',
    '''        public void SaveAutoSettings(AutoSettingsState settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                _state.AutoSettings = settings;
                SaveState();
            }
        }

''',
    'state-save-auto-settings')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public void SaveRecents(IEnumerable<RecentItem> recents)',
    '        public void RemoveWorkspacesForPath(string videoPath)',
    '''        public void SaveRecents(IEnumerable<RecentItem> recents)
        {
            if (recents == null)
                throw new ArgumentNullException(nameof(recents));

            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                _state.Recents = recents
                    .Select(r => new RecentItemState
                    {
                        Title = r.Title,
                        Path = r.Path,
                        LastOpened = r.LastOpened
                    })
                    .ToList();

                SaveState();
            }
        }

''',
    'state-save-recents')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public void RemoveWorkspacesForPath(string videoPath)',
    '        public bool TryLoadWorkspace(',
    '''        public void RemoveWorkspacesForPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

            lock (GlobalStateGate)
            {
                RefreshStateLocked();
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

                // Never delete workspace payloads while the backup can still reference
                // them. If backup synchronization fails, leaving orphaned files is safer
                // than creating a backup that points at missing data.
                if (!TrySyncBackupToCurrentState())
                    return;

                TryDeleteWorkspaceBaseDirectory(videoPath);
            }
        }

''',
    'state-remove-workspaces')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public bool TryLoadWorkspace(',
    '        private static WorkspaceState? FindWorkspaceState(',
    '''        public bool TryLoadWorkspace(
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

''',
    'state-load-workspace')

replace_between(
    'Services/Workspace/WorkspaceStateStore.cs',
    '        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)',
    '        private string GetWorkspaceBaseDir(string videoPath)',
    '''        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)
            => SaveWorkspaceCore(snapshot, maskProvider, useBorrowedStoredMasks: false);

        internal void SaveWorkspaceSnapshot(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider maskProviderSnapshot)
            => SaveWorkspaceCore(snapshot, maskProviderSnapshot, useBorrowedStoredMasks: true);

        private void SaveWorkspaceCore(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider maskProvider,
            bool useBorrowedStoredMasks)
        {
            if (snapshot == null)
                return;
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));

            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                string generation = Guid.NewGuid().ToString("N");
                string dir = GetWorkspaceDir(snapshot.VideoPath, snapshot.Mode, generation);
                WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>
                    string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                    string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));

                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);

                try
                {
                    IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> entries =
                        useBorrowedStoredMasks
                            ? maskProvider.GetStoredMaskBorrowedSnapshot()
                            : maskProvider.GetStoredMaskSnapshot();
                    var indices = new List<int>(entries.Count);
                    var indexSet = new HashSet<int>();

                    try
                    {
                        foreach (var entry in entries)
                        {
                            indices.Add(entry.Key);
                            indexSet.Add(entry.Key);
                            string filePath = Path.Combine(dir, $"mask_{entry.Key}.png");
                            SaveMask(filePath, entry.Value);
                        }
                    }
                    finally
                    {
                        if (!useBorrowedStoredMasks)
                        {
                            foreach (var entry in entries)
                                entry.Value.Dispose();
                        }
                    }

                    var faceMasks = maskProvider.GetFaceMaskEntries()
                        .Where(entry => !indexSet.Contains(entry.Key))
                        .OrderBy(entry => entry.Key)
                        .Select(entry => new FaceMaskState
                        {
                            FrameIndex = entry.Key,
                            Width = entry.Value.Size.Width,
                            Height = entry.Value.Size.Height,
                            MinConfidence = entry.Value.MinConfidence,
                            Faces = entry.Value.Faces
                                .Select(r => new RectState
                                {
                                    X = r.X,
                                    Y = r.Y,
                                    Width = r.Width,
                                    Height = r.Height
                                })
                                .ToList(),
                            Confidences = entry.Value.Confidences.ToList()
                        })
                        .ToList();

                    var newState = new WorkspaceState
                    {
                        VideoPath = snapshot.VideoPath,
                        Mode = snapshot.Mode.ToString(),
                        StorageGeneration = generation,
                        SelectedFrameIndex = snapshot.SelectedFrameIndex,
                        ViewStartSeconds = snapshot.ViewStartSeconds,
                        SecondsPerScreen = snapshot.SecondsPerScreen,
                        TimelineExtentSeconds = snapshot.TimelineExtentSeconds,
                        LastOpened = snapshot.LastOpened,
                        MaskIndices = indices,
                        FaceMasks = faceMasks,
                        AutoResumeIndex = snapshot.AutoResumeIndex,
                        AutoCompleted = snapshot.AutoCompleted,
                        AutoRunSignature = snapshot.AutoRunSignature,
                        AutoExecutionSignature = snapshot.AutoExecutionSignature,
                        AutoExportGateRequired = snapshot.AutoExportGateRequired,
                        AutoExportGatePassed = snapshot.AutoExportGatePassed,
                        AutoExportGateFailure = snapshot.AutoExportGateFailure,
                        AutoExportHybridPolicyAvailable = snapshot.AutoExportHybridPolicyAvailable,
                        AutoExportAllowHybridCopy = snapshot.AutoExportAllowHybridCopy,
                        AutoExportHybridDisableReasons = snapshot.AutoExportHybridDisableReasons
                    };

                    _state.Workspaces.RemoveAll(w =>
                        string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&
                        string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));
                    _state.Workspaces.Add(newState);

                    try
                    {
                        SaveState();
                    }
                    catch
                    {
                        _state.Workspaces.Remove(newState);
                        if (previousState != null)
                            _state.Workspaces.Add(previousState);
                        throw;
                    }

                    CleanupUnreferencedWorkspaceDirectories(snapshot.VideoPath, snapshot.Mode);
                }
                catch
                {
                    try
                    {
                        if (Directory.Exists(dir))
                            Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // Keep the failed generation for diagnostics if cleanup itself fails.
                    }

                    throw;
                }
            }
        }

''',
    'state-save-workspace-core')

replace_once(
    'Services/Workspace/WorkspaceStateStore.cs',
    '''        private AppState LoadState()
''',
    '''        private void RefreshStateLocked()
        {
            _state = LoadState();
        }

        private AppState LoadState()
''',
    'state-refresh-helper')


# 3) Persistence coordinator: capture mask state at queue/call time and dispose every snapshot.
replace_between(
    'Services/Workspace/WorkspacePersistenceCoordinator.cs',
    '        public Task QueueSaveAsync(WorkspaceSnapshot snapshot)',
    '        public async Task FlushAsync()',
    '''        public Task QueueSaveAsync(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return Task.CompletedTask;
            ThrowIfDisposed();

            FrameMaskProvider maskSnapshot = _maskProvider.CreateSnapshot();
            long requestId = Interlocked.Increment(ref _latestRequestId);
            Task task;
            try
            {
                task = SaveQueuedAsync(snapshot, maskSnapshot, requestId);
            }
            catch
            {
                maskSnapshot.Dispose();
                throw;
            }

            lock (_taskGate)
                _latestTask = task;
            return task;
        }

        private async Task SaveQueuedAsync(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider maskSnapshot,
            long requestId)
        {
            bool entered = false;
            try
            {
                await _saveGate.WaitAsync().ConfigureAwait(false);
                entered = true;
                if (requestId != Volatile.Read(ref _latestRequestId))
                    return;

                await Task.Run(() => _store.SaveWorkspaceSnapshot(snapshot, maskSnapshot))
                    .ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                    _saveGate.Release();
                maskSnapshot.Dispose();
            }
        }

''',
    'persistence-queue-snapshot')

replace_between(
    'Services/Workspace/WorkspacePersistenceCoordinator.cs',
    '        public void SaveNow(WorkspaceSnapshot snapshot)',
    '        public void Dispose()',
    '''        public void SaveNow(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            ThrowIfDisposed();

            using FrameMaskProvider maskSnapshot = _maskProvider.CreateSnapshot();
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
                _store.SaveWorkspaceSnapshot(snapshot, maskSnapshot);
            }
            finally
            {
                _saveGate.Release();
            }
        }

''',
    'persistence-save-now-snapshot')


# 4) Automatic export must use the same user-selected quality preset as manual export.
replace_once(
    'ViewModels/Workspace/AutoMaskRunCoordinator.cs',
    '''                    autoRunSummary: generator.LastRunSummary,
                    autoRunOptions: runOptions);
''',
    '''                    autoRunSummary: generator.LastRunSummary,
                    autoRunOptions: runOptions,
                    qualityPreset: _toolPanel.ExportQualityPreset);
''',
    'auto-export-quality-preset')


# 5) Weighted evidence is per-track, not per-gap.
replace_once(
    'Services/Analysis/FaceTrackInterpolator.cs',
    '''                var detections = track.Detections;
                for (int i = 1; i < detections.Count; i++)
''',
    '''                bool confirmedTrack = IsConfirmedTrack(track, options);
                var detections = track.Detections;
                for (int i = 1; i < detections.Count; i++)
''',
    'track-confirmed-cache')

replace_once(
    'Services/Analysis/FaceTrackInterpolator.cs',
    '''                    int maxFillGap = options.MaxFillGap;
                    if (IsConfirmedTrack(track, options))
                        maxFillGap = Math.Max(maxFillGap, options.MaxConfirmedTrackHoldFrames);
''',
    '''                    int maxFillGap = options.MaxFillGap;
                    if (confirmedTrack)
                        maxFillGap = Math.Max(maxFillGap, options.MaxConfirmedTrackHoldFrames);
''',
    'track-confirmed-cache-use')


# 6) Detector-pool construction rollback: dispose secondaries already created if a later create throws.
replace_between(
    'Services/Analysis/AutoMaskGenerationStrategyPlanner.cs',
    '    internal static AutoMaskDetectorPool CreateCompatibleDetectorPool(',
    '\n}\n\ninternal sealed class AutoMaskDetectorPool',
    '''    internal static AutoMaskDetectorPool CreateCompatibleDetectorPool(
        IBgraFaceDetector primary,
        IFaceDetectorFactory factory,
        int requestedCount)
    {
        if (primary == null)
            throw new ArgumentNullException(nameof(primary));
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        int targetCount = Math.Max(1, requestedCount);
        var detectors = new List<IBgraFaceDetector>(targetCount)
        {
            primary
        };

        try
        {
            for (int i = 1; i < targetCount; i++)
            {
                IFaceDetector candidate = factory.CreateDetector();
                if (candidate is not IBgraFaceDetector bgraCandidate)
                {
                    candidate.Dispose();
                    break;
                }

                if (!DetectorExecutionProviderIdentity.AreCompatible(primary, candidate))
                {
                    Debug.WriteLine(
                        $"[AutoMask] parallel detector provider mismatch; expected={DetectorExecutionProviderIdentity.GetCanonicalLabel(primary)}, actual={DetectorExecutionProviderIdentity.GetCanonicalLabel(candidate)}, usingSessions={detectors.Count}");
                    candidate.Dispose();
                    break;
                }

                detectors.Add(bgraCandidate);
            }

            return new AutoMaskDetectorPool(detectors);
        }
        catch
        {
            DisposeCreatedSecondaryDetectors(detectors);
            throw;
        }
    }

    private static void DisposeCreatedSecondaryDetectors(IReadOnlyList<IBgraFaceDetector> detectors)
    {
        // Index 0 is caller-owned. Roll back only detectors created by this planner.
        for (int i = 1; i < detectors.Count; i++)
        {
            try
            {
                detectors[i].Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AutoMask] detector-pool rollback dispose failed at index={i}: {ex.Message}");
            }
        }
    }
''',
    'detector-pool-rollback')

print('stabilization patch applied')
