param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = if ([IO.Path]::IsPathRooted($SourcePath)) {
    (Resolve-Path $SourcePath).Path
}
else {
    (Resolve-Path (Join-Path $repo $SourcePath)).Path
}
$work = Join-Path $repo ".tmp\automask-pipeline-faults"
$project = Join-Path $work "AutoMaskPipelineFaultsHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

@'
using Avalonia.Media.Imaging;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

if (args.Length != 2)
    throw new ArgumentException("Expected <case> <source>.");

string caseName = args[0];
string sourcePath = args[1];
FFmpegBootstrap.Initialize();

switch (caseName)
{
    case "single-success":
        await RunSuccessCase(
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 1),
            factory: null,
            expectedMode: "pipe-single");
        break;

    case "parallel-success":
        await RunSuccessCase(
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 2),
            new TestDetectorFactory(() => new EmptyDetector()),
            expectedMode: "pipe-parallel");
        break;

    case "sparse-success":
        await RunSuccessCase(
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Tracked, 5, 2),
            new TestDetectorFactory(() => new EmptyDetector()),
            expectedMode: "sparse-pipe-parallel");
        break;

    case "single-detector-fault":
        await RunFaultCase(
            caseName,
            new ThrowingDetector(caseName),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 1),
            factory: null,
            callback: null);
        break;

    case "single-writer-fault":
        await RunFaultCase(
            caseName,
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 1),
            factory: null,
            callback: _ => throw new InjectedPipelineException(caseName));
        break;

    case "parallel-detector-fault":
        await RunFaultCase(
            caseName,
            new ThrowingDetector(caseName),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 2),
            new TestDetectorFactory(() => new ThrowingDetector(caseName)),
            callback: null);
        break;

    case "parallel-writer-fault":
        await RunFaultCase(
            caseName,
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Raw, 1, 2),
            new TestDetectorFactory(() => new EmptyDetector()),
            callback: _ => throw new InjectedPipelineException(caseName));
        break;

    case "sparse-detector-fault":
        await RunFaultCase(
            caseName,
            new ThrowingDetector(caseName),
            CreateOptions(AutoMaskProcessingMode.Tracked, 5, 2),
            new TestDetectorFactory(() => new ThrowingDetector(caseName)),
            callback: null);
        break;

    case "sparse-producer-callback-fault":
        await RunFaultCase(
            caseName,
            new EmptyDetector(),
            CreateOptions(AutoMaskProcessingMode.Tracked, 5, 2),
            new TestDetectorFactory(() => new EmptyDetector()),
            callback: _ => throw new InjectedPipelineException(caseName));
        break;

    case "running-cancel":
        await RunCancellationCase();
        break;

    case "pre-cancel-private":
        await RunPreCanceledPrivateCase();
        break;

    default:
        throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case.");
}

Console.WriteLine($"[AutoMaskPipelineFaultsVerify] PASS case={caseName}");

