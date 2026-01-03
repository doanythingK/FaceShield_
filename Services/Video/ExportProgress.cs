namespace FaceShield.Services.Video
{
    public readonly struct ExportProgress
    {
        public int FrameIndex { get; }
        public int TotalFrames { get; }

        public ExportProgress(int frameIndex, int totalFrames)
        {
            FrameIndex = frameIndex;
            TotalFrames = totalFrames;
        }

        public int Percent =>
            TotalFrames > 0
                ? (int)System.Math.Round(FrameIndex * 100.0 / System.Math.Max(1, TotalFrames))
                : 0;
    }
}
