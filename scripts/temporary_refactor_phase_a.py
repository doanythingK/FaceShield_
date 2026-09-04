from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8", newline="")
    candidates = [(old, new)]
    if "\n" in old:
        candidates.append((old.replace("\n", "\r\n"), new.replace("\n", "\r\n")))
    for old_candidate, new_candidate in candidates:
        if old_candidate in text:
            if text.count(old_candidate) != 1:
                raise RuntimeError(f"Expected exactly one match in {path}, found {text.count(old_candidate)}")
            p.write_text(text.replace(old_candidate, new_candidate, 1), encoding="utf-8", newline="")
            return
    raise RuntimeError(f"Patch target not found in {path}: {old[:120]!r}")


replace_once(
    "Services/Video/IFrameMaskProvider.cs",
    """        /// <summary>\n        /// 프레임별 마스크 저장/갱신.\n        /// (프리뷰에서 만든 마스크, 추후 자동 분석에서 생성한 마스크 등)\n        /// </summary>\n        void SetMask(int frameIndex, WriteableBitmap mask);\n""",
    """        /// <summary>\n        /// 프레임별 마스크 저장/갱신.\n        /// (프리뷰에서 만든 마스크, 추후 자동 분석에서 생성한 마스크 등)\n        /// 호출이 성공하면 bitmap 소유권은 provider로 이전됩니다.\n        /// 호출자는 이후 bitmap을 수정하거나 Dispose하면 안 됩니다.\n        /// </summary>\n        void SetMask(int frameIndex, WriteableBitmap mask);\n""")

replace_once(
    "Services/Video/FrameMaskProvider.cs",
    """    private readonly ConcurrentDictionary<int, FaceMaskData> _faceMasks = new();\n    private long _version;\n\n    internal sealed class SparseFaceMaskWorkingCopy\n""",
    """    private readonly ConcurrentDictionary<int, FaceMaskData> _faceMasks = new();\n    private long _version;\n    private readonly bool _allowsBorrowedBitmapReads;\n\n    public FrameMaskProvider()\n    {\n    }\n\n    private FrameMaskProvider(bool allowsBorrowedBitmapReads)\n    {\n        _allowsBorrowedBitmapReads = allowsBorrowedBitmapReads;\n    }\n\n    internal sealed class SparseFaceMaskWorkingCopy\n""")

replace_once(
    "Services/Video/FrameMaskProvider.cs",
    """\n\n    public void SetMask(int frameIndex, WriteableBitmap mask)\n    {\n""",
    """\n\n    /// <summary>\n    /// Stores the supplied bitmap and takes ownership of it.\n    /// After this call succeeds, the caller must not mutate or dispose the bitmap.\n    /// </summary>\n    public void SetMask(int frameIndex, WriteableBitmap mask)\n    {\n""")

replace_once(
    "Services/Video/FrameMaskProvider.cs",
    """    internal bool TryGetStoredMaskBorrowed(int frameIndex, out WriteableBitmap mask)\n    {\n        lock (_stateGate)\n            return _masks.TryGetValue(frameIndex, out mask!);\n    }\n""",
    """    internal bool TryGetStoredMaskBorrowed(int frameIndex, out WriteableBitmap mask)\n    {\n        if (!_allowsBorrowedBitmapReads)\n        {\n            throw new InvalidOperationException(\n                \"Borrowed stored-mask reads are only allowed on detached provider snapshots.\");\n        }\n\n        lock (_stateGate)\n            return _masks.TryGetValue(frameIndex, out mask!);\n    }\n""")

replace_once(
    "Services/Video/FrameMaskProvider.cs",
    """            var snapshot = new FrameMaskProvider();\n""",
    """            var snapshot = new FrameMaskProvider(allowsBorrowedBitmapReads: true);\n""")

replace_once(
    "Services/Video/FrameMaskProvider.cs",
    """    public readonly record struct FaceMaskData(\n        PixelSize Size,\n        IReadOnlyList<Rect> Faces,\n        float? MinConfidence,\n        IReadOnlyList<float> Confidences);\n""",
    """    public readonly record struct FaceMaskData\n    {\n        public FaceMaskData(\n            PixelSize size,\n            IReadOnlyList<Rect> faces,\n            float? minConfidence,\n            IReadOnlyList<float> confidences)\n        {\n            Size = size;\n            Faces = Array.AsReadOnly(\n                faces == null ? Array.Empty<Rect>() : faces.ToArray());\n            MinConfidence = minConfidence;\n            Confidences = Array.AsReadOnly(\n                confidences == null ? Array.Empty<float>() : confidences.ToArray());\n        }\n\n        public PixelSize Size { get; }\n        public IReadOnlyList<Rect> Faces { get; }\n        public float? MinConfidence { get; }\n        public IReadOnlyList<float> Confidences { get; }\n    }\n""")

