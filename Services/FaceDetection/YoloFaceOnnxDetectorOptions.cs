namespace FaceShield.Services.FaceDetection
{
    public sealed class YoloFaceOnnxDetectorOptions
    {
        public string? ModelPath { get; init; }

        public YoloFaceModelType ModelType { get; init; } = YoloFaceModelType.YoloV8Face;

        public bool UseOrtOptimization { get; init; } = true;

        public bool UseGpu { get; init; } = false;

        public int? IntraOpNumThreads { get; init; }

        public int? InterOpNumThreads { get; init; }

        public bool? UseParallelExecution { get; init; }

        public int? InputWidth { get; init; }

        public int? InputHeight { get; init; }

        public float ObjectnessThreshold { get; init; } = 0.25f;

        public float ConfidenceThreshold { get; init; } = 0.35f;

        public float NmsThreshold { get; init; } = 0.45f;

        public int MaxDetections { get; init; } = 300;

        public bool UseLowConfidencePositionFilter { get; init; } = false;

        public float LowConfidencePositionMaxConfidence { get; init; } = 0.0f;

        public double LowConfidencePositionMinCenterYRatio { get; init; } = 1.0;

        public bool UseSmallAreaFilter { get; init; } = false;

        public double SmallAreaMaxAreaRatio { get; init; } = 0.0;

        public bool UseAspectRatioFilter { get; init; } = false;

        public double MinAspectRatio { get; init; } = 0.35;

        public double MaxAspectRatio { get; init; } = 1.65;

        public bool UseTopSmallLowConfidenceFilter { get; init; } = false;

        public double TopSmallLowConfidenceMaxCenterYRatio { get; init; } = 0.08;

        public double TopSmallLowConfidenceMaxAreaRatio { get; init; } = 0.006;

        public float TopSmallLowConfidenceMaxConfidence { get; init; } = 0.65f;

        public float LargeBoxWidthScale { get; init; } = 1.0f;

        public float LargeBoxHeightScale { get; init; } = 1.0f;

        public double LargeBoxMinAreaRatio { get; init; } = 0.0;

        public bool UseYolo5LandmarkBoxRefine { get; init; } = false;

        public double Yolo5LandmarkBoxMinAreaRatio { get; init; } = 0.03;

        public float Yolo5LandmarkBoxWidthScale { get; init; } = 1.80f;

        public float Yolo5LandmarkBoxHeightScale { get; init; } = 2.10f;

        public float Yolo5LandmarkBoxCenterYOffsetRatio { get; init; } = -0.04f;

        public float Yolo5LandmarkBoxMinOriginalIou { get; init; } = 0.30f;

        public bool UseTiling { get; init; } = false;

        public bool IncludeFullFrameWhenTiling { get; init; } = true;

        public int TileColumns { get; init; } = 2;

        public int TileRows { get; init; } = 2;

        public double TileOverlapRatio { get; init; } = 0.15;

        public bool UseLetterboxResize { get; init; } = true;

        public bool CenterLetterboxPadding { get; init; } = true;

        public float LetterboxPaddingValue { get; init; } = 114.0f;

        public bool UseRgbInput { get; init; } = true;

        public float InputScale { get; init; } = 1.0f / 255.0f;

        public bool DumpDebug { get; init; } = false;

        public int DebugCandidateLimit { get; init; } = 5;
    }
}
