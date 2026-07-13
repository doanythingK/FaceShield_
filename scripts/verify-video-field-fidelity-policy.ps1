param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\video-field-fidelity-policy"
$project = Join-Path $work "VideoFieldFidelityPolicyHarness.csproj"
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
"@ | Set-Content -Encoding UTF8 -Path $project

@'
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Reflection;

MethodInfo isInterlaced = typeof(VideoExportService).GetMethod(
    "IsInterlacedFieldOrder",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("IsInterlacedFieldOrder was not found.");
MethodInfo requiresSoftwareChroma = typeof(VideoExportService).GetMethod(
    "RequiresSoftwareEncoderForChromaLocation",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("RequiresSoftwareEncoderForChromaLocation was not found.");

AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_UNKNOWN, expected: false);
AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_PROGRESSIVE, expected: false);
AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_TT, expected: true);
AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_BB, expected: true);
AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_TB, expected: true);
AssertPolicy(isInterlaced, AVFieldOrder.AV_FIELD_BT, expected: true);

int chromaCases = 0;
foreach (AVChromaLocation location in Enum.GetValues<AVChromaLocation>())
{
    bool expected = location is not
        AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED and not
        AVChromaLocation.AVCHROMA_LOC_LEFT;
    AssertPolicy(requiresSoftwareChroma, location, expected);
    chromaCases++;
}

Console.WriteLine(
    $"[VideoFieldFidelityPolicyVerify] PASS fieldOrders=6 chromaLocations={chromaCases} " +
    "interlacedFailClosed=true nonLeftChromaSoftware=true");

static void AssertPolicy<T>(MethodInfo method, T value, bool expected)
    where T : struct, Enum
{
    bool actual = (bool)(method.Invoke(null, [value])
        ?? throw new InvalidOperationException($"{method.Name} did not return a value."));
    if (actual != expected)
    {
        throw new InvalidOperationException(
            $"{method.Name} mismatch for {value}: expected={expected}, actual={actual}.");
    }
}
'@ | Set-Content -Encoding UTF8 -Path $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
