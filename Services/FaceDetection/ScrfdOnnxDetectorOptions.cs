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

        public float InputMean { get; init; } = 127.5f;

        public float InputStd { get; init; } = 128.0f;

        public bool MultiplyBboxByStride { get; init; } = true;

        public bool UseLetterboxResize { get; init; } = true;

        public bool UseRgbInput { get; init; } = true;
    }
}
