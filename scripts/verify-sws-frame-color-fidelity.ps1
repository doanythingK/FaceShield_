param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exportPath = Join-Path $repo "Services\Video\VideoExportService.cs"
$exportSource = Get-Content -Raw $exportPath
$singleline = [Text.RegularExpressions.RegexOptions]::Singleline

function Assert-SourceMatch {
    param(
        [string]$Name,
        [string]$Pattern
    )

    if (-not [regex]::IsMatch($exportSource, $Pattern, $singleline)) {
        throw "SWS source policy is missing: $Name"
    }
}

if ([regex]::IsMatch($exportSource, '\bffmpeg\.sws_getContext\s*\(')) {
    throw "Legacy sws_getContext must not be used by VideoExportService."
}
if ([regex]::IsMatch($exportSource, '\bffmpeg\.sws_scale\s*\(')) {
    throw "Legacy sws_scale must not be used by VideoExportService."
}

Assert-SourceMatch "dynamic context allocation" '\bffmpeg\.sws_alloc_context\s*\('
Assert-SourceMatch "frame-aware scaling" '\bffmpeg\.sws_scale_frame\s*\('
Assert-SourceMatch "explicit scaling policy" '\bffmpeg\.av_opt_set_int\s*\(\s*context,\s*"sws_flags"'

$copyHelperStart = $exportSource.IndexOf(
    "private static unsafe void CopyFrameEncodingProperties",
    [StringComparison]::Ordinal)
$copyHelperEnd = $exportSource.IndexOf(
    "private static unsafe SwsContext* CreateDynamicSwsContext",
    [StringComparison]::Ordinal)
if ($copyHelperStart -lt 0 -or $copyHelperEnd -le $copyHelperStart) {
    throw "CopyFrameEncodingProperties helper boundaries were not found."
}
$copyHelper = $exportSource.Substring($copyHelperStart, $copyHelperEnd - $copyHelperStart)
$sideDataFreeIndex = $copyHelper.IndexOf(
    "ffmpeg.av_frame_side_data_free",
    [StringComparison]::Ordinal)
$metadataFreeIndex = $copyHelper.IndexOf(
    "ffmpeg.av_dict_free(&destination->metadata)",
    [StringComparison]::Ordinal)
$copyPropsIndex = $copyHelper.IndexOf(
    "ffmpeg.av_frame_copy_props(destination, source)",
    [StringComparison]::Ordinal)
if (-not [regex]::IsMatch(
        $copyHelper,
        'ffmpeg\.av_frame_side_data_free\s*\(\s*&destination->side_data,\s*' +
            '&destination->nb_side_data\s*\)',
        $singleline) -or
    $sideDataFreeIndex -lt 0 -or
    $metadataFreeIndex -le $sideDataFreeIndex -or
    $copyPropsIndex -le $metadataFreeIndex) {
    throw (
        "CopyFrameEncodingProperties must remove all destination side data, free destination " +
        "metadata, and then copy source properties.")
}

$bgraHelperStart = $exportSource.IndexOf(
    "private static unsafe void SetBgraColorProperties",
    [StringComparison]::Ordinal)
$bgraHelperEnd = $exportSource.IndexOf(
    "private static void ThrowUnsupportedDynamicVideoMetadata",
    [StringComparison]::Ordinal)
if ($bgraHelperStart -lt 0 -or $bgraHelperEnd -le $bgraHelperStart) {
    throw "SetBgraColorProperties helper boundaries were not found."
}
$bgraHelper = $exportSource.Substring($bgraHelperStart, $bgraHelperEnd - $bgraHelperStart)
$bgraCopyIndex = $bgraHelper.IndexOf(
    "CopyFrameEncodingProperties(source, bgra)",
    [StringComparison]::Ordinal)
$bgraRangeIndex = $bgraHelper.IndexOf("bgra->color_range", [StringComparison]::Ordinal)
if ($bgraCopyIndex -lt 0 -or $bgraRangeIndex -le $bgraCopyIndex) {
    throw "SetBgraColorProperties must copy source frame properties before overriding BGRA color fields."
}

foreach ($contextName in @("swsDecToBgra", "swsBgraToEnc", "swsDecToEnc")) {
    Assert-SourceMatch "$contextName dynamic initialization" (
        [regex]::Escape($contextName) + '\s*=\s*CreateDynamicSwsContext\s*\(')
}

