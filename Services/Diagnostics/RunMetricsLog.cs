using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
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

        public static void AppendExportQualityGate(
            AutoMaskRunSummary? autoRunSummary,
            ExportRunSummary exportSummary,
            bool allowHybridCopy,
            IReadOnlyList<string>? autoHybridDisableReasons)
        {
            string run = string.IsNullOrWhiteSpace(exportSummary.RunId)
                ? (autoRunSummary?.RunId ?? "n/a")
                : exportSummary.RunId;
            int outputFrames = exportSummary.SubmittedVideoFrames > 0
                ? exportSummary.SubmittedVideoFrames
                : exportSummary.Frames;
            int totalFrames = Math.Max(1, Math.Max(autoRunSummary?.TotalFrames ?? 0, outputFrames));
            int inputVideoPackets = Math.Max(
                0,
                exportSummary.HybridCopyUsed
                    ? Math.Max(exportSummary.CopiedSourceVideoPackets, exportSummary.EncodedSourceVideoPackets)
                    : exportSummary.InputVideoPackets);
            int droppedPackets = Math.Max(0, exportSummary.DroppedVideoPackets);
            double packetDropRate = inputVideoPackets > 0 ? (double)droppedPackets / inputVideoPackets : 0.0;
            int expectedVideoFrames = Math.Max(
                0,
                exportSummary.SubmittedVideoFrames + exportSummary.CopiedSourceVideoPackets);
            int droppedFrames = Math.Max(0, exportSummary.VideoFrameDropCount);
            double frameDropRate = expectedVideoFrames > 0
                ? (double)droppedFrames / expectedVideoFrames
                : 0.0;

            var riskReasons = new List<string>();
            int reviewRiskScore = 0;
            if (autoRunSummary?.FinalMaskReviewRequired == true)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-review-required");
            }
            if (exportSummary.HybridCopyAttempted)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-attempted");
            }
            if (exportSummary.HybridCopyUsed && exportSummary.HybridCopyTimestampFixCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-timestamp-fixes");
            }
            if (exportSummary.HybridCopyUsed &&
                exportSummary.HybridEncodedPacketFrameStep > 0 &&
                exportSummary.HybridCopyPacketFrameStep > 0 &&
                exportSummary.HybridEncodedPacketFrameStep != exportSummary.HybridCopyPacketFrameStep)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-step-mismatch");
            }
            if (packetDropRate > 0.001 || frameDropRate > 0.001)
            {
                reviewRiskScore++;
                riskReasons.Add("video-drop-rate");
            }
            if (autoRunSummary != null &&
                (autoRunSummary.FinalMaskShortGapCount > 0 ||
                    autoRunSummary.FinalMaskPerFaceShortGapCount > 0 ||
                    autoRunSummary.FinalMaskLargeJumpGapCount > 0))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-short-gap");
            }
            if (autoRunSummary != null &&
                (autoRunSummary.FinalSceneCutCarryRemovedCount > 0 ||
                    autoRunSummary.FinalSceneCutCarryPairCount > 0 ||
                    autoRunSummary.FinalSceneCutProtectedFrameCount > 0))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-scene-cut-carry");
            }
            int offModeResetPairCount = autoRunSummary?.FinalOffModeSceneCutResetPairCount ?? 0;
            int finalOffModeWeakCleanupCount = autoRunSummary?.FinalOffModeWeakCleanupCount ?? 0;
            if (offModeResetPairCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-off-mode-scene-cut-reset");
            }
            if (finalOffModeWeakCleanupCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-off-mode-weak-cleanup");
            }
            if (autoRunSummary != null &&
                (!string.IsNullOrWhiteSpace(autoRunSummary.FinalMaskReviewReasons) &&
                    autoRunSummary.FinalMaskReviewReasons != "none"))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-review-reasons");
            }

            if (!string.IsNullOrWhiteSpace(exportSummary.PacketLossFallbackReason))
            {
                reviewRiskScore++;
                riskReasons.Add("packet-loss-fallback");
            }
            if (!string.IsNullOrWhiteSpace(exportSummary.HybridCopyFallbackReason))
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-fallback");
            }
            if (!allowHybridCopy)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-hybrid-disabled-by-summary");
            }
            if (autoHybridDisableReasons != null)
            {
                foreach (string reason in autoHybridDisableReasons)
                {
                    reviewRiskScore++;
                    riskReasons.Add($"auto-hybrid-reason:{reason}");
                }
            }

            int sampleWindowFrames = autoRunSummary?.SampleWindowFrames > 0
                ? autoRunSummary.SampleWindowFrames
                : autoRunSummary != null && autoRunSummary.SourceFps > 0
                    ? Math.Min(autoRunSummary.TotalFrames, (int)Math.Round(autoRunSummary.SourceFps * 30.0))
                    : Math.Min(autoRunSummary?.TotalFrames ?? 0, 900);
            int sampleFrameCount = autoRunSummary?.SampleFrameCount ?? 0;
            int sampleRowCount = autoRunSummary?.SampleRowCount ?? 0;
            int sampleShortGapCount = autoRunSummary?.SampleShortGapCount ?? 0;
            int samplePerFaceShortGapCount = autoRunSummary?.SamplePerFaceShortGapCount ?? 0;
            int sampleIsolatedFrameCount = autoRunSummary?.SampleIsolatedFrameCount ?? 0;
            int sampleLargeJumpGapCount = autoRunSummary?.SampleLargeJumpGapCount ?? 0;
            int sampleProtectedCarryCount = autoRunSummary?.SampleProtectedSceneCarryFrameCount ?? 0;
            int sampleGapFillBlockedCutGapFrames = autoRunSummary?.SampleGapFillBlockedCutGapFrames ?? 0;
            int sampleGapFillBlockedCutGapFramesBeforeCut = autoRunSummary?.SampleGapFillBlockedCutGapFramesBeforeCut ?? 0;
            int sampleGapFillBlockedCutGapFramesAfterCut = autoRunSummary?.SampleGapFillBlockedCutGapFramesAfterCut ?? 0;
            int sampleGapFillBlockedCleanupGapFrames = autoRunSummary?.SampleGapFillBlockedCleanupGapFrames ?? 0;
            int sampleGapFillBlockedSceneCarryGapFrames = autoRunSummary?.SampleGapFillBlockedSceneCarryGapFrames ?? 0;
            int sampleOffModeWeakCleanupSuppressionCount = autoRunSummary?.SampleOffModeWeakCleanupSuppressionCount ?? 0;
            int offModeResetRemoved = autoRunSummary?.FinalOffModeSceneCutResetRemovedFrameCount ?? 0;
            int offModeResetBeforeWindowFrames = autoRunSummary?.FinalOffModeSceneCutResetBeforeWindowFrameCount ?? 0;
            int offModeResetAfterWindowFrames = autoRunSummary?.FinalOffModeSceneCutResetAfterWindowFrameCount ?? 0;
            int offModeResetRemovedBeforeFrames = autoRunSummary?.FinalOffModeSceneCutResetRemovedBeforeFrameCount ?? 0;
            int offModeResetRemovedAfterFrames = autoRunSummary?.FinalOffModeSceneCutResetRemovedAfterFrameCount ?? 0;
            double offModeResetBeforeRate = offModeResetBeforeWindowFrames > 0
                ? offModeResetRemovedBeforeFrames / (double)offModeResetBeforeWindowFrames
                : 0.0;
            double offModeResetAfterRate = offModeResetAfterWindowFrames > 0
                ? offModeResetRemovedAfterFrames / (double)offModeResetAfterWindowFrames
                : 0.0;
            bool sampleReviewRequired = autoRunSummary?.SampleReviewRequired == true;
            string sampleReviewReasons = autoRunSummary?.SampleReviewReasons ?? "none";
            var sampleReasons = new List<string>();
            int sampleRiskScore = 0;
            if (sampleReviewRequired || string.IsNullOrWhiteSpace(sampleReviewReasons) == false && sampleReviewReasons != "none")
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-review-required");
            }
            if (sampleShortGapCount > 0 || samplePerFaceShortGapCount > 0 || sampleLargeJumpGapCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-short-gap");
            }
            if (sampleProtectedCarryCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-scene-carry");
            }
            if (sampleGapFillBlockedCutGapFrames > 0
                || sampleGapFillBlockedCutGapFramesBeforeCut > 0
                || sampleGapFillBlockedCutGapFramesAfterCut > 0
                || sampleGapFillBlockedCleanupGapFrames > 0
                || sampleGapFillBlockedSceneCarryGapFrames > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-gap-fill-blocked");
            }
            if (sampleOffModeWeakCleanupSuppressionCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-off-mode-weak-cleanup");
            }
            string sampleRiskLabel = sampleRiskScore >= 3
                ? "high"
                : sampleRiskScore >= 2
                    ? "medium"
                    : sampleRiskScore >= 1
                        ? "low"
                        : "safe";

            string hybridRange = exportSummary.HybridCopyUsed
                ? $"{exportSummary.HybridWindowStartFrame}-{exportSummary.HybridWindowEndFrame}"
                : "n/a";
            int hybridWindowLength = exportSummary.HybridCopyUsed
                ? Math.Max(0, exportSummary.HybridWindowEndFrame - exportSummary.HybridWindowStartFrame)
                : 0;
            double throughputFps = exportSummary.TotalMs > 0 && outputFrames > 0
                ? outputFrames * 1000.0 / exportSummary.TotalMs
                : 0.0;
            double finalSceneCutRemovalRate = autoRunSummary != null && autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutCarryRemovedCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalSceneCutProtectedRate = autoRunSummary != null && autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutProtectedFrameCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            int finalGapFillBlockedCutGapFrames = autoRunSummary?.FinalGapFillBlockedCutGapFrames ?? 0;
            int finalGapFillBlockedCutGapFramesBeforeCut = autoRunSummary?.FinalGapFillBlockedCutGapFramesBeforeCut ?? 0;
            int finalGapFillBlockedCutGapFramesAfterCut = autoRunSummary?.FinalGapFillBlockedCutGapFramesAfterCut ?? 0;
            int finalGapFillBlockedCleanupGapFrames = autoRunSummary?.FinalGapFillBlockedCleanupGapFrames ?? 0;
            int finalGapFillBlockedSceneCarryGapFrames = autoRunSummary?.FinalGapFillBlockedSceneCarryGapFrames ?? 0;
            int finalGapFillBlockedTotal = finalGapFillBlockedCutGapFrames
                + finalGapFillBlockedCutGapFramesBeforeCut
                + finalGapFillBlockedCutGapFramesAfterCut
                + finalGapFillBlockedCleanupGapFrames
                + finalGapFillBlockedSceneCarryGapFrames;
            int finalGapFillBlockedWindowFrames = Math.Max(1, autoRunSummary?.TotalFrames ?? 0);
            double finalGapFillBlockedRate = finalGapFillBlockedWindowFrames > 0
                ? finalGapFillBlockedTotal / (double)finalGapFillBlockedWindowFrames
                : 0.0;
            int opsWindowFrames = sampleWindowFrames > 0 ? sampleWindowFrames : 1;
            double sampleMissRecoveryRate = autoRunSummary != null
                ? autoRunSummary.SampleMissRecoveryFillCount / (double)opsWindowFrames
                : 0.0;
            double sampleFpSuppressedRate = autoRunSummary != null
                ? autoRunSummary.SampleFalsePositiveSuppressionCount / (double)opsWindowFrames
                : 0.0;
            int sampleGapFillBlockedTotal = autoRunSummary != null
                ? autoRunSummary.SampleGapFillBlockedCutGapFrames
                    + autoRunSummary.SampleGapFillBlockedCutGapFramesBeforeCut
                    + autoRunSummary.SampleGapFillBlockedCutGapFramesAfterCut
                    + autoRunSummary.SampleGapFillBlockedCleanupGapFrames
                    + autoRunSummary.SampleGapFillBlockedSceneCarryGapFrames
                : 0;
            double sampleGapFillBlockedRate = opsWindowFrames > 0
                ? sampleGapFillBlockedTotal / (double)opsWindowFrames
                : 0.0;
            string riskLabel = reviewRiskScore >= 3
                ? "high"
                : reviewRiskScore >= 2
                    ? "medium"
                    : reviewRiskScore >= 1
                        ? "low"
                        : "safe";

            System.Diagnostics.Debug.WriteLine(
                $"[QualityGate] runId={run}, totalFrames={totalFrames}, sampleWindowFrames={sampleWindowFrames}, exportMode={exportSummary.ExportMode}, throughputFps={throughputFps:0.00}, risk={riskLabel}, riskReasons={FormatTextListForLog(riskReasons)}, frameDropRate={frameDropRate:0.000000}, frameDrops={droppedFrames}/{expectedVideoFrames}, packetDropRate={packetDropRate:0.000000}, packetDrops={droppedPackets}/{inputVideoPackets}, outputFrames={outputFrames}, hybridRequested={allowHybridCopy.ToString().ToLowerInvariant()}, hybridUsed={exportSummary.HybridCopyUsed.ToString().ToLowerInvariant()}, hybridRange={hybridRange}, hybridLength={hybridWindowLength}, hybridFixes={exportSummary.HybridCopyTimestampFixCount}, hybridTransitions={exportSummary.HybridModeTransitionCount}, copiedPackets={exportSummary.CopiedVideoPackets}, copiedSourcePackets={exportSummary.CopiedSourceVideoPackets}, encodedSourcePackets={exportSummary.EncodedSourceVideoPackets}, outputPackets={exportSummary.OutputVideoPackets}, autoReviewRequired={autoRunSummary?.FinalMaskReviewRequired.ToString().ToLowerInvariant() ?? "n/a"}, finalShortGaps={autoRunSummary?.FinalMaskShortGapCount ?? -1}, finalPerFaceShortGaps={autoRunSummary?.FinalMaskPerFaceShortGapCount ?? -1}, finalLargeJumps={autoRunSummary?.FinalMaskLargeJumpGapCount ?? -1}, finalCarryFrames={autoRunSummary?.FinalProtectedSceneCarryFrameCount ?? -1}, sceneCut=preGuard:{autoRunSummary?.FinalSceneCutPreGuardPairCount ?? -1},preStrong:{autoRunSummary?.FinalSceneCutPreStrongProbePairCount ?? -1},postGuard:{autoRunSummary?.FinalSceneCutPostGuardPairCount ?? -1},postStrong:{autoRunSummary?.FinalSceneCutPostStrongProbePairCount ?? -1},carryPairs:{autoRunSummary?.FinalSceneCutCarryPairCount ?? -1},carryRemoved:{autoRunSummary?.FinalSceneCutCarryRemovedCount ?? -1},carryProtected:{autoRunSummary?.FinalSceneCutProtectedFrameCount ?? -1},offModeResetPairs:{offModeResetPairCount},offModeResetRemoved:{offModeResetRemoved},offModeResetWindows={offModeResetBeforeWindowFrames}/{offModeResetAfterWindowFrames},offModeResetRemovedWindows={offModeResetRemovedBeforeFrames}/{offModeResetRemovedAfterFrames},offModeResetBeforeRate={offModeResetBeforeRate:0.0000},offModeResetAfterRate={offModeResetAfterRate:0.0000}, finalOffModeWeakCleanupSuppressed={finalOffModeWeakCleanupCount}, autoHybridDisableReasons={FormatTextListForLog(autoHybridDisableReasons ?? Array.Empty<string>())}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateHybridTiming] runId={run}, encStep={exportSummary.HybridEncodedPacketFrameStep}, copyStep={exportSummary.HybridCopyPacketFrameStep}, frameGap={Math.Abs(exportSummary.HybridEncodedPacketFrameStep - exportSummary.HybridCopyPacketFrameStep)}, fallback={exportSummary.HybridCopyFallbackReason ?? "n/a"}, copyFixes={exportSummary.HybridCopyTimestampFixCount}, mode={exportSummary.ExportMode}, transitions={exportSummary.HybridModeTransitionCount}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateSample] runId={run}, mode={autoRunSummary?.Mode ?? "n/a"}, sampleWindow={autoRunSummary?.SampleWindowFrames ?? 0}, sampleFrames={sampleFrameCount}, sampleRows={sampleRowCount}, sampleRisk={sampleRiskLabel}, sampleRiskReasons={FormatTextListForLog(sampleReasons)}, sampleShortGaps={sampleShortGapCount}, samplePerFaceShortGaps={samplePerFaceShortGapCount}, sampleIsolated={sampleIsolatedFrameCount}, sampleLargeJumps={sampleLargeJumpGapCount}, sampleProtectedCarry={sampleProtectedCarryCount}, sampleReviewRequired={sampleReviewRequired.ToString().ToLowerInvariant()}, sampleReviewReasons={sampleReviewReasons}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, packetLossFallbackReason={exportSummary.PacketLossFallbackReason ?? "n/a"}, hybridFallbackReason={exportSummary.HybridCopyFallbackReason ?? "n/a"}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateSampleGapFillBlocked] runId={run}, sampleGapFillBlocked={sampleGapFillBlockedCutGapFrames}/{sampleGapFillBlockedCutGapFramesBeforeCut}/{sampleGapFillBlockedCutGapFramesAfterCut}/{sampleGapFillBlockedCleanupGapFrames}/{sampleGapFillBlockedSceneCarryGapFrames}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, sampleWindow={autoRunSummary?.SampleWindowFrames ?? 0}");
            int sampleGapCount = sampleShortGapCount + samplePerFaceShortGapCount + sampleLargeJumpGapCount;
            string sampleGapRisk = sampleWindowFrames > 0
                ? $"{sampleGapCount}/{sampleWindowFrames}"
                : "0/0";
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateOps] runId={run}, finalSceneCutRemovalRate={finalSceneCutRemovalRate:0.0000}, finalSceneCutProtectedRate={finalSceneCutProtectedRate:0.0000}, finalGapFillBlocked={finalGapFillBlockedCutGapFrames}/{finalGapFillBlockedCutGapFramesBeforeCut}/{finalGapFillBlockedCutGapFramesAfterCut}/{finalGapFillBlockedCleanupGapFrames}/{finalGapFillBlockedSceneCarryGapFrames}, finalGapFillBlockedRate={finalGapFillBlockedRate:0.0000}, finalOffModeWeakCleanupSuppressed={finalOffModeWeakCleanupCount}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, offModeResetPairCount={offModeResetPairCount}, offModeResetBeforeRate={offModeResetBeforeRate:0.0000}, offModeResetAfterRate={offModeResetAfterRate:0.0000}, sampleMissRecoveryRate={sampleMissRecoveryRate:0.0000}, sampleFpSuppressedRate={sampleFpSuppressedRate:0.0000}, sampleGapRisk={sampleGapRisk}, finalGapFillRiskRate={finalGapFillBlockedRate:0.0000}, frameDropRate={frameDropRate:0.000000}, throughputFps={throughputFps:0.00}, hybridRequested={allowHybridCopy.ToString().ToLowerInvariant()}, hybridUsed={exportSummary.HybridCopyUsed.ToString().ToLowerInvariant()}, hybridFixFallback={exportSummary.HybridCopyFallbackReason ?? "n/a"}, packetFallback={exportSummary.PacketLossFallbackReason ?? "n/a"}");
        }

        private static string FormatTextListForLog(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return "none";

            const int maxValues = 12;
            string text = string.Join(",", values.Take(maxValues));
            return values.Count > maxValues
                ? $"{text},+{values.Count - maxValues}"
                : text;
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
