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
$work = Join-Path $repo ".tmp\automask-resume-eof"
$project = Join-Path $work "AutoMaskResumeEofHarness.csproj"
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
using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

if (args.Length != 1)
    throw new ArgumentException("Expected <source>.");

string sourcePath = args[0];
FFmpegBootstrap.Initialize();

int actualFrames = await VerifyTrackedResumeMatchesContinuous();
await VerifyFullReplayCancellation(actualFrames);
await VerifyMisalignedSparseResume();
await VerifySparseCancellationWatermark();
await VerifySparseFaultWatermark();
await VerifySparseOutOfOrderFaultCleanup();
await VerifyMetadataFrameCountMismatch(actualFrames);
await VerifyStaleResumeRestartsAtZero(actualFrames);

Console.WriteLine($"[AutoMaskResumeEofVerify] PASS actualFrames={actualFrames} trackedResume=full-replay firstCallbackCancel=zero misalignedSparse=full-replay sparseCancelWatermark=10 sparseFaultWatermark=0 sparseOutOfOrderClean=true sparseResumeEquivalent=true metadataMismatch=zero+under+over staleResume=full-replay");

async Task<int> VerifyTrackedResumeMatchesContinuous()
{
    AutoMaskOptions options = CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 1);
    var continuousProvider = new FrameMaskProvider();
    using (var detector = new IndexedDetector())
    {
        var generator = new AutoMaskGenerator(detector, continuousProvider, options);
        await generator.GenerateAsync(sourcePath, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(12));
        AssertComplete(generator.LastRunSummary, expectedStart: 0, "continuous tracked run");
    }

    int actualFrames = continuousProvider.GetFaceMaskFrameIndices().Length > 0
        ? (continuousProvider.GetFaceMaskFrameIndices().Max() + 1)
        : 0;
    using (var detector = new EmptyDetector())
    {
        var countGenerator = new AutoMaskGenerator(
            detector,
            new FrameMaskProvider(),
            CreateOptions(AutoMaskProcessingMode.Raw, detectEvery: 1));
        await countGenerator.GenerateAsync(sourcePath, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(12));
        AssertComplete(countGenerator.LastRunSummary, expectedStart: 0, "frame-count run");
        actualFrames = countGenerator.LastRunSummary!.TotalFrames;
    }

    if (actualFrames < 24)
        throw new InvalidOperationException($"Resume fixture must contain at least 24 frames, got {actualFrames}.");

    var resumedProvider = new FrameMaskProvider();
    var staleSize = new PixelSize(640, 360);
    resumedProvider.SetFaceRects(0, [new Rect(500, 250, 40, 40)], staleSize, 0.11f, [0.11f]);
    resumedProvider.SetFaceRects(9, [new Rect(510, 250, 40, 40)], staleSize, 0.12f, [0.12f]);
    resumedProvider.SetFaceRects(18, [new Rect(520, 250, 40, 40)], staleSize, 0.13f, [0.13f]);

    var callbacks = new List<int>();
    int resumeIndex = Math.Min(17, actualFrames - 2);
    using (var detector = new IndexedDetector())
    {
        var generator = new AutoMaskGenerator(detector, resumedProvider, options);
        await generator.GenerateAsync(
                sourcePath,
                null,
                CancellationToken.None,
                startFrameIndex: resumeIndex,
                onFrameProcessed: callbacks.Add)
            .WaitAsync(TimeSpan.FromSeconds(12));
        AssertComplete(generator.LastRunSummary, expectedStart: 0, "resumed tracked run");
    }

    if (callbacks.Count == 0 || callbacks[0] != 0 || callbacks[^1] != actualFrames - 1)
    {
        throw new InvalidOperationException(
            $"Tracked resume did not replay the full timeline: callbacks={string.Join(',', callbacks.Take(4))}...{callbacks.LastOrDefault()}.");
    }

    string continuous = SerializeMasks(continuousProvider);
    string resumed = SerializeMasks(resumedProvider);
    if (!string.Equals(continuous, resumed, StringComparison.Ordinal))
        throw new InvalidOperationException("Tracked resume masks differ from a continuous run.");

    return actualFrames;
}