$decoderToEncoderOrder = [regex]::Matches(
    $exportSource,
    'CopyFrameEncodingProperties\s*\(\s*frame,\s*encFrame\s*\);\s*' +
        'ScaleFramePreservingColor\s*\(\s*swsDecToEnc,\s*encFrame,\s*frame,',
    $singleline).Count
if ($decoderToEncoderOrder -ne 2) {
    throw "Expected two metadata-first decoder-to-encoder conversions, found $decoderToEncoderOrder."
}

Assert-SourceMatch "BGRA destination metadata before conversion" (
    'SetBgraColorProperties\s*\(\s*frame,\s*bgra\s*\);\s*' +
        'ScaleFramePreservingColor\s*\(\s*swsDecToBgra,\s*bgra,\s*frame,')
Assert-SourceMatch "encoder destination metadata before BGRA conversion" (
    'CopyFrameEncodingProperties\s*\(\s*frame,\s*encFrame\s*\);\s*' +
        'ScaleFramePreservingColor\s*\(\s*swsBgraToEnc,\s*encFrame,\s*bgra,')
Assert-SourceMatch "BGRA full range" 'bgra->color_range\s*=\s*AVColorRange\.AVCOL_RANGE_JPEG\s*;'
Assert-SourceMatch "BGRA RGB matrix" 'bgra->colorspace\s*=\s*AVColorSpace\.AVCOL_SPC_RGB\s*;'
Assert-SourceMatch "BGRA has no chroma siting" (
    'bgra->chroma_location\s*=\s*AVChromaLocation\.AVCHROMA_LOC_UNSPECIFIED\s*;')
Assert-SourceMatch "BGRA color primaries" (
    'bgra->color_primaries\s*=\s*source->color_primaries\s*;')
Assert-SourceMatch "BGRA transfer characteristics" (
    'bgra->color_trc\s*=\s*source->color_trc\s*;')
Assert-SourceMatch "BGRA sample aspect ratio" (
    'bgra->sample_aspect_ratio\s*=\s*source->sample_aspect_ratio\s*;')

$work = Join-Path $repo ".tmp\sws-frame-color-fidelity"
$project = Join-Path $work "SwsFrameColorFidelityHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
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
"@ | Set-Content -Encoding UTF8 -Path $project

    @'
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

FFmpegBootstrap.Initialize();

