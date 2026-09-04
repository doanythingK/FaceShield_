namespace FaceShield.Services.FaceDetection
{
    internal interface IExecutionProviderDiagnostics
    {
        string ExecutionProviderLabel { get; }
        string? ExecutionProviderError { get; }
    }
}