async Task VerifyFullReplayCancellation(int actualFrames)
{
    var provider = new FrameMaskProvider();
    var size = new PixelSize(640, 360);
    provider.SetFaceRects(0, [new Rect(50, 50, 72, 72)], size, 0.95f, [0.95f]);
    provider.SetFaceRects(8, [new Rect(58, 50, 72, 72)], size, 0.95f, [0.95f]);
    using var detector = new IndexedDetector();
    using var cts = new CancellationTokenSource();
    var callbacks = new List<int>();
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 1));

    await generator.GenerateAsync(
        sourcePath,
        null,
        cts.Token,
        startFrameIndex: Math.Min(17, actualFrames - 2),
        onFrameProcessed: frame =>
        {
            callbacks.Add(frame);
            cts.Cancel();
        });

    if (callbacks.Count != 1 || callbacks[0] != 0 || provider.GetFaceMaskFrameIndices().Length != 0)
    {
        throw new InvalidOperationException(
            $"Full replay cancellation did not persist the zero resume boundary before clearing masks: callbacks=[{string.Join(',', callbacks)}] masks={provider.GetFaceMaskFrameIndices().Length}.");
    }
}

async Task VerifySparseCancellationWatermark()
{
    FrameMaskProvider baseline = await GenerateSparseBaseline();
    var provider = new FrameMaskProvider();
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    using var detector = new GateDetector(entered, release, blockAtCall: 3);
    using var cts = new CancellationTokenSource();
    var callbacks = new List<int>();
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 5),
        new TestDetectorFactory(() => new EmptyDetector()));

    Task operation = generator.GenerateAsync(
        sourcePath,
        null,
        cts.Token,
        startFrameIndex: 0,
        onFrameProcessed: callbacks.Add);
    if (!entered.Wait(TimeSpan.FromSeconds(4)))
        throw new InvalidOperationException("Sparse cancellation detector did not reach the third anchor.");

    cts.Cancel();
    release.Set();
    await operation.WaitAsync(TimeSpan.FromSeconds(6));

    AssertSingleWatermark(callbacks, 10, "sparse cancellation");
    AssertMaterializedPrefix(provider, 0, 10, "sparse cancellation");
    await CompleteSparseResume(provider, resumeIndex: 10);
    AssertEquivalentMasks(baseline, provider, "sparse cancellation resume");
}

async Task VerifyMisalignedSparseResume()
{
    FrameMaskProvider baseline = await GenerateSparseBaseline();
    var provider = new FrameMaskProvider();
    var size = new PixelSize(640, 360);
    provider.SetFaceRects(0, [new Rect(500, 250, 40, 40)], size, 0.11f, [0.11f]);
    provider.SetFaceRects(5, [new Rect(510, 250, 40, 40)], size, 0.12f, [0.12f]);
    var callbacks = new List<int>();
    using var detector = new SparseOffsetDetector(startFrame: 0);
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 5),
        new TestDetectorFactory(() => new EmptyDetector()));
    await generator.GenerateAsync(
            sourcePath,
            null,
            CancellationToken.None,
            startFrameIndex: 7,
            onFrameProcessed: callbacks.Add)
        .WaitAsync(TimeSpan.FromSeconds(12));

    AssertComplete(generator.LastRunSummary, expectedStart: 0, "misaligned sparse replay");
    if (callbacks.Count == 0 || callbacks[0] != 0)
        throw new InvalidOperationException("Misaligned sparse resume did not publish the zero replay boundary.");
    AssertEquivalentMasks(baseline, provider, "misaligned sparse replay");
}

async Task VerifySparseFaultWatermark()
{
    FrameMaskProvider baseline = await GenerateSparseBaseline();
    var provider = new FrameMaskProvider();
    using var detector = new FaultAfterDetector(failAtCall: 3);
    var callbacks = new List<int>();
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 5),
        new TestDetectorFactory(() => new EmptyDetector()));

    bool propagated = false;
    try
    {
        await generator.GenerateAsync(
                sourcePath,
                null,
                CancellationToken.None,
                startFrameIndex: 0,
                onFrameProcessed: callbacks.Add)
            .WaitAsync(TimeSpan.FromSeconds(6));
    }
    catch (InjectedDetectorException)
    {
        propagated = true;
        AssertSingleWatermark(callbacks, 0, "sparse detector fault");
        AssertMaterializedPrefix(provider, 0, 0, "sparse detector fault");
    }

    if (!propagated)
        throw new InvalidOperationException("Sparse detector fault was not propagated.");

    await CompleteSparseResume(provider, resumeIndex: 0);
    AssertEquivalentMasks(baseline, provider, "sparse detector fault resume");
}