unsafe
{
    const int width = 8;
    const int height = 4;
    const byte sourceY = 100;
    const byte sourceU = 90;
    const byte sourceV = 200;

    AVFrame* limitedYuv = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, width, height);
    AVFrame* fullYuv = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, width, height);
    AVFrame* limitedBgra = AllocateFrame(AVPixelFormat.AV_PIX_FMT_BGRA, width, height);
    AVFrame* fullBgra = AllocateFrame(AVPixelFormat.AV_PIX_FMT_BGRA, width, height);
    AVFrame* roundTripYuv = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, width, height);
    AVFrame* firstHdrYuv = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, width, height);
    AVFrame* secondHdrYuv = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, width, height);
    AVFrame* reusedHdrBgra = AllocateFrame(AVPixelFormat.AV_PIX_FMT_BGRA, width, height);
    SwsContext* toBgra = CreateDynamicContext();
    SwsContext* toYuv = CreateDynamicContext();

    try
    {
        SetYuvProperties(limitedYuv, AVColorRange.AVCOL_RANGE_MPEG);
        SetYuvProperties(fullYuv, AVColorRange.AVCOL_RANGE_JPEG);
        SetYuvProperties(roundTripYuv, AVColorRange.AVCOL_RANGE_MPEG);
        SetYuvProperties(firstHdrYuv, AVColorRange.AVCOL_RANGE_MPEG);
        SetYuvProperties(secondHdrYuv, AVColorRange.AVCOL_RANGE_MPEG);
        FillYuv420(limitedYuv, sourceY, sourceU, sourceV);
        FillYuv420(fullYuv, sourceY, sourceU, sourceV);
        FillYuv420(firstHdrYuv, sourceY, sourceU, sourceV);
        FillYuv420(secondHdrYuv, sourceY, sourceU, sourceV);

        SetBgraProperties(limitedYuv, limitedBgra);
        Scale(toBgra, limitedBgra, limitedYuv, "BT.709 limited YUV to BGRA");
        SetBgraProperties(fullYuv, fullBgra);
        Scale(toBgra, fullBgra, fullYuv, "BT.709 full YUV to BGRA");
        BgraPixel limited = ReadBgra(limitedBgra, 3, 2);
        BgraPixel full = ReadBgra(fullBgra, 3, 2);
        Scale(toYuv, roundTripYuv, limitedBgra, "BGRA to BT.709 limited YUV");
        YuvPixel roundTrip = ReadYuv420(roundTripYuv, 3, 2);

        AddStaticHdrMetadata(firstHdrYuv, 1_000, 400, 200, "first");
        AddStaticHdrMetadata(secondHdrYuv, 2_000, 800, 300, "second");
        SetBgraProperties(firstHdrYuv, reusedHdrBgra);
        Scale(toBgra, reusedHdrBgra, firstHdrYuv, "first HDR YUV to reused BGRA");
        AssertStaticHdrMetadata(reusedHdrBgra, 1_000, 400, 200, "first");
        SetBgraProperties(secondHdrYuv, reusedHdrBgra);
        Scale(toBgra, reusedHdrBgra, secondHdrYuv, "second HDR YUV to reused BGRA");
        AssertStaticHdrMetadata(reusedHdrBgra, 2_000, 800, 300, "second");

        AssertNear(limited.R, 227, 5, "limited red");
        AssertNear(limited.G, 68, 5, "limited green");
        AssertNear(limited.B, 18, 5, "limited blue");
        AssertNear(full.R, 213, 5, "full red");
        AssertNear(full.G, 73, 5, "full green");
        AssertNear(full.B, 30, 5, "full blue");
        if (limited.A != byte.MaxValue || full.A != byte.MaxValue)
            throw new InvalidOperationException(
                $"Unexpected converted alpha: limited={limited.A}, full={full.A}.");

        int rangeDelta = Math.Abs(limited.R - full.R) +
            Math.Abs(limited.G - full.G) +
            Math.Abs(limited.B - full.B);
        if (rangeDelta < 20)
        {
            throw new InvalidOperationException(
                $"Frame color range metadata did not affect conversion: delta={rangeDelta}.");
        }

        AssertNear(roundTrip.Y, sourceY, 3, "round-trip luma");
        AssertNear(roundTrip.U, sourceU, 3, "round-trip Cb");
        AssertNear(roundTrip.V, sourceV, 3, "round-trip Cr");

        AssertProperties(limitedYuv, AVColorRange.AVCOL_RANGE_MPEG, AVColorSpace.AVCOL_SPC_BT709,
            AVChromaLocation.AVCHROMA_LOC_LEFT, "limited source");
        AssertProperties(limitedBgra, AVColorRange.AVCOL_RANGE_JPEG, AVColorSpace.AVCOL_SPC_RGB,
            AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED, "BGRA destination");
        AssertProperties(roundTripYuv, AVColorRange.AVCOL_RANGE_MPEG, AVColorSpace.AVCOL_SPC_BT709,
            AVChromaLocation.AVCHROMA_LOC_LEFT, "round-trip destination");

        Console.WriteLine(
            $"[SwsFrameColorFidelityVerify] native limitedBgra={limited.B},{limited.G},{limited.R},{limited.A} " +
            $"fullBgra={full.B},{full.G},{full.R},{full.A} " +
            $"roundTripYuv={roundTrip.Y},{roundTrip.U},{roundTrip.V} rangeDelta={rangeDelta} " +
            "hdrSideData=2 hdrReplacement=true");
    }
    finally
    {
        ffmpeg.sws_freeContext(toBgra);
        ffmpeg.sws_freeContext(toYuv);
        ffmpeg.av_frame_free(&limitedYuv);
        ffmpeg.av_frame_free(&fullYuv);
        ffmpeg.av_frame_free(&limitedBgra);
        ffmpeg.av_frame_free(&fullBgra);
        ffmpeg.av_frame_free(&roundTripYuv);
        ffmpeg.av_frame_free(&firstHdrYuv);
        ffmpeg.av_frame_free(&secondHdrYuv);
        ffmpeg.av_frame_free(&reusedHdrBgra);
    }
}

