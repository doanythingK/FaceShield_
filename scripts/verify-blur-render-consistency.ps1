param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\blur-render-consistency"
$project = Join-Path $work "BlurRenderConsistencyHarness.csproj"
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
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FaceShield;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Reflection;

unsafe
{
    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();

    const int width = 96;
    const int height = 72;
    const int blurRadius = 28;
    using var source = CreateSource(width, height);

    VerifyRadiusPolicy();
    VerifyRadiusMapLogicalHeight();

    Rect[] faces =
    [
        new Rect(24, 18, 26, 24),
        new Rect(42, 25, 30, 28)
    ];
    using var faceMask = FrameMaskProvider.CreateMaskFromFaceRects(
        new PixelSize(width, height),
        faces);
    using var preview = PreviewBlurProcessor.CreateBlurPreview(
        source,
        faceMask,
        blurRadius,
        faces);
    VerifyCrossThreadCacheReset(source, faceMask, faces);
    using var direct = Clone(source);
    Apply(direct, faceMask, blurRadius, faces, new MaskedVideoExporter());
    AssertEqual(preview, direct, "preview/export BGRA output");

    Rect[] reversedFaces = [faces[1], faces[0]];
    using var reversedMask = FrameMaskProvider.CreateMaskFromFaceRects(
        new PixelSize(width, height),
        reversedFaces);
    using var reversed = PreviewBlurProcessor.CreateBlurPreview(
        source,
        reversedMask,
        blurRadius,
        reversedFaces);
    AssertEqual(preview, reversed, "overlapping face order");

    using var firstMask = CreateRectMask(width, height, hollow: false);
    using var secondMask = CreateRectMask(width, height, hollow: true);
    var reusedExporter = new MaskedVideoExporter();
    using var firstFrame = Clone(source);
    Apply(firstFrame, firstMask, 12, null, reusedExporter);
    using var reusedSecond = Clone(source);
    Apply(reusedSecond, secondMask, 12, null, reusedExporter);
    using var freshSecond = Clone(source);
    Apply(freshSecond, secondMask, 12, null, new MaskedVideoExporter());
    AssertEqual(reusedSecond, freshSecond, "renderer call history");

    using var previewFirst = PreviewBlurProcessor.CreateBlurPreview(source, firstMask, 12);
    using var previewSecond = PreviewBlurProcessor.CreateBlurPreview(source, secondMask, 12);
    AssertEqual(previewSecond, freshSecond, "preview call history");

    Console.WriteLine(
        "[BlurRenderConsistencyVerify] PASS radiusPolicy=true radiusMapHeight=true " +
        "previewExportMatch=true overlapOrderIndependent=true stateless=true " +
        "crossThreadCacheReset=true");

    void VerifyCrossThreadCacheReset(
        WriteableBitmap sourceBitmap,
        WriteableBitmap maskBitmap,
        IReadOnlyList<Rect> faceRects)
    {
        FieldInfo rendererField = typeof(PreviewBlurProcessor).GetField(
            "_previewExporter",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Preview renderer cache was not found.");
        MethodInfo releaseMethod = typeof(PreviewBlurProcessor).GetMethod(
            "ReleaseCachedRenderer",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Preview renderer release method was not found.");
        object before = rendererField.GetValue(null)
            ?? throw new InvalidOperationException("Preview renderer was not cached.");

        System.Threading.Tasks.Task.Run(() => releaseMethod.Invoke(null, null))
            .GetAwaiter()
            .GetResult();
        using var refreshed = PreviewBlurProcessor.CreateBlurPreview(
            sourceBitmap,
            maskBitmap,
            blurRadius,
            faceRects);
        object after = rendererField.GetValue(null)
            ?? throw new InvalidOperationException("Preview renderer was not recreated.");
        if (ReferenceEquals(before, after))
            throw new InvalidOperationException("Cross-thread cache release kept the stale renderer.");
    }

    void VerifyRadiusPolicy()
    {
        Type geometry = typeof(MaskedVideoExporter).Assembly.GetType(
            "FaceShield.Services.Video.FaceBlurGeometry")
            ?? throw new InvalidOperationException("FaceBlurGeometry was not found.");
        MethodInfo getRadius = geometry.GetMethod(
            "GetRadius",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FaceBlurGeometry.GetRadius was not found.");

        var cases = new (Rect Face, int Expected)[]
        {
            (new Rect(0, 0, 50, 50), 11),
            (new Rect(0, 0, 100, 100), 15),
            (new Rect(0, 0, 150, 100), 15),
            (new Rect(0, 0, 200, 150), 20),
            (new Rect(0, 0, 250, 200), 28)
        };
        foreach (var item in cases)
        {
            int actual = (int)(getRadius.Invoke(null, [item.Face, 1000, 1000, blurRadius])
                ?? throw new InvalidOperationException("Face blur radius was not returned."));
            if (actual != item.Expected)
                throw new InvalidOperationException(
                    $"Unexpected face blur radius: expected={item.Expected}, actual={actual}.");
        }
    }

    void VerifyRadiusMapLogicalHeight()
    {
        MethodInfo getChromaRadius = typeof(MaskedVideoExporter).GetMethod(
            "GetChromaRadius",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetChromaRadius was not found.");
        byte[] reusedMap = new byte[8];
        reusedMap[4] = 99;
        int actual = (int)(getChromaRadius.Invoke(
            null,
            [reusedMap, 2, 2, 0, 0, 0, 1, 7, 1, 1])
            ?? throw new InvalidOperationException("Chroma radius was not returned."));
        if (actual != 7)
            throw new InvalidOperationException(
                $"Radius map read stale rows: expected=7, actual={actual}.");
    }

    static WriteableBitmap CreateSource(int w, int h)
    {
        var bitmap = NewBitmap(w, h);
        using var framebuffer = bitmap.Lock();
        byte* address = (byte*)framebuffer.Address;
        for (int y = 0; y < h; y++)
        {
            byte* row = address + y * framebuffer.RowBytes;
            for (int x = 0; x < w; x++)
            {
                row[x * 4] = (byte)((x * 13 + y * 3) & 0xff);
                row[x * 4 + 1] = (byte)((x * 5 + y * 11) & 0xff);
                row[x * 4 + 2] = (byte)((x * 17 + y * 7) & 0xff);
                row[x * 4 + 3] = byte.MaxValue;
            }
        }
        return bitmap;
    }

    static WriteableBitmap CreateRectMask(int w, int h, bool hollow)
    {
        var bitmap = NewBitmap(w, h);
        using var framebuffer = bitmap.Lock();
        byte* address = (byte*)framebuffer.Address;
        for (int y = 0; y < h; y++)
            new Span<byte>(address + y * framebuffer.RowBytes, w * 4).Clear();

        const int x0 = 20;
        const int x1 = 76;
        const int y0 = 14;
        const int y1 = 58;
        for (int y = y0; y < y1; y++)
        {
            byte* row = address + y * framebuffer.RowBytes;
            for (int x = x0; x < x1; x++)
            {
                bool boundary = x == x0 || x == x1 - 1 || y == y0 || y == y1 - 1;
                if (!hollow || boundary)
                    row[x * 4 + 3] = byte.MaxValue;
            }
        }
        return bitmap;
    }

    static WriteableBitmap Clone(WriteableBitmap sourceBitmap)
    {
        var clone = NewBitmap(sourceBitmap.PixelSize.Width, sourceBitmap.PixelSize.Height);
        using var sourceBuffer = sourceBitmap.Lock();
        using var cloneBuffer = clone.Lock();
        int rowBytes = sourceBitmap.PixelSize.Width * 4;
        for (int y = 0; y < sourceBitmap.PixelSize.Height; y++)
        {
            Buffer.MemoryCopy(
                (byte*)sourceBuffer.Address + y * sourceBuffer.RowBytes,
                (byte*)cloneBuffer.Address + y * cloneBuffer.RowBytes,
                cloneBuffer.RowBytes,
                rowBytes);
        }
        return clone;
    }

    static void Apply(
        WriteableBitmap bitmap,
        WriteableBitmap mask,
        int radius,
        IReadOnlyList<Rect>? faceRects,
        MaskedVideoExporter exporter)
    {
        using var framebuffer = bitmap.Lock();
        AVFrame frame = default;
        frame.width = bitmap.PixelSize.Width;
        frame.height = bitmap.PixelSize.Height;
        frame.format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        frame.data[0] = (byte*)framebuffer.Address;
        frame.linesize[0] = framebuffer.RowBytes;
        if (!exporter.ApplyMaskAndBlur(&frame, mask, radius, faceRects))
            throw new InvalidOperationException("BGRA blur was not applied.");
    }

    static void AssertEqual(WriteableBitmap expected, WriteableBitmap actual, string name)
    {
        using var expectedBuffer = expected.Lock();
        using var actualBuffer = actual.Lock();
        int rowBytes = expected.PixelSize.Width * 4;
        for (int y = 0; y < expected.PixelSize.Height; y++)
        {
            ReadOnlySpan<byte> expectedRow = new(
                (byte*)expectedBuffer.Address + y * expectedBuffer.RowBytes,
                rowBytes);
            ReadOnlySpan<byte> actualRow = new(
                (byte*)actualBuffer.Address + y * actualBuffer.RowBytes,
                rowBytes);
            if (!expectedRow.SequenceEqual(actualRow))
                throw new InvalidOperationException($"Mismatch in {name} at row {y}.");
        }
    }

    static WriteableBitmap NewBitmap(int w, int h)
        => new(
            new PixelSize(w, h),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
}
'@ | Set-Content -Encoding UTF8 -Path $program

    & dotnet run --project $project --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Blur render consistency harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $work
}
