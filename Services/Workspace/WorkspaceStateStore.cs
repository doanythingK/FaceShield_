using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Enums.Workspace;
using FaceShield.Models;
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
        private const string FaceMaskFileName = "face_masks.bin";
        private const int FaceMaskFileVersion = 1;
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

            maskProvider.Clear();

            string dir = GetWorkspaceDir(videoPath, mode);
            foreach (int index in state.MaskIndices)
            {
                string filePath = Path.Combine(dir, $"mask_{index}.png");
                if (!File.Exists(filePath))
                    continue;

                var mask = LoadMask(filePath);
                if (mask != null)
                    maskProvider.SetMask(index, mask);
            }

            LoadFaceMasks(dir, maskProvider);

            snapshot = new WorkspaceSnapshot(
                state.VideoPath,
                mode,
                state.SelectedFrameIndex,
                state.ViewStartSeconds,
                state.SecondsPerScreen,
                state.LastOpened,
                state.AutoResumeIndex,
                state.AutoCompleted);

            return true;
        }

        public void SaveWorkspace(WorkspaceSnapshot snapshot, FrameMaskProvider maskProvider)
        {
            if (snapshot == null)
                return;

            string dir = GetWorkspaceDir(snapshot.VideoPath, snapshot.Mode);
            Directory.CreateDirectory(dir);

            var entries = maskProvider.GetMaskEntries();
            var indices = new List<int>(entries.Count);

            foreach (var entry in entries)
            {
                indices.Add(entry.Key);
                string filePath = Path.Combine(dir, $"mask_{entry.Key}.png");
                SaveMask(filePath, entry.Value);
            }

            SaveFaceMasks(dir, maskProvider);
            DeleteStaleMasks(dir, indices);

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
                AutoResumeIndex = snapshot.AutoResumeIndex,
                AutoCompleted = snapshot.AutoCompleted
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
            string tempPath = path + ".tmp";
            try
            {
                using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    mask.Save(stream);
                    stream.Flush(true);
                }

                File.Move(tempPath, path, true);
            }
            catch
            {
                TryDeleteFile(tempPath);
            }
        }

        private static void SaveFaceMasks(string dir, FrameMaskProvider maskProvider)
        {
            string path = Path.Combine(dir, FaceMaskFileName);
            var entries = maskProvider.GetFaceEntries();
            if (entries.Count == 0)
            {
                TryDeleteFile(path);
                return;
            }

            string tempPath = path + ".tmp";
            try
            {
                using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(FaceMaskFileVersion);
                    writer.Write(entries.Count);

                    foreach (var entry in entries)
                    {
                        var data = entry.Value;
                        writer.Write(entry.Key);
                        writer.Write(data.Size.Width);
                        writer.Write(data.Size.Height);

                        int faceCount = data.Faces?.Count ?? 0;
                        writer.Write(faceCount);
                        for (int i = 0; i < faceCount; i++)
                        {
                            var rect = data.Faces[i];
                            writer.Write(rect.X);
                            writer.Write(rect.Y);
                            writer.Write(rect.Width);
                            writer.Write(rect.Height);
                        }

                        if (data.MinConfidence.HasValue)
                        {
                            writer.Write(true);
                            writer.Write(data.MinConfidence.Value);
                        }
                        else
                        {
                            writer.Write(false);
                        }

                        int confCount = data.Confidences?.Count ?? 0;
                        writer.Write(confCount);
                        for (int i = 0; i < confCount; i++)
                            writer.Write(data.Confidences[i]);
                    }

                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(tempPath, path, true);
            }
            catch
            {
                TryDeleteFile(tempPath);
            }
        }

        private static void LoadFaceMasks(string dir, FrameMaskProvider maskProvider)
        {
            string path = Path.Combine(dir, FaceMaskFileName);
            if (!File.Exists(path))
                return;

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);

                int version = reader.ReadInt32();
                if (version != FaceMaskFileVersion)
                    return;

                int entryCount = reader.ReadInt32();
                for (int i = 0; i < entryCount; i++)
                {
                    int frameIndex = reader.ReadInt32();
                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    int faceCount = reader.ReadInt32();
                    if (faceCount <= 0 || width <= 0 || height <= 0)
                    {
                        SkipFaceData(reader, faceCount);
                        continue;
                    }

                    var faces = new Rect[faceCount];
                    for (int j = 0; j < faceCount; j++)
                    {
                        double x = reader.ReadDouble();
                        double y = reader.ReadDouble();
                        double w = reader.ReadDouble();
                        double h = reader.ReadDouble();
                        faces[j] = new Rect(x, y, w, h);
                    }

                    bool hasMin = reader.ReadBoolean();
                    float? minConfidence = hasMin ? reader.ReadSingle() : null;

                    int confCount = reader.ReadInt32();
                    float[]? confs = null;
                    if (confCount > 0)
                    {
                        confs = new float[confCount];
                        for (int j = 0; j < confCount; j++)
                            confs[j] = reader.ReadSingle();
                    }

                    if (!maskProvider.TryGetStoredMask(frameIndex, out _))
                    {
                        maskProvider.SetFaceRects(
                            frameIndex,
                            faces,
                            new PixelSize(width, height),
                            minConfidence,
                            confs);
                    }
                }
            }
            catch
            {
                // Ignore invalid face data.
            }
        }

        private static void SkipFaceData(BinaryReader reader, int faceCount)
        {
            for (int j = 0; j < Math.Max(0, faceCount); j++)
            {
                reader.ReadDouble();
                reader.ReadDouble();
                reader.ReadDouble();
                reader.ReadDouble();
            }

            bool hasMin = reader.ReadBoolean();
            if (hasMin)
                reader.ReadSingle();

            int confCount = reader.ReadInt32();
            for (int j = 0; j < Math.Max(0, confCount); j++)
                reader.ReadSingle();
        }

        private static void DeleteStaleMasks(string dir, IReadOnlyCollection<int> activeIndices)
        {
            if (!Directory.Exists(dir))
                return;

            var keep = new HashSet<int>(activeIndices);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "mask_*.png");
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith("mask_", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!int.TryParse(name.Substring(5), out int index))
                    continue;

                if (keep.Contains(index))
                    continue;

                TryDeleteFile(file);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore cleanup failures
            }
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
            public int AutoResumeIndex { get; set; }
            public bool AutoCompleted { get; set; }
        }
    }

    public sealed class AutoSettingsState
    {
        public double DownscaleRatio { get; set; } = 1.0;
        public int DownscaleQuality { get; set; }
        public bool AutoTrackingEnabled { get; set; }
        public int AutoDetectEveryNFrames { get; set; } = 1;
        public int ParallelSessionCount { get; set; } = 2;
        public bool AutoUseOrtOptimization { get; set; } = true;
        public bool AutoUseGpu { get; set; }
        public int? OrtThreads { get; set; }
        public bool AutoExportAfter { get; set; } = true;
        public double? DetectionThreshold { get; set; }
        public double? ConfidenceThreshold { get; set; }
        public double? NmsThreshold { get; set; }
        public int? BlurRadius { get; set; }
        public int ProxyPreset { get; set; }
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

        public WorkspaceSnapshot(
            string videoPath,
            WorkspaceMode mode,
            int selectedFrameIndex,
            double viewStartSeconds,
            double secondsPerScreen,
            DateTimeOffset lastOpened,
            int autoResumeIndex,
            bool autoCompleted)
        {
            VideoPath = videoPath;
            Mode = mode;
            SelectedFrameIndex = selectedFrameIndex;
            ViewStartSeconds = viewStartSeconds;
            SecondsPerScreen = secondsPerScreen;
            LastOpened = lastOpened;
            AutoResumeIndex = autoResumeIndex;
            AutoCompleted = autoCompleted;
        }
    }
}
