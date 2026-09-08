from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 exact match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 regex match, found {count}")
    return updated


identity_path = ROOT / "Services/Workspace/WorkspacePathIdentity.cs"
identity_path.write_text('''using System;\nusing System.IO;\n\nnamespace FaceShield.Services.Workspace\n{\n    /// <summary>\n    /// Defines the single lexical identity used for workspace paths. This deliberately\n    /// does not resolve symbolic links; link-target identity is a separate policy.\n    /// </summary>\n    internal static class WorkspacePathIdentity\n    {\n        internal static StringComparison Comparison =>\n            OperatingSystem.IsWindows()\n                ? StringComparison.OrdinalIgnoreCase\n                : StringComparison.Ordinal;\n\n        internal static StringComparer Comparer =>\n            OperatingSystem.IsWindows()\n                ? StringComparer.OrdinalIgnoreCase\n                : StringComparer.Ordinal;\n\n        internal static string Normalize(string path)\n        {\n            if (string.IsNullOrWhiteSpace(path))\n                throw new ArgumentException("A workspace path is required.", nameof(path));\n\n            string fullPath = Path.GetFullPath(path);\n            fullPath = Path.TrimEndingDirectorySeparator(fullPath);\n\n            // Keep the identity string stable anywhere it is hashed or embedded in a\n            // composite cache key. Non-Windows platforms remain case-sensitive by\n            // policy; filesystem/symlink canonicalization is intentionally out of scope.\n            return OperatingSystem.IsWindows()\n                ? fullPath.ToUpperInvariant()\n                : fullPath;\n        }\n\n        internal static bool Equals(string? left, string? right)\n        {\n            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))\n                return false;\n\n            try\n            {\n                return Comparer.Equals(Normalize(left), Normalize(right));\n            }\n            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)\n            {\n                return false;\n            }\n        }\n    }\n}\n''', encoding="utf-8")

store_path = ROOT / "Services/Workspace/WorkspaceStateStore.cs"
store = store_path.read_text(encoding="utf-8")
store = replace_once(
    store,
    "using System.Text.Json;\n",
    "using System.Text.Json;\nusing System.Threading;\n",
    "store threading using")

store = regex_once(
    store,
    r'''        private static readonly StringComparison FilePathComparison =\n            OperatingSystem\.IsWindows\(\)\n                \? StringComparison\.OrdinalIgnoreCase\n                : StringComparison\.Ordinal;\n        private static readonly StringComparer FilePathComparer =\n            OperatingSystem\.IsWindows\(\)\n                \? StringComparer\.OrdinalIgnoreCase\n                : StringComparer\.Ordinal;\n        private static readonly object GlobalStateGate = new\(\);\n        private static readonly object WorkspaceDirectoryGate = new\(\);\n        // A directory can be reserved by more than one reader/save concurrently\.\n        // Reference counts prevent one load from releasing another load's protection\.\n        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =\n            new\(FilePathComparer\);\n        // Logical deletion epochs are process-local because pending saves are also\n        // process-local\. A save captures the epoch when it is requested and may commit\n        // only if no removal for that video path has happened since then\.\n        private static readonly Dictionary<string, long> WorkspaceRemovalEpochs =\n            new\(FilePathComparer\);''',
    '''        private const int CurrentWorkspaceStoragePathVersion = 2;\n        private static readonly StringComparison FilePathComparison =\n            WorkspacePathIdentity.Comparison;\n        private static readonly StringComparer FilePathComparer =\n            WorkspacePathIdentity.Comparer;\n        private static readonly object GlobalStateGate = new();\n        private static readonly object WorkspaceDirectoryGate = new();\n        // A directory can be reserved by more than one reader/save concurrently.\n        // Reference counts prevent one load from releasing another load's protection.\n        private static readonly Dictionary<string, int> ActiveWorkspacePreparationDirectories =\n            new(FilePathComparer);\n        // Save lifetime state is process-local because pending saves are process-local.\n        // Removed paths keep a tombstone until an explicit reopen. Non-removed entries\n        // are reclaimed as soon as their last save lease is released.\n        private static readonly Dictionary<string, WorkspacePathLifetimeState> WorkspacePathLifetimes =\n            new(FilePathComparer);''',
    "store identity declarations")

