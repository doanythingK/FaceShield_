param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\encoder-quality-options"
$project = Join-Path $work "EncoderQualityOptionsHarness.csproj"
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
using System.Collections.Generic;

FFmpegBootstrap.Initialize();

var cases = new EncoderCase[]
{
    new("libx264", true,
        Required: new[] { ("preset", "fast"), ("crf", "14") }),
    new("libx265", false,
        Required: new[] { ("preset", "fast"), ("crf", "16") }),
    new("h264_nvenc", false,
        Required: new[] { ("preset", "p6"), ("tune", "hq"), ("rc", "vbr"), ("cq", "12") },
        Optional: new[] { ("multipass", "qres"), ("spatial_aq", "1"), ("temporal_aq", "1"), ("rc-lookahead", "20"), ("extra_sei", "1") }),
    new("hevc_nvenc", false,
        Required: new[] { ("preset", "p6"), ("tune", "hq"), ("rc", "vbr"), ("cq", "12") },
        Optional: new[] { ("multipass", "qres"), ("spatial_aq", "1"), ("temporal_aq", "1"), ("rc-lookahead", "20"), ("extra_sei", "1") }),
    new("av1_nvenc", false,
        Required: new[] { ("preset", "p6"), ("tune", "hq"), ("rc", "vbr"), ("cq", "12") },
        Optional: new[] { ("multipass", "qres"), ("spatial_aq", "1"), ("temporal_aq", "1"), ("rc-lookahead", "20"), ("extra_sei", "1") }),
    new("h264_qsv", false,
        Required: new[] { ("preset", "veryslow") },
        Optional: new[] { ("look_ahead", "1"), ("look_ahead_depth", "40"), ("rdo", "1"), ("adaptive_i", "1"), ("adaptive_b", "1") }),
    new("hevc_qsv", false,
        Required: new[] { ("preset", "veryslow") },
        Optional: new[] { ("look_ahead_depth", "40"), ("rdo", "1"), ("adaptive_i", "1"), ("adaptive_b", "1") }),
    new("av1_qsv", false,
        Required: new[] { ("preset", "veryslow") },
        Optional: new[] { ("look_ahead_depth", "40"), ("rdo", "1"), ("adaptive_i", "1"), ("adaptive_b", "1") }),
    new("h264_amf", false,
        Required: new[] { ("usage", "high_quality"), ("quality", "quality"), ("rc", "hqvbr") },
        Optional: new[] { ("preanalysis", "1"), ("vbaq", "1"), ("high_motion_quality_boost_enable", "1") }),
    new("hevc_amf", false,
        Required: new[] { ("usage", "high_quality"), ("quality", "quality"), ("rc", "hqvbr") },
        Optional: new[] { ("preanalysis", "1"), ("vbaq", "1"), ("high_motion_quality_boost_enable", "1") }),
    new("av1_amf", false,
        Required: new[] { ("usage", "high_quality"), ("quality", "quality"), ("rc", "hqvbr") },
        Optional: new[] { ("preanalysis", "1"), ("vbaq", "1"), ("high_motion_quality_boost_enable", "1") }),
    new("libsvtav1", false,
        Required: new[] { ("preset", "6"), ("crf", "12"), ("svtav1-params", "tune=0") }),
    new("libaom-av1", false,
        Required: new[] { ("usage", "good"), ("cpu-used", "4"), ("crf", "12") },
        Optional: new[] { ("row-mt", "1"), ("tune", "psnr") }),
    new("h264_videotoolbox", false,
        Required: new[] { ("realtime", "false"), ("prio_speed", "0") },
        Optional: new[] { ("spatial_aq", "1") }),
    new("hevc_videotoolbox", false,
        Required: new[] { ("realtime", "false"), ("prio_speed", "0") },
        Optional: new[] { ("spatial_aq", "1") })
};

int available = 0;
int availableAv1Software = 0;
foreach (EncoderCase test in cases)
{
    unsafe
    {
        AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(test.Name);
        if (encoder == null)
        {
            if (test.MustExist)
                throw new InvalidOperationException($"Required encoder not found: {test.Name}");
            Console.WriteLine($"[EncoderQualityOptionsVerify] SKIP encoder={test.Name} reason=not-available");
            continue;
        }

        available++;
        if (test.Name is "libsvtav1" or "libaom-av1")
            availableAv1Software++;
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(encoder);
        if (context == null || context->priv_data == null)
            throw new InvalidOperationException($"Unable to allocate encoder options: {test.Name}");

        try
        {
            foreach ((string key, string value) in test.Required)
                SetOption(context, test.Name, key, value, required: true);
            foreach ((string key, string value) in test.Optional ?? Array.Empty<(string Key, string Value)>())
                SetOption(context, test.Name, key, value, required: false);
        }
        finally
        {
            ffmpeg.avcodec_free_context(&context);
        }
    }
}

if (availableAv1Software == 0)
    throw new InvalidOperationException("No supported AV1 software encoder was found.");

Console.WriteLine($"[EncoderQualityOptionsVerify] PASS available={available} av1Software={availableAv1Software} cases={cases.Length}");

static unsafe void SetOption(
    AVCodecContext* context,
    string encoder,
    string key,
    string value,
    bool required)
{
    int result = ffmpeg.av_opt_set(context->priv_data, key, value, 0);
    if (result >= 0)
    {
        Console.WriteLine($"[EncoderQualityOptionsVerify] PASS encoder={encoder} option={key} value={value}");
        return;
    }

    string error = GetError(result);
    if (required)
        throw new InvalidOperationException($"encoder={encoder}, option={key}, value={value}, error={error}");
    Console.WriteLine($"[EncoderQualityOptionsVerify] OPTIONAL encoder={encoder} option={key} value={value} error={error}");
}

static unsafe string GetError(int result)
{
    byte* buffer = stackalloc byte[512];
    ffmpeg.av_strerror(result, buffer, 512);
    return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
}

sealed record EncoderCase(
    string Name,
    bool MustExist,
    IReadOnlyList<(string Key, string Value)> Required,
    IReadOnlyList<(string Key, string Value)>? Optional = null);
'@ | Set-Content -Encoding UTF8 $program

& dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    throw "Encoder quality options verifier failed with exit code $LASTEXITCODE."
}
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
    if (Test-Path $work) {
        throw "Encoder quality options harness directory remained: $work"
    }
}
