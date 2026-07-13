namespace FaceShield.Services.Video
{
    public readonly struct ExportProgress
    {
        public int FrameIndex { get; }
        public int TotalFrames { get; }
        public string? StatusMessage { get; }
        public bool IsComplete { get; }

        public ExportProgress(int frameIndex, int totalFrames, string? statusMessage = null)
            : this(frameIndex, totalFrames, statusMessage, isComplete: false)
        {
        }

        private ExportProgress(
            int frameIndex,
            int totalFrames,
            string? statusMessage,
            bool isComplete)
        {
            FrameIndex = frameIndex;
            TotalFrames = totalFrames;
            StatusMessage = statusMessage;
            IsComplete = isComplete;
        }

        public static ExportProgress Completed(int totalFrames, string? statusMessage = null)
        {
            int boundedTotal = System.Math.Max(1, totalFrames);
            return new ExportProgress(
                boundedTotal,
                boundedTotal,
                statusMessage,
                isComplete: true);
        }

        public int Percent =>
            IsComplete
                ? 100
                : TotalFrames > 0
                    ? System.Math.Min(
                        99,
                        (int)System.Math.Round(
                            FrameIndex * 100.0 / System.Math.Max(1, TotalFrames)))
                    : 0;
    }
}