replace_once(
    "Services/FaceDetection/FaceOnnxDetector.cs",
    """    public sealed class FaceOnnxDetector : IBgraFaceDetector\n""",
    """    public sealed class FaceOnnxDetector : IBgraFaceDetector, IExecutionProviderDiagnostics\n""")

replace_once(
    "Services/FaceDetection/FaceOnnxDetector.cs",
    """        internal string ExecutionProviderLabel { get; }\n        internal string? ExecutionProviderError { get; private set; }\n\n        internal bool UsesGpuExecutionProvider =>\n""",
    """        internal string ExecutionProviderLabel { get; }\n        internal string? ExecutionProviderError { get; private set; }\n\n        string IExecutionProviderDiagnostics.ExecutionProviderLabel => ExecutionProviderLabel;\n        string? IExecutionProviderDiagnostics.ExecutionProviderError => ExecutionProviderError;\n\n        internal bool UsesGpuExecutionProvider =>\n""")

replace_once(
    "Services/FaceDetection/YoloFaceOnnxDetector.cs",
    """    public sealed class YoloFaceOnnxDetector : IBgraFaceDetector\n""",
    """    public sealed class YoloFaceOnnxDetector : IBgraFaceDetector, IExecutionProviderDiagnostics\n""")

replace_once(
    "Services/FaceDetection/YoloFaceOnnxDetector.cs",
    """        internal string ExecutionProviderLabel { get; }\n        internal string? ExecutionProviderError { get; private set; }\n\n        private static readonly (float Width, float Height)[][] Yolo5FaceAnchors =\n""",
    """        internal string ExecutionProviderLabel { get; }\n        internal string? ExecutionProviderError { get; private set; }\n\n        string IExecutionProviderDiagnostics.ExecutionProviderLabel => ExecutionProviderLabel;\n        string? IExecutionProviderDiagnostics.ExecutionProviderError => ExecutionProviderError;\n\n        private static readonly (float Width, float Height)[][] Yolo5FaceAnchors =\n""")

replace_once(
    "Services/Analysis/AutoMaskGenerator.cs",
    """        private string GetDetectorName()\n        {\n            if (_detector is FaceOnnxDetector)\n            {\n                string provider = FaceOnnxDetector.GetLastExecutionProviderLabel();\n                string? error = FaceOnnxDetector.GetLastExecutionProviderError();\n                if (!string.IsNullOrWhiteSpace(error))\n                    return $\"{_detector.GetType().Name}/{provider}({error})\";\n\n                return $\"{_detector.GetType().Name}/{provider}\";\n            }\n\n            if (_detector is YoloFaceOnnxDetector)\n            {\n                string provider = YoloFaceOnnxDetector.GetLastExecutionProviderLabel();\n                string? error = YoloFaceOnnxDetector.GetLastExecutionProviderError();\n                if (!string.IsNullOrWhiteSpace(error))\n                    return $\"{_detector.GetType().Name}/{provider}({error})\";\n\n                return $\"{_detector.GetType().Name}/{provider}\";\n            }\n\n            return _detector.GetType().Name;\n        }\n""",
    """        private string GetDetectorName()\n        {\n            if (_detector is IExecutionProviderDiagnostics diagnostics)\n            {\n                string provider = diagnostics.ExecutionProviderLabel;\n                string? error = diagnostics.ExecutionProviderError;\n                if (!string.IsNullOrWhiteSpace(error))\n                    return $\"{_detector.GetType().Name}/{provider}({error})\";\n\n                return $\"{_detector.GetType().Name}/{provider}\";\n            }\n\n            return _detector.GetType().Name;\n        }\n""")

Path("Services/FaceDetection/IExecutionProviderDiagnostics.cs").write_text(
    """namespace FaceShield.Services.FaceDetection\n{\n    internal interface IExecutionProviderDiagnostics\n    {\n        string ExecutionProviderLabel { get; }\n        string? ExecutionProviderError { get; }\n    }\n}\n""",
    encoding="utf-8",
    newline="")

print("Phase A ownership and diagnostics patch applied.")
