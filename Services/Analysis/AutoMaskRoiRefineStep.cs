using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Diagnostics;
using System.Linq;

namespace FaceShield.Services.Analysis
{
    public sealed class AutoMaskRoiRefineStep
    {
        public void Apply(
            FrameMaskProvider maskProvider,
            string videoPath,
            IBgraFaceDetector detector,
            FaceTrackPostProcessResult trackPost,
            FaceOnnxDetectorOptions detectorOptions,
            AutoMaskOptions options,
            bool useFaceOnnxRoiDetector)
        {
            var candidates = trackPost.FilledGapFacesInfo
                .Concat(trackPost.FilledLostFacesInfo)
                .Concat(trackPost.FilledInitialFacesInfo)
                .ToArray();
            if (candidates.Length == 0)
                return;

            using var roiDetector = useFaceOnnxRoiDetector
                ? new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions))
                : null;
            var refineDetector = roiDetector ?? detector;
            var refine = new FaceTrackRoiRefiner().Apply(
                maskProvider,
                videoPath,
                refineDetector,
                candidates,
                options.DownscaleQuality);

            if (refine.Attempts > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackRoiRefine] attempts={refine.Attempts} hits={refine.Hits} seeks={refine.SeekCount} decoded={refine.DecodedFrames} elapsedMs={refine.ElapsedMs}");
            }
        }

        private static FaceOnnxDetectorOptions CreateRoiRefinerDetectorOptions(FaceOnnxDetectorOptions source)
        {
            var defaults = FaceOnnxDetector.GetDefaultThresholds();
            float detection = source.DetectionThreshold ?? defaults.Detection;
            float confidence = source.ConfidenceThreshold ?? defaults.Confidence;
            float nms = source.NmsThreshold ?? defaults.Nms;

            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = true,
                UseGpu = false,
                IntraOpNumThreads = source.IntraOpNumThreads,
                InterOpNumThreads = source.InterOpNumThreads,
                UseParallelExecution = false,
                EnablePreprocessParallelism = true,
                AllowAutoTune = false,
                AllowAutoGpu = false,
                DetectionThreshold = Math.Min(detection, 0.12f),
                ConfidenceThreshold = Math.Min(confidence, 0.12f),
                NmsThreshold = Math.Max(nms, 0.75f)
            };
        }
    }
}