async Task<FrameMaskProvider> GenerateSparseBaseline()
{
    var provider = new FrameMaskProvider();
    using var detector = new SparseOffsetDetector(startFrame: 0);
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 5),
        new TestDetectorFactory(() => new EmptyDetector()));
    await generator.GenerateAsync(sourcePath, null, CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(12));
    AssertComplete(generator.LastRunSummary, expectedStart: 0, "sparse baseline");
    return provider;
}

async Task VerifySparseOutOfOrderFaultCleanup()
{
    using var laterResultReady = new ManualResetEventSlim();
    using var releaseFirst = new ManualResetEventSlim();
    var coordinator = new OutOfOrderCoordinator(laterResultReady, releaseFirst);
    using var detector = new OutOfOrderDetector(coordinator);
    var provider = new FrameMaskProvider();
    var callbacks = new List<int>();
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        new AutoMaskOptions
        {
            ProcessingMode = AutoMaskProcessingMode.Tracked,
            DownscaleRatio = 1.0,
            DetectEveryNFrames = 5,
            ParallelDetectorCount = 2,
            FilterProfile = FaceFilterProfile.FaceOnnx
        },
        new TestDetectorFactory(() => new OutOfOrderDetector(coordinator)));

    Task operation = generator.GenerateAsync(
        sourcePath,
        null,
        CancellationToken.None,
        startFrameIndex: 0,
        onFrameProcessed: callbacks.Add);
    if (!laterResultReady.Wait(TimeSpan.FromSeconds(4)))
        throw new InvalidOperationException("Out-of-order sparse result was not produced.");

    releaseFirst.Set();
    try
    {
        await operation.WaitAsync(TimeSpan.FromSeconds(6));
    }
    catch (InjectedDetectorException)
    {
        if (callbacks.Count != 0 || provider.GetFaceMaskFrameIndices().Length != 0)
        {
            throw new InvalidOperationException(
                $"Out-of-order sparse masks escaped the safe watermark: callbacks=[{string.Join(',', callbacks)}] masks=[{string.Join(',', provider.GetFaceMaskFrameIndices())}].");
        }
        return;
    }

    throw new InvalidOperationException("Out-of-order sparse detector fault was not propagated.");
}

async Task CompleteSparseResume(FrameMaskProvider provider, int resumeIndex)
{
    provider.RemoveFaceMasksFrom(resumeIndex);
    using var detector = new SparseOffsetDetector(resumeIndex);
    var generator = new AutoMaskGenerator(
        detector,
        provider,
        CreateOptions(AutoMaskProcessingMode.Tracked, detectEvery: 5),
        new TestDetectorFactory(() => new EmptyDetector()));
    await generator.GenerateAsync(
            sourcePath,
            null,
            CancellationToken.None,
            startFrameIndex: resumeIndex)
        .WaitAsync(TimeSpan.FromSeconds(12));
    AssertComplete(generator.LastRunSummary, expectedStart: resumeIndex, "sparse resumed run");
}

async Task VerifyMetadataFrameCountMismatch(int actualFrames)
{
    int underReported = Math.Max(1, actualFrames - 11);
    int overReported = actualFrames + 13;
    await RunPrivatePipelineWithReportedTotal(0, actualFrames, "unknown-total");
    await RunPrivatePipelineWithReportedTotal(underReported, actualFrames, "under-reported");
    await RunPrivatePipelineWithReportedTotal(overReported, actualFrames, "over-reported");
}

async Task VerifyStaleResumeRestartsAtZero(int actualFrames)
{
    using var detector = new EmptyDetector();
    var callbacks = new List<int>();
    var generator = new AutoMaskGenerator(
        detector,
        new FrameMaskProvider(),
        CreateOptions(AutoMaskProcessingMode.Raw, detectEvery: 1));
    await generator.GenerateAsync(
            sourcePath,
            null,
            CancellationToken.None,
            startFrameIndex: actualFrames + 20,
            onFrameProcessed: callbacks.Add)
        .WaitAsync(TimeSpan.FromSeconds(15));

    AssertComplete(generator.LastRunSummary, expectedStart: 0, "stale resume replay");
    if (generator.LastRunSummary!.TotalFrames != actualFrames ||
        callbacks.Count == 0 || callbacks[0] != 0 || callbacks[^1] != actualFrames - 1)
    {
        throw new InvalidOperationException(
            $"Stale resume did not restart at zero: summary={generator.LastRunSummary.ToLogLine()} callbacks=[{string.Join(',', callbacks.Take(4))}...{callbacks.LastOrDefault()}].");
    }
}

