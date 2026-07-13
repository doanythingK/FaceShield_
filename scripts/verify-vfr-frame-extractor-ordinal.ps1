param(
    [string]$SourcePath = "",
    [string]$DuplicatePtsSourcePath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = ""
if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
    $source = if ([IO.Path]::IsPathRooted($SourcePath)) {
        (Resolve-Path $SourcePath).Path
    }
    else {
        (Resolve-Path (Join-Path $repo $SourcePath)).Path
    }
}
$duplicatePtsSource = ""
if (-not [string]::IsNullOrWhiteSpace($DuplicatePtsSourcePath)) {
    $duplicatePtsSource = if ([IO.Path]::IsPathRooted($DuplicatePtsSourcePath)) {
        (Resolve-Path $DuplicatePtsSourcePath).Path
    }
    else {
        (Resolve-Path (Join-Path $repo $DuplicatePtsSourcePath)).Path
    }
}
$work = Join-Path $repo ".tmp\vfr-frame-extractor-ordinal"
$project = Join-Path $work "VfrFrameExtractorOrdinalHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

@'
using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

if (args.Length is < 1 or > 2)
    throw new ArgumentException("Expected a VFR source and optional duplicate-PTS source.");

string sourcePath = Path.GetFullPath(args[0]);
AppBuilder.Configure<FaceShield.App>()
    .UsePlatformDetect()
    .SetupWithoutStarting();
FFmpegBootstrap.Initialize();
VerifyNonMonotonicTimelineGuard();

string[] expectedMarkers = ["red", "green", "blue", "yellow"];
double[] expectedTimestamps = [0.0, 0.3, 3.0, 3.3];
int[] randomOrder = [2, 0, 3, 1];
using (var extractor = new FfFrameExtractor(sourcePath, enableHardware: false))
{
    foreach (int ordinal in randomOrder)
    {
        using WriteableBitmap frame = extractor.GetFrameByIndex(ordinal)
            ?? throw new InvalidOperationException($"GetFrameByIndex({ordinal}) returned null.");
        AssertMarker(frame, expectedMarkers[ordinal], $"random ordinal {ordinal}");
        AssertTimestamp(
            extractor.LastDecodedTimestampSeconds,
            expectedTimestamps[ordinal],
            $"random ordinal {ordinal}");
    }
}

if (args.Length == 2)
{
    string duplicatePtsSource = Path.GetFullPath(args[1]);
    using var extractor = new FfFrameExtractor(duplicatePtsSource, enableHardware: false);
    using (WriteableBitmap frame = extractor.GetFrameByIndex(1)
        ?? throw new InvalidOperationException("Duplicate-PTS ordinal 1 returned null."))
    {
        AssertMarker(frame, "green", "duplicate-PTS random ordinal 1");
    }
    AssertTimestamp(extractor.LastDecodedTimestampSeconds, 0.0, "duplicate-PTS random ordinal 1");

    extractor.StartSequentialRead(1);
    AssertNext(extractor, 1, "green", 0.0);
    AssertNext(extractor, 2, "blue", 1.0);
}

using (var extractor = new FfFrameExtractor(sourcePath, enableHardware: false))
{
    extractor.StartSequentialRead(2);
    AssertNext(extractor, 2, expectedMarkers[2], expectedTimestamps[2]);
    AssertNext(extractor, 3, expectedMarkers[3], expectedTimestamps[3]);
    if (extractor.TryGetNextFrame(
            CancellationToken.None,
            out WriteableBitmap? unexpected,
            out int unexpectedOrdinal))
    {
        unexpected?.Dispose();
        throw new InvalidOperationException(
            $"Expected EOF after ordinal 3, got ordinal {unexpectedOrdinal}.");
    }
    if (!extractor.SequentialReachedEndOfStream)
        throw new InvalidOperationException("Sequential read did not report decoder EOF.");
}

