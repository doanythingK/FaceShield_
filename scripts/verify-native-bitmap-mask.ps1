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
using System.Runtime.InteropServices;

unsafe
{
    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();

    const int width = 32;
    const int height = 24;
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
        Verify(AVPixelFormat.AV_PIX_FMT_YUV420P10LE, p010: false);
        Verify(AVPixelFormat.AV_PIX_FMT_P010LE, p010: true);
        Console.WriteLine("[NativeBitmapMaskVerify] PASS formats=yuv420p10le,p010le outsideBitExact=true maskedChanged=true");
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
            summary.HybridCopyAttempted ||
            summary.HybridCopyUsed)
            throw new InvalidOperationException($"Unexpected native bitmap summary: {summary.ToLogLine()}");
        Console.WriteLine($"[NativeBitmapMaskE2E] PASS {summary.ToLogLine()}");
    }

    void Verify(AVPixelFormat format, bool p010)
    {
        int chromaWidth = width / 2;
        int chromaHeight = height / 2;
        int ySamples = width * height;
        int chromaSamples = chromaWidth * chromaHeight;
        ushort* yPlane = (ushort*)NativeMemory.Alloc((nuint)(ySamples * sizeof(ushort)));
        ushort* uPlane = (ushort*)NativeMemory.Alloc((nuint)(chromaSamples * (p010 ? 2 : 1) * sizeof(ushort)));
        ushort* vPlane = p010 ? null : (ushort*)NativeMemory.Alloc((nuint)(chromaSamples * sizeof(ushort)));
        ushort* originalY = (ushort*)NativeMemory.Alloc((nuint)(ySamples * sizeof(ushort)));
        try
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int value = 80 + ((x * 17 + y * 29) % 850);
                    ushort stored = (ushort)(p010 ? value << 6 : value);
                    yPlane[y * width + x] = stored;
                    originalY[y * width + x] = stored;
                }
            }
            for (int i = 0; i < chromaSamples; i++)
            {
                int u = 100 + (i * 13 % 800);
                int v = 120 + (i * 19 % 780);
                if (p010)
                {
                    uPlane[i * 2] = (ushort)(u << 6);
                    uPlane[i * 2 + 1] = (ushort)(v << 6);
                }
                else
                {
                    uPlane[i] = (ushort)u;
                    vPlane![i] = (ushort)v;
                }
            }

            AVFrame frame = default;
            frame.width = width;
            frame.height = height;
            frame.format = (int)format;
            frame.data[0] = (byte*)yPlane;
            frame.linesize[0] = width * 2;
            frame.data[1] = (byte*)uPlane;
            frame.linesize[1] = p010 ? chromaWidth * 4 : chromaWidth * 2;
            if (!p010)
            {
                frame.data[2] = (byte*)vPlane;
                frame.linesize[2] = chromaWidth * 2;
            }

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
                    if (!inside && yPlane[index] != originalY[index])
                        throw new InvalidOperationException($"Unmasked luma changed for {format} at {x},{y}.");
                    if (inside && yPlane[index] != originalY[index])
                        changedInside++;
                }
            }
            if (changedInside == 0)
                throw new InvalidOperationException($"Masked luma did not change for {format}.");
        }
        finally
        {
            NativeMemory.Free(yPlane);
            NativeMemory.Free(uPlane);
            if (vPlane != null)
                NativeMemory.Free(vPlane);
            NativeMemory.Free(originalY);
        }
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