async Task RunSuccessCase(
    IBgraFaceDetector detector,
    AutoMaskOptions options,
    IFaceDetectorFactory? factory,
    string expectedMode)
{
    using (detector)
    {
        var generator = new AutoMaskGenerator(
            detector,
            new FrameMaskProvider(),
            options,
            factory);
        try
        {
            await generator.GenerateAsync(sourcePath, null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Successful pipeline timed out: {expectedMode}", ex);
        }

        AutoMaskRunSummary summary = generator.LastRunSummary
            ?? throw new InvalidOperationException($"Summary was not created: {expectedMode}");
        if (summary.TotalFrames < 120)
            throw new InvalidOperationException("Pipeline fixture must contain at least 120 frames.");
        if (!string.Equals(summary.Mode, expectedMode, StringComparison.Ordinal) ||
            summary.DecodedFrames != summary.TotalFrames ||
            !summary.ReachedDecoderEof ||
            summary.DecodeCancelled ||
            !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected success summary: {summary.ToLogLine()}");
        }
    }
}

async Task RunFaultCase(
    string marker,
    IBgraFaceDetector detector,
    AutoMaskOptions options,
    IFaceDetectorFactory? factory,
    Action<int>? callback)
{
    using (detector)
    {
        var generator = new AutoMaskGenerator(
            detector,
            new FrameMaskProvider(),
            options,
            factory);
        Task operation = generator.GenerateAsync(
            sourcePath,
            progress: null,
            CancellationToken.None,
            startFrameIndex: 0,
            onFrameProcessed: callback);

        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (InjectedPipelineException ex) when (ex.Marker == marker)
        {
            return;
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Pipeline timed out: {marker}", ex);
        }

        throw new InvalidOperationException($"Injected failure was not propagated: {marker}");
    }
}

async Task RunCancellationCase()
{
    using var entered = new ManualResetEventSlim();
    using var detector = new SlowDetector(entered);
    using var cts = new CancellationTokenSource();
    var generator = new AutoMaskGenerator(
        detector,
        new FrameMaskProvider(),
        CreateOptions(AutoMaskProcessingMode.Raw, 1, 1));
    Task operation = generator.GenerateAsync(sourcePath, null, cts.Token);
    if (!entered.Wait(TimeSpan.FromSeconds(2)))
        throw new InvalidOperationException("Detector did not start before cancellation.");

    cts.Cancel();
    try
    {
        await operation.WaitAsync(TimeSpan.FromSeconds(4));
    }
    catch (TimeoutException ex)
    {
        throw new InvalidOperationException("Running cancellation timed out.", ex);
    }
}

async Task RunPreCanceledPrivateCase()
{
    using var detector = new EmptyDetector();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var generator = new AutoMaskGenerator(
        detector,
        new FrameMaskProvider(),
        CreateOptions(AutoMaskProcessingMode.Raw, 1, 1));
    MethodInfo method = typeof(AutoMaskGenerator).GetMethod(
        "GeneratePipelinedDetectAll",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("GeneratePipelinedDetectAll was not found.");

    Task invocation = Task.Run(() =>
    {
        bool cancellationObserved = false;
        try
        {
            _ = method.Invoke(
                generator,
                [sourcePath, detector, null, cts.Token, 0, 120, null]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            cancellationObserved = true;
        }

        if (!cancellationObserved)
            throw new InvalidOperationException("Pre-canceled private pipeline did not propagate cancellation.");
    });

    try
    {
        await invocation.WaitAsync(TimeSpan.FromSeconds(4));
    }
    catch (TimeoutException ex)
    {
        throw new InvalidOperationException("Pre-canceled private pipeline timed out.", ex);
    }
}

static AutoMaskOptions CreateOptions(
    AutoMaskProcessingMode mode,
    int detectEvery,
    int parallelDetectors)
{
    return new AutoMaskOptions
    {
        ProcessingMode = mode,
        DownscaleRatio = 1.0,
        DetectEveryNFrames = detectEvery,
        ParallelDetectorCount = parallelDetectors,
        FilterProfile = FaceFilterProfile.FaceOnnx
    };
}

sealed class InjectedPipelineException : InvalidOperationException
{
    public InjectedPipelineException(string marker)
        : base($"Injected pipeline failure: {marker}")
    {
        Marker = marker;
    }

    public string Marker { get; }
}

class EmptyDetector : IBgraFaceDetector
{
    public virtual IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame) =>
        Array.Empty<FaceDetectionResult>();

    public virtual IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(
        WriteableBitmap frame,
        double ratio,
        DownscaleQuality quality) => Array.Empty<FaceDetectionResult>();

    public virtual IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality) => Array.Empty<FaceDetectionResult>();

    public virtual void Dispose()
    {
    }
}

sealed class ThrowingDetector : EmptyDetector
{
    private readonly string _marker;

    public ThrowingDetector(string marker)
    {
        _marker = marker;
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        throw new InjectedPipelineException(_marker);
    }
}

sealed class SlowDetector : EmptyDetector
{
    private readonly ManualResetEventSlim _entered;

    public SlowDetector(ManualResetEventSlim entered)
    {
        _entered = entered;
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        _entered.Set();
        Thread.Sleep(150);
        return Array.Empty<FaceDetectionResult>();
    }
}

sealed class TestDetectorFactory : IFaceDetectorFactory
{
    private readonly Func<IBgraFaceDetector> _create;

    public TestDetectorFactory(Func<IBgraFaceDetector> create)
    {
        _create = create;
    }

    public IFaceDetector CreateDetector() => _create();
}
'@ | Set-Content -Encoding UTF8 $program

dotnet build $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$harness = Join-Path $work "bin\Debug\net8.0\AutoMaskPipelineFaultsHarness.dll"
$cases = @(
    "single-success",
    "parallel-success",
    "sparse-success",
    "single-detector-fault",
    "single-writer-fault",
    "parallel-detector-fault",
    "parallel-writer-fault",
    "sparse-detector-fault",
    "sparse-producer-callback-fault",
    "running-cancel",
    "pre-cancel-private"
)

foreach ($case in $cases) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & dotnet $harness $case $source 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "Pipeline case failed: $case"
    }
    if (-not (($output | Out-String).Contains("PASS case=$case"))) {
        throw "Pipeline case did not report PASS: $case"
    }
}

$sourceText = Get-Content -Raw (Join-Path $repo "Services\Analysis\AutoMaskGenerator.cs")
if ([regex]::Matches($sourceText, "pipeline\.ThrowIfFailedOrCanceled\(\);").Count -lt 3) {
    throw "All three bounded pipelines must propagate their first failure."
}
if ([regex]::Matches($sourceText, "ReturnQueuedBuffers\(queue, pool\);").Count -lt 3) {
    throw "All three bounded pipelines must return queued buffers."
}
if ($sourceText.Contains("GetConsumingEnumerable()")) {
    throw "Bounded pipeline consumers must use the linked cancellation token."
}

Write-Host "[AutoMaskPipelineFaultsVerify] PASS cases=$($cases.Count)"
