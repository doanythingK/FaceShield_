namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceDetectorFactoryOptions
    {
        public FaceDetectorBackend Backend { get; init; } = FaceDetectorBackend.FaceOnnx;

        public FaceOnnxDetectorOptions? FaceOnnxOptions { get; init; }

        public static FaceDetectorFactoryOptions ForOnnx(FaceOnnxDetectorOptions? options)
        {
            return new FaceDetectorFactoryOptions
            {
                Backend = FaceDetectorBackend.FaceOnnx,
                FaceOnnxOptions = options
            };
        }
    }
}
