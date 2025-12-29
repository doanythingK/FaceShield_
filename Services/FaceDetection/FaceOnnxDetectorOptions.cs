namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceOnnxDetectorOptions
    {
        /// <summary>
        /// ONNX Runtime 최적화 옵션 사용 여부.
        /// </summary>
        public bool UseOrtOptimization { get; init; } = false;

        /// <summary>
        /// Intra-op 스레드 수 (null이면 기본값).
        /// </summary>
        public int? IntraOpNumThreads { get; init; }

        /// <summary>
        /// Inter-op 스레드 수 (null이면 기본값).
        /// </summary>
        public int? InterOpNumThreads { get; init; }
    }
}
