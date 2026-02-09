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
                string probeInfo = ProbeNativeLoad("avcodec", searchPaths);
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
            string? platformSubDir = GetBundledFfmpegSubDirectory();

            AddPathIfExists(paths, Path.GetFullPath(baseDir));
            AddPathIfExists(paths, Environment.CurrentDirectory);
            AddPathIfExists(paths, Path.Combine(baseDir, "FFmpeg"));
            if (!string.IsNullOrWhiteSpace(platformSubDir))
                AddPathIfExists(paths, Path.Combine(baseDir, "FFmpeg", platformSubDir));
            AddPathIfExists(paths, Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks")));
            AddPathIfExists(paths, Path.GetFullPath(Path.Combine(baseDir, "..", "Resources")));

            var current = new DirectoryInfo(baseDir);
            for (int i = 0; i < 8 && current != null; i++)
            {
                AddPathIfExists(paths, current.FullName);
                AddPathIfExists(paths, Path.Combine(current.FullName, "FFmpeg"));
                if (!string.IsNullOrWhiteSpace(platformSubDir))
                    AddPathIfExists(paths, Path.Combine(current.FullName, "FFmpeg", platformSubDir));

                current = current.Parent;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                AddPathsFromEnvironment(paths, "PATH");

            return paths;
        }

        private static void AddPathIfExists(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(fullPath))
                return;

            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (paths.Any(p => string.Equals(p, fullPath, cmp)))
                return;

            paths.Add(fullPath);
        }

        private static void AddPathsFromEnvironment(List<string> paths, string envName)
        {
            string? value = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (var part in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                AddPathIfExists(paths, part.Trim());
        }

        private static string? GetBundledFfmpegSubDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "win-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "osx-arm64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "linux-x64";
            return null;
        }

        private static string BuildLibraryFileName(string libraryName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return $"{libraryName}.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return $"lib{libraryName}.so";
            return $"lib{libraryName}.dylib";
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
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".dll".Length);
            int soIndex = name.IndexOf(".so", StringComparison.OrdinalIgnoreCase);
            if (soIndex > 0)
                name = name.Substring(0, soIndex);
            int versionIndex = name.IndexOf('.');
            if (versionIndex > 0)
                name = name.Substring(0, versionIndex);
            int dashIndex = name.LastIndexOf('-');
            if (dashIndex > 0 &&
                int.TryParse(name.Substring(dashIndex + 1), out _))
            {
                name = name.Substring(0, dashIndex);
            }
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
            var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var result = new HashSet<string>(comparer);

            void AddIfExists(string path)
            {
                if (File.Exists(path))
                    result.Add(path);
            }

            void AddByPattern(string pattern)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                        result.Add(file);
                }
                catch
                {
                    // Ignore unreadable paths.
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AddIfExists(Path.Combine(dir, $"{normalized}.dll"));
                AddIfExists(Path.Combine(dir, $"lib{normalized}.dll"));
                AddByPattern($"{normalized}*.dll");
                AddByPattern($"lib{normalized}*.dll");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                AddIfExists(Path.Combine(dir, $"lib{normalized}.so"));
                AddByPattern($"lib{normalized}.so*");
            }
            else
            {
                AddIfExists(Path.Combine(dir, $"lib{normalized}.dylib"));
                AddByPattern($"lib{normalized}*.dylib");
            }

            foreach (var candidate in result.OrderBy(path => path.Length))
                yield return candidate;
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

        private static string ProbeNativeLoad(string libraryName, List<string> searchPaths)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ProbeDylibLoad(libraryName, searchPaths);

            string normalized = NormalizeLibraryName(libraryName);
            bool foundCandidate = false;

            foreach (var dir in searchPaths)
            {
                foreach (var candidate in EnumerateLibraryCandidates(dir, normalized))
                {
                    foundCandidate = true;
                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        NativeLibrary.Free(handle);
                        return $"loaded {candidate}";
                    }
                }
            }

            if (!foundCandidate)
                return "no matching native library found";

            return "native library exists but failed to load";
        }

        private static string ProbeDylibLoad(string libraryName, List<string> searchPaths)
        {
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

            return string.IsNullOrWhiteSpace(lastError)
                ? "no matching dylib found"
                : lastError;
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
