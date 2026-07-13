param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\export-output-container"
$project = Join-Path $work "ExportOutputContainerHarness.csproj"
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
using FaceShield.ViewModels.Pages;
using System;
using System.Reflection;

MethodInfo method = typeof(WorkspaceViewModel).GetMethod(
    "BuildDefaultExportPath",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("BuildDefaultExportPath not found.");

var cases = new (string Input, string Expected)[]
{
    (@"C:\clips\sample.mp4", @"C:\clips\sample_blur.mp4"),
    (@"C:\clips\sample.mov", @"C:\clips\sample_blur.mov"),
    (@"C:\clips\sample.mkv", @"C:\clips\sample_blur.mkv"),
    (@"C:\clips\sample.avi", @"C:\clips\sample_blur.avi"),
    (@"C:\clips\sample.wmv", @"C:\clips\sample_blur.wmv"),
    (@"C:\clips\sample.webm", @"C:\clips\sample_blur.webm"),
    (@"C:\clips\sample.MKV", @"C:\clips\sample_blur.MKV"),
    (@"C:\clips\sample.m4v", @"C:\clips\sample_blur.mp4")
};

foreach ((string input, string expected) in cases)
{
    string actual = (string?)method.Invoke(null, new object[] { input })
        ?? throw new InvalidOperationException($"No output for {input}.");
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"input={input}, expected={expected}, actual={actual}");
}

Console.WriteLine($"[ExportOutputContainerVerify] PASS cases={cases.Length}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
