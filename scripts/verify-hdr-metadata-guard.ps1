param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\hdr-metadata-guard"
$project = Join-Path $work "HdrMetadataGuardHarness.csproj"
$program = Join-Path $work "Program.cs"

try {
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
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Linq;
using System.Reflection;

FFmpegBootstrap.Initialize();

unsafe
{
    AssertPacketMetadata(AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF, "Dolby Vision");
    AssertPacketMetadata(AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS, "HDR10+");
    AssertPacketMetadata(
        AVPacketSideDataType.AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT,
        "HDR ambient viewing environment");

    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER, "Dolby Vision");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA, "Dolby Vision");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS, "HDR10+");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID, "HDR Vivid");
    AssertFrameMetadata(
        AVFrameSideDataType.AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT,
        "HDR ambient viewing environment");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_FILM_GRAIN_PARAMS, "AV1 film grain");

    AVFrame* staticHdrFrame = ffmpeg.av_frame_alloc();
    if (staticHdrFrame == null)
        throw new InvalidOperationException("Unable to allocate static HDR frame.");
    try
    {
        if (ffmpeg.av_frame_new_side_data(
                staticHdrFrame,
                AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
                1) == null)
        {
            throw new InvalidOperationException("Unable to add static HDR metadata.");
        }

        if (!FFmpegHdrMetadataGuard.HasStaticHdrMetadata(staticHdrFrame))
            throw new InvalidOperationException("Static HDR metadata was not detected.");
        if (FFmpegHdrMetadataGuard.FindUnsupportedMetadata(staticHdrFrame) != null)
            throw new InvalidOperationException("Static HDR metadata was incorrectly rejected.");
        if (!FFmpegHdrMetadataGuard.RequiresStaticHdrConfiguration(staticHdrFrame, false))
            throw new InvalidOperationException("Unconfigured static HDR metadata was not rejected.");
        if (FFmpegHdrMetadataGuard.RequiresStaticHdrConfiguration(staticHdrFrame, true))
            throw new InvalidOperationException("Configured static HDR metadata was incorrectly rejected.");
    }
    finally
    {
        ffmpeg.av_frame_free(&staticHdrFrame);
    }
}

Console.WriteLine("[HdrMetadataGuardVerify] PASS cases=12");
VerifySvtHdrCompatibility();
VerifyH264RetryPolicy();

static unsafe void AssertPacketMetadata(AVPacketSideDataType type, string expected)
{
    AVCodecParameters* parameters = ffmpeg.avcodec_parameters_alloc();
    if (parameters == null)
        throw new InvalidOperationException("Unable to allocate codec parameters.");
    try
    {
        AVPacketSideData* sideData = ffmpeg.av_packet_side_data_new(
            &parameters->coded_side_data,
            &parameters->nb_coded_side_data,
            type,
            1,
            0);
        if (sideData == null)
            throw new InvalidOperationException($"Unable to add packet side data: {type}");

        string? actual = FFmpegHdrMetadataGuard.FindUnsupportedMetadata(parameters);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Packet metadata mismatch: {type}, actual={actual}");
    }
    finally
    {
        ffmpeg.avcodec_parameters_free(&parameters);
    }
}

static unsafe void AssertFrameMetadata(AVFrameSideDataType type, string expected)
{
    AVFrame* frame = ffmpeg.av_frame_alloc();
    if (frame == null)
        throw new InvalidOperationException("Unable to allocate frame.");
    try
    {
        if (ffmpeg.av_frame_new_side_data(frame, type, 1) == null)
            throw new InvalidOperationException($"Unable to add frame side data: {type}");

        string? actual = FFmpegHdrMetadataGuard.FindUnsupportedMetadata(frame);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Frame metadata mismatch: {type}, actual={actual}");
    }
    finally
    {
        ffmpeg.av_frame_free(&frame);
    }
}

