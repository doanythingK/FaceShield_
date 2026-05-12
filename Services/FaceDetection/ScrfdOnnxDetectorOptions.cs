namespace FaceShield.Services.FaceDetection
{
    public sealed class ScrfdOnnxDetectorOptions
    {
        public string? ModelPath { get; init; }

        public bool UseOrtOptimization { get; init; } = true;

        public bool UseGpu { get; init; } = false;

        public int? IntraOpNumThreads { get; init; }

        public int? InterOpNumThreads { get; init; }

        public bool? UseParallelExecution { get; init; }

        public float ConfidenceThreshold { get; init; } = 0.25f;

        public float NmsThreshold { get; init; } = 0.45f;

        public int? InputWidth { get; init; }

        public int? InputHeight { get; init; }

        public float InputMean { get; init; } = 127.5f;

        public float InputStd { get; init; } = 128.0f;

        public bool MultiplyBboxByStride { get; init; } = true;

        public float AnchorCenterOffset { get; init; } = 0.0f;

        public bool UseLetterboxResize { get; init; } = true;

        public bool CenterLetterboxPadding { get; init; } = false;

        public float LetterboxPaddingValue { get; init; } = 0.0f;

        public bool UseRgbInput { get; init; } = true;

        public bool DumpDebug { get; init; } = false;

        public int DebugCandidateLimit { get; init; } = 5;
    }
}
