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
    double[] nextSignature,
    double sceneCutThreshold)
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
        nextSignature,
        sceneCutThreshold
    })!;
}

var dark = new double[24 * 14];
var bright = new double[24 * 14];
Array.Fill(bright, 0.72);

var sameA = new double[24 * 14];
var sameB = new double[24 * 14];
Array.Fill(sameA, 0.35);
Array.Fill(sameB, 0.38);

bool hardCutStops = InvokeShouldStop(10, 15, 10, true, dark, bright, 0.32);
bool sameSceneStops = InvokeShouldStop(10, 15, 10, true, sameA, sameB, 0.32);
bool guardDisabledStops = InvokeShouldStop(10, 15, 10, false, dark, bright, 0.32);
bool farNextStops = InvokeShouldStop(10, 25, 10, true, dark, bright, 0.32);
bool strictThresholdStops = InvokeShouldStop(10, 15, 10, true, dark, bright, 0.80);

if (!hardCutStops)
    throw new InvalidOperationException("Expected YOLO sparse hard-cut carry to stop.");
if (sameSceneStops)
    throw new InvalidOperationException("Expected same-scene sparse carry to continue.");
if (guardDisabledStops)
    throw new InvalidOperationException("Expected disabled sparse scene guard not to stop carry.");
if (farNextStops)
    throw new InvalidOperationException("Expected far next detection outside bridge window not to count as sparse scene cut.");
if (strictThresholdStops)
    throw new InvalidOperationException("Expected a difference below the supplied threshold not to stop carry.");

Console.WriteLine("[AutoMaskSparseSceneCutGuardVerify] hardCutStops=True, sameSceneStops=False, guardDisabledStops=False, farNextStops=False, strictThresholdStops=False");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
