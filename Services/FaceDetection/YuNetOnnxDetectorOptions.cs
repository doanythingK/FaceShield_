namespace FaceShield.Services.FaceDetection
{
    public sealed class YuNetOnnxDetectorOptions
    {
        public string? ModelPath { get; init; }

        public bool UseOrtOptimization { get; init; } = true;

        public int? IntraOpNumThreads { get; init; }

        public int? InterOpNumThreads { get; init; }

        public bool? UseParallelExecution { get; init; }

        public float ConfidenceThreshold { get; init; } = 0.6f;

        public float NmsThreshold { get; init; } = 0.3f;

        public int TopK { get; init; } = 5000;

        public bool UseTiling { get; init; } = false;

        public bool IncludeFullFrameWhenTiling { get; init; } = true;

        public int TileColumns { get; init; } = 2;

        public int TileRows { get; init; } = 2;

        public double TileOverlapRatio { get; init; } = 0.15;
    }
}
