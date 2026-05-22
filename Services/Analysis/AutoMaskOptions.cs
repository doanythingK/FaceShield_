namespace FaceShield.Services.Analysis
{
    public enum DownscaleQuality
    {
        FastNearest,
        BalancedBilinear
    }

    public enum FaceFilterProfile
    {
        FaceOnnx,
        Scrfd,
        Yolo
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
        /// true면 DetectEveryNFrames 간격으로 검출하고, 간격이 2 이상일 때 중간 프레임을 추적/보간한다.
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
        /// 자동 검출과 export 로그를 같은 실행 단위로 묶기 위한 식별자.
        /// </summary>
        public string? RunId { get; init; }

        /// <summary>
        /// detector 출력 특성에 맞춘 후보 필터 프로필.
        /// </summary>
        public FaceFilterProfile FilterProfile { get; init; } = FaceFilterProfile.FaceOnnx;

        /// <summary>
        /// detector raw 후보와 post-filter 후보 수를 frame별 로그로 남긴다.
        /// </summary>
        public bool DumpDetectionDiagnostics { get; init; } = false;
    }
}
