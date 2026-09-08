using System;
using System.IO;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Separates the source-file access path from the lexical identity used by
    /// workspace state, cache, save lifetime, and storage hashing.
    /// </summary>
    internal static class WorkspacePathIdentity
    {
        internal static string NormalizeAccessPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A workspace path is required.", nameof(path));

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        internal static string CreateIdentityKey(string path)
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
                return string.Equals(
                    CreateIdentityKey(left),
                    CreateIdentityKey(right),
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
                // Directory enumeration is an identity refinement, not an access
                // requirement. Preserve the lexical component when it is unavailable.
                return requestedName;
            }
        }
    }
}
