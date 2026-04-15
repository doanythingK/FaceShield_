namespace FaceShield.Services.FaceDetection
{
    public interface IFaceDetectorFactory
    {
        IFaceDetector CreateDetector();
    }
}