static unsafe void VerifySvtHdrCompatibility()
{
    Type metadataType = typeof(VideoExportService).GetNestedType(
        "VideoHdrMetadata",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VideoHdrMetadata was not found.");
    ConstructorInfo constructor = metadataType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(candidate => candidate.GetParameters().Length == 4);
    MethodInfo method = typeof(VideoExportService).GetMethod(
        "CanConfigureSvtAv1StaticHdr",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CanConfigureSvtAv1StaticHdr was not found.");

    byte[] fullMastering = CreateMasteringPayload(primaries: true, luminance: true);
    byte[] validContentLight = CreateContentLightPayload(1_000, 400);
    AssertCompatibility("full", fullMastering, validContentLight, true);
    AssertCompatibility(
        "primaries-only",
        CreateMasteringPayload(primaries: true, luminance: false),
        null,
        false);
    AssertCompatibility(
        "luminance-only",
        CreateMasteringPayload(primaries: false, luminance: true),
        null,
        false);
    AssertCompatibility("zero-max-cll", null, CreateContentLightPayload(0, 0), false);
    AssertCompatibility(
        "non-q16-chromaticity",
        CreateMasteringPayload(true, true, chromaticityDenominator: 3),
        null,
        false);
    AssertCompatibility(
        "q16-overflow",
        CreateMasteringPayload(true, true, chromaticityNumerator: 1, chromaticityDenominator: 1),
        null,
        false);
    AssertCompatibility(
        "non-q8-max-luminance",
        CreateMasteringPayload(true, true, maxLuminanceNumerator: 1, maxLuminanceDenominator: 3),
        null,
        false);
    AssertCompatibility(
        "cll-overflow",
        null,
        CreateContentLightPayload(ushort.MaxValue + 1u, 400),
        false);

    Console.WriteLine("[HdrMetadataGuardVerify] PASS svtHdrCompatibility=8");

    void AssertCompatibility(
        string name,
        byte[]? mastering,
        byte[]? contentLight,
        bool expected)
    {
        object metadata = constructor.Invoke([null, null, mastering, contentLight]);
        object?[] arguments = [metadata, null];
        bool actual = (bool)(method.Invoke(null, arguments)
            ?? throw new InvalidOperationException($"Compatibility result was null: {name}"));
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"SVT HDR compatibility mismatch: {name}, expected={expected}, actual={actual}, error={arguments[1]}");
        }
    }
}

