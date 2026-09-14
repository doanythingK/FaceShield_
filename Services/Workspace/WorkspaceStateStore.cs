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

namespace FaceShield.Services.Workspace
{
    public sealed class WorkspaceStateStore
    {
        private static readonly StringComparison FilePathComparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        private static readonly StringComparer FilePathComparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
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
            _state = LoadState();
        }

        public IReadOnlyList<RecentItem> GetRecents()
        {
            return _state.Recents
                .Select(r => new RecentItem(r.Title, r.Path, r.LastOpened))
                .ToList();
        }

        public AutoSettingsState? GetAutoSettings()
        {
            return _state.AutoSettings;
        }

        public void SaveAutoSettings(AutoSettingsState settings)
        {
            _state.AutoSettings = settings;
            SaveState();
        }

        public void SaveRecents(IEnumerable<RecentItem> recents)
        {
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

        public void RemoveWorkspacesForPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

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

        public bool TryLoadWorkspace(
            string videoPath,
            WorkspaceMode mode,
            FrameMaskProvider maskProvider,
            out WorkspaceSnapshot? snapshot)
        {
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

        private static WorkspaceState? FindWorkspaceState(
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

        private bool TryLoadWorkspacePayload(
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
            WorkspaceMode mode)
        {
            return new WorkspaceSnapshot(
                state.VideoPath,
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
                state.AutoExecutionSignature);
        }

        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)
        {
            if (snapshot == null)
                return;

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
                var entries = maskProvider.GetMaskEntries();
                var indices = new List<int>(entries.Count);
                var indexSet = new HashSet<int>();

                foreach (var entry in entries)
                {
                    indices.Add(entry.Key);
                    indexSet.Add(entry.Key);
                    string filePath = Path.Combine(dir, $"mask_{entry.Key}.png");
                    SaveMask(filePath, entry.Value);
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

        private string GetWorkspaceBaseDir(string videoPath)
        {
            string hash = HashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private string GetWorkspaceDir(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration = null)
        {
            string directoryName = string.IsNullOrWhiteSpace(storageGeneration)
                ? mode.ToString()
                : $"{mode}-{storageGeneration}";
            return Path.Combine(GetWorkspaceBaseDir(videoPath), directoryName);
        }

        private string ResolveWorkspaceDirForRead(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration)
        {
            string current = GetWorkspaceDir(videoPath, mode, storageGeneration);
            if (Directory.Exists(current))
                return current;

            string legacy = GetLegacyWorkspaceDir(videoPath, mode, storageGeneration);
            return Directory.Exists(legacy) ? legacy : current;
        }

        private string GetLegacyWorkspaceDir(
            string videoPath,
            WorkspaceMode mode,
            string? storageGeneration)
        {
            string directoryName = string.IsNullOrWhiteSpace(storageGeneration)
                ? mode.ToString()
                : $"{mode}-{storageGeneration}";
            return Path.Combine(GetLegacyWorkspaceBaseDir(videoPath), directoryName);
        }

        private string GetLegacyWorkspaceBaseDir(string videoPath)
        {
            string hash = LegacyHashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
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

            string tempPath = _stateFile + ".restore";
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

            string tempPath = _stateFile + ".tmp";
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

            string tempPath = _stateBackupFile + ".tmp";
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
            var candidates = new HashSet<string>(FilePathComparer)
            {
                GetWorkspaceBaseDir(videoPath),
                GetLegacyWorkspaceBaseDir(videoPath)
            };

            foreach (string baseDir in candidates)
            {
                if (!Directory.Exists(baseDir))
                    continue;

                try
                {
                    Directory.Delete(baseDir, recursive: true);
                }
                catch (Exception ex)
                {
                    // State and backup no longer reference this directory. Keeping an
                    // orphan is safe and allows a later cleanup attempt.
                    System.Diagnostics.Debug.WriteLine(
                        $"[WorkspaceStateStore] orphan workspace cleanup deferred: {ex.Message}");
                }
            }
        }

        private void CleanupUnreferencedWorkspaceDirectories(string videoPath, WorkspaceMode mode)
        {
            try
            {
                string baseDir = GetWorkspaceBaseDir(videoPath);
                if (!Directory.Exists(baseDir))
                    return;

                var keep = new HashSet<string>(FilePathComparer);

                void AddReferencedDirectories(AppState? appState)
                {
                    if (appState == null)
                        return;

                    foreach (var workspace in appState.Workspaces)
                    {
                        if (!string.Equals(workspace.VideoPath, videoPath, FilePathComparison) ||
                            !string.Equals(workspace.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        keep.Add(Path.GetFullPath(GetWorkspaceDir(
                            videoPath,
                            mode,
                            workspace.StorageGeneration)));
                    }
                }

                AddReferencedDirectories(_state);
                AddReferencedDirectories(TryLoadStateFile(_stateBackupFile));

                foreach (string candidate in Directory.EnumerateDirectories(baseDir, $"{mode}-*"))
                {
                    if (!keep.Contains(Path.GetFullPath(candidate)))
                        Directory.Delete(candidate, recursive: true);
                }

                string legacyDir = GetWorkspaceDir(videoPath, mode);
                if (Directory.Exists(legacyDir) &&
                    !keep.Contains(Path.GetFullPath(legacyDir)))
                {
                    Directory.Delete(legacyDir, recursive: true);
                }
            }
            catch
            {
                // Cleanup is best-effort; never invalidate a successful save.
            }
        }

        private static string HashPath(string value)
        {
            string identity = OperatingSystem.IsWindows()
                ? value.ToLowerInvariant()
                : value;

            using var sha1 = SHA1.Create();
            byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string LegacyHashPath(string value)
        {
            using var sha1 = SHA1.Create();
            byte[] bytes = sha1.ComputeHash(
                Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
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
            public string VideoPath { get; set; } = string.Empty;
            public string Mode { get; set; } = string.Empty;
            public string? StorageGeneration { get; set; }
            public int SelectedFrameIndex { get; set; }
            public double ViewStartSeconds { get; set; }
            public double SecondsPerScreen { get; set; }
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
            string? autoExecutionSignature = null)
        {
            VideoPath = videoPath;
            Mode = mode;
            SelectedFrameIndex = selectedFrameIndex;
            ViewStartSeconds = viewStartSeconds;
            SecondsPerScreen = secondsPerScreen;
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