store = regex_once(
    store,
    r'''        internal long CaptureWorkspaceRemovalEpoch\(string videoPath\).*?(?=        public void RemoveWorkspacesForPath\(string videoPath\))''',
    '''        internal SaveLease AcquireWorkspaceSaveLease(string videoPath)\n        {\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            lock (GlobalStateGate)\n            {\n                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime))\n                {\n                    lifetime = new WorkspacePathLifetimeState();\n                    WorkspacePathLifetimes.Add(identity, lifetime);\n                }\n\n                if (lifetime.Removed)\n                {\n                    throw new InvalidOperationException(\n                        "The workspace path was removed and must be reopened before it can be saved again.");\n                }\n\n                lifetime.ActiveSaveCount = checked(lifetime.ActiveSaveCount + 1);\n                return new SaveLease(identity, lifetime.Epoch);\n            }\n        }\n\n        internal void ReopenWorkspacePath(string videoPath)\n        {\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            lock (GlobalStateGate)\n            {\n                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime) ||\n                    !lifetime.Removed)\n                {\n                    return;\n                }\n\n                lifetime.Removed = false;\n                if (lifetime.ActiveSaveCount == 0)\n                    WorkspacePathLifetimes.Remove(identity);\n            }\n        }\n\n        internal bool IsWorkspaceSaveLeaseCurrent(SaveLease saveLease)\n        {\n            if (saveLease == null)\n                throw new ArgumentNullException(nameof(saveLease));\n\n            lock (GlobalStateGate)\n                return IsWorkspaceSaveLeaseCurrentLocked(saveLease);\n        }\n\n        private static bool IsWorkspaceSaveLeaseCurrentLocked(SaveLease saveLease)\n            => WorkspacePathLifetimes.TryGetValue(saveLease.PathIdentity, out var lifetime) &&\n               !lifetime.Removed &&\n               lifetime.Epoch == saveLease.Epoch;\n\n        private static void ReleaseWorkspaceSaveLease(string identity)\n        {\n            lock (GlobalStateGate)\n            {\n                if (!WorkspacePathLifetimes.TryGetValue(identity, out var lifetime))\n                    return;\n\n                if (lifetime.ActiveSaveCount <= 0)\n                    throw new InvalidOperationException("Workspace save lease count underflow.");\n\n                lifetime.ActiveSaveCount--;\n                if (lifetime.ActiveSaveCount == 0 && !lifetime.Removed)\n                    WorkspacePathLifetimes.Remove(identity);\n            }\n        }\n\n''',
    "store save lease methods")

store = regex_once(
    store,
    r'''        public void RemoveWorkspacesForPath\(string videoPath\)\n        \{.*?\n        \}\n\n(?=        public bool TryLoadWorkspace)''',
    '''        public void RemoveWorkspacesForPath(string videoPath)\n        {\n            if (string.IsNullOrWhiteSpace(videoPath))\n                return;\n\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            bool deletePayloads;\n            List<string> removedStoragePaths;\n            lock (GlobalStateGate)\n            {\n                RefreshStateLocked();\n                var previousWorkspaces = _state.Workspaces.ToList();\n                bool lifetimeExisted = WorkspacePathLifetimes.TryGetValue(identity, out var lifetime);\n                lifetime ??= new WorkspacePathLifetimeState();\n                if (!lifetimeExisted)\n                    WorkspacePathLifetimes.Add(identity, lifetime);\n\n                long previousEpoch = lifetime.Epoch;\n                bool previousRemoved = lifetime.Removed;\n                removedStoragePaths = _state.Workspaces\n                    .Where(w => WorkspacePathIdentity.Equals(w.VideoPath, identity))\n                    .Select(w => w.VideoPath)\n                    .Distinct(StringComparer.Ordinal)\n                    .ToList();\n\n                try\n                {\n                    // Removal and save-lease validation are linearized by the same\n                    // state gate. Every lease acquired before this point becomes stale.\n                    lifetime.Epoch = checked(previousEpoch + 1);\n                    lifetime.Removed = true;\n\n                    _state.Workspaces.RemoveAll(w =>\n                        WorkspacePathIdentity.Equals(w.VideoPath, identity));\n\n                    SaveState();\n                }\n                catch\n                {\n                    _state.Workspaces = previousWorkspaces;\n                    lifetime.Epoch = previousEpoch;\n                    lifetime.Removed = previousRemoved;\n                    if (!lifetimeExisted &&\n                        lifetime.ActiveSaveCount == 0 &&\n                        !lifetime.Removed)\n                    {\n                        WorkspacePathLifetimes.Remove(identity);\n                    }\n                    throw;\n                }\n\n                // Keep payloads while backup state can still reference them.\n                if (!TrySyncBackupToCurrentState())\n                    return;\n                deletePayloads = true;\n            }\n\n            if (deletePayloads)\n            {\n                foreach (string storagePath in removedStoragePaths\n                    .Append(identity)\n                    .Distinct(StringComparer.Ordinal))\n                {\n                    TryDeleteWorkspaceBaseDirectory(storagePath);\n                }\n            }\n        }\n\n''',
    "store remove method")

