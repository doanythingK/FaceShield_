using FaceShield.Models.Analysis;
using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public sealed class FrameAnalyzer : IFrameAnalyzer
    {
        private readonly IFaceDetector _faceDetector;

        public FrameAnalyzer(IFaceDetector faceDetector)
        {
            _faceDetector = faceDetector;
        }

        public async Task<IReadOnlyList<FrameAnalysisResult>> AnalyzeAsync(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            var results = new List<FrameAnalysisResult>();

            int fps = 30;
            int totalFrames = 300; // 더미

            for (int i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();

                var face = _faceDetector.Detect(null);

                results.Add(new FrameAnalysisResult
                {
                    FrameIndex = i,
                    TimestampSec = i / (double)fps,
                    HasFace = face != null,
                    Confidence = face?.Confidence ?? 0f,
                    FaceBounds = face?.Bounds
                });

                progress?.Report(i);
                await Task.Yield();
            }

            return results;
        }
    }
}
