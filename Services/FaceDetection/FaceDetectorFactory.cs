using System;

namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceDetectorFactory : IFaceDetectorFactory
    {
        private readonly FaceDetectorFactoryOptions _options;

        public FaceDetectorFactory(FaceDetectorFactoryOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public static FaceDetectorFactory ForOnnx(FaceOnnxDetectorOptions? options)
        {
            return new FaceDetectorFactory(FaceDetectorFactoryOptions.ForOnnx(options));
        }

        public IFaceDetector CreateDetector()
        {
            return _options.Backend switch
            {
                FaceDetectorBackend.FaceOnnx => new FaceOnnxDetector(_options.FaceOnnxOptions),
                _ => throw new NotSupportedException($"Unsupported face detector backend: {_options.Backend}")
            };
        }
    }
}
