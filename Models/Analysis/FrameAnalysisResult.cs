using Avalonia;

namespace FaceShield.Models.Analysis
{
    public sealed class FrameAnalysisResult
    {
        public int FrameIndex { get; init; }
        public double TimestampSec { get; init; }

        public bool HasFace { get; init; }
        public float Confidence { get; init; }

        public Rect? FaceBounds { get; init; }
    }
}