store = replace_once(
    store,
    '''            return appState.Workspaces.FirstOrDefault(w =>\n                string.Equals(w.VideoPath, videoPath, FilePathComparison) &&\n                string.Equals(w.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase));''',
    '''            return appState.Workspaces.FirstOrDefault(w =>\n                WorkspacePathIdentity.Equals(w.VideoPath, videoPath) &&\n                string.Equals(w.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase));''',
    "store find workspace identity")

store = replace_once(
    store,
    '''                VideoPath = source.VideoPath,\n                Mode = source.Mode,''',
    '''                VideoPath = source.VideoPath,\n                Mode = source.Mode,\n                StoragePathVersion = source.StoragePathVersion,''',
    "store clone storage version")

store = replace_once(
    store,
    '''            string dir = ResolveWorkspaceDirForRead(\n                videoPath,\n                mode,\n                state.StorageGeneration);''',
    '''            string dir = ResolveWorkspaceDirForRead(\n                videoPath,\n                mode,\n                state);''',
    "store load resolver call")

store = regex_once(
    store,
    r'''        public void SaveWorkspace\(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider\)\n        \{.*?\n        private void SaveWorkspaceCore\(\n            WorkspaceSnapshot snapshot,\n            FrameMaskProvider\.PersistenceSnapshot persistenceSnapshot,\n            long removalEpoch\)\n        \{''',
    '''        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)\n        {\n            if (snapshot == null)\n                return;\n            if (maskProvider == null)\n                throw new ArgumentNullException(nameof(maskProvider));\n\n            using SaveLease saveLease = AcquireWorkspaceSaveLease(snapshot.VideoPath);\n            using FrameMaskProvider.PersistenceSnapshot persistenceSnapshot =\n                maskProvider.CreatePersistenceSnapshot();\n            SaveWorkspaceSnapshot(snapshot, persistenceSnapshot, saveLease);\n        }\n\n        internal void SaveWorkspaceSnapshot(\n            WorkspaceSnapshot snapshot,\n            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,\n            SaveLease saveLease)\n            => SaveWorkspaceCore(snapshot, persistenceSnapshot, saveLease);\n\n        private void SaveWorkspaceCore(\n            WorkspaceSnapshot snapshot,\n            FrameMaskProvider.PersistenceSnapshot persistenceSnapshot,\n            SaveLease saveLease)\n        {''',
    "store save signatures")

store = replace_once(
    store,
    '''            if (persistenceSnapshot == null)\n                throw new ArgumentNullException(nameof(persistenceSnapshot));\n\n            string generation = Guid.NewGuid().ToString("N");\n            string dir = GetWorkspaceDir(snapshot.VideoPath, snapshot.Mode, generation);''',
    '''            if (persistenceSnapshot == null)\n                throw new ArgumentNullException(nameof(persistenceSnapshot));\n            if (saveLease == null)\n                throw new ArgumentNullException(nameof(saveLease));\n\n            string identity = saveLease.PathIdentity;\n            string generation = Guid.NewGuid().ToString("N");\n            string dir = GetWorkspaceDir(identity, snapshot.Mode, generation);''',
    "store save identity")

store = replace_once(
    store,
    '''            bool committed = false;\n            bool invalidatedByRemoval = false;''',
    '''            bool committed = false;\n            bool invalidatedByRemoval = false;\n            var cleanupStoragePaths = new List<string>();''',
    "store cleanup path list")

store = replace_once(
    store,
    '''                    VideoPath = snapshot.VideoPath,\n                    Mode = snapshot.Mode.ToString(),\n                    StorageGeneration = generation,''',
    '''                    VideoPath = identity,\n                    Mode = snapshot.Mode.ToString(),\n                    StoragePathVersion = CurrentWorkspaceStoragePathVersion,\n                    StorageGeneration = generation,''',
    "store new state identity")

