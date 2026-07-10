using FaceShield.Services.FaceDetection;

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

    /// <summary>
    /// 자동 마스크 분석 단계의 동작 범위.
    /// Legacy는 기존 저장 설정 및 결과 호환을 위해 기존 bool 조합을 그대로 사용합니다.
    /// </summary>
    public enum AutoMaskProcessingMode
    {
        Legacy = 0,
        Raw = 1,
        Tracked = 2,
        Full = 3
    }

    public sealed class AutoMaskOptions
    {
        /// <summary>
        /// 분석 단계의 명시 모드. 기본값 Legacy는 기존 동작을 변경하지 않습니다.
        /// </summary>
        public AutoMaskProcessingMode ProcessingMode { get; init; } = AutoMaskProcessingMode.Legacy;

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
        /// 후처리 전체 활성화 토글 (temporal/smoothing/scene-cut/최종 보정 계열 단계).
        /// false일 때는 고급 후처리 단계는 비활성화되며, 미세한 탐지 누락 구간의 최소 보완은 동작합니다.
        /// </summary>
        public bool EnablePostProcessing { get; init; } = false;

        /// <summary>
        /// ROI 보강 후처리 단계(객체 내부 재검증/정제 단계) 사용 여부.
        /// </summary>
        public bool EnableRoiPostProcess { get; init; } = false;

        /// <summary>
        /// YOLO 전용: 약한 단일/열림 오탐 후보 제거 단계.
        /// </summary>
        public bool EnableYoloWeakIsolatedCleanup { get; init; } = false;

        /// <summary>
        /// YOLO 전용: 추적/마스킹 간극 짧은 구간 보강 단계.
        /// </summary>
        public bool EnableYoloGapFill { get; init; } = false;

        /// <summary>
        /// YOLO 전용: 장면 전환 경계 기반 carry 제거 단계.
        /// </summary>
        public bool EnableYoloSceneCutCarryCleanup { get; init; } = false;

        /// <summary>
        /// YOLO 전용: temporal smoothing 단계.
        /// </summary>
        public bool EnableYoloTemporalSmoothing { get; init; } = false;

        /// <summary>
        /// YOLO 위험 프레임만 강한 FaceONNX 검출기로 재검증합니다. 기본값은 검증 전 안전을 위해 false입니다.
        /// </summary>
        public bool EnableYoloRiskCascade { get; init; } = false;

        public double YoloStrongFullScanIntervalSeconds { get; init; } = 2.0;

        public float YoloRiskLowConfidenceThreshold { get; init; } = 0.45f;

        public double YoloRiskSmallFaceAreaRatio { get; init; } = 0.0012;

        public double YoloRiskEdgeMarginRatio { get; init; } = 0.025;

        public int YoloRiskMaxTrackGapFrames { get; init; } = 8;

        public int YoloStrongConfirmationFrames { get; init; } = 2;

        /// <summary>
        /// YOLO single-session 경로에서 이전 얼굴 ROI만 먼저 검사하는 단축 경로입니다.
        /// false이면 신규 진입 얼굴 보호를 위해 매 검출 프레임 전체 화면을 검사합니다.
        /// </summary>
        public bool EnableYoloPrimaryRoiShortcut { get; init; } = true;

        public FaceOnnxDetectorOptions? YoloSecondaryDetectorOptions { get; init; }

        /// <summary>
        /// 검출 간격 (1이면 모든 프레임 검출)
        /// </summary>
        public int DetectEveryNFrames { get; init; } = 1;

        /// <summary>
        /// ROI 후처리에서 사용할 face-onx 후보 검출기 옵션.
        /// null이면 ROI 단계는 스킵됩니다.
        /// </summary>
        public FaceOnnxDetectorOptions? RoiRefinerDetectorOptions { get; init; }

        /// <summary>
        /// ROI refine 단계에서 bgra 기반 탐지기 대신 FaceOnnx 전용 후보 탐지기를 사용할지 여부.
        /// </summary>
        public bool UseFaceOnnxRoiRefiner { get; init; } = false;

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

        /// <summary>
        /// 명시 모드의 불변 조건을 적용한 실행 옵션을 만듭니다.
        /// Legacy는 기존 옵션 조합을 그대로 보존합니다.
        /// </summary>
        public AutoMaskOptions ResolveProcessingMode()
        {
            if (ProcessingMode == AutoMaskProcessingMode.Legacy)
                return this;

            bool useTracking = ProcessingMode != AutoMaskProcessingMode.Raw;
            bool enablePostProcessing = ProcessingMode == AutoMaskProcessingMode.Full;

            return new AutoMaskOptions
            {
                ProcessingMode = ProcessingMode,
                DownscaleRatio = DownscaleRatio,
                DownscaleQuality = DownscaleQuality,
                UseTracking = useTracking,
                EnablePostProcessing = enablePostProcessing,
                EnableRoiPostProcess = enablePostProcessing,
                EnableYoloWeakIsolatedCleanup = enablePostProcessing,
                EnableYoloGapFill = enablePostProcessing,
                EnableYoloSceneCutCarryCleanup = enablePostProcessing,
                EnableYoloTemporalSmoothing = enablePostProcessing,
                EnableYoloRiskCascade = ProcessingMode == AutoMaskProcessingMode.Full && EnableYoloRiskCascade,
                YoloStrongFullScanIntervalSeconds = YoloStrongFullScanIntervalSeconds,
                YoloRiskLowConfidenceThreshold = YoloRiskLowConfidenceThreshold,
                YoloRiskSmallFaceAreaRatio = YoloRiskSmallFaceAreaRatio,
                YoloRiskEdgeMarginRatio = YoloRiskEdgeMarginRatio,
                YoloRiskMaxTrackGapFrames = YoloRiskMaxTrackGapFrames,
                YoloStrongConfirmationFrames = YoloStrongConfirmationFrames,
                EnableYoloPrimaryRoiShortcut = false,
                YoloSecondaryDetectorOptions = ProcessingMode == AutoMaskProcessingMode.Full
                    ? YoloSecondaryDetectorOptions
                    : null,
                DetectEveryNFrames = useTracking ? System.Math.Max(1, DetectEveryNFrames) : 1,
                RoiRefinerDetectorOptions = enablePostProcessing ? RoiRefinerDetectorOptions : null,
                UseFaceOnnxRoiRefiner = enablePostProcessing && UseFaceOnnxRoiRefiner,
                ParallelDetectorCount = System.Math.Max(1, ParallelDetectorCount),
                RunId = RunId,
                FilterProfile = FilterProfile,
                DumpDetectionDiagnostics = DumpDetectionDiagnostics
            };
        }
    }
}
