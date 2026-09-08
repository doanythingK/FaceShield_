using System;
using System.IO;

namespace FaceShield.Services.Workspace
{
    /// <summary>
    /// Defines the single lexical identity used for workspace paths. This deliberately
    /// does not resolve symbolic links; link-target identity is a separate policy.
    /// </summary>
    internal static class WorkspacePathIdentity
    {
        internal static StringComparison Comparison =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        internal static StringComparer Comparer =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A workspace path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);

            // Keep the identity string stable anywhere it is hashed or embedded in a
            // composite cache key. Non-Windows platforms remain case-sensitive by
            // policy; filesystem/symlink canonicalization is intentionally out of scope.
            return OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
        }

        internal static bool Equals(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return Comparer.Equals(Normalize(left), Normalize(right));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}