static unsafe AVFrame* AllocateFrame(AVPixelFormat format, int width, int height)
{
    AVFrame* frame = ffmpeg.av_frame_alloc();
    if (frame == null)
        throw new InvalidOperationException("Unable to allocate an SWS verification frame.");

    frame->format = (int)format;
    frame->width = width;
    frame->height = height;
    int result = ffmpeg.av_frame_get_buffer(frame, 32);
    if (result < 0)
    {
        ffmpeg.av_frame_free(&frame);
        throw new InvalidOperationException($"Unable to allocate frame data: {GetError(result)}");
    }

    return frame;
}

static unsafe SwsContext* CreateDynamicContext()
{
    SwsContext* context = ffmpeg.sws_alloc_context();
    if (context == null)
        throw new InvalidOperationException("Unable to allocate a dynamic SWS context.");

    int result = ffmpeg.av_opt_set_int(
        context,
        "sws_flags",
        (long)SwsFlags.SWS_FAST_BILINEAR,
        0);
    if (result < 0)
    {
        ffmpeg.sws_freeContext(context);
        throw new InvalidOperationException($"Unable to configure SWS: {GetError(result)}");
    }

    return context;
}

static unsafe void SetYuvProperties(AVFrame* frame, AVColorRange range)
{
    frame->color_range = range;
    frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
    frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
    frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
    frame->chroma_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
    frame->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
}

static unsafe void SetBgraProperties(AVFrame* source, AVFrame* frame)
{
    CopyFrameEncodingProperties(source, frame);
    frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
    frame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
    frame->color_primaries = source->color_primaries;
    frame->color_trc = source->color_trc;
    frame->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
    frame->sample_aspect_ratio = source->sample_aspect_ratio;
}

static unsafe void CopyFrameEncodingProperties(AVFrame* source, AVFrame* destination)
{
    ffmpeg.av_frame_side_data_free(
        &destination->side_data,
        &destination->nb_side_data);
    ffmpeg.av_dict_free(&destination->metadata);
    ThrowIfError(
        ffmpeg.av_frame_copy_props(destination, source),
        "Unable to copy frame properties");
}

static unsafe void AddStaticHdrMetadata(
    AVFrame* frame,
    int maxLuminance,
    uint maxCll,
    uint maxFall,
    string marker)
{
    AVMasteringDisplayMetadata* mastering =
        ffmpeg.av_mastering_display_metadata_create_side_data(frame);
    if (mastering == null)
        throw new InvalidOperationException("Unable to create mastering display metadata.");
    mastering->has_luminance = 1;
    mastering->min_luminance = new AVRational { num = 1, den = 1_000 };
    mastering->max_luminance = new AVRational { num = maxLuminance, den = 1 };

    AVContentLightMetadata* contentLight =
        ffmpeg.av_content_light_metadata_create_side_data(frame);
    if (contentLight == null)
        throw new InvalidOperationException("Unable to create content light metadata.");
    contentLight->MaxCLL = maxCll;
    contentLight->MaxFALL = maxFall;

    ThrowIfError(
        ffmpeg.av_dict_set(&frame->metadata, "hdr-marker", marker, 0),
        "Unable to set frame metadata marker");
}

