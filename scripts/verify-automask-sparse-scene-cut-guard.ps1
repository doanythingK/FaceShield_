param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\automask-sparse-scene-cut-guard"
$project = Join-Path $work "AutoMaskSparseSceneCutGuardHarness.csproj"
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
"@ | Set-Content -Encoding UTF8 $project

@'
using System;
using System.Reflection;
using FaceShield.Services.Analysis;

static bool InvokeShouldStop(
    int currentKey,
    int nextKey,
    int maxBridgeFrames,
    bool guardSceneCuts,
    double[] currentSignature,
    double[] nextSignature)
{
    var method = typeof(AutoMaskGenerator).GetMethod(
        "ShouldStopSparseSceneCarry",
        BindingFlags.NonPublic | BindingFlags.Static);
    if (method == null)
        throw new InvalidOperationException("ShouldStopSparseSceneCarry was not found.");

    return (bool)method.Invoke(null, new object[]
    {
        currentKey,
        nextKey,
        maxBridgeFrames,
        guardSceneCuts,
        currentSignature,
        nextSignature
    })!;
}

var dark = new double[24 * 14];
var bright = new double[24 * 14];
Array.Fill(bright, 0.72);

var sameA = new double[24 * 14];
var sameB = new double[24 * 14];
Array.Fill(sameA, 0.35);
Array.Fill(sameB, 0.38);

bool hardCutStops = InvokeShouldStop(10, 15, 10, true, dark, bright);
bool sameSceneStops = InvokeShouldStop(10, 15, 10, true, sameA, sameB);
bool faceOnnxStops = InvokeShouldStop(10, 15, 10, false, dark, bright);
bool farNextStops = InvokeShouldStop(10, 25, 10, true, dark, bright);

if (!hardCutStops)
    throw new InvalidOperationException("Expected YOLO sparse hard-cut carry to stop.");
if (sameSceneStops)
    throw new InvalidOperationException("Expected same-scene sparse carry to continue.");
if (faceOnnxStops)
    throw new InvalidOperationException("Expected non-YOLO sparse carry guard to stay disabled.");
if (farNextStops)
    throw new InvalidOperationException("Expected far next detection outside bridge window not to count as sparse scene cut.");

Console.WriteLine("[AutoMaskSparseSceneCutGuardVerify] hardCutStops=True, sameSceneStops=False, nonYoloStops=False, farNextStops=False");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
