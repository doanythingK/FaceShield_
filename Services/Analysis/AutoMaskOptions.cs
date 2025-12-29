namespace FaceShield.Services.Analysis
{
    public sealed class AutoMaskOptions
    {
        /// <summary>
        /// 1.0 = 원본 해상도, 0.5 = 가로/세로 절반
        /// </summary>
        public double DownscaleRatio { get; init; } = 1.0;

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
