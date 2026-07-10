using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FaceShield.Services.Diagnostics
{
    internal static class RunMetricsLog
    {
        private static readonly object Sync = new();

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield",
            "Logs");

        public static void AppendRunLines(string? runId, params string?[] lines)
        {
            IReadOnlyList<string> validLines = lines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => NormalizeLine(line!))
                .ToArray();
            if (validLines.Count == 0)
                return;

            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    string safeRunId = SanitizeRunId(runId);
                    string path = Path.Combine(
                        LogDirectory,
                        $"run-metrics-{safeRunId}.log");
                    using var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.WriteLine(
                        $"[RunMetricsRecord] runId={safeRunId}, recordedAt={DateTimeOffset.Now:O}");
                    foreach (string line in validLines)
                        writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RunMetricsLog] write failed: {ex.Message}");
            }
        }

        private static string NormalizeLine(string value)
            => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string SanitizeRunId(string? runId)
        {
            string value = string.IsNullOrWhiteSpace(runId)
                ? $"unscoped-{DateTime.Now:yyyyMMdd-HHmmssfff}"
                : runId.Trim();
            var chars = value
                .Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                .Take(96)
                .ToArray();
            return chars.Length > 0
                ? new string(chars)
                : $"unscoped-{DateTime.Now:yyyyMMdd-HHmmssfff}";
        }
    }
}
