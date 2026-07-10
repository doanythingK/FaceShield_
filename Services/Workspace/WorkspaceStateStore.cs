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
        private readonly string _rootDir;
        private readonly string _stateFile;
        private AppState _state;

        public WorkspaceStateStore()
        {
            _rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FaceShield");
            _stateFile = Path.Combine(_rootDir, "state.json");
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

            _state.Workspaces.RemoveAll(w =>
                string.Equals(w.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase));

            string baseDir = GetWorkspaceBaseDir(videoPath);
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);

            SaveState();
        }

        public bool TryLoadWorkspace(
            string videoPath,
            WorkspaceMode mode,
            FrameMaskProvider maskProvider,
            out WorkspaceSnapshot? snapshot)
        {
            snapshot = null;
            var state = _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(w.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase));

            if (state == null)
                return false;

            string dir = GetWorkspaceDir(videoPath, mode);
            maskProvider.Clear();

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

            foreach (int index in state.MaskIndices ?? new List<int>())
            {
                string filePath = Path.Combine(dir, $"mask_{index}.png");
                if (!File.Exists(filePath))
                    continue;

                var mask = LoadMask(filePath);
                if (mask != null)
                    maskProvider.SetMask(index, mask);
            }

            snapshot = new WorkspaceSnapshot(
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
                state.AutoExportHybridDisableReasons);

            return true;
        }

        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)
        {
            if (snapshot == null)
                return;

            string dir = GetWorkspaceDir(snapshot.VideoPath, snapshot.Mode);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

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

            _state.Workspaces.RemoveAll(w =>
                string.Equals(w.VideoPath, snapshot.VideoPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(w.Mode, snapshot.Mode.ToString(), StringComparison.OrdinalIgnoreCase));

            _state.Workspaces.Add(new WorkspaceState
            {
                VideoPath = snapshot.VideoPath,
                Mode = snapshot.Mode.ToString(),
                SelectedFrameIndex = snapshot.SelectedFrameIndex,
                ViewStartSeconds = snapshot.ViewStartSeconds,
                SecondsPerScreen = snapshot.SecondsPerScreen,
                LastOpened = snapshot.LastOpened,
                MaskIndices = indices,
                FaceMasks = faceMasks,
                AutoResumeIndex = snapshot.AutoResumeIndex,
                AutoCompleted = snapshot.AutoCompleted,
                AutoRunSignature = snapshot.AutoRunSignature,
                AutoExportGateRequired = snapshot.AutoExportGateRequired,
                AutoExportGatePassed = snapshot.AutoExportGatePassed,
                AutoExportGateFailure = snapshot.AutoExportGateFailure,
                AutoExportHybridPolicyAvailable = snapshot.AutoExportHybridPolicyAvailable,
                AutoExportAllowHybridCopy = snapshot.AutoExportAllowHybridCopy,
                AutoExportHybridDisableReasons = snapshot.AutoExportHybridDisableReasons
            });

            SaveState();
        }

        private string GetWorkspaceBaseDir(string videoPath)
        {
            string hash = HashPath(videoPath);
            return Path.Combine(_rootDir, "workspaces", hash);
        }

        private string GetWorkspaceDir(string videoPath, WorkspaceMode mode)
        {
            return Path.Combine(GetWorkspaceBaseDir(videoPath), mode.ToString());
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
            catch
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // ignore cleanup failures
                }
                return null;
            }
        }

        private AppState LoadState()
        {
            try
            {
                if (!File.Exists(_stateFile))
                    return new AppState();

                var json = File.ReadAllText(_stateFile);
                var state = JsonSerializer.Deserialize<AppState>(json);
                return state ?? new AppState();
            }
            catch
            {
                return new AppState();
            }
        }

        private void SaveState()
        {
            Directory.CreateDirectory(_rootDir);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_stateFile, json);
        }

        private static string HashPath(string value)
        {
            using var sha1 = SHA1.Create();
            byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
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
            public int SelectedFrameIndex { get; set; }
            public double ViewStartSeconds { get; set; }
            public double SecondsPerScreen { get; set; }
            public DateTimeOffset LastOpened { get; set; }
            public List<int> MaskIndices { get; set; } = new();
            public List<FaceMaskState> FaceMasks { get; set; } = new();
            public int AutoResumeIndex { get; set; }
            public bool AutoCompleted { get; set; }
            public string? AutoRunSignature { get; set; }
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
        public int ProcessingMode { get; set; } = (int)AutoMaskProcessingMode.Legacy;
        public double DownscaleRatio { get; set; } = 1.0;
        public int DownscaleQuality { get; set; }
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
            string? autoExportHybridDisableReasons)
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
            AutoExportGateRequired = autoExportGateRequired;
            AutoExportGatePassed = autoExportGatePassed;
            AutoExportGateFailure = autoExportGateFailure;
            AutoExportHybridPolicyAvailable = autoExportHybridPolicyAvailable;
            AutoExportAllowHybridCopy = autoExportAllowHybridCopy;
            AutoExportHybridDisableReasons = autoExportHybridDisableReasons;
        }
    }
}
