param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\export-progress-completion"
$project = Join-Path $work "ExportProgressCompletionHarness.csproj"
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
using System;

AssertProgress(new ExportProgress(0, 300), 0, complete: false);
AssertProgress(new ExportProgress(150, 300), 50, complete: false);
AssertProgress(new ExportProgress(299, 300), 99, complete: false);
AssertProgress(new ExportProgress(300, 300), 99, complete: false);
AssertProgress(new ExportProgress(450, 300), 99, complete: false);

ExportProgress completed = ExportProgress.Completed(300, "done");
AssertProgress(completed, 100, complete: true);
if (completed.FrameIndex != 300 || completed.TotalFrames != 300 || completed.StatusMessage != "done")
    throw new InvalidOperationException("Completed progress did not preserve its frame count or status.");

ExportProgress emptyCompleted = ExportProgress.Completed(0);
AssertProgress(emptyCompleted, 100, complete: true);
if (emptyCompleted.FrameIndex != 1 || emptyCompleted.TotalFrames != 1)
    throw new InvalidOperationException("Completed progress did not normalize an unknown frame count.");

Console.WriteLine(
    "[ExportProgressCompletionVerify] PASS processingMax=99 committed=100 unknownTotal=100");

static void AssertProgress(ExportProgress progress, int expectedPercent, bool complete)
{
    if (progress.Percent != expectedPercent || progress.IsComplete != complete)
    {
        throw new InvalidOperationException(
            $"Progress mismatch: percent={progress.Percent}, complete={progress.IsComplete}, " +
            $"expectedPercent={expectedPercent}, expectedComplete={complete}.");
    }
}
'@ | Set-Content -Encoding UTF8 -Path $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