store = replace_once(
    store,
    '''                    if (GetWorkspaceRemovalEpochLocked(snapshot.VideoPath) != removalEpoch)''',
    '''                    if (!IsWorkspaceSaveLeaseCurrentLocked(saveLease))''',
    "store lease commit check")

store = replace_once(
    store,
    '''                        WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>\n                            string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&\n                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));\n\n                        _state.Workspaces.RemoveAll(w =>\n                            string.Equals(w.VideoPath, snapshot.VideoPath, FilePathComparison) &&\n                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));''',
    '''                        WorkspaceState? previousState = _state.Workspaces.FirstOrDefault(w =>\n                            WorkspacePathIdentity.Equals(w.VideoPath, identity) &&\n                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));\n\n                        if (previousState != null)\n                            cleanupStoragePaths.Add(previousState.VideoPath);\n\n                        AppState? backupBeforeCommit = TryLoadStateFile(_stateBackupFile);\n                        if (backupBeforeCommit != null)\n                        {\n                            cleanupStoragePaths.AddRange(backupBeforeCommit.Workspaces\n                                .Where(w => WorkspacePathIdentity.Equals(w.VideoPath, identity))\n                                .Select(w => w.VideoPath));\n                        }\n\n                        _state.Workspaces.RemoveAll(w =>\n                            WorkspacePathIdentity.Equals(w.VideoPath, identity) &&\n                            string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));''',
    "store save state identity")

store = replace_once(
    store,
    '''                TryDeleteWorkspaceBaseDirectory(snapshot.VideoPath);\n                return;\n            }\n\n            CleanupUnreferencedWorkspaceDirectories(snapshot.VideoPath, snapshot.Mode);''',
    '''                TryDeleteWorkspaceBaseDirectory(identity);\n                return;\n            }\n\n            CleanupUnreferencedWorkspaceDirectories(identity, snapshot.Mode);\n            foreach (string storagePath in cleanupStoragePaths.Distinct(StringComparer.Ordinal))\n                TryDeleteWorkspaceBaseDirectory(storagePath);''',
    "store post save cleanup")

store = regex_once(
    store,
    r'''        private string GetWorkspaceBaseDir\(string videoPath\).*?(?=        private static void SaveMask)''',
    '''        private string GetWorkspaceBaseDir(string videoPath)\n        {\n            string hash = HashPath(videoPath);\n            return Path.Combine(_rootDir, "workspaces", hash);\n        }\n\n        private string GetPreIdentityWorkspaceBaseDir(string videoPath)\n        {\n            string hash = PreIdentityHashPath(videoPath);\n            return Path.Combine(_rootDir, "workspaces", hash);\n        }\n\n        private string GetLegacyWorkspaceBaseDir(string videoPath)\n        {\n            string hash = LegacyHashPath(videoPath);\n            return Path.Combine(_rootDir, "workspaces", hash);\n        }\n\n        private static string GetWorkspaceDirectoryName(\n            WorkspaceMode mode,\n            string? storageGeneration)\n            => string.IsNullOrWhiteSpace(storageGeneration)\n                ? mode.ToString()\n                : $"{mode}-{storageGeneration}";\n\n        private string GetWorkspaceDir(\n            string videoPath,\n            WorkspaceMode mode,\n            string? storageGeneration = null)\n            => Path.Combine(\n                GetWorkspaceBaseDir(videoPath),\n                GetWorkspaceDirectoryName(mode, storageGeneration));\n\n        private string GetPreIdentityWorkspaceDir(\n            string videoPath,\n            WorkspaceMode mode,\n            string? storageGeneration)\n            => Path.Combine(\n                GetPreIdentityWorkspaceBaseDir(videoPath),\n                GetWorkspaceDirectoryName(mode, storageGeneration));\n\n        private string GetLegacyWorkspaceDir(\n            string videoPath,\n            WorkspaceMode mode,\n            string? storageGeneration)\n            => Path.Combine(\n                GetLegacyWorkspaceBaseDir(videoPath),\n                GetWorkspaceDirectoryName(mode, storageGeneration));\n\n        private string ResolveWorkspaceDirForRead(\n            string videoPath,\n            WorkspaceMode mode,\n            WorkspaceState state)\n        {\n            List<string> candidates = GetWorkspaceDirectoryCandidates(\n                videoPath,\n                mode,\n                state);\n\n            foreach (string candidate in candidates)\n            {\n                if (Directory.Exists(candidate))\n                    return candidate;\n            }\n\n            return candidates[0];\n        }\n\n        private List<string> GetWorkspaceDirectoryCandidates(\n            string videoPath,\n            WorkspaceMode mode,\n            WorkspaceState state)\n        {\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            var candidates = new List<string>();\n\n            if (state.StoragePathVersion >= CurrentWorkspaceStoragePathVersion)\n            {\n                candidates.Add(GetWorkspaceDir(identity, mode, state.StorageGeneration));\n            }\n            else\n            {\n                candidates.Add(GetPreIdentityWorkspaceDir(\n                    state.VideoPath,\n                    mode,\n                    state.StorageGeneration));\n                candidates.Add(GetLegacyWorkspaceDir(\n                    state.VideoPath,\n                    mode,\n                    state.StorageGeneration));\n                candidates.Add(GetWorkspaceDir(identity, mode, state.StorageGeneration));\n            }\n\n            return candidates\n                .Select(Path.GetFullPath)\n                .Distinct(FilePathComparer)\n                .ToList();\n        }\n\n        private List<string> GetWorkspaceBaseDirectoryCandidates(\n            string videoPath,\n            WorkspaceState state)\n        {\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            var candidates = new List<string>();\n\n            if (state.StoragePathVersion >= CurrentWorkspaceStoragePathVersion)\n            {\n                candidates.Add(GetWorkspaceBaseDir(identity));\n            }\n            else\n            {\n                candidates.Add(GetPreIdentityWorkspaceBaseDir(state.VideoPath));\n                candidates.Add(GetLegacyWorkspaceBaseDir(state.VideoPath));\n                candidates.Add(GetWorkspaceBaseDir(identity));\n            }\n\n            return candidates\n                .Select(Path.GetFullPath)\n                .Distinct(FilePathComparer)\n                .ToList();\n        }\n\n''',
    "store workspace directory helpers")