async Task RunPrivatePipelineWithReportedTotal(int reportedFrames, int actualFrames, string label)
{
    using var detector = new EmptyDetector();
    var generator = new AutoMaskGenerator(
        detector,
        new FrameMaskProvider(),
        CreateOptions(AutoMaskProcessingMode.Raw, detectEvery: 1));
    var progress = new CaptureProgress();
    MethodInfo method = typeof(AutoMaskGenerator).GetMethod(
        "GeneratePipelinedDetectAll",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("GeneratePipelinedDetectAll was not found.");

    Task invocation = Task.Run(() => InvokePrivate(method, generator,
        [sourcePath, detector, progress, CancellationToken.None, 0, reportedFrames, null]));
    await invocation.WaitAsync(TimeSpan.FromSeconds(12));

    AutoMaskRunSummary summary = generator.LastRunSummary
        ?? throw new InvalidOperationException($"{label}: summary missing.");
    if (!summary.ReachedDecoderEof || summary.DecodeCancelled ||
        !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase) ||
        summary.DecodedFrames != actualFrames || summary.TotalFrames != actualFrames ||
        progress.LastValue != 100)
    {
        throw new InvalidOperationException($"{label}: actual EOF was not authoritative: {summary.ToLogLine()} progress={progress.LastValue}.");
    }
}

static void InvokePrivate(MethodInfo method, object target, object?[] arguments)
{
    try
    {
        method.Invoke(target, arguments);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
    }
}

static AutoMaskOptions CreateOptions(AutoMaskProcessingMode mode, int detectEvery)
{
    return new AutoMaskOptions
    {
        ProcessingMode = mode,
        DownscaleRatio = 1.0,
        DetectEveryNFrames = detectEvery,
        ParallelDetectorCount = 1,
        FilterProfile = FaceFilterProfile.FaceOnnx
    };
}

static void AssertComplete(AutoMaskRunSummary? summary, int expectedStart, string label)
{
    if (summary == null || !summary.ReachedDecoderEof || summary.DecodeCancelled ||
        !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase) ||
        summary.StartFrameIndex != expectedStart || summary.DecodedFrames != summary.TotalFrames - expectedStart)
    {
        throw new InvalidOperationException($"{label} incomplete: {summary?.ToLogLine() ?? "summary-missing"}.");
    }
}

static void AssertSingleWatermark(IReadOnlyList<int> callbacks, int expected, string label)
{
    if (callbacks.Count != 1 || callbacks[0] != expected)
        throw new InvalidOperationException($"{label}: expected watermark={expected}, actual=[{string.Join(',', callbacks)}].");
}

static void AssertMaterializedPrefix(FrameMaskProvider provider, int start, int endInclusive, string label)
{
    for (int frame = start; frame <= endInclusive; frame++)
    {
        if (!provider.TryGetFaceMaskData(frame, out var data) || data.Faces.Count != 1)
            throw new InvalidOperationException($"{label}: frame {frame} was not materialized before watermark {endInclusive}.");
    }

    int[] beyond = provider.GetFaceMaskFrameIndices().Where(frame => frame > endInclusive).ToArray();
    if (beyond.Length > 0)
        throw new InvalidOperationException($"{label}: masks escaped watermark {endInclusive}: {string.Join(',', beyond)}.");
}

static void AssertEquivalentMasks(FrameMaskProvider expected, FrameMaskProvider actual, string label)
{
    string expectedText = SerializeMasks(expected);
    string actualText = SerializeMasks(actual);
    if (!string.Equals(expectedText, actualText, StringComparison.Ordinal))
        throw new InvalidOperationException($"{label}: resumed masks differ from the uninterrupted baseline.");
}

