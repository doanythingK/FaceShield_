using Avalonia;
using System;
using System.Collections.Generic;

namespace FaceShield.Models.Analysis
{
    public sealed class FrameAnalysisResult
    {
        public int FrameIndex { get; init; }
        public double TimestampSec { get; init; }

        public bool HasFace { get; init; }
        public float Confidence { get; init; }

        public Rect? FaceBounds { get; init; }
        public IReadOnlyList<Rect> FaceBoundsList { get; init; } = Array.Empty<Rect>();
    }
}