store = replace_once(
    store,
    '''            var candidates = new HashSet<string>(FilePathComparer)\n            {\n                GetWorkspaceBaseDir(videoPath),\n                GetLegacyWorkspaceBaseDir(videoPath)\n            };''',
    '''            var candidates = new HashSet<string>(FilePathComparer)\n            {\n                GetWorkspaceBaseDir(videoPath),\n                GetPreIdentityWorkspaceBaseDir(videoPath),\n                GetLegacyWorkspaceBaseDir(videoPath)\n            };''',
    "store base cleanup candidates")

store = replace_once(
    store,
    '''                    referenced =\n                        IsWorkspacePathReferencedByState(_state, videoPath) ||\n                        IsWorkspacePathReferencedByState(\n                            TryLoadStateFile(_stateBackupFile),\n                            videoPath);''',
    '''                    referenced =\n                        IsWorkspaceBaseDirectoryReferencedByState(_state, videoPath, directory) ||\n                        IsWorkspaceBaseDirectoryReferencedByState(\n                            TryLoadStateFile(_stateBackupFile),\n                            videoPath,\n                            directory);''',
    "store base reference check")

store = regex_once(
    store,
    r'''        private bool IsWorkspacePathReferencedByState\(\n            AppState\? state,\n            string videoPath\)\n        \{.*?\n        \}\n\n(?=        private void CleanupUnreferencedWorkspaceDirectories)''',
    '''        private bool IsWorkspaceBaseDirectoryReferencedByState(\n            AppState? state,\n            string videoPath,\n            string directory)\n        {\n            if (state == null)\n                return false;\n\n            foreach (var workspace in state.Workspaces)\n            {\n                if (!WorkspacePathIdentity.Equals(workspace.VideoPath, videoPath))\n                    continue;\n\n                foreach (string referenced in GetWorkspaceBaseDirectoryCandidates(\n                    videoPath,\n                    workspace))\n                {\n                    if (string.Equals(referenced, directory, FilePathComparison))\n                        return true;\n                }\n            }\n\n            return false;\n        }\n\n''',
    "store base reference method")

store = regex_once(
    store,
    r'''        private bool IsWorkspaceDirectoryReferencedByState\(\n            AppState\? state,\n            string videoPath,\n            WorkspaceMode mode,\n            string directory\)\n        \{.*?\n        \}\n\n(?=        private static void RegisterActiveWorkspaceDirectory)''',
    '''        private bool IsWorkspaceDirectoryReferencedByState(\n            AppState? state,\n            string videoPath,\n            WorkspaceMode mode,\n            string directory)\n        {\n            if (state == null)\n                return false;\n\n            foreach (var workspace in state.Workspaces)\n            {\n                if (!WorkspacePathIdentity.Equals(workspace.VideoPath, videoPath) ||\n                    !string.Equals(workspace.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase))\n                {\n                    continue;\n                }\n\n                foreach (string referenced in GetWorkspaceDirectoryCandidates(\n                    videoPath,\n                    mode,\n                    workspace))\n                {\n                    if (string.Equals(referenced, directory, FilePathComparison))\n                        return true;\n                }\n            }\n\n            return false;\n        }\n\n''',
    "store directory reference method")