static string SerializeMasks(FrameMaskProvider provider)
{
    return string.Join(
        ";",
        provider.GetFaceMaskEntries()
            .OrderBy(entry => entry.Key)
            .Select(entry =>
                $"{entry.Key}:" + string.Join(
                    "|",
                    entry.Value.Faces.Select((face, index) =>
                        $"{face.X:0.000},{face.Y:0.000},{face.Width:0.000},{face.Height:0.000},{entry.Value.Confidences[index]:0.000}"))));
}

static class TestFaces
{
    public static FaceDetectionResult Create(int frame)
    {
        return new FaceDetectionResult
        {
            Bounds = new Rect(48 + frame % 7, 42 + frame % 5, 72, 72),
            Confidence = 0.95f
        };
    }

    public static IReadOnlyList<FaceDetectionResult> CreateSparse(int frame)
    {
        return frame == 5
            ? Array.Empty<FaceDetectionResult>()
            : [Create(frame)];
    }
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

sealed class IndexedDetector : EmptyDetector
{
    private int _callIndex = -1;

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        int frame = Interlocked.Increment(ref _callIndex);
        if (frame % 13 is 5 or 6)
            return Array.Empty<FaceDetectionResult>();

        return [TestFaces.Create(frame)];
    }
}

sealed class SparseOffsetDetector : EmptyDetector
{
    private int _nextFrame;

    public SparseOffsetDetector(int startFrame)
    {
        _nextFrame = Math.Max(0, startFrame);
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        int frame = _nextFrame;
        long next = ((long)frame / 5L + 1L) * 5L;
        _nextFrame = next >= int.MaxValue ? int.MaxValue : (int)next;
        return TestFaces.CreateSparse(frame);
    }
}

sealed class GateDetector : EmptyDetector
{
    private readonly ManualResetEventSlim _entered;
    private readonly ManualResetEventSlim _release;
    private readonly int _blockAtCall;
    private int _calls;

    public GateDetector(ManualResetEventSlim entered, ManualResetEventSlim release, int blockAtCall)
    {
        _entered = entered;
        _release = release;
        _blockAtCall = blockAtCall;
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        int call = Interlocked.Increment(ref _calls);
        if (call == _blockAtCall)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("Sparse cancellation detector release timed out.");
        }

        return TestFaces.CreateSparse((call - 1) * 5);
    }
}

sealed class FaultAfterDetector : EmptyDetector
{
    private readonly int _failAtCall;
    private int _calls;

    public FaultAfterDetector(int failAtCall)
    {
        _failAtCall = failAtCall;
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality)
    {
        int call = Interlocked.Increment(ref _calls);
        if (call == _failAtCall)
            throw new InjectedDetectorException();

        return TestFaces.CreateSparse((call - 1) * 5);
    }
}

sealed class OutOfOrderCoordinator
{
    private readonly ManualResetEventSlim _laterResultReady;
    private readonly ManualResetEventSlim _releaseFirst;
    private int _calls;

    public OutOfOrderCoordinator(
        ManualResetEventSlim laterResultReady,
        ManualResetEventSlim releaseFirst)
    {
        _laterResultReady = laterResultReady;
        _releaseFirst = releaseFirst;
    }

    public IReadOnlyList<FaceDetectionResult> Detect()
    {
        int call = Interlocked.Increment(ref _calls);
        if (call == 1)
        {
            if (!_releaseFirst.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("First out-of-order detector was not released.");
            throw new InjectedDetectorException();
        }

        _laterResultReady.Set();
        return [TestFaces.Create((call - 1) * 5)];
    }
}

sealed class OutOfOrderDetector : EmptyDetector
{
    private readonly OutOfOrderCoordinator _coordinator;

    public OutOfOrderDetector(OutOfOrderCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public override IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
        IntPtr data,
        int stride,
        int width,
        int height,
        double ratio,
        DownscaleQuality quality) => _coordinator.Detect();
}

sealed class InjectedDetectorException : InvalidOperationException
{
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

sealed class CaptureProgress : IProgress<int>
{
    public int LastValue { get; private set; } = -1;

    public void Report(int value)
    {
        LastValue = value;
    }
}
'@ | Set-Content -Encoding UTF8 $program

dotnet build $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$harness = Join-Path $work "bin\Debug\net8.0\AutoMaskResumeEofHarness.dll"
& dotnet $harness $source
exit $LASTEXITCODE
