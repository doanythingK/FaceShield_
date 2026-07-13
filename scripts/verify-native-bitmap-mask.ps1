param(
    [string]$Source = "",
    [string]$Output = "",
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\native-bitmap-mask"
$project = Join-Path $work "NativeBitmapMaskHarness.csproj"
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
using Avalonia.Platform;
using FaceShield;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

unsafe
{
    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();

    const int width = 33;
    const int height = 25;
    using var mask = new WriteableBitmap(
        new PixelSize(width, height),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Premul);
    using (var framebuffer = mask.Lock())
    {
        byte* address = (byte*)framebuffer.Address;
        for (int y = 0; y < height; y++)
            new Span<byte>(address + y * framebuffer.RowBytes, width * 4).Clear();
        for (int y = 8; y < 16; y++)
        {
            byte* row = address + y * framebuffer.RowBytes;
            for (int x = 10; x < 22; x++)
                row[x * 4 + 3] = byte.MaxValue;
        }
    }

    if (args.Length == 0)
    {
        var formats = new (AVPixelFormat Format, int Bits, int ShiftX, int ShiftY, bool Interleaved, int ValueShift)[]
        {
            (AVPixelFormat.AV_PIX_FMT_YUV420P, 8, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P, 8, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P, 8, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV420P9LE, 9, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV420P10LE, 10, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV420P12LE, 12, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV420P14LE, 14, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV420P16LE, 16, 1, 1, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P9LE, 9, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P10LE, 10, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P12LE, 12, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P14LE, 14, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV422P16LE, 16, 1, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P9LE, 9, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P10LE, 10, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P12LE, 12, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P14LE, 14, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_YUV444P16LE, 16, 0, 0, false, 0),
            (AVPixelFormat.AV_PIX_FMT_NV12, 8, 1, 1, true, 0),
            (AVPixelFormat.AV_PIX_FMT_P010LE, 10, 1, 1, true, 6),
            (AVPixelFormat.AV_PIX_FMT_P012LE, 12, 1, 1, true, 4),
            (AVPixelFormat.AV_PIX_FMT_P016LE, 16, 1, 1, true, 0)
        };
        foreach (var item in formats)
            Verify(item.Format, item.Bits, item.ShiftX, item.ShiftY, item.Interleaved, item.ValueShift);
        Console.WriteLine($"[NativeBitmapMaskVerify] PASS formats={formats.Length} outsideBitExact=true maskedYuvChanged=true oddDimensions=true");
    }

    if (args.Length >= 4)
    {
        int videoWidth = int.Parse(args[2]);
        int videoHeight = int.Parse(args[3]);
        using var videoMask = new WriteableBitmap(
            new PixelSize(videoWidth, videoHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var framebuffer = videoMask.Lock())
        {
            byte* address = (byte*)framebuffer.Address;
            for (int y = 0; y < videoHeight; y++)
                new Span<byte>(address + y * framebuffer.RowBytes, videoWidth * 4).Clear();

            int x0 = videoWidth * 2 / 5;
            int x1 = videoWidth * 3 / 5;
            int y0 = videoHeight * 2 / 5;
            int y1 = videoHeight * 3 / 5;
            for (int y = y0; y < y1; y++)
            {
                byte* row = address + y * framebuffer.RowBytes;
                for (int x = x0; x < x1; x++)
                    row[x * 4 + 3] = byte.MaxValue;
            }
        }

        var provider = new FrameMaskProvider();
        provider.SetMask(10, videoMask);
        var service = new VideoExportService(provider);
        service.Export(args[0], args[1], 28, runId: "native-bitmap-e2e");
        var summary = service.LastExportSummary ?? throw new InvalidOperationException("Missing export summary.");
        if (summary.BitmapMaskFrames != 1 ||
            summary.NativeYuvBlurFrames != 1 ||
            summary.SwsToBgraMs != 0 ||
            summary.EncodeMs < summary.EncoderFlushMs ||
            !summary.OutputCommitted ||
            summary.AttemptCount != 1 ||
            summary.FinalAttemptMs <= 0 ||
            summary.TotalMs < summary.FinalAttemptMs ||
            summary.HybridCopyAttempted ||
            summary.HybridCopyUsed)
            throw new InvalidOperationException($"Unexpected native bitmap summary: {summary.ToLogLine()}");
        AssertVideoPacketTimingPreserved(args[0], args[1]);
        Console.WriteLine($"[NativeBitmapMaskE2E] PASS {summary.ToLogLine()}");
        Console.WriteLine("[NativeBitmapMaskE2E] PASS packetTimingPreserved=true");
    }

    void Verify(
        AVPixelFormat format,
        int bits,
        int chromaShiftX,
        int chromaShiftY,
        bool interleaved,
        int valueShift)
    {
        int bytesPerSample = bits > 8 ? 2 : 1;
        int chromaWidth = (width + (1 << chromaShiftX) - 1) >> chromaShiftX;
        int chromaHeight = (height + (1 << chromaShiftY) - 1) >> chromaShiftY;
        int ySamples = width * height;
        int chromaSamples = chromaWidth * chromaHeight;
        byte* yPlane = (byte*)NativeMemory.Alloc((nuint)(ySamples * bytesPerSample));
        byte* uPlane = (byte*)NativeMemory.Alloc((nuint)(chromaSamples * bytesPerSample * (interleaved ? 2 : 1)));
        byte* vPlane = interleaved ? null : (byte*)NativeMemory.Alloc((nuint)(chromaSamples * bytesPerSample));
        var originalY = new ushort[ySamples];
        var originalU = new ushort[chromaSamples];
        var originalV = new ushort[chromaSamples];
        try
        {
            int maxValue = bits == 16 ? ushort.MaxValue : (1 << bits) - 1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int value = 16 + ((x * x * 17 + y * y * 29 + x * y * 7) % Math.Max(1, maxValue - 16));
                    int index = y * width + x;
                    WriteSample(yPlane + index * bytesPerSample, bytesPerSample, valueShift, value);
                    originalY[index] = (ushort)value;
                }
            }
            for (int y = 0; y < chromaHeight; y++)
            {
                for (int x = 0; x < chromaWidth; x++)
                {
                    int index = y * chromaWidth + x;
                    int u = 16 + ((x * x * 13 + y * y * 23 + x * y * 5) % Math.Max(1, maxValue - 16));
                    int v = 16 + ((x * x * 19 + y * y * 31 + x * y * 11) % Math.Max(1, maxValue - 16));
                    if (interleaved)
                    {
                        byte* sample = uPlane + index * bytesPerSample * 2;
                        WriteSample(sample, bytesPerSample, valueShift, u);
                        WriteSample(sample + bytesPerSample, bytesPerSample, valueShift, v);
                    }
                    else
                    {
                        WriteSample(uPlane + index * bytesPerSample, bytesPerSample, valueShift, u);
                        WriteSample(vPlane + index * bytesPerSample, bytesPerSample, valueShift, v);
                    }
                    originalU[index] = (ushort)u;
                    originalV[index] = (ushort)v;
                }
            }

            AVFrame frame = default;
            frame.width = width;
            frame.height = height;
            frame.format = (int)format;
            frame.data[0] = yPlane;
            frame.linesize[0] = width * bytesPerSample;
            frame.data[1] = uPlane;
            frame.linesize[1] = chromaWidth * bytesPerSample * (interleaved ? 2 : 1);
            if (!interleaved)
            {
                frame.data[2] = vPlane;
                frame.linesize[2] = chromaWidth * bytesPerSample;
            }

            if (!MaskedVideoExporter.CanApplyNativeYuv(&frame))
                throw new InvalidOperationException($"Native layout was rejected for {format}.");
            var exporter = new MaskedVideoExporter();
            if (!exporter.TryApplyMaskAndBlurNative(&frame, mask, blurRadius: 4))
                throw new InvalidOperationException($"Native mask was not applied for {format}.");

            int changedInside = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    bool inside = x >= 10 && x < 22 && y >= 8 && y < 16;
                    int actual = ReadSample(yPlane + index * bytesPerSample, bytesPerSample, valueShift);
                    if (!inside && actual != originalY[index])
                        throw new InvalidOperationException($"Unmasked luma changed for {format} at {x},{y}.");
                    if (inside && actual != originalY[index])
                        changedInside++;
                }
            }
            if (changedInside == 0)
                throw new InvalidOperationException($"Masked luma did not change for {format}.");

            int chromaX0 = 10 >> chromaShiftX;
            int chromaY0 = 8 >> chromaShiftY;
            int chromaX1 = (22 + (1 << chromaShiftX) - 1) >> chromaShiftX;
            int chromaY1 = (16 + (1 << chromaShiftY) - 1) >> chromaShiftY;
            int changedChroma = 0;
            for (int y = 0; y < chromaHeight; y++)
            {
                for (int x = 0; x < chromaWidth; x++)
                {
                    int index = y * chromaWidth + x;
                    byte* uSample = uPlane + index * bytesPerSample * (interleaved ? 2 : 1);
                    byte* vSample = interleaved
                        ? uSample + bytesPerSample
                        : vPlane + index * bytesPerSample;
                    int actualU = ReadSample(uSample, bytesPerSample, valueShift);
                    int actualV = ReadSample(vSample, bytesPerSample, valueShift);
                    bool inside = x >= chromaX0 && x < chromaX1 && y >= chromaY0 && y < chromaY1;
                    if (!inside && (actualU != originalU[index] || actualV != originalV[index]))
                        throw new InvalidOperationException($"Unmasked chroma changed for {format} at {x},{y}.");
                    if (inside && (actualU != originalU[index] || actualV != originalV[index]))
                        changedChroma++;
                }
            }
            if (changedChroma == 0)
                throw new InvalidOperationException($"Masked chroma did not change for {format}.");
        }
        finally
        {
            NativeMemory.Free(yPlane);
            NativeMemory.Free(uPlane);
            if (vPlane != null)
                NativeMemory.Free(vPlane);
        }
    }

    static void AssertVideoPacketTimingPreserved(string sourcePath, string outputPath)
    {
        var source = ReadVideoPacketTiming(sourcePath);
        var output = ReadVideoPacketTiming(outputPath);
        if (source.Count == 0 || output.Count != source.Count)
        {
            throw new InvalidOperationException(
                $"Video packet timing count changed: source={source.Count}, output={output.Count}.");
        }

        source.Sort(static (left, right) => left.PtsUs.CompareTo(right.PtsUs));
        output.Sort(static (left, right) => left.PtsUs.CompareTo(right.PtsUs));
        for (int i = 0; i < source.Count; i++)
        {
            var sourcePacket = source[i];
            var outputPacket = output[i];
            if (Math.Abs(sourcePacket.PtsUs - outputPacket.PtsUs) > 1 ||
                (sourcePacket.DurationUs > 0 &&
                 Math.Abs(sourcePacket.DurationUs - outputPacket.DurationUs) > 1))
            {
                throw new InvalidOperationException(
                    $"Video packet timing changed at presentation index {i}: " +
                    $"{sourcePacket.PtsUs}/{sourcePacket.DurationUs} -> " +
                    $"{outputPacket.PtsUs}/{outputPacket.DurationUs} microseconds.");
            }
        }
    }

    static List<(long PtsUs, long DurationUs)> ReadVideoPacketTiming(string path)
    {
        AVFormatContext* format = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
            throw new InvalidOperationException("Unable to allocate a packet timing probe.");

        var result = new List<(long PtsUs, long DurationUs)>();
        try
        {
            Check(ffmpeg.avformat_open_input(&format, Path.GetFullPath(path), null, null));
            Check(ffmpeg.avformat_find_stream_info(format, null));
            int videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(format);
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("Packet timing probe found no primary video stream.");

            AVRational timeBase = format->streams[videoStreamIndex]->time_base;
            AVRational microseconds = new() { num = 1, den = ffmpeg.AV_TIME_BASE };
            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == videoStreamIndex)
                {
                    if (packet->pts == ffmpeg.AV_NOPTS_VALUE)
                        throw new InvalidOperationException("Packet timing probe found a missing video PTS.");
                    if (packet->duration < 0)
                        throw new InvalidOperationException("Packet timing probe found a negative video duration.");

                    result.Add((
                        ffmpeg.av_rescale_q(packet->pts, timeBase, microseconds),
                        ffmpeg.av_rescale_q(packet->duration, timeBase, microseconds)));
                }
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }

        return result;
    }

    static void Check(int result)
    {
        if (result < 0)
            throw new InvalidOperationException($"Packet timing probe failed with FFmpeg error {result}.");
    }

    static int ReadSample(byte* sample, int bytesPerSample, int valueShift) =>
        bytesPerSample == 1 ? sample[0] : (*(ushort*)sample) >> valueShift;

    static void WriteSample(byte* sample, int bytesPerSample, int valueShift, int value)
    {
        if (bytesPerSample == 1)
            sample[0] = (byte)value;
        else
            *(ushort*)sample = (ushort)(value << valueShift);
    }
}
'@ | Set-Content -Encoding UTF8 $program

if ([string]::IsNullOrWhiteSpace($Source) -xor [string]::IsNullOrWhiteSpace($Output)) {
    throw "Source and Output must be provided together."
}
if (-not [string]::IsNullOrWhiteSpace($Source)) {
    if (-not (Test-Path $Source)) {
        throw "Source video not found: $Source"
    }
    if ($Width -le 0 -or $Height -le 0) {
        throw "Width and Height must be positive for the end-to-end verification."
    }
    dotnet run --project $project --configuration Debug -- $Source $Output $Width $Height
}
else {
    dotnet run --project $project --configuration Debug
}
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