store = regex_once(
    store,
    r'''        private static string HashPath\(string value\)\n        \{.*?\n        \}\n\n        private static string LegacyHashPath''',
    '''        private static string HashPath(string value)\n        {\n            string identity = WorkspacePathIdentity.Normalize(value);\n            using var sha1 = SHA1.Create();\n            byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(identity));\n            return Convert.ToHexString(bytes).ToLowerInvariant();\n        }\n\n        private static string PreIdentityHashPath(string value)\n        {\n            string identity = OperatingSystem.IsWindows()\n                ? value.ToLowerInvariant()\n                : value;\n\n            using var sha1 = SHA1.Create();\n            byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(identity));\n            return Convert.ToHexString(bytes).ToLowerInvariant();\n        }\n\n        private static string LegacyHashPath''',
    "store hash migration")

store = replace_once(
    store,
    '''        private sealed class AppState\n        {''',
    '''        private sealed class WorkspacePathLifetimeState\n        {\n            internal long Epoch { get; set; }\n            internal int ActiveSaveCount { get; set; }\n            internal bool Removed { get; set; }\n        }\n\n        internal sealed class SaveLease : IDisposable\n        {\n            private int _disposed;\n\n            internal SaveLease(string pathIdentity, long epoch)\n            {\n                PathIdentity = pathIdentity;\n                Epoch = epoch;\n            }\n\n            internal string PathIdentity { get; }\n            internal long Epoch { get; }\n\n            public void Dispose()\n            {\n                if (Interlocked.Exchange(ref _disposed, 1) == 0)\n                    ReleaseWorkspaceSaveLease(PathIdentity);\n            }\n        }\n\n        private sealed class AppState\n        {''',
    "store lifetime classes")

store = replace_once(
    store,
    '''            public string VideoPath { get; set; } = string.Empty;\n            public string Mode { get; set; } = string.Empty;\n            public string? StorageGeneration { get; set; }''',
    '''            public string VideoPath { get; set; } = string.Empty;\n            public string Mode { get; set; } = string.Empty;\n            public int StoragePathVersion { get; set; }\n            public string? StorageGeneration { get; set; }''',
    "store state storage version")

# Ensure the old divergent removal comparer and epoch dictionary are gone.
for forbidden in [
    "WorkspaceRemovalEpochs",
    "CaptureWorkspaceRemovalEpoch",
    "GetWorkspaceRemovalEpochLocked",
    "StringComparison.OrdinalIgnoreCase));\n\n                    // Commit the reference removal first",
]:
    if forbidden in store:
        raise RuntimeError(f"store still contains forbidden pattern: {forbidden}")

store_path.write_text(store, encoding="utf-8")

coord_path = ROOT / "Services/Workspace/WorkspacePersistenceCoordinator.cs"
coord = coord_path.read_text(encoding="utf-8")
coord = replace_once(
    coord,
    '''            ThrowIfQueueClosed();\n            long removalEpoch = _store.CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);\n            FrameMaskProvider.PersistenceSnapshot maskSnapshot =\n                _maskProvider.CreatePersistenceSnapshot();''',
    '''            ThrowIfQueueClosed();\n            WorkspaceStateStore.SaveLease saveLease =\n                _store.AcquireWorkspaceSaveLease(snapshot.VideoPath);\n            FrameMaskProvider.PersistenceSnapshot maskSnapshot;\n            try\n            {\n                maskSnapshot = _maskProvider.CreatePersistenceSnapshot();\n            }\n            catch\n            {\n                saveLease.Dispose();\n                throw;\n            }''',
    "coordinator queue lease acquire")

coord = replace_once(
    coord,
    '''                    maskSnapshot.Dispose();\n                    if (_disposed)''',
    '''                    maskSnapshot.Dispose();\n                    saveLease.Dispose();\n                    if (_disposed)''',
    "coordinator queue rejection lease cleanup")

