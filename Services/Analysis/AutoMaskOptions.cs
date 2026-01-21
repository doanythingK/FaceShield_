namespace FaceShield.Services.Analysis
{
    public enum DownscaleQuality
    {
        FastNearest,
        BalancedBilinear
    }

    public sealed class AutoMaskOptions
    {
        /// <summary>
        /// 1.0 = 원본 해상도, 0.5 = 가로/세로 절반
        /// </summary>
        public double DownscaleRatio { get; init; } = 1.0;

        /// <summary>
        /// 다운스케일 품질/속도 선택.
        /// </summary>
        public DownscaleQuality DownscaleQuality { get; init; } = DownscaleQuality.BalancedBilinear;

        /// <summary>
        /// true면 DetectEveryNFrames 간격으로만 검출하고 중간 프레임은 이전 결과를 재사용.
        /// </summary>
        public bool UseTracking { get; init; } = false;

        /// <summary>
        /// 검출 간격 (1이면 모든 프레임 검출)
        /// </summary>
        public int DetectEveryNFrames { get; init; } = 1;

        /// <summary>
        /// 병렬 ONNX 세션 수 (파이프라인 모드에서만 적용).
        /// </summary>
        public int ParallelDetectorCount { get; init; } = 2;

        /// <summary>
        /// 정확도 유지용 하이브리드 재검출 사용 여부.
        /// </summary>
        public bool EnableHybridRefine { get; init; } = false;

        /// <summary>
        /// 하이브리드 1차 다운스케일 비율.
        /// </summary>
        public double HybridDownscaleRatio { get; init; } = 0.67;

        /// <summary>
        /// 하이브리드 정밀 재검출 간격 (N프레임마다 원본 재검출).
        /// </summary>
        public int HybridRefineInterval { get; init; } = 8;

        /// <summary>
        /// 작은 얼굴 판별 면적 비율 (프레임 대비).
        /// </summary>
        public double HybridSmallFaceAreaRatio { get; init; } = 0.0012;

        /// <summary>
        /// 검출 프록시 생성 프리셋.
        /// </summary>
        public FaceShield.Services.Video.DetectionProxyPreset ProxyPreset { get; init; } = FaceShield.Services.Video.DetectionProxyPreset.Default;
    }
}
