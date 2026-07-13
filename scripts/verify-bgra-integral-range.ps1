param(
    [switch]$RunFullFrame
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\bgra-integral-range"
$project = Join-Path $work "BgraIntegralRangeHarness.csproj"
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
using FaceShield.ViewModels.Workspace;
using FFmpeg.AutoGen;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

const int dci4kWidth = 4096;
const int dci4kHeight = 2160;
const int eightKWidth = 7680;
const int eightKHeight = 4320;
const int propertyCases = 10_000;

Type exporterType = typeof(MaskedVideoExporter);
MethodInfo getIntegralSum = exporterType.GetMethod(
    "GetIntegralSum",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("GetIntegralSum was not found.");
MethodInfo ensureIntegralBuffers = exporterType.GetMethod(
    "EnsureIntegralBuffers",
    BindingFlags.NonPublic | BindingFlags.Instance)
    ?? throw new InvalidOperationException("EnsureIntegralBuffers was not found.");

foreach (string fieldName in new[] { "_integralB", "_integralG", "_integralR", "_integralA" })
{
    FieldInfo field = exporterType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{fieldName} was not found.");
    if (field.FieldType != typeof(uint[]))
        throw new InvalidOperationException($"{fieldName} is not a 32-bit unsigned buffer.");
}
if (sizeof(uint) != sizeof(int))
    throw new InvalidOperationException("Integral storage size changed.");

FieldInfo maxRadiusField = typeof(ToolPanelViewModel).GetField(
    "MaxBlurRadiusValue",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("MaxBlurRadiusValue was not found.");
int maxRadius = (int)(maxRadiusField.GetRawConstantValue()
    ?? throw new InvalidOperationException("MaxBlurRadiusValue was not returned."));
int maxWindow = checked(maxRadius * 2 + 1);
if (maxRadius != 40 || maxWindow != 81)
    throw new InvalidOperationException(
        $"Unexpected configured blur range: radius={maxRadius}, window={maxWindow}.");

uint dci4kFull = SumRect(0, 0, dci4kWidth, dci4kHeight, byte.MaxValue);
ulong expectedDci4k = (ulong)dci4kWidth * dci4kHeight * byte.MaxValue;
if (expectedDci4k <= int.MaxValue || dci4kFull != expectedDci4k)
    throw new InvalidOperationException(
        $"DCI 4K full sum mismatch: expected={expectedDci4k}, actual={dci4kFull}.");

var random = new Random(0x5A17);
for (int i = 0; i < propertyCases; i++)
{
    int width = random.Next(1, maxWindow + 1);
    int height = random.Next(1, maxWindow + 1);
    int x0 = random.Next(0, eightKWidth - width + 1);
    int y0 = random.Next(0, eightKHeight - height + 1);
    int value = random.Next(0, byte.MaxValue + 1);
    uint actual = SumRect(x0, y0, x0 + width, y0 + height, value);
    uint expected = checked((uint)(width * height * value));
    if (actual != expected)
    {
        throw new InvalidOperationException(
            $"Wrapped prefix mismatch at case {i}: expected={expected}, actual={actual}, " +
            $"rect={x0},{y0},{width},{height}, value={value}.");
    }
}

bool checkedSize = false;
try
{
    ensureIntegralBuffers.Invoke(new MaskedVideoExporter(), [int.MaxValue, 1]);
}
catch (TargetInvocationException ex) when (ex.InnerException is OverflowException)
{
    checkedSize = true;
}
if (!checkedSize)
    throw new InvalidOperationException("Integral buffer size overflow was not rejected.");

bool fullFrame = Array.Exists(
    args,
    value => string.Equals(value, "--full-frame", StringComparison.Ordinal));
if (fullFrame)
    VerifyFullFrame();

Console.WriteLine(
    $"[BgraIntegralRangeVerify] PASS storage=uint32 dci4kFull={dci4kFull} " +
    $"wrapCases={propertyCases} maxWindow={maxWindow} checkedSize={checkedSize.ToString().ToLowerInvariant()} " +
    $"fullFrame={fullFrame.ToString().ToLowerInvariant()}");

uint SumRect(int x0, int y0, int x1, int y1, int value)
{
    uint[] corners =
    [
        Prefix(x1, y1, value),
        Prefix(x1, y0, value),
        Prefix(x0, y1, value),
        Prefix(x0, y0, value)
    ];
    return (uint)(getIntegralSum.Invoke(null, [corners, 0, 1, 2, 3])
        ?? throw new InvalidOperationException("Integral sum was not returned."));
}

static uint Prefix(int x, int y, int value)
    => unchecked((uint)((ulong)x * (ulong)y * (uint)value));

static unsafe void VerifyFullFrame()
{
    const int width = 4096;
    const int height = 2160;
    const int stride = width * 4;
    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();

    using var mask = new WriteableBitmap(
        new PixelSize(width, height),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Premul);
    using (var framebuffer = mask.Lock())
    {
        byte* maskBase = (byte*)framebuffer.Address;
        for (int y = 0; y < height; y++)
            new Span<byte>(maskBase + y * framebuffer.RowBytes, stride).Fill(byte.MaxValue);
    }

    byte* data = (byte*)NativeMemory.Alloc((nuint)(stride * height));
    try
    {
        for (int y = 0; y < height; y++)
        {
            byte* row = data + y * stride;
            for (int x = 0; x < width; x++)
            {
                row[x * 4] = 243;
                row[x * 4 + 1] = 249;
                row[x * 4 + 2] = 255;
                row[x * 4 + 3] = 255;
            }
        }

        AVFrame frame = default;
        frame.width = width;
        frame.height = height;
        frame.format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        frame.data[0] = data;
        frame.linesize[0] = stride;
        if (!new MaskedVideoExporter().ApplyMaskAndBlur(&frame, mask, 40))
            throw new InvalidOperationException("DCI 4K BGRA blur was not applied.");

        var samples = new (int X, int Y)[]
        {
            (0, 0),
            (width - 1, 0),
            (width / 2, height / 2),
            (0, height - 1),
            (width - 1, height - 1)
        };
        foreach (var sample in samples)
        {
            byte* pixel = data + sample.Y * stride + sample.X * 4;
            if (pixel[0] != 243 || pixel[1] != 249 || pixel[2] != 255 || pixel[3] != 255)
            {
                throw new InvalidOperationException(
                    $"DCI 4K uniform color changed at {sample.X},{sample.Y}: " +
                    $"{pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}.");
            }
        }
    }
    finally
    {
        NativeMemory.Free(data);
    }
}
'@ | Set-Content -Encoding UTF8 -Path $program

    $runArguments = @("run", "--project", $project, "--configuration", "Release", "--nologo")
    if ($RunFullFrame) {
        $runArguments += @("--", "--full-frame")
    }
    & dotnet @runArguments
    if ($LASTEXITCODE -ne 0) {
        throw "BGRA integral range harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $work
}
