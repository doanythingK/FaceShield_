using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Enums.Workspace;
using FaceShield.Models;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FaceShield.Services.Workspace
{
    public sealed class WorkspaceStateStore
    {
        private const int CurrentWorkspaceStoragePathVersion = 4;
        // Identity keys and generated workspace-storage paths are normalized before
        // they enter these dictionaries/comparisons.
        private static readonly StringComparison FilePathComparison =
            StringComparison.Ordinal;
        private static readonly StringComparer FilePathComparer =
            StringComparer.Ordinal;
        private static readonly object GlobalStateGate = new();
        private static readonly object WorkspaceDirectoryGate = new();
        // A directory can be reserved by more than one reader/save concurrently.
        // Reference counts prevent one load from releasing another load's protection.
        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =
            new(FilePathComparer);
        // Save lifetime state is process-local because pending saves are process-local.
        // Removed paths keep a tombstone until an explicit reopen. Non-removed entries
        // are reclaimed as soon as their last save lease is released.
        private static readonly Dictionary<string, WorkspacePathLifetimeState> WorkspacePathLifetimes =
            new(FilePathComparer);
        private readonly string _rootDir;
        private readonly string _stateFile;
        private readonly string _stateBackupFile;
        private AppState _state;

        public WorkspaceStateStore()
        {
            _rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FaceShield");
            _stateFile = Path.Combine(_rootDir, "state.json");
            _stateBackupFile = Path.Combine(_rootDir, "state.json.bak");
            lock (GlobalStateGate)
                _state = LoadState();
        }

        public IReadOnlyList<RecentItem> GetRecents()
        {
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                return _state.Recents
                    .Select(r => new RecentItem(r.Title, r.Path, r.LastOpened))
                    .ToList();
            }
        }

        public AutoSettingsState? GetAutoSettings()
        {
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                return _state.AutoSettings;
            }
        }

        public void SaveAutoSettings(AutoSettingsState settings)
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

        public void SaveRecents(IEnumerable<RecentItem> recents)
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

        internal SaveLease AcquireWorkspaceSaveLease(string videoPath)
        {
            WorkspacePathIdentity.PathContext pathContext =
                WorkspacePathIdentity.CreatePathContext(videoPath);
            string identity = pathContext.IdentityKey;
            lock (GlobalStateGate)
            {
                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime))
                {
                    lifetime = new WorkspacePathLifetimeState();
                    WorkspacePathLifetimes.Add(identity, lifetime);
                }

                if (lifetime.Removed)
                {
                    throw new InvalidOperationException(
                        "The workspace path was removed and must be reopened before it can be saved again.");
                }

                lifetime.ActiveSaveCount = checked(lifetime.ActiveSaveCount + 1);
                return new SaveLease(pathContext, lifetime.Epoch);
            }
        }

        internal void ReopenWorkspacePath(string videoPath)
        {
            WorkspacePathIdentity.PathContext pathContext =
                WorkspacePathIdentity.CreatePathContext(videoPath);
            string identity = pathContext.IdentityKey;
            lock (GlobalStateGate)
            {
                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime) ||
                    !lifetime.Removed)
                {
                    return;
                }

                lifetime.Removed = false;
                if (lifetime.ActiveSaveCount == 0)
                    WorkspacePathLifetimes.Remove(identity);
            }
        }

        internal bool IsWorkspaceSaveLeaseCurrent(SaveLease saveLease)
        {
            if (saveLease == null)
                throw new ArgumentNullException(nameof(saveLease));

            lock (GlobalStateGate)
                return IsWorkspaceSaveLeaseCurrentLocked(saveLease);
        }

        private static bool IsWorkspaceSaveLeaseCurrentLocked(SaveLease saveLease)
            => WorkspacePathLifetimes.TryGetValue(saveLease.PathIdentity, out var lifetime) &&
               !lifetime.Removed &&
               lifetime.Epoch == saveLease.Epoch;

        private static void ReleaseWorkspaceSaveLease(string identity)
        {
            lock (GlobalStateGate)
            {
                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime))
                    return;

                if (lifetime.ActiveSaveCount <= 0)
                    throw new InvalidOperationException("Workspace save lease count underflow.");

                lifetime.ActiveSaveCount--;
                if (lifetime.ActiveSaveCount == 0 && !lifetime.Removed)
                    WorkspacePathLifetimes.Remove(identity);
            }
        }

        public void RemoveWorkspacesForPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

            WorkspacePathIdentity.PathContext pathContext =
                WorkspacePathIdentity.CreatePathContext(videoPath);
            string identity = pathContext.IdentityKey;
            bool deletePayloads;
            List<string> removedStoragePaths;
            List<string> removedStorageIdentities;
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                var previousWorkspaces = _state.Workspaces.ToList();
                bool lifetimeExisted = WorkspacePathLifetimes.TryGetValue(identity, out var lifetime);
                lifetime ??= new WorkspacePathLifetimeState();
                if (!lifetimeExisted)
                    WorkspacePathLifetimes.Add(identity, lifetime);

                long previousEpoch = lifetime.Epoch;
                bool previousRemoved = lifetime.Removed;
                var matchedWorkspaces = _state.Workspaces
                    .Where(w => WorkspaceStateMatchesPath(w, pathContext))
                    .ToList();
                removedStoragePaths = matchedWorkspaces
                    .Select(w => w.VideoPath)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                removedStorageIdentities = matchedWorkspaces
                    .Select(w => w.PathIdentity)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                try
                {
                    // Removal and save-lease validation are linearized by the same
                    // state gate. Every lease acquired before this point becomes stale.
                    lifetime.Epoch = checked(previousEpoch + 1);
                    lifetime.Removed = true;

                    _state.Workspaces.RemoveAll(w =>
                        WorkspaceStateMatchesPath(w, pathContext));

                    SaveState();
                }
                catch
                {
                    _state.Workspaces = previousWorkspaces;
                    lifetime.Epoch = previousEpoch;
                    lifetime.Removed = previousRemoved;
                    if (!lifetimeExisted &&
                        lifetime.ActiveSaveCount == 0 &&
                        !lifetime.Removed)
                    {
                        WorkspacePathLifetimes.Remove(identity);
                    }
                    throw;
                }

                if (!TrySyncBackupToCurrentState())
                    return;
                deletePayloads = true;
            }

            if (deletePayloads)
            {
                foreach (string storagePath in removedStoragePaths
                    .Append(pathContext.AccessPath)
                    .Distinct(StringComparer.Ordinal))
                {
                    TryDeleteWorkspaceBaseDirectory(storagePath);
                }

                foreach (string storageIdentity in removedStorageIdentities)
                    TryDeleteWorkspaceBaseDirectoryByIdentity(pathContext, storageIdentity);
            }
        }

        public bool TryLoadWorkspace(
            string videoPath,
            WorkspaceMode mode,
            FrameMaskProvider maskProvider,
            out WorkspaceSnapshot? snapshot)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));

            WorkspacePathIdentity.PathContext pathContext =
                WorkspacePathIdentity.CreatePathContext(videoPath);
            snapshot = null;
            WorkspaceState primaryState;
            WorkspaceState? backupState;

            // Filesystem-dependent identity work is completed before taking the
            // process-wide state gate. Matching below is pure against this context.
            lock (GlobalStateGate)
            {
                RefreshStateLocked();
                WorkspaceState? primary = FindWorkspaceState(_state, pathContext, mode);
                if (primary == null)
                    return false;

                primaryState = CloneWorkspaceState(primary);
                AppState? backupAppState = TryLoadStateFile(_stateBackupFile);
                WorkspaceState? backup = FindWorkspaceState(backupAppState, pathContext, mode);
                backupState = backup == null ? null : CloneWorkspaceState(backup);
            }

            WorkspaceState stateToUse = primaryState;
            bool loadedComplete = TryLoadWorkspacePayload(
                pathContext,
                mode,
                primaryState,
                maskProvider,
                requireComplete: true);

            if (!loadedComplete)
            {
                if (backupState != null &&
                    TryLoadWorkspacePayload(
                        pathContext,
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
                    TryLoadWorkspacePayload(
                        pathContext,
                        mode,
                        primaryState,
                        maskProvider,
                        requireComplete: false);
                }
            }

            snapshot = CreateWorkspaceSnapshot(stateToUse, pathContext, mode);
            return true;
        }

        private static WorkspaceState? FindWorkspaceState(
            AppState? appState,
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode)
        {
            if (appState == null)
                return null;

            return appState.Workspaces.FirstOrDefault(w =>
                WorkspaceStateMatchesPath(w, pathContext) &&
                string.Equals(w.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool WorkspaceStateMatchesPath(
            WorkspaceState state,
            WorkspacePathIdentity.PathContext pathContext)
        {
            if (state.StoragePathVersion >= CurrentWorkspaceStoragePathVersion &&
                !string.IsNullOrWhiteSpace(state.PathIdentity))
            {
                return string.Equals(
                    state.PathIdentity,
                    pathContext.IdentityKey,
                    StringComparison.Ordinal);
            }

            if (state.StoragePathVersion == 3 &&
                !string.IsNullOrWhiteSpace(state.PathIdentity))
            {
                if (string.Equals(
                        state.PathIdentity,
                        pathContext.V3IdentityKey,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                // v3 identity encoded the display casing of directory entries.
                // Re-evaluate the stored access path with the current operation's
                // case policy so case-only renames still match without filesystem I/O.
                if (pathContext.MatchesAccessPath(state.VideoPath))
                    return true;
            }

            if (state.StoragePathVersion >= 2 &&
                string.Equals(
                    state.VideoPath,
                    pathContext.V2IdentityKey,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return pathContext.MatchesAccessPath(state.VideoPath);
        }

        private static WorkspaceState CloneWorkspaceState(WorkspaceState source)
        {
            return new WorkspaceState
            {
                VideoPath = source.VideoPath,
                PathIdentity = source.PathIdentity,
                Mode = source.Mode,
                StoragePathVersion = source.StoragePathVersion,
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

        private bool TryLoadWorkspacePayload(
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode,
            WorkspaceState state,
            FrameMaskProvider maskProvider,
            bool requireComplete)
        {
            string dir = ResolveWorkspaceDirForRead(
                pathContext,
                mode,
                state);
            string fullDir = Path.GetFullPath(dir);
            var loadedMasks = new List<KeyValuePair<int, WriteableBitmap>>();
            RegisterActiveWorkspaceDirectory(fullDir);

            try
            {
                foreach (int index in state.MaskIndices ?? new List<int>())
                {
                    string filePath = Path.Combine(dir, $"mask_{index}.png");
                    if (!File.Exists(filePath))
                    {
                        if (requireComplete)
                            return false;
                        continue;
                    }

                    WriteableBitmap? mask = LoadMask(filePath);
                    if (mask == null)
                    {
                        if (requireComplete)
                            return false;
                        continue;
                    }

                    loadedMasks.Add(new KeyValuePair<int, WriteableBitmap>(index, mask));
                }

                maskProvider.Clear();
                ApplyFaceMasks(state, maskProvider);

                foreach (var entry in loadedMasks)
                    maskProvider.SetMask(entry.Key, entry.Value);

                loadedMasks.Clear();
                return true;
            }
            finally
            {
                foreach (var entry in loadedMasks)
                    entry.Value.Dispose();
                UnregisterActiveWorkspaceDirectory(fullDir);
            }
        }

        private static void ApplyFaceMasks(WorkspaceState state, FrameMaskProvider maskProvider)
        {
            foreach (var faceState in state.FaceMasks ?? new List<FaceMaskState>())
            {
                if (faceState == null ||
                    faceState.FrameIndex < 0 ||
                    faceState.Width <= 0 ||
                    faceState.Height <= 0 ||
                    faceState.Faces == null ||
                    faceState.Faces.Count == 0)
                {
                    continue;
                }

                var faces = faceState.Faces
                    .Select(r => new Rect(r.X, r.Y, Math.Max(0.0, r.Width), Math.Max(0.0, r.Height)))
                    .Where(r => r.Width > 0 && r.Height > 0)
                    .ToArray();
                if (faces.Length == 0)
                    continue;

                maskProvider.SetFaceRects(
                    faceState.FrameIndex,
                    faces,
                    new PixelSize(faceState.Width, faceState.Height),
                    faceState.MinConfidence,
                    faceState.Confidences);
            }
        }

        private static WorkspaceSnapshot CreateWorkspaceSnapshot(
            WorkspaceState state,
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode)
        {
            return new WorkspaceSnapshot(
                pathContext.AccessPath,
                mode,
                state.SelectedFrameIndex,
                state.ViewStartSeconds,
                state.SecondsPerScreen,
                state.LastOpened,
                state.AutoResumeIndex,
                state.AutoCompleted,
                state.AutoRunSignature,
                state.AutoExportGateRequired,
                state.AutoExportGatePassed,
                state.AutoExportGateFailure,
                state.AutoExportHybridPolicyAvailable,
                state.AutoExportAllowHybridCopy,
                state.AutoExportHybridDisableReasons,
                state.AutoExecutionSignature,
                state.TimelineExtentSeconds);
        }

        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)
        {
            if (snapshot == null)
                return;
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));

            using SaveLease saveLease = AcquireWorkspaceSaveLease(snapshot.VideoPath);
            using FrameMaskProvider.PersistenceSnapshot persistenceSnapshot =
                maskProvider.CreatePersistenceSnapshot();
            SaveWorkspaceSnapshot(snapshot, persistenceSnapshot, saveLease);
        }

        internal void SaveWorkspaceSnapshot(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,
            SaveLease saveLease)
            => SaveWorkspaceCore(snapshot, persistenceSnapshot, saveLease);

        private void SaveWorkspaceCore(
            WorkspaceSnapshot snapshot,
            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,
            SaveLease saveLease)
        {
            if (snapshot == null)
                return;
            if (persistenceSnapshot == null)
                throw new ArgumentNullException(nameof(persistenceSnapshot));
            if (saveLease == null)
                throw new ArgumentNullException(nameof(saveLease));

            WorkspacePathIdentity.PathContext pathContext = saveLease.PathContext;
            string accessPath = pathContext.AccessPath;
            string identity = pathContext.IdentityKey;
            string generation = Guid.NewGuid().ToString("N");
            string dir = GetWorkspaceDir(identity, snapshot.Mode, generation);
            string fullDir = Path.GetFullPath(dir);
            RegisterActiveWorkspaceDirectory(fullDir);
            bool committed = false;
            bool invalidatedByRemoval = false;
            var cleanupStoragePaths = new List<string>();
            var cleanupStorageIdentities = new List<string>();

            try
            {
                // Prepare the payload outside GlobalStateGate. The persistence
                // snapshot owns the lifetime of stored bitmaps until this returns.
                Directory.CreateDirectory(dir);
                var indices = new List<int>(persistenceSnapshot.StoredMasks.Count);
                var indexSet = new HashSet<int>();
                foreach (var entry in persistenceSnapshot.StoredMasks)
                {
                    indices.Add(entry.FrameIndex);
                    indexSet.Add(entry.FrameIndex);
                    string filePath = Path.Combine(dir, $"mask_{entry.FrameIndex}.png");
                    SaveMask(filePath, entry.Bitmap);
                }

                var faceMasks = persistenceSnapshot.FaceMasks
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
                    VideoPath = accessPath,
                    PathIdentity = identity,
                    Mode = snapshot.Mode.ToString(),
                    StoragePathVersion = CurrentWorkspaceStoragePathVersion,
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

                // Commit only the small in-memory/state-file portion while the
                // process-wide state gate is held.
                lock (GlobalStateGate)
                {
                    RefreshStateLocked();
                    if (!IsWorkspaceSaveLeaseCurrentLocked(saveLease))
                    {
                        // The user removed this workspace after the save was requested.
                        // Do not let an older pending save recreate the deleted state.
                        invalidatedByRemoval = true;
                    }
                    else
                    {
                        WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>
                            WorkspaceStateMatchesPath(w, pathContext) &&
                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));

                        if (previousState != null)
                        {
                            cleanupStoragePaths.Add(previousState.VideoPath);
                            if (!string.IsNullOrWhiteSpace(previousState.PathIdentity))
                                cleanupStorageIdentities.Add(previousState.PathIdentity);
                        }

                        AppState? backupBeforeCommit = TryLoadStateFile(_stateBackupFile);
                        if (backupBeforeCommit != null)
                        {
                            var matchingBackupWorkspaces = backupBeforeCommit.Workspaces
                                .Where(w => WorkspaceStateMatchesPath(w, pathContext))
                                .ToList();
                            cleanupStoragePaths.AddRange(
                                matchingBackupWorkspaces.Select(w => w.VideoPath));
                            cleanupStorageIdentities.AddRange(
                                matchingBackupWorkspaces
                                    .Select(w => w.PathIdentity)
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Select(value => value!));
                        }

                        _state.Workspaces.RemoveAll(w =>
                            WorkspaceStateMatchesPath(w, pathContext) &&
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
            }
            catch
            {
                if (!committed)
                {
                    DeleteDirectoryBestEffort(dir);
                }

                throw;
            }
            finally
            {
                UnregisterActiveWorkspaceDirectory(fullDir);
            }

            if (invalidatedByRemoval)
            {
                DeleteDirectoryBestEffort(dir);
                // Removal may have deferred base cleanup while this generation was
                // reserved. Retry after unregistering the stale preparation directory.
                TryDeleteWorkspaceBaseDirectory(pathContext.AccessPath);
                return;
            }

            CleanupUnreferencedWorkspaceDirectories(pathContext, snapshot.Mode);
            foreach (string storagePath in cleanupStoragePaths.Distinct(StringComparer.Ordinal))
                TryDeleteWorkspaceBaseDirectory(storagePath);
            foreach (string storageIdentity in cleanupStorageIdentities.Distinct(StringComparer.Ordinal))
                TryDeleteWorkspaceBaseDirectoryByIdentity(pathContext, storageIdentity);
        }

        private string GetWorkspaceBaseDir(string pathIdentity)
        {
            string hash = HashIdentity(pathIdentity);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private string GetV2WorkspaceBaseDir(string videoPath)
        {
            string hash = V2HashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private string GetPreIdentityWorkspaceBaseDir(string videoPath)
        {
            string hash = PreIdentityHashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private string GetLegacyWorkspaceBaseDir(string videoPath)
        {
            string hash = LegacyHashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private static string GetWorkspaceDirectoryName(
            WorkspaceMode mode,
            string? storageGeneration)
            => string.IsNullOrWhiteSpace(storageGeneration)
                ? mode.ToString()
                : $"{mode}-{storageGeneration}";

        private string GetWorkspaceDir(
            string pathIdentity,
            WorkspaceMode mode,
            string? storageGeneration = null)
            => Path.Combine(
                GetWorkspaceBaseDir(pathIdentity),
                GetWorkspaceDirectoryName(mode, storageGeneration));

        private string GetV2WorkspaceDir(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration)
            => Path.Combine(
                GetV2WorkspaceBaseDir(videoPath),
                GetWorkspaceDirectoryName(mode, storageGeneration));

        private string GetPreIdentityWorkspaceDir(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration)
            => Path.Combine(
                GetPreIdentityWorkspaceBaseDir(videoPath),
                GetWorkspaceDirectoryName(mode, storageGeneration));

        private string GetLegacyWorkspaceDir(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration)
            => Path.Combine(
                GetLegacyWorkspaceBaseDir(videoPath),
                GetWorkspaceDirectoryName(mode, storageGeneration));

        private string ResolveWorkspaceDirForRead(
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode,
            WorkspaceState state)
        {
            List<string> candidates = GetWorkspaceDirectoryCandidates(
                pathContext,
                mode,
                state);

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return candidates[0];
        }

        private List<string> GetWorkspaceDirectoryCandidates(
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode,
            WorkspaceState state)
        {
            var candidates = new List<string>
            {
                // v4 current policy identity is always first.
                GetWorkspaceDir(pathContext.IdentityKey, mode, state.StorageGeneration)
            };

            // Storage-v3 used entry display casing. Prefer the identity persisted in
            // the state so a case-only rename can still find the old payload.
            if (state.StoragePathVersion == 3 &&
                !string.IsNullOrWhiteSpace(state.PathIdentity))
            {
                candidates.Add(GetWorkspaceDir(
                    state.PathIdentity,
                    mode,
                    state.StorageGeneration));
            }
            candidates.Add(GetWorkspaceDir(
                pathContext.V3IdentityKey,
                mode,
                state.StorageGeneration));

            // v4 -> v3 -> f7bbac4 v2 -> pre-identity -> legacy.
            candidates.Add(GetV2WorkspaceDir(
                state.VideoPath,
                mode,
                state.StorageGeneration));
            if (!string.Equals(
                    state.VideoPath,
                    pathContext.AccessPath,
                    StringComparison.Ordinal))
            {
                candidates.Add(GetV2WorkspaceDir(
                    pathContext.AccessPath,
                    mode,
                    state.StorageGeneration));
            }
            candidates.Add(GetPreIdentityWorkspaceDir(
                state.VideoPath,
                mode,
                state.StorageGeneration));
            candidates.Add(GetLegacyWorkspaceDir(
                state.VideoPath,
                mode,
                state.StorageGeneration));

            return candidates
                .Select(Path.GetFullPath)
                .Distinct(FilePathComparer)
                .ToList();
        }

        private List<string> GetWorkspaceBaseDirectoryCandidates(
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceState state)
        {
            var candidates = new List<string>();

            // Reference checks protect only storage locations that the persisted
            // state can authoritatively own. Read fallbacks must not keep old bases
            // alive after a successful migration and backup advance.
            if (state.StoragePathVersion >= CurrentWorkspaceStoragePathVersion &&
                !string.IsNullOrWhiteSpace(state.PathIdentity))
            {
                candidates.Add(GetWorkspaceBaseDir(state.PathIdentity));
            }
            else if (state.StoragePathVersion == 3 &&
                !string.IsNullOrWhiteSpace(state.PathIdentity))
            {
                candidates.Add(GetWorkspaceBaseDir(state.PathIdentity));
            }
            else if (state.StoragePathVersion >= 2)
            {
                candidates.Add(GetV2WorkspaceBaseDir(state.VideoPath));
            }
            else
            {
                candidates.Add(GetPreIdentityWorkspaceBaseDir(state.VideoPath));
                candidates.Add(GetLegacyWorkspaceBaseDir(state.VideoPath));
            }

            return candidates
                .Select(Path.GetFullPath)
                .Distinct(FilePathComparer)
                .ToList();
        }

        private static void SaveMask(string path, WriteableBitmap mask)
        {
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            mask.Save(stream);
        }

        private static WriteableBitmap? LoadMask(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                    return null;

                using var bitmap = new Bitmap(path);
                var wb = new WriteableBitmap(
                    bitmap.PixelSize,
                    bitmap.Dpi,
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using var fb = wb.Lock();
                int stride = fb.RowBytes;
                int size = stride * fb.Size.Height;
                bitmap.CopyPixels(new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    fb.Address, size, stride);

                return wb;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WorkspaceStateStore] failed to load mask '{path}': {ex.Message}");
                return null;
            }
        }

        private void RefreshStateLocked()
        {
            _state = LoadState();
        }

        private AppState LoadState()
        {
            var primary = TryLoadStateFile(_stateFile);
            if (primary != null)
                return primary;

            var backup = TryLoadStateFile(_stateBackupFile);
            if (backup != null)
            {
                TryRestoreStateBackup();
                return backup;
            }

            return new AppState();
        }

        private static AppState? TryLoadStateFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppState>(json);
            }
            catch
            {
                return null;
            }
        }

        private void TryRestoreStateBackup()
        {
            if (!File.Exists(_stateBackupFile))
                return;

            string tempPath = CreateUniqueTempPath(_stateFile, "restore");
            try
            {
                Directory.CreateDirectory(_rootDir);
                File.Copy(_stateBackupFile, tempPath, overwrite: true);
                File.Move(tempPath, _stateFile, overwrite: true);
            }
            catch
            {
                // The in-memory backup state is still usable for this process.
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore restore cleanup failures
                }
            }
        }

        private void SaveState()
        {
            Directory.CreateDirectory(_rootDir);
            string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempPath = CreateUniqueTempPath(_stateFile, "state");
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_stateFile))
                    File.Copy(_stateFile, _stateBackupFile, overwrite: true);

                File.Move(tempPath, _stateFile, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        private bool TrySyncBackupToCurrentState()
        {
            if (!File.Exists(_stateFile))
                return false;

            string tempPath = CreateUniqueTempPath(_stateBackupFile, "backup");
            try
            {
                Directory.CreateDirectory(_rootDir);

                using (var source = new FileStream(
                    _stateFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (var destination = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.WriteThrough))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _stateBackupFile, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WorkspaceStateStore] backup sync skipped workspace cleanup: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        private void TryDeleteWorkspaceBaseDirectory(string videoPath)
        {
            WorkspacePathIdentity.PathContext pathContext;
            try
            {
                pathContext = WorkspacePathIdentity.CreatePathContext(videoPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return;
            }

            var candidates = new HashSet<string>(FilePathComparer)
            {
                GetWorkspaceBaseDir(pathContext.IdentityKey),
                GetWorkspaceBaseDir(pathContext.V3IdentityKey),
                GetV2WorkspaceBaseDir(videoPath),
                GetV2WorkspaceBaseDir(pathContext.AccessPath),
                GetPreIdentityWorkspaceBaseDir(videoPath),
                GetLegacyWorkspaceBaseDir(videoPath)
            };

            foreach (string baseDir in candidates)
            {
                string fullBaseDir = Path.GetFullPath(baseDir);
                if (!TryDeleteWorkspaceBaseDirectoryIfUnreferenced(pathContext, fullBaseDir))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WorkspaceStateStore] workspace base cleanup deferred because '{fullBaseDir}' became active or referenced.");
                }
            }
        }

        private void TryDeleteWorkspaceBaseDirectoryByIdentity(
            WorkspacePathIdentity.PathContext pathContext,
            string storageIdentity)
        {
            string fullBaseDir = Path.GetFullPath(GetWorkspaceBaseDir(storageIdentity));
            TryDeleteWorkspaceBaseDirectoryIfUnreferenced(pathContext, fullBaseDir);
        }

        private bool TryDeleteWorkspaceBaseDirectoryIfUnreferenced(
            WorkspacePathIdentity.PathContext pathContext,
            string directory)
        {
            lock (WorkspaceDirectoryGate)
            {
                if (HasActiveWorkspaceDirectoryUnderLocked(directory))
                    return false;

                bool referenced;
                lock (GlobalStateGate)
                {
                    RefreshStateLocked();
                    referenced =
                        IsWorkspaceBaseDirectoryReferencedByState(_state, pathContext, directory) ||
                        IsWorkspaceBaseDirectoryReferencedByState(
                            TryLoadStateFile(_stateBackupFile),
                            pathContext,
                            directory);
                }

                if (referenced)
                    return false;

                if (!Directory.Exists(directory))
                    return true;

                try
                {
                    Directory.Delete(directory, recursive: true);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WorkspaceStateStore] workspace base cleanup deferred: {ex.Message}");
                    return false;
                }
            }
        }

        private bool IsWorkspaceBaseDirectoryReferencedByState(
            AppState? state,
            WorkspacePathIdentity.PathContext pathContext,
            string directory)
        {
            if (state == null)
                return false;

            foreach (var workspace in state.Workspaces)
            {
                if (!WorkspaceStateMatchesPath(workspace, pathContext))
                    continue;

                foreach (string referenced in GetWorkspaceBaseDirectoryCandidates(
                    pathContext,
                    workspace))
                {
                    if (string.Equals(referenced, directory, FilePathComparison))
                        return true;
                }
            }

            return false;
        }

        private void CleanupUnreferencedWorkspaceDirectories(
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode)
        {
            try
            {
                string baseDir = GetWorkspaceBaseDir(pathContext.IdentityKey);
                if (!Directory.Exists(baseDir))
                    return;

                string[] candidates = Directory
                    .EnumerateDirectories(baseDir, $"{mode}-*")
                    .ToArray();
                string legacyDir = GetWorkspaceDir(pathContext.IdentityKey, mode);
                if (Directory.Exists(legacyDir))
                    candidates = candidates.Append(legacyDir).ToArray();

                foreach (string candidate in candidates)
                {
                    string fullCandidate = Path.GetFullPath(candidate);
                    TryDeleteWorkspaceDirectoryIfUnreferenced(
                        pathContext,
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
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode,
            string directory)
        {
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
                            pathContext,
                            mode,
                            directory) ||
                        IsWorkspaceDirectoryReferencedByState(
                            TryLoadStateFile(_stateBackupFile),
                            pathContext,
                            mode,
                            directory);
                }

                if (referenced)
                    return false;

                if (!Directory.Exists(directory))
                    return true;

                try
                {
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

        private bool IsWorkspaceDirectoryReferencedByState(
            AppState? state,
            WorkspacePathIdentity.PathContext pathContext,
            WorkspaceMode mode,
            string directory)
        {
            if (state == null)
                return false;

            foreach (var workspace in state.Workspaces)
            {
                if (!WorkspaceStateMatchesPath(workspace, pathContext) ||
                    !string.Equals(workspace.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string referenced in GetWorkspaceDirectoryCandidates(
                    pathContext,
                    mode,
                    workspace))
                {
                    if (string.Equals(referenced, directory, FilePathComparison))
                        return true;
                }
            }

            return false;
        }

        private static void RegisterActiveWorkspaceDirectory(string directory)
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

        private static bool TryDeleteDirectoryIfInactive(string directory)
        {
            lock (WorkspaceDirectoryGate)
            {
                if (HasActiveWorkspaceDirectoryUnderLocked(directory))
                    return false;

                if (!Directory.Exists(directory))
                    return true;

                try
                {
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

        private static bool HasActiveWorkspaceDirectoryUnderLocked(string directory)
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

        private static void DeleteDirectoryBestEffort(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Keep the failed generation for diagnostics if cleanup itself fails.
            }
        }

        private static string CreateUniqueTempPath(string targetPath, string purpose)
            => $"{targetPath}.{Environment.ProcessId}.{purpose}.{Guid.NewGuid():N}.tmp";

        private static string HashIdentity(string identity)
        {
            using var sha1 = SHA1.Create();
            byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string V2HashPath(string value)
            => HashIdentity(WorkspacePathIdentity.CreateV2IdentityKey(value));

        private static string PreIdentityHashPath(string value)
        {
            string identity = OperatingSystem.IsWindows()
                ? value.ToLowerInvariant()
                : value;
            return HashIdentity(identity);
        }

        private static string LegacyHashPath(string value)
            => HashIdentity(value.ToLowerInvariant());

        private sealed class WorkspacePathLifetimeState
        {
            internal long Epoch { get; set; }
            internal int ActiveSaveCount { get; set; }
            internal bool Removed { get; set; }
        }

        internal sealed class SaveLease : IDisposable
        {
            private int _disposed;

            internal SaveLease(
                WorkspacePathIdentity.PathContext pathContext,
                long epoch)
            {
                PathContext = pathContext;
                Epoch = epoch;
            }

            internal WorkspacePathIdentity.PathContext PathContext { get; }
            internal string PathIdentity => PathContext.IdentityKey;
            internal long Epoch { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    ReleaseWorkspaceSaveLease(PathIdentity);
            }
        }

        private sealed class AppState
        {
            public List<RecentItemState> Recents { get; set; } = new();
            public List<WorkspaceState> Workspaces { get; set; } = new();
            public AutoSettingsState? AutoSettings { get; set; }
        }

        private sealed class RecentItemState
        {
            public string Title { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public DateTimeOffset LastOpened { get; set; }
        }

        private sealed class WorkspaceState
        {
            // VideoPath is an access path; PathIdentity is compare/cache/hash-only.
            public string VideoPath { get; set; } = string.Empty;
            public string? PathIdentity { get; set; }
            public string Mode { get; set; } = string.Empty;
            public int StoragePathVersion { get; set; }
            public string? StorageGeneration { get; set; }
            public int SelectedFrameIndex { get; set; }
            public double ViewStartSeconds { get; set; }
            public double SecondsPerScreen { get; set; }
            public double TimelineExtentSeconds { get; set; }
            public DateTimeOffset LastOpened { get; set; }
            public List<int> MaskIndices { get; set; } = new();
            public List<FaceMaskState> FaceMasks { get; set; } = new();
            public int AutoResumeIndex { get; set; }
            public bool AutoCompleted { get; set; }
            public string? AutoRunSignature { get; set; }
            public string? AutoExecutionSignature { get; set; }
            public bool AutoExportGateRequired { get; set; }
            public bool AutoExportGatePassed { get; set; }
            public string? AutoExportGateFailure { get; set; }
            public bool AutoExportHybridPolicyAvailable { get; set; }
            public bool AutoExportAllowHybridCopy { get; set; }
            public string? AutoExportHybridDisableReasons { get; set; }
        }

        private sealed class FaceMaskState
        {
            public int FrameIndex { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public float? MinConfidence { get; set; }
            public List<RectState> Faces { get; set; } = new();
            public List<float> Confidences { get; set; } = new();
        }

        private sealed class RectState
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }
    }

    public sealed class AutoSettingsState
    {
        public int SettingsVersion { get; set; }
        public int ProcessingMode { get; set; } = (int)AutoMaskProcessingMode.Tracked;
        public double DownscaleRatio { get; set; } = 1.0;
        public int DownscaleQuality { get; set; } = (int)FaceShield.Services.Analysis.DownscaleQuality.BalancedBilinear;
        public bool AutoTrackingEnabled { get; set; } = true;
        public int AutoDetectEveryNFrames { get; set; } = 1;
        public int ParallelSessionCount { get; set; } = 2;
        public bool AutoUseOrtOptimization { get; set; } = true;
        public bool AutoUseGpu { get; set; }
        public bool AutoYoloEnableCoreMl { get; set; } = false;
        public int? OrtThreads { get; set; }
        public bool AutoExportAfter { get; set; } = true;
        public double? DetectionThreshold { get; set; }
        public double? ConfidenceThreshold { get; set; }
        public double? NmsThreshold { get; set; }
        public int? BlurRadius { get; set; }
        public int DetectorBackend { get; set; }
        public int YoloModelType { get; set; }
        public string? YoloModelPath { get; set; }
        public double? YoloObjectnessThreshold { get; set; }
        public double? YoloConfidenceThreshold { get; set; }
        public double? YoloNmsThreshold { get; set; }
        public int? YoloInputSize { get; set; }
        public bool YoloUseTiling { get; set; }
        public bool YoloTileOnly { get; set; }
        public int? YoloTileColumns { get; set; }
        public int? YoloTileRows { get; set; }
        public double? YoloTileOverlapRatio { get; set; }
        public string? YoloV8ModelPath { get; set; }
        public double? YoloV8ObjectnessThreshold { get; set; }
        public double? YoloV8ConfidenceThreshold { get; set; }
        public double? YoloV8NmsThreshold { get; set; }
        public int? YoloV8InputSize { get; set; }
        public bool? YoloV8UseTiling { get; set; }
        public bool? YoloV8TileOnly { get; set; }
        public int? YoloV8TileColumns { get; set; }
        public int? YoloV8TileRows { get; set; }
        public double? YoloV8TileOverlapRatio { get; set; }
        public double? YoloV8DownscaleRatio { get; set; }
        public int? YoloV8DownscaleQuality { get; set; }
        public bool? YoloV8AutoTrackingEnabled { get; set; }
        public int? YoloV8AutoDetectEveryNFrames { get; set; }
        public int? YoloV8ParallelSessionCount { get; set; }
        public string? Yolo5ModelPath { get; set; }
        public double? Yolo5ObjectnessThreshold { get; set; }
        public double? Yolo5ConfidenceThreshold { get; set; }
        public double? Yolo5NmsThreshold { get; set; }
        public int? Yolo5InputSize { get; set; }
        public bool? Yolo5UseTiling { get; set; }
        public bool? Yolo5TileOnly { get; set; }
        public int? Yolo5TileColumns { get; set; }
        public int? Yolo5TileRows { get; set; }
        public double? Yolo5TileOverlapRatio { get; set; }
        public double? Yolo5DownscaleRatio { get; set; }
        public int? Yolo5DownscaleQuality { get; set; }
        public bool? Yolo5AutoTrackingEnabled { get; set; }
        public int? Yolo5AutoDetectEveryNFrames { get; set; }
        public int? Yolo5ParallelSessionCount { get; set; }
        public bool EnablePostProcessing { get; set; } = false;
        public bool EnableRoiPostProcess { get; set; } = false;
        public bool EnableYoloWeakIsolatedCleanup { get; set; } = false;
        public bool EnableYoloGapFill { get; set; } = false;
        public bool EnableYoloSceneCutCarryCleanup { get; set; } = false;
        public bool EnableYoloTemporalSmoothing { get; set; } = false;
        public bool EnableYoloRiskCascade { get; set; } = false;
    }

    public sealed class WorkspaceSnapshot
    {
        public string VideoPath { get; }
        public WorkspaceMode Mode { get; }
        public int SelectedFrameIndex { get; }
        public double ViewStartSeconds { get; }
        public double SecondsPerScreen { get; }
        public double TimelineExtentSeconds { get; }
        public DateTimeOffset LastOpened { get; }
        public int AutoResumeIndex { get; }
        public bool AutoCompleted { get; }
        public string? AutoRunSignature { get; }
        public string? AutoExecutionSignature { get; }
        public bool AutoExportGateRequired { get; }
        public bool AutoExportGatePassed { get; }
        public string? AutoExportGateFailure { get; }
        public bool AutoExportHybridPolicyAvailable { get; }
        public bool AutoExportAllowHybridCopy { get; }
        public string? AutoExportHybridDisableReasons { get; }

        public WorkspaceSnapshot(
            string videoPath,
            WorkspaceMode mode,
            int selectedFrameIndex,
            double viewStartSeconds,
            double secondsPerScreen,
            DateTimeOffset lastOpened,
            int autoResumeIndex,
            bool autoCompleted,
            string? autoRunSignature,
            bool autoExportGateRequired,
            bool autoExportGatePassed,
            string? autoExportGateFailure,
            bool autoExportHybridPolicyAvailable,
            bool autoExportAllowHybridCopy,
            string? autoExportHybridDisableReasons,
            string? autoExecutionSignature = null,
            double timelineExtentSeconds = 0)
        {
            VideoPath = videoPath;
            Mode = mode;
            SelectedFrameIndex = selectedFrameIndex;
            ViewStartSeconds = viewStartSeconds;
            SecondsPerScreen = secondsPerScreen;
            TimelineExtentSeconds = double.IsFinite(timelineExtentSeconds) && timelineExtentSeconds > 0
                ? timelineExtentSeconds
                : 0;
            LastOpened = lastOpened;
            AutoResumeIndex = autoResumeIndex;
            AutoCompleted = autoCompleted;
            AutoRunSignature = autoRunSignature;
            AutoExecutionSignature = autoExecutionSignature;
            AutoExportGateRequired = autoExportGateRequired;
            AutoExportGatePassed = autoExportGatePassed;
            AutoExportGateFailure = autoExportGateFailure;
            AutoExportHybridPolicyAvailable = autoExportHybridPolicyAvailable;
            AutoExportAllowHybridCopy = autoExportAllowHybridCopy;
            AutoExportHybridDisableReasons = autoExportHybridDisableReasons;
        }
    }
}
