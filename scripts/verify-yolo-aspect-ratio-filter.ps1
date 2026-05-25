param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-aspect-ratio-filter-verify"
$project = Join-Path $work "YoloAspectRatioFilterVerify.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 -Path $project

@'
using System;
using System.Reflection;
using FaceShield.Services.FaceDetection;

static void AssertEqual(string name, bool expected, bool actual)
{
    if (expected != actual)
        throw new InvalidOperationException($"{name} expected={expected} actual={actual}");
}

var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var method = typeof(YoloFaceOnnxDetector).GetMethod("IsAspectRatioAllowed", flags)
    ?? throw new InvalidOperationException("IsAspectRatioAllowed method was not found.");
var topSmallMethod = typeof(YoloFaceOnnxDetector).GetMethod("IsTopSmallLowConfidenceCandidate", flags)
    ?? throw new InvalidOperationException("IsTopSmallLowConfidenceCandidate method was not found.");

bool Invoke(double width, double height, double min, double max)
    => (bool)(method.Invoke(null, new object[] { width, height, min, max }) ?? false);
bool InvokeTopSmall(double confidence, double centerY, double areaRatio, double maxConfidence, double maxCenterY, double maxAreaRatio)
    => (bool)(topSmallMethod.Invoke(null, new object[] { confidence, centerY, areaRatio, maxConfidence, maxCenterY, maxAreaRatio }) ?? false);

AssertEqual("normal face-ish box", true, Invoke(65.0, 100.0, 0.35, 1.65));
AssertEqual("too tall box", false, Invoke(20.0, 100.0, 0.35, 1.65));
AssertEqual("too wide box", false, Invoke(200.0, 80.0, 0.35, 1.65));
AssertEqual("swapped threshold still allows valid", true, Invoke(65.0, 100.0, 1.65, 0.35));
AssertEqual("disabled threshold allows any ratio", true, Invoke(200.0, 80.0, 0.0, 0.0));
AssertEqual("zero height rejects when enabled", false, Invoke(10.0, 0.0, 0.35, 1.65));

AssertEqual("top small low-confidence candidate drops", true, InvokeTopSmall(0.60, 0.05, 0.0050, 0.65, 0.08, 0.0060));
AssertEqual("top small high-confidence candidate stays", false, InvokeTopSmall(0.80, 0.05, 0.0050, 0.65, 0.08, 0.0060));
AssertEqual("lower small low-confidence candidate stays", false, InvokeTopSmall(0.60, 0.12, 0.0050, 0.65, 0.08, 0.0060));
AssertEqual("top larger low-confidence candidate stays", false, InvokeTopSmall(0.60, 0.05, 0.0100, 0.65, 0.08, 0.0060));

Console.WriteLine("[YoloAspectRatioFilterVerify] all requested checks passed");
'@ | Set-Content -Encoding UTF8 -Path $program

$output = & dotnet run --project $project 2>&1
$exitCode = $LASTEXITCODE
$text = $output | Out-String
Write-Host $text
if ($exitCode -ne 0) {
    throw "YoloAspectRatioFilterVerify failed with exit code $exitCode"
}

$source = Get-Content -Raw -Path (Join-Path $repo "Services\FaceDetection\YoloFaceOnnxDetector.cs")
if ($source -notmatch "ApplyAspectRatioFilter[\s\S]*IsAspectRatioAllowed") {
    throw "ApplyAspectRatioFilter does not call IsAspectRatioAllowed"
}
if ($source -notmatch "ApplyTopSmallLowConfidenceFilter[\s\S]*IsTopSmallLowConfidenceCandidate") {
    throw "ApplyTopSmallLowConfidenceFilter does not call IsTopSmallLowConfidenceCandidate"
}
