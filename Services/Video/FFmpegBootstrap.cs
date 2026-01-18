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

            var searchPaths = GetCandidateLibraryPaths();

            try
            {
                ConfigureDllImportResolver(searchPaths);
                PreloadLibraries(searchPaths);
                ConfigureRootPath(searchPaths);

                ffmpeg.avcodec_version();
                Console.WriteLine("[FFmpeg] Native libraries loaded successfully.");
            }
            catch (Exception ex)
            {
                string pathInfo = searchPaths.Count > 0
                    ? string.Join(", ", searchPaths)
                    : "(none)";
                string probeInfo = ProbeDylibLoad("avcodec", searchPaths);
                throw new InvalidOperationException(
                    $"FFmpeg native libraries not loaded. SearchPaths: {pathInfo}. Probe: {probeInfo}",
                    ex);
            }
        }

        private static void ConfigureRootPath(List<string> searchPaths)
        {
            string rootPath = FindFfmpegRootPath(searchPaths);
            if (!string.IsNullOrWhiteSpace(rootPath))
                ffmpeg.RootPath = rootPath;
        }

        private static void ConfigureDllImportResolver(List<string> searchPaths)
        {
            if (_resolverConfigured || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;

            if (searchPaths.Count == 0)
                return;

            _resolverConfigured = true;
            NativeLibrary.SetDllImportResolver(typeof(ffmpeg).Assembly, (name, assembly, path) =>
            {
                string normalized = NormalizeLibraryName(name);
                if (!IsFfmpegLibrary(normalized))
                    return IntPtr.Zero;

                foreach (var dir in searchPaths)
                {
                    string direct = Path.Combine(dir, BuildLibraryFileName(normalized));
                    if (File.Exists(direct) && NativeLibrary.TryLoad(direct, out var handle))
                        return handle;

                    foreach (var candidate in EnumerateLibraryCandidates(dir, normalized))
                    {
                        if (NativeLibrary.TryLoad(candidate, out handle))
                            return handle;
                    }
                }

                return IntPtr.Zero;
            });
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
            foreach (var lib in KnownLibraries)
            {
                if (string.Equals(lib, libraryName, StringComparison.OrdinalIgnoreCase))
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

        private static void PreloadLibraries(List<string> searchPaths)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || searchPaths.Count == 0)
                return;

            foreach (var lib in KnownLibraries)
            {
                foreach (var dir in searchPaths)
                {
                    foreach (var candidate in EnumerateLibraryCandidates(dir, lib))
                    {
                        if (NativeLibrary.TryLoad(candidate, out _))
                            goto NextLib;
                    }
                }
            NextLib:
                continue;
            }
        }

        private static IEnumerable<string> EnumerateLibraryCandidates(string dir, string normalized)
        {
            string baseName = $"lib{normalized}";
            string direct = Path.Combine(dir, $"{baseName}.dylib");
            if (File.Exists(direct))
                yield return direct;

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(dir, $"{baseName}*.dylib");
            }
            catch
            {
                yield break;
            }

            foreach (var match in matches.OrderBy(path => path.Length))
            {
                if (!string.Equals(match, direct, StringComparison.OrdinalIgnoreCase))
                    yield return match;
            }
        }

        private static string FindFfmpegRootPath(List<string> searchPaths)
        {
            string pattern = GetNativeLibrarySearchPattern();
            if (string.IsNullOrWhiteSpace(pattern))
                return string.Empty;

            foreach (var dir in searchPaths)
            {
                try
                {
                    if (Directory.EnumerateFiles(dir, pattern).Any())
                        return dir;
                }
                catch
                {
                    // Ignore unreadable paths.
                }
            }
            return string.Empty;
        }

        private static string GetNativeLibrarySearchPattern()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "libavcodec*.dylib";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "avcodec*.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "libavcodec*.so*";
            return string.Empty;
        }

        private static string ProbeDylibLoad(string libraryName, List<string> searchPaths)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "unsupported platform";

            string normalized = NormalizeLibraryName(libraryName);
            string lastError = string.Empty;

            foreach (var dir in searchPaths)
            {
                foreach (var candidate in EnumerateLibraryCandidates(dir, normalized))
                {
                    IntPtr handle = dlopen(candidate, RTLD_NOW | RTLD_LOCAL);
                    if (handle != IntPtr.Zero)
                    {
                        dlclose(handle);
                        return $"loaded {candidate}";
                    }

                    string error = GetDlError();
                    if (!string.IsNullOrWhiteSpace(error))
                        lastError = error;
                }
            }

            if (!string.IsNullOrWhiteSpace(lastError))
                return lastError;

            return "no matching dylib found";
        }

        private static string GetDlError()
        {
            IntPtr ptr = dlerror();
            return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }

        private const int RTLD_NOW = 2;
        private const int RTLD_LOCAL = 4;

        [DllImport("libSystem.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport("libSystem.dylib")]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libSystem.dylib")]
        private static extern IntPtr dlerror();

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
