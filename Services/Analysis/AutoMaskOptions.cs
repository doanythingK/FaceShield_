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
    }
}
