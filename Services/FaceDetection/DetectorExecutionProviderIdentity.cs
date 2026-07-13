using System;

namespace FaceShield.Services.FaceDetection
{
    internal static class DetectorExecutionProviderIdentity
    {
        internal static string GetCanonicalLabel(IFaceDetector detector)
        {
            ArgumentNullException.ThrowIfNull(detector);
            string label = detector switch
            {
                FaceOnnxDetector faceOnnx => faceOnnx.ExecutionProviderLabel,
                YoloFaceOnnxDetector yolo => yolo.ExecutionProviderLabel,
                ScrfdOnnxDetector scrfd => scrfd.ExecutionProviderLabel,
                YuNetOnnxDetector => "CPU",
                _ => detector.GetType().FullName ?? detector.GetType().Name
            };
            return NormalizeLabel(label);
        }

        internal static bool AreCompatible(IFaceDetector primary, IFaceDetector candidate)
            => string.Equals(
                GetCanonicalLabel(primary),
                GetCanonicalLabel(candidate),
                StringComparison.Ordinal);

        internal static bool AreLabelsCompatible(string? primary, string? candidate)
            => string.Equals(
                NormalizeLabel(primary),
                NormalizeLabel(candidate),
                StringComparison.Ordinal);

        internal static string NormalizeLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return "unknown";

            string normalized = label.Trim();
            if (normalized.StartsWith("GPU:", StringComparison.OrdinalIgnoreCase))
                return $"gpu:{normalized[4..].Trim().ToLowerInvariant()}";
            if (normalized.Contains("CoreML", StringComparison.OrdinalIgnoreCase))
                return "gpu:coreml";
            if (normalized.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
                return "cpu";

            return normalized.Replace('|', '_').ToLowerInvariant();
        }
    }
}
