using Avalonia;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrack
    {
        private readonly List<FaceTrackDetection> _detections = new();

        public FaceTrack(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public IReadOnlyList<FaceTrackDetection> Detections => _detections;

        public int StartFrame => _detections.Count == 0 ? -1 : _detections[0].FrameIndex;

        public int EndFrame => _detections.Count == 0 ? -1 : _detections[^1].FrameIndex;

        public int DetectionCount => _detections.Count;

        public float MaxConfidence { get; private set; }

        public FaceTrackDetection? LastDetection => _detections.Count == 0 ? null : _detections[^1];

        public void Add(FaceTrackDetection detection)
        {
            if (_detections.Count > 0 && detection.FrameIndex <= _detections[^1].FrameIndex)
                throw new InvalidOperationException("Face track detections must be appended in frame order.");

            _detections.Add(detection);
            MaxConfidence = Math.Max(MaxConfidence, detection.Confidence);
        }
    }

    public readonly record struct FaceTrackDetection(
        int FrameIndex,
        Rect Bounds,
        PixelSize Size,
        float Confidence);
}