coord = replace_once(
    coord,
    '''                    snapshot,\n                    maskSnapshot,\n                    removalEpoch);''',
    '''                    snapshot,\n                    maskSnapshot,\n                    saveLease);''',
    "coordinator pending lease")

coord = replace_once(
    coord,
    '''                        pending.Snapshot,\n                        pending.MaskSnapshot,\n                        pending.RemovalEpoch)).ConfigureAwait(false);''',
    '''                        pending.Snapshot,\n                        pending.MaskSnapshot,\n                        pending.SaveLease)).ConfigureAwait(false);''',
    "coordinator worker lease")

coord = replace_once(
    coord,
    '''                try\n                {\n                    pending.MaskSnapshot.Dispose();\n                }\n                catch (Exception ex)\n                {\n                    failure ??= ex;\n                }\n\n                if (failure == null)''',
    '''                try\n                {\n                    pending.MaskSnapshot.Dispose();\n                }\n                catch (Exception ex)\n                {\n                    failure ??= ex;\n                }\n\n                try\n                {\n                    pending.SaveLease.Dispose();\n                }\n                catch (Exception ex)\n                {\n                    failure ??= ex;\n                }\n\n                if (failure == null)''',
    "coordinator worker lease release")

coord = replace_once(
    coord,
    '''            long removalEpoch = _store.CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);\n            Task predecessor = Task.CompletedTask;''',
    '''            Task predecessor = Task.CompletedTask;''',
    "coordinator SaveNow old epoch")

coord = replace_once(
    coord,
    '''                FrameMaskProvider.PersistenceSnapshot? maskSnapshot = null;\n                try\n                {\n                    // Metadata/lease capture may be proportional to the number of masks.\n                    // Keep it outside _taskGate after the final boundary is published.\n                    maskSnapshot = _maskProvider.CreatePersistenceSnapshot();\n                    var pending = new PendingSave(\n                        finalRequestId,\n                        snapshot,\n                        maskSnapshot,\n                        removalEpoch,\n                        finalCompletion!);\n                    maskSnapshot = null; // PendingSave owns the persistence lease.\n                    _ = ExecutePendingSaveAsync(pending, predecessor);\n                }\n                catch (Exception ex)\n                {\n                    try\n                    {\n                        maskSnapshot?.Dispose();\n                    }\n                    catch\n                    {\n                        // Preserve the snapshot-capture failure as the terminal error.\n                    }\n\n                    finalCompletion!.TrySetException(ex);\n                }''',
    '''                WorkspaceStateStore.SaveLease? saveLease = null;\n                FrameMaskProvider.PersistenceSnapshot? maskSnapshot = null;\n                try\n                {\n                    // The save lease starts before snapshot capture and remains owned by\n                    // PendingSave through commit, stale skip, or failure.\n                    saveLease = _store.AcquireWorkspaceSaveLease(snapshot.VideoPath);\n                    maskSnapshot = _maskProvider.CreatePersistenceSnapshot();\n                    var pending = new PendingSave(\n                        finalRequestId,\n                        snapshot,\n                        maskSnapshot,\n                        saveLease,\n                        finalCompletion!);\n                    maskSnapshot = null;\n                    saveLease = null;\n                    _ = ExecutePendingSaveAsync(pending, predecessor);\n                }\n                catch (Exception ex)\n                {\n                    try\n                    {\n                        maskSnapshot?.Dispose();\n                        saveLease?.Dispose();\n                    }\n                    catch\n                    {\n                        // Preserve the lease/snapshot-capture failure as the terminal error.\n                    }\n\n                    finalCompletion!.TrySetException(ex);\n                }''',
    "coordinator SaveNow lease")

coord = replace_once(
    coord,
    '''                FrameMaskProvider.PersistenceSnapshot maskSnapshot,\n                long removalEpoch,\n                TaskCompletionSource<object?>? completion = null)''',
    '''                FrameMaskProvider.PersistenceSnapshot maskSnapshot,\n                WorkspaceStateStore.SaveLease saveLease,\n                TaskCompletionSource<object?>? completion = null)''',
    "coordinator pending ctor signature")

coord = replace_once(
    coord,
    '''                MaskSnapshot = maskSnapshot;\n                RemovalEpoch = removalEpoch;\n                Completion = completion''',
    '''                MaskSnapshot = maskSnapshot;\n                SaveLease = saveLease;\n                Completion = completion''',
    "coordinator pending assign")