static unsafe void AssertStaticHdrMetadata(
    AVFrame* frame,
    int expectedMaxLuminance,
    uint expectedMaxCll,
    uint expectedMaxFall,
    string expectedMarker)
{
    if (frame->nb_side_data != 2)
    {
        throw new InvalidOperationException(
            $"Expected two HDR side-data entries, found {frame->nb_side_data}.");
    }

    AVFrameSideData* masteringSideData = ffmpeg.av_frame_get_side_data(
        frame,
        AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    if (masteringSideData == null ||
        masteringSideData->data == null ||
        masteringSideData->size < (ulong)sizeof(AVMasteringDisplayMetadata))
    {
        throw new InvalidOperationException("Mastering display metadata was not preserved.");
    }

    AVMasteringDisplayMetadata* mastering =
        (AVMasteringDisplayMetadata*)masteringSideData->data;
    if (mastering->has_luminance == 0 ||
        mastering->max_luminance.num != expectedMaxLuminance ||
        mastering->max_luminance.den != 1)
    {
        throw new InvalidOperationException("Mastering display metadata value was not replaced.");
    }

    AVFrameSideData* contentLightSideData = ffmpeg.av_frame_get_side_data(
        frame,
        AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
    if (contentLightSideData == null ||
        contentLightSideData->data == null ||
        contentLightSideData->size < (ulong)sizeof(AVContentLightMetadata))
    {
        throw new InvalidOperationException("Content light metadata was not preserved.");
    }

    AVContentLightMetadata* contentLight =
        (AVContentLightMetadata*)contentLightSideData->data;
    if (contentLight->MaxCLL != expectedMaxCll || contentLight->MaxFALL != expectedMaxFall)
        throw new InvalidOperationException("Content light metadata value was not replaced.");

    AVDictionaryEntry* marker = ffmpeg.av_dict_get(
        frame->metadata,
        "hdr-marker",
        null,
        0);
    string? actualMarker = marker == null || marker->value == null
        ? null
        : Marshal.PtrToStringUTF8((IntPtr)marker->value);
    if (!string.Equals(actualMarker, expectedMarker, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Frame metadata marker mismatch: expected={expectedMarker}, actual={actualMarker}.");
    }
}

static void ThrowIfError(int result, string operation)
{
    if (result < 0)
        throw new InvalidOperationException($"{operation}: {GetError(result)}");
}

static unsafe void FillYuv420(AVFrame* frame, byte yValue, byte uValue, byte vValue)
{
    for (int y = 0; y < frame->height; y++)
        new Span<byte>(frame->data[0] + y * frame->linesize[0], frame->width).Fill(yValue);

    for (int y = 0; y < frame->height / 2; y++)
    {
        new Span<byte>(frame->data[1] + y * frame->linesize[1], frame->width / 2).Fill(uValue);
        new Span<byte>(frame->data[2] + y * frame->linesize[2], frame->width / 2).Fill(vValue);
    }
}

static unsafe void Scale(SwsContext* context, AVFrame* destination, AVFrame* source, string name)
{
    int result = ffmpeg.sws_scale_frame(context, destination, source);
    if (result < 0)
        throw new InvalidOperationException($"{name} failed: {GetError(result)}");
}

static unsafe BgraPixel ReadBgra(AVFrame* frame, int x, int y)
{
    byte* pixel = frame->data[0] + y * frame->linesize[0] + x * 4;
    return new BgraPixel(pixel[0], pixel[1], pixel[2], pixel[3]);
}

static unsafe YuvPixel ReadYuv420(AVFrame* frame, int x, int y)
{
    byte luma = frame->data[0][y * frame->linesize[0] + x];
    byte cb = frame->data[1][(y / 2) * frame->linesize[1] + x / 2];
    byte cr = frame->data[2][(y / 2) * frame->linesize[2] + x / 2];
    return new YuvPixel(luma, cb, cr);
}

static unsafe void AssertProperties(
    AVFrame* frame,
    AVColorRange expectedRange,
    AVColorSpace expectedSpace,
    AVChromaLocation expectedChroma,
    string name)
{
    if (frame->color_range != expectedRange ||
        frame->colorspace != expectedSpace ||
        frame->color_primaries != AVColorPrimaries.AVCOL_PRI_BT709 ||
        frame->color_trc != AVColorTransferCharacteristic.AVCOL_TRC_BT709 ||
        frame->chroma_location != expectedChroma ||
        frame->sample_aspect_ratio.num != 1 ||
        frame->sample_aspect_ratio.den != 1)
    {
        throw new InvalidOperationException($"Unexpected color properties on {name}.");
    }
}

static void AssertNear(int actual, int expected, int tolerance, string name)
{
    if (Math.Abs(actual - expected) > tolerance)
    {
        throw new InvalidOperationException(
            $"Unexpected {name}: expected={expected}+/-{tolerance}, actual={actual}.");
    }
}

static unsafe string GetError(int result)
{
    byte* buffer = stackalloc byte[512];
    ffmpeg.av_strerror(result, buffer, 512);
    return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
}

readonly record struct BgraPixel(byte B, byte G, byte R, byte A);
readonly record struct YuvPixel(byte Y, byte U, byte V);
'@ | Set-Content -Encoding UTF8 -Path $program

    & dotnet run --project $project --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "SWS frame color fidelity harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $work
}

Write-Host (
    "[SwsFrameColorFidelityVerify] PASS legacyCalls=0 dynamicContexts=3 " +
    "metadataFirstConversions=4 framePropertiesReset=true nativeRoundTrip=true " +
    "hdrSideDataReplacement=true")
