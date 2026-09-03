using Avalonia;
using System;
using System.Collections.Generic;

namespace FaceShield.Models.Analysis
{
    public sealed class FrameAnalysisResult
    {
        public int FrameIndex { get; init; }
        
        /// <summary>
        /// Exact decoded presentation time in seconds.
        /// NaN means the decoder did not expose a reliable presentation timestamp.
        /// </summary>
        public double TimestampSec { get; init; }

        public bool HasFace { get; init; }
        public float Confidence { get; init; }

        public Rect? FaceBounds { get; init; }
        public IReadOnlyList<Rect> FaceBoundsList { get; init; } = Array.Empty<Rect>();
    }
}