coord = replace_once(
    coord,
    '''            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }\n            internal long RemovalEpoch { get; }\n            internal TaskCompletionSource<object?> Completion { get; }''',
    '''            internal FrameMaskProvider.PersistenceSnapshot MaskSnapshot { get; }\n            internal WorkspaceStateStore.SaveLease SaveLease { get; }\n            internal TaskCompletionSource<object?> Completion { get; }''',
    "coordinator pending property")

if "RemovalEpoch" in coord or "CaptureWorkspaceRemovalEpoch" in coord:
    raise RuntimeError("coordinator still contains epoch-only save ownership")
coord_path.write_text(coord, encoding="utf-8")

home_path = ROOT / "ViewModels/Pages/HomePageViewModel.cs"
home = home_path.read_text(encoding="utf-8")
home = regex_once(
    home,
    r'''        private static readonly StringComparison FilePathComparison =\n            RuntimeInformation\.IsOSPlatform\(OSPlatform\.Windows\)\n                \? StringComparison\.OrdinalIgnoreCase\n                : StringComparison\.Ordinal;\n        private static readonly StringComparer FilePathComparer =\n            RuntimeInformation\.IsOSPlatform\(OSPlatform\.Windows\)\n                \? StringComparer\.OrdinalIgnoreCase\n                : StringComparer\.Ordinal;\n''',
    '''''',
    "home duplicate path policy")

home = replace_once(
    home,
    '''        private readonly Dictionary<string, WorkspaceViewModel> _workspaceCache = new(FilePathComparer);''',
    '''        private readonly Dictionary<string, WorkspaceViewModel> _workspaceCache = new(StringComparer.Ordinal);''',
    "home cache comparer")

home = replace_once(
    home,
    '''            SelectedVideoPath = localPath;\n            TouchRecent(localPath);''',
    '''            SelectedVideoPath = WorkspacePathIdentity.Normalize(localPath);\n            TouchRecent(SelectedVideoPath);''',
    "home picked video normalization")

home = replace_once(
    home,
    '''            string key = $"{mode}:{SelectedVideoPath}";\n            if (_workspaceCache.TryGetValue(key, out var cached))''',
    '''            string workspacePath = WorkspacePathIdentity.Normalize(SelectedVideoPath);\n            string key = $"{mode}:{workspacePath}";\n            if (_workspaceCache.TryGetValue(key, out var cached))''',
    "home normalized cache key")

home = replace_once(
    home,
    '''            var vm = new WorkspaceViewModel(\n                SelectedVideoPath,''',
    '''            // A removed path cannot save again until a fresh workspace instance\n            // explicitly reopens its process-local persistence identity.\n            _stateStore.ReopenWorkspacePath(workspacePath);\n\n            var vm = new WorkspaceViewModel(\n                workspacePath,''',
    "home explicit reopen")

home = regex_once(
    home,
    r'''        private void TouchRecent\(string\? videoPath\)\n        \{.*?\n        \}\n\n(?=        public void PersistAllWorkspaces)''',
    '''        private void TouchRecent(string? videoPath)\n        {\n            if (string.IsNullOrWhiteSpace(videoPath))\n                return;\n\n            string identity = WorkspacePathIdentity.Normalize(videoPath);\n            int existingIndex = -1;\n            for (int i = 0; i < Recents.Count; i++)\n            {\n                if (WorkspacePathIdentity.Equals(Recents[i].Path, identity))\n                {\n                    existingIndex = i;\n                    break;\n                }\n            }\n\n            if (existingIndex >= 0)\n                Recents.RemoveAt(existingIndex);\n\n            Recents.Insert(0, new RecentItem(\n                Path.GetFileName(identity),\n                identity,\n                DateTimeOffset.Now));\n            TrimRecents();\n            _stateStore.SaveRecents(Recents);\n            OnPropertyChanged(nameof(HasRecents));\n            OnPropertyChanged(nameof(HasNoRecents));\n        }\n\n''',
    "home recents identity")

home = replace_once(
    home,
    '''                if (string.Equals(\n                        entry.Value.FrameList.VideoPath,\n                        videoPath,\n                        FilePathComparison))''',
    '''                if (WorkspacePathIdentity.Equals(\n                        entry.Value.FrameList.VideoPath,\n                        videoPath))''',
    "home cache removal identity")

if "FilePathComparison" in home or "FilePathComparer" in home:
    raise RuntimeError("home still contains duplicate path identity policy")
home_path.write_text(home, encoding="utf-8")

print("[WorkspaceIdentityPatch] PASS")