string cancellationSource = Path.Combine(
    Path.GetDirectoryName(sourcePath)!,
    $"vfr-cancel-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
File.Copy(sourcePath, cancellationSource, overwrite: true);
try
{
    using var extractor = new FfFrameExtractor(cancellationSource, enableHardware: false);
    var startTimer = Stopwatch.StartNew();
    extractor.StartSequentialRead(3);
    startTimer.Stop();
    if (startTimer.ElapsedMilliseconds > 2_000)
    {
        throw new InvalidOperationException(
            $"StartSequentialRead synchronously built the ordinal index: {startTimer.ElapsedMilliseconds} ms.");
    }

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var cancellationTimer = Stopwatch.StartNew();
    bool decoded = extractor.TryGetNextFrameRaw(
        cancelled.Token,
        requireBgra: false,
        out _,
        out _);
    cancellationTimer.Stop();
    if (decoded || !extractor.SequentialReadCancelled)
        throw new InvalidOperationException("Cancelled ordinal index resolution did not stop decoding.");
    if (cancellationTimer.ElapsedMilliseconds > 2_000)
    {
        throw new InvalidOperationException(
            $"Cancelled ordinal index resolution was delayed: {cancellationTimer.ElapsedMilliseconds} ms.");
    }

    using var singleFrameExtractor = new FfFrameExtractor(cancellationSource, enableHardware: false);
    var singleFrameTimer = Stopwatch.StartNew();
    using WriteableBitmap? cancelledFrame = singleFrameExtractor.GetFrameByIndex(3, cancelled.Token);
    singleFrameTimer.Stop();
    if (cancelledFrame != null || singleFrameTimer.ElapsedMilliseconds > 2_000)
    {
        throw new InvalidOperationException(
            $"Cancelled single-frame extraction did not stop promptly: " +
            $"frame={cancelledFrame != null}, elapsedMs={singleFrameTimer.ElapsedMilliseconds}.");
    }

    using var gatedExtractor = new FfFrameExtractor(cancellationSource, enableHardware: false);
    var exactProvider = new ExactFrameProvider(gatedExtractor);
    var gate = (SemaphoreSlim)(typeof(ExactFrameProvider).GetField(
        "_decodeGate",
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(exactProvider)
        ?? throw new InvalidOperationException("ExactFrameProvider gate was not found."));
    await gate.WaitAsync();
    try
    {
        var gateCancellationTimer = Stopwatch.StartNew();
        WriteableBitmap? gatedFrame = await exactProvider.GetExactAsync(3, cancelled.Token);
        gateCancellationTimer.Stop();
        gatedFrame?.Dispose();
        if (gatedFrame != null || gateCancellationTimer.ElapsedMilliseconds > 2_000)
        {
            throw new InvalidOperationException(
                $"Cancelled exact-frame gate wait did not stop promptly: " +
                $"frame={gatedFrame != null}, elapsedMs={gateCancellationTimer.ElapsedMilliseconds}.");
        }
    }
    finally
    {
        gate.Release();
    }
}
finally
{
    File.Delete(cancellationSource);
}

Console.WriteLine(
    "[VfrFrameExtractorOrdinalVerify] PASS pts=0,0.3,3.0,3.3 " +
    "get-frame=4 sequential=2 cancellation=true single-frame-cancellation=true " +
    "gate-cancellation=true nonmonotonic-fallback=true");

static void VerifyNonMonotonicTimelineGuard()
{
    Type extractorType = typeof(FfFrameExtractor);
    Type timelineType = extractorType.GetNestedType(
        "DecodedFrameTimeline",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Decoded frame timeline type was not found.");
    object timeline = Activator.CreateInstance(timelineType, nonPublic: true)
        ?? throw new InvalidOperationException("Decoded frame timeline could not be created.");
    MethodInfo append = extractorType.GetMethod(
        "TryAddOrValidateTimelineEntry",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Decoded frame timeline append method was not found.");
    long[] timestamps = [0, 10, 5, 10, 15];
    for (int ordinal = 0; ordinal < timestamps.Length; ordinal++)
    {
        bool appended = (bool)(append.Invoke(null, [timeline, ordinal, timestamps[ordinal]])
            ?? false);
        if (!appended)
        {
            throw new InvalidOperationException(
                $"Synthetic non-monotonic timeline append failed at ordinal {ordinal}.");
        }
    }

    bool supportsExactSeek = (bool)(timelineType.GetProperty(
        "SupportsExactTimestampSeek",
        BindingFlags.Instance | BindingFlags.Public)?.GetValue(timeline)
        ?? true);
    if (supportsExactSeek)
    {
        throw new InvalidOperationException(
            "Non-monotonic, non-adjacent duplicate PTS timeline remained seekable.");
    }
}

static void AssertNext(
    FfFrameExtractor extractor,
    int expectedOrdinal,
    string expectedMarker,
    double expectedTimestamp)
{
    if (!extractor.TryGetNextFrame(
            CancellationToken.None,
            out WriteableBitmap? frame,
            out int ordinal) ||
        frame == null)
    {
        throw new InvalidOperationException(
            $"Sequential decode ended before ordinal {expectedOrdinal}.");
    }

    using (frame)
    {
        if (ordinal != expectedOrdinal)
        {
            throw new InvalidOperationException(
                $"Sequential ordinal mismatch: expected={expectedOrdinal}, actual={ordinal}.");
        }
        AssertMarker(frame, expectedMarker, $"sequential ordinal {ordinal}");
    }
    AssertTimestamp(
        extractor.LastDecodedTimestampSeconds,
        expectedTimestamp,
        $"sequential ordinal {ordinal}");
}

static void AssertTimestamp(double actual, double expected, string label)
{
    if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 0.000001)
    {
        throw new InvalidOperationException(
            $"Timestamp mismatch for {label}: expected={expected}, actual={actual}.");
    }
}

static unsafe void AssertMarker(WriteableBitmap bitmap, string marker, string label)
{
    using var framebuffer = bitmap.Lock();
    int x = bitmap.PixelSize.Width / 2;
    int y = bitmap.PixelSize.Height / 2;
    byte* pixel = (byte*)framebuffer.Address + y * framebuffer.RowBytes + x * 4;
    int blue = pixel[0];
    int green = pixel[1];
    int red = pixel[2];

    bool matches = marker switch
    {
        "red" => red > 180 && green < 80 && blue < 80,
        "green" => green > 80 && red < 80 && blue < 80,
        "blue" => blue > 180 && red < 80 && green < 80,
        "yellow" => red > 180 && green > 180 && blue < 80,
        _ => false
    };
    if (!matches)
    {
        throw new InvalidOperationException(
            $"Pixel marker mismatch for {label}: expected={marker}, " +
            $"actual=R{red}/G{green}/B{blue}.");
    }
}
'@ | Set-Content -Encoding UTF8 $program

if (-not [string]::IsNullOrWhiteSpace($source)) {
    $harnessArguments = @(
        "run", "--project", $project, "--configuration", "Debug", "--", $source
    )
    if (-not [string]::IsNullOrWhiteSpace($duplicatePtsSource)) {
        $harnessArguments += $duplicatePtsSource
    }
    $runOutput = & dotnet @harnessArguments 2>&1
    $exitCode = $LASTEXITCODE
    $runOutput | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        exit $exitCode
    }
}

$extractorSource = Get-Content -Raw (Join-Path $repo "Services\Video\FfFrameExtractor.cs")
if ($extractorSource -match 'startFrameIndex\s*/\s*_fps') {
    throw "Sequential ordinal seek must not be derived from average FPS."
}
foreach ($required in @(
    "best_effort_timestamp",
    "FileLength",
    "LastWriteTimeUtcTicks",
    "MatchIndexedTimestamp",
    "PrepareSequentialDecodeFromBeginning",
    "CancellationToken cancellationToken"
)) {
    if (-not $extractorSource.Contains($required)) {
        throw "Missing exact ordinal implementation guard: $required"
    }
}

$frameImageProviderSource = Get-Content -Raw (Join-Path $repo "Services\Video\FrameImageProvider.cs")
$exactFrameProviderSource = Get-Content -Raw (Join-Path $repo "Services\Video\Session\ExactFrameProvider.cs")
$autoMaskGeneratorSource = Get-Content -Raw (Join-Path $repo "Services\Analysis\AutoMaskGenerator.cs")
$frameAnalyzerSource = Get-Content -Raw (Join-Path $repo "Services\Analysis\FrameAnalyzer.cs")
if (-not $frameImageProviderSource.Contains("GetFrameByIndex(frameIndex, ct)") -or
    -not $exactFrameProviderSource.Contains("GetFrameByIndex(frameIndex, ct)") -or
    -not $autoMaskGeneratorSource.Contains("GetFrameByIndex(frameIndex, ct)") -or
    -not $frameAnalyzerSource.Contains("GetFrameByIndex(idx, ct)") -or
    -not $exactFrameProviderSource.Contains("WaitAsync(ct)") -or
    -not $exactFrameProviderSource.Contains("frame?.Dispose()")) {
    throw "Single-frame async providers must forward cancellation into frame extraction."
}
if (-not $extractorSource.Contains("previous.PresentationTimestamp >= target.PresentationTimestamp") -or
    -not $extractorSource.Contains("SupportsExactTimestampSeek") -or
    -not $extractorSource.Contains("MarkDecodedFrameTimelineCompleteIfContiguous")) {
    throw "Non-monotonic PTS and decoder-drain paths must fail closed and complete the timeline."
}

$actual = (-not [string]::IsNullOrWhiteSpace($source)).ToString().ToLowerInvariant()
$duplicate = (-not [string]::IsNullOrWhiteSpace($duplicatePtsSource)).ToString().ToLowerInvariant()
Write-Host "[VfrFrameExtractorOrdinalVerify] PASS source-guards=true actual=$actual duplicatePts=$duplicate"
