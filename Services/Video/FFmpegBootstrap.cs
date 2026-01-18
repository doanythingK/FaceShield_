using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video
{
    public static class FFmpegBootstrap
    {
        private static int _initialized;
        private static bool _resolverConfigured;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
                return;

            try
            {
                ConfigureRootPath();
                ConfigureDllImportResolver();
                ffmpeg.avcodec_version();
                Console.WriteLine("[FFmpeg] Native libraries loaded successfully.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "FFmpeg native libraries not loaded. Check DLL placement.",
                    ex);
            }
        }

        private static void ConfigureRootPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var macFrameworks = Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks"));
            if (Directory.Exists(macFrameworks))
            {
                ffmpeg.RootPath = macFrameworks;
                return;
            }

            var ffmpegDir = Path.Combine(baseDir, "FFmpeg");
            if (Directory.Exists(ffmpegDir))
            {
                ffmpeg.RootPath = ffmpegDir;
            }
        }

        private static void ConfigureDllImportResolver()
        {
            if (_resolverConfigured || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;

            var searchPaths = GetCandidateLibraryPaths();
            if (searchPaths.Count == 0)
                return;

            _resolverConfigured = true;
            ffmpeg.RootPath = searchPaths[0];

            NativeLibrary.SetDllImportResolver(typeof(ffmpeg).Assembly, (name, assembly, path) =>
            {
                if (!IsFfmpegLibrary(name))
                    return IntPtr.Zero;

                string fileName = BuildLibraryFileName(name);
                foreach (var dir in searchPaths)
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                        return handle;
                }

                return IntPtr.Zero;
            });

            foreach (var lib in KnownLibraries)
            {
                string fileName = BuildLibraryFileName(lib);
                foreach (var dir in searchPaths)
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        _ = NativeLibrary.TryLoad(candidate, out _);
                        break;
                    }
                }
            }
        }

        private static List<string> GetCandidateLibraryPaths()
        {
            var baseDir = AppContext.BaseDirectory;
            var paths = new List<string>();

            AddPathIfExists(paths, Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks")));
            AddPathIfExists(paths, Path.GetFullPath(Path.Combine(baseDir, "..", "Resources")));
            AddPathIfExists(paths, Path.GetFullPath(baseDir));
            AddPathIfExists(paths, Path.Combine(baseDir, "FFmpeg"));

            return paths;
        }

        private static void AddPathIfExists(List<string> paths, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && !paths.Contains(path))
                paths.Add(path);
        }

        private static string BuildLibraryFileName(string libraryName)
        {
            string name = libraryName;
            if (!name.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
                name = $"lib{name}";
            if (!name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                name += ".dylib";
            return name;
        }

        private static bool IsFfmpegLibrary(string libraryName)
        {
            string normalized = NormalizeLibraryName(libraryName);
            foreach (var lib in KnownLibraries)
            {
                if (string.Equals(lib, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string NormalizeLibraryName(string libraryName)
        {
            string name = libraryName;
            if (name.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(3);
            if (name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".dylib".Length);
            int versionIndex = name.IndexOf('.');
            if (versionIndex > 0)
                name = name.Substring(0, versionIndex);
            return name;
        }

        private static readonly string[] KnownLibraries =
        {
            "avcodec",
            "avformat",
            "avutil",
            "swscale",
            "swresample",
            "avfilter",
            "avdevice",
            "postproc"
        };
    }
}
