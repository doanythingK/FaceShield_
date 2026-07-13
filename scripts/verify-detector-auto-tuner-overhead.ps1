param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\detector-auto-tuner-overhead"
$project = Join-Path $work "DetectorAutoTunerOverheadHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 -Path $project

    @'
using FaceShield.Services.FaceDetection;
using System.Reflection;

var method = typeof(DetectorAutoTuner).GetMethod(
    "BuildPerformanceProbePlan",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("BuildPerformanceProbePlan was not found.");

var cases = new (int Samples, bool Gpu, int Warmup, int Iterations)[]
{
    (3, false, 1, 2),
    (3, true, 1, 3),
    (1, false, 1, 2),
    (0, false, 0, 2)
};
foreach (var test in cases)
{
    object plan = method.Invoke(null, [test.Samples, test.Gpu])
        ?? throw new InvalidOperationException("Performance probe plan was not returned.");
    Type type = plan.GetType();
    int warmup = (int)(type.GetProperty("WarmupSampleCount")?.GetValue(plan)
        ?? throw new InvalidOperationException("WarmupSampleCount was not found."));
    int iterations = (int)(type.GetProperty("Iterations")?.GetValue(plan)
        ?? throw new InvalidOperationException("Iterations was not found."));
    if (warmup != test.Warmup || iterations != test.Iterations)
    {
        throw new InvalidOperationException(
            $"samples={test.Samples} gpu={test.Gpu}: " +
            $"expected warmup={test.Warmup} iterations={test.Iterations}, " +
            $"actual warmup={warmup} iterations={iterations}.");
    }
}

Console.WriteLine($"[DetectorAutoTunerOverheadVerify] PASS plans={cases.Length}");
'@ | Set-Content -Encoding UTF8 -Path $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $source = Get-Content -Raw -Path (Join-Path $repo "Services\FaceDetection\DetectorAutoTuner.cs")
    $promoteMatch = [regex]::Match(
        $source,
        'private\s+static\s+unsafe\s+void\s+TryPromoteQualitySample\([\s\S]*?private\s+static\s+IReadOnlyList<FaceDetectionResult>\?\s+DetectOnce\(')
    if (-not $promoteMatch.Success) {
        throw "Could not isolate TryPromoteQualitySample."
    }
    $promoteSource = $promoteMatch.Value
    if ([regex]::Matches($promoteSource, 'new\s+FaceOnnxDetector\(cpuOptions\)').Count -ne 1 -or
        [regex]::Matches($promoteSource, 'DetectOnce\(\s*qualityDetector,').Count -ne 2) {
        throw "Quality sample promotion does not reuse one CPU detector session."
    }

    if ($source -notmatch 'probePlan\.WarmupSampleCount[\s\S]*DetectSamples\([\s\S]*samples\.Count[\s\S]*probePlan\.Iterations') {
        throw "Performance warmup and timed sample limits are not connected to the probe plan."
    }

    Write-Host "[DetectorAutoTunerOverheadVerify] PASS qualitySessionReuse=True warmupSampleLimit=True"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
}
