namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceOnnxDetectorOptions
    {
        /// <summary>
        /// ONNX Runtime 최적화 옵션 사용 여부.
        /// </summary>
        public bool UseOrtOptimization { get; init; } = true;

        /// <summary>
        /// GPU 실행 공급자 사용 시도 (가능한 경우).
        /// </summary>
        public bool UseGpu { get; init; } = false;

        /// <summary>
        /// GPU 실행 공급자 장치 ID (null이면 기본 장치).
        /// </summary>
        public int? GpuDeviceId { get; init; }

        /// <summary>
        /// true면 GPU 실행 공급자를 붙이지 못한 후보를 자동 튜닝에서 제외.
        /// </summary>
        public bool RequireGpuExecutionProvider { get; init; } = false;

        /// <summary>
        /// Intra-op 스레드 수 (null이면 기본값).
        /// </summary>
        public int? IntraOpNumThreads { get; init; }

        /// <summary>
        /// Inter-op 스레드 수 (null이면 기본값).
        /// </summary>
        public int? InterOpNumThreads { get; init; }

        /// <summary>
        /// ONNX Runtime parallel execution mode 사용 여부 (null이면 기본값).
        /// </summary>
        public bool? UseParallelExecution { get; init; }

        /// <summary>
        /// 검출 임계값 (null이면 기본값).
        /// </summary>
        public float? DetectionThreshold { get; init; }

        /// <summary>
        /// 신뢰도 임계값 (null이면 기본값).
        /// </summary>
        public float? ConfidenceThreshold { get; init; }

        /// <summary>
        /// NMS 임계값 (null이면 기본값).
        /// </summary>
        public float? NmsThreshold { get; init; }

        /// <summary>
        /// 전처리 병렬화 허용 여부 (null이면 기본값).
        /// </summary>
        public bool? EnablePreprocessParallelism { get; init; }

        /// <summary>
        /// 자동 튜닝 허용 여부 (null이면 기본값).
        /// </summary>
        public bool? AllowAutoTune { get; init; } = true;

        /// <summary>
        /// GPU 자동 선택 허용 여부 (null이면 기본값).
        /// </summary>
        public bool? AllowAutoGpu { get; init; } = false;
    }
}
