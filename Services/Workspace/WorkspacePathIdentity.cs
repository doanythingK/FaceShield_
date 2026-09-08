using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Separates source-file access paths from stable workspace identity keys.
    /// Identity casing is derived from the parent directory's actual case policy;
    /// symlink/junction targets are intentionally not canonicalized.
    /// </summary>
    internal static class WorkspacePathIdentity
    {
        private const int DarwinPcCaseSensitive = 11;
        private const int FileCaseSensitiveInfo = 23;
        private const uint FileCsFlagCaseSensitiveDir = 0x00000001;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint OpenExisting = 3;
        private const uint ShareReadWriteDelete = 0x00000007;

        internal static string NormalizeAccessPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A workspace path is required.", nameof(path));

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        internal static PathContext CreatePathContext(string path)
        {
            string accessPath = NormalizeAccessPath(path);
            SplitPath(accessPath, out string root, out string[] segments);

            bool[] segmentCaseSensitive = new bool[segments.Length];
            bool inheritedCaseSensitive = DefaultCaseSensitiveFallback;
            string currentParent = root;
            bool currentParentExists = Directory.Exists(currentParent);
            if (currentParentExists)
            {
                inheritedCaseSensitive = GetDirectoryCaseSensitive(
                    currentParent,
                    inheritedCaseSensitive);
            }

            for (int i = 0; i < segments.Length; i++)
            {
                segmentCaseSensitive[i] = inheritedCaseSensitive;
                string candidate = Path.Combine(currentParent, segments[i]);

                if (Directory.Exists(candidate))
                {
                    currentParent = candidate;
                    currentParentExists = true;
                    inheritedCaseSensitive = GetDirectoryCaseSensitive(
                        currentParent,
                        inheritedCaseSensitive);
                }
                else
                {
                    // Missing descendants inherit the nearest existing parent's policy.
                    // This is also what keeps deletion/removal identity stable after the
                    // target file itself disappears.
                    currentParent = candidate;
                    currentParentExists = false;
                }
            }

            string identityKey = BuildPolicyIdentity(root, segments, segmentCaseSensitive);
            return new PathContext(
                accessPath,
                identityKey,
                CreateV3IdentityKey(accessPath),
                CreateV2IdentityKey(accessPath),
                root,
                segmentCaseSensitive);
        }

        internal static string CreateIdentityKey(string path)
            => CreatePathContext(path).IdentityKey;

        // Reproduce the 2582a08 storage-v3 identity exactly for read compatibility.
        internal static string CreateV3IdentityKey(string path)
        {
            string accessPath = NormalizeAccessPath(path);
            string root = Path.GetPathRoot(accessPath) ?? string.Empty;
            if (root.Length == 0)
                return accessPath;

            string identityRoot = OperatingSystem.IsWindows()
                ? root.ToUpperInvariant()
                : root;
            string currentAccess = root;
            string currentIdentity = identityRoot;
            string remainder = accessPath[root.Length..];
            string[] segments = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                string candidate = Path.Combine(currentAccess, segment);
                string resolvedSegment = ResolveExistingEntryName(
                    currentAccess,
                    candidate,
                    segment);

                currentAccess = Path.Combine(currentAccess, resolvedSegment);
                currentIdentity = Path.Combine(currentIdentity, resolvedSegment);
            }

            return Path.TrimEndingDirectorySeparator(currentIdentity);
        }

        // Reproduce the f7bbac4 storage-v2 identity exactly for hash compatibility.
        internal static string CreateV2IdentityKey(string path)
        {
            string accessPath = NormalizeAccessPath(path);
            return OperatingSystem.IsWindows()
                ? accessPath.ToUpperInvariant()
                : accessPath;
        }

        internal static bool Equals(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                PathContext context = CreatePathContext(left);
                return context.MatchesAccessPath(right);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return false;
            }
        }

        private static bool DefaultCaseSensitiveFallback =>
            !OperatingSystem.IsWindows();

        private static bool GetDirectoryCaseSensitive(
            string directory,
            bool fallback)
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindowsDirectoryCaseSensitive(directory, out bool value)
                    ? value
                    : fallback;

            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    long result = pathconf(directory, DarwinPcCaseSensitive);
                    if (result == 0)
                        return false;
                    if (result == 1)
                        return true;
                }
                catch (Exception ex) when (
                    ex is DllNotFoundException or
                    EntryPointNotFoundException)
                {
                    return fallback;
                }

                return fallback;
            }

            // Supported non-Windows/non-macOS development environments are treated
            // as case-sensitive. The shipping runtime identifiers are Windows/macOS.
            return true;
        }

        private static bool TryGetWindowsDirectoryCaseSensitive(
            string directory,
            out bool caseSensitive)
        {
            caseSensitive = false;
            try
            {
                using SafeFileHandle handle = CreateFileW(
                    directory,
                    0,
                    ShareReadWriteDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                    return false;

                if (!GetFileInformationByHandleEx(
                        handle,
                        FileCaseSensitiveInfo,
                        out FileCaseSensitiveInformation info,
                        (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
                {
                    return false;
                }

                caseSensitive = (info.Flags & FileCsFlagCaseSensitiveDir) != 0;
                return true;
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or
                EntryPointNotFoundException or
                MarshalDirectiveException)
            {
                return false;
            }
        }

        private static string BuildPolicyIdentity(
            string root,
            IReadOnlyList<string> segments,
            IReadOnlyList<bool> segmentCaseSensitive)
        {
            if (segments.Count != segmentCaseSensitive.Count)
                throw new ArgumentException("Path identity policy length mismatch.");

            string identity = NormalizeRootForIdentity(root);
            for (int i = 0; i < segments.Count; i++)
            {
                string component = segmentCaseSensitive[i]
                    ? segments[i]
                    : FoldCaseInsensitiveComponent(segments[i]);
                identity = Path.Combine(identity, component);
            }

            return Path.TrimEndingDirectorySeparator(identity);
        }

        private static string NormalizeRootForIdentity(string root)
            => OperatingSystem.IsWindows()
                ? root.ToUpperInvariant()
                : root;

        private static string FoldCaseInsensitiveComponent(string value)
            => value.ToUpperInvariant();

        private static void SplitPath(
            string accessPath,
            out string root,
            out string[] segments)
        {
            root = Path.GetPathRoot(accessPath) ?? string.Empty;
            if (root.Length == 0)
            {
                segments = new[] { accessPath };
                return;
            }

            string remainder = accessPath[root.Length..];
            segments = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string ResolveExistingEntryName(
            string parent,
            string candidate,
            string requestedName)
        {
            if ((!File.Exists(candidate) && !Directory.Exists(candidate)) ||
                !Directory.Exists(parent))
            {
                return requestedName;
            }

            try
            {
                string? caseInsensitiveMatch = null;
                foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    string name = Path.GetFileName(entry);
                    if (string.Equals(name, requestedName, StringComparison.Ordinal))
                        return name;

                    if (caseInsensitiveMatch == null &&
                        string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase))
                    {
                        caseInsensitiveMatch = name;
                    }
                }

                return caseInsensitiveMatch ?? requestedName;
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return requestedName;
            }
        }

        internal sealed class PathContext
        {
            private readonly string _root;
            private readonly bool[] _segmentCaseSensitive;

            internal PathContext(
                string accessPath,
                string identityKey,
                string v3IdentityKey,
                string v2IdentityKey,
                string root,
                bool[] segmentCaseSensitive)
            {
                AccessPath = accessPath;
                IdentityKey = identityKey;
                V3IdentityKey = v3IdentityKey;
                V2IdentityKey = v2IdentityKey;
                _root = root;
                _segmentCaseSensitive = segmentCaseSensitive;
            }

            internal string AccessPath { get; }
            internal string IdentityKey { get; }
            internal string V3IdentityKey { get; }
            internal string V2IdentityKey { get; }

            internal bool MatchesAccessPath(string path)
            {
                try
                {
                    string accessPath = NormalizeAccessPath(path);
                    SplitPath(accessPath, out string root, out string[] segments);
                    if (!RootsEqual(_root, root) ||
                        segments.Length != _segmentCaseSensitive.Length)
                    {
                        return false;
                    }

                    string candidateIdentity = BuildPolicyIdentity(
                        root,
                        segments,
                        _segmentCaseSensitive);
                    return string.Equals(
                        candidateIdentity,
                        IdentityKey,
                        StringComparison.Ordinal);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
                {
                    return false;
                }
            }

            private static bool RootsEqual(string left, string right)
                => string.Equals(
                    left,
                    right,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileCaseSensitiveInformation
        {
            internal uint Flags;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            int fileInformationClass,
            out FileCaseSensitiveInformation lpFileInformation,
            uint dwBufferSize);

        [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
        private static extern long pathconf(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int name);
    }
}
