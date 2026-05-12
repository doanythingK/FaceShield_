namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceDetectorFactoryOptions
    {
        public FaceDetectorBackend Backend { get; init; } = FaceDetectorBackend.FaceOnnx;

        public FaceOnnxDetectorOptions? FaceOnnxOptions { get; init; }

        public ScrfdOnnxDetectorOptions? ScrfdOnnxOptions { get; init; }

        public YuNetOnnxDetectorOptions? YuNetOnnxOptions { get; init; }

        public static FaceDetectorFactoryOptions ForOnnx(FaceOnnxDetectorOptions? options)
        {
            return new FaceDetectorFactoryOptions
            {
                Backend = FaceDetectorBackend.FaceOnnx,
                FaceOnnxOptions = options
            };
        }

        public static FaceDetectorFactoryOptions ForScrfdOnnx(ScrfdOnnxDetectorOptions options)
        {
            return new FaceDetectorFactoryOptions
            {
                Backend = FaceDetectorBackend.ScrfdOnnx,
                ScrfdOnnxOptions = options
            };
        }

        public static FaceDetectorFactoryOptions ForYuNetOnnx(YuNetOnnxDetectorOptions options)
        {
            return new FaceDetectorFactoryOptions
            {
                Backend = FaceDetectorBackend.YuNetOnnx,
                YuNetOnnxOptions = options
            };
        }

        public FaceDetectorFactoryOptions WithFaceOnnxOptions(FaceOnnxDetectorOptions? options)
        {
            return new FaceDetectorFactoryOptions
            {
                Backend = Backend,
                FaceOnnxOptions = options,
                ScrfdOnnxOptions = ScrfdOnnxOptions,
                YuNetOnnxOptions = YuNetOnnxOptions
            };
        }
    }
}