static void VerifyH264RetryPolicy()
{
    MethodInfo method = typeof(VideoExportService).GetMethod(
        "ShouldRetryWithH264Fallback",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ShouldRetryWithH264Fallback was not found.");
    FieldInfo staticHdrConfigured = typeof(VideoExportService).GetField(
        "_staticHdrConfigured",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_staticHdrConfigured was not found.");

    var service = new VideoExportService(new FrameMaskProvider());
    var invalidArgument = new InvalidOperationException("Invalid argument: retry policy test");

    staticHdrConfigured.SetValue(service, false);
    bool standardRetry = (bool)(method.Invoke(service, [invalidArgument])
        ?? throw new InvalidOperationException("Standard H.264 retry result was null."));
    staticHdrConfigured.SetValue(service, true);
    bool hdrRetry = (bool)(method.Invoke(service, [invalidArgument])
        ?? throw new InvalidOperationException("HDR H.264 retry result was null."));
    if (!standardRetry || hdrRetry)
    {
        throw new InvalidOperationException(
            $"H.264 retry policy mismatch: standard={standardRetry}, hdr={hdrRetry}");
    }

    Console.WriteLine("[HdrMetadataGuardVerify] PASS h264RetryPolicy=standard:true,hdr:false");
}

static unsafe byte[] CreateMasteringPayload(
    bool primaries,
    bool luminance,
    int chromaticityNumerator = 1,
    int chromaticityDenominator = 2,
    int maxLuminanceNumerator = 1_000,
    int maxLuminanceDenominator = 1)
{
    var payload = new byte[sizeof(AVMasteringDisplayMetadata)];
    fixed (byte* data = payload)
    {
        AVMasteringDisplayMetadata* metadata = (AVMasteringDisplayMetadata*)data;
        metadata->has_primaries = primaries ? 1 : 0;
        metadata->has_luminance = luminance ? 1 : 0;
        if (primaries)
        {
            AVRational* primaryValues = (AVRational*)&metadata->display_primaries;
            for (int index = 0; index < 6; index++)
            {
                primaryValues[index] = new AVRational
                {
                    num = chromaticityNumerator,
                    den = chromaticityDenominator
                };
            }
            AVRational* whitePoint = (AVRational*)&metadata->white_point;
            for (int index = 0; index < 2; index++)
            {
                whitePoint[index] = new AVRational
                {
                    num = chromaticityNumerator,
                    den = chromaticityDenominator
                };
            }
        }
        if (luminance)
        {
            metadata->max_luminance = new AVRational
            {
                num = maxLuminanceNumerator,
                den = maxLuminanceDenominator
            };
            metadata->min_luminance = new AVRational { num = 1, den = 16_384 };
        }
    }
    return payload;
}

static unsafe byte[] CreateContentLightPayload(uint maxCll, uint maxFall)
{
    var payload = new byte[sizeof(AVContentLightMetadata)];
    fixed (byte* data = payload)
    {
        AVContentLightMetadata* metadata = (AVContentLightMetadata*)data;
        metadata->MaxCLL = maxCll;
        metadata->MaxFALL = maxFall;
    }
    return payload;
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run `
    --project $project `
    --configuration Debug `
    --property:UseArtifactsOutput=true `
    --property:ArtifactsPath="$work\artifacts"
if ($LASTEXITCODE -ne 0) {
    throw "HDR metadata guard harness failed with exit code $LASTEXITCODE."
}

$exportSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
$losslessRemuxIndex = $exportSource.IndexOf(
    "if (blurFrameSet.Count == 0)",
    [StringComparison]::Ordinal)
$streamGuardIndex = $exportSource.IndexOf(
    "FFmpegHdrMetadataGuard.FindUnsupportedMetadata(inStream->codecpar)",
    [StringComparison]::Ordinal)
$remuxReturnIndex = if ($losslessRemuxIndex -ge 0) {
    $exportSource.IndexOf("return;", $losslessRemuxIndex, [StringComparison]::Ordinal)
}
else {
    -1
}
$probeCallIndex = $exportSource.IndexOf(
    "ProbeVideoHdrMetadata(",
    [Math]::Max(0, $losslessRemuxIndex),
    [StringComparison]::Ordinal)
$probeCancellationIndex = $exportSource.IndexOf(
    "cancellationToken)",
    [Math]::Max(0, $probeCallIndex),
    [StringComparison]::Ordinal)
$frameGuardIndex = $exportSource.IndexOf(
    "FFmpegHdrMetadataGuard.FindUnsupportedMetadata(frame)",
    [Math]::Max(0, $losslessRemuxIndex),
    [StringComparison]::Ordinal)
if ($losslessRemuxIndex -lt 0 -or
    $remuxReturnIndex -le $losslessRemuxIndex -or
    $streamGuardIndex -le $remuxReturnIndex -or
    $probeCallIndex -le $remuxReturnIndex -or
    $probeCancellationIndex -le $probeCallIndex -or
    $frameGuardIndex -le $remuxReturnIndex) {
    throw "Dynamic metadata guard must run after the lossless no-mask remux path."
}

$hdrProbeSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoHdrProbePolicy.cs")
$ioGuardSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoIoInterruptGuard.cs")
if ($hdrProbeSource -notmatch 'CancellationToken\s+cancellationToken' -or
    $hdrProbeSource -notmatch 'VideoIoInterruptGuard' -or
    $hdrProbeSource -notmatch 'ThrowIfCancellationRequested\(\)' -or
    $ioGuardSource -notmatch 'interrupt_callback\.callback' -or
    $ioGuardSource -notmatch 'CancellationTokenRegistration') {
    throw "HDR probe native cancellation guard is incomplete."
}

Write-Host "[HdrMetadataGuardVerify] PASS no-mask-remux-before-reencode-guard nativeCancel=true"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force $work
    }
    if (Test-Path $work) {
        throw "HDR metadata guard harness cleanup failed: $work"
    }
}
