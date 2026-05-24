param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-startup-smoke-state"
$project = Join-Path $work "YoloStartupSmokeHarness.csproj"
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
using System.IO;
using Avalonia;
using FaceShield.Enums.Workspace;
using FaceShield.Models;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels;
using FaceShield.ViewModels.Pages;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertPathEndsWith(string? path, string expectedSuffix, string name)
{
    Assert(!string.IsNullOrWhiteSpace(path), $"{name} is empty.");
    string normalized = path!.Replace('\\', '/');
    Assert(normalized.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase), $"{name} unexpected path: {path}");
    Assert(File.Exists(path), $"{name} does not exist: {path}");
}

var smoke = AppStartupOptions.Parse(new[] { "--yolo-smoke", "--open-manual" });
Assert(smoke.DetectorBackend == FaceDetectorBackend.YoloFaceOnnx, "Smoke preset must select YOLO backend.");
Assert(smoke.YoloModelType == YoloFaceModelType.Yolo5Face, "Smoke preset must select YOLO5Face.");
Assert(smoke.OpenMode == WorkspaceMode.Manual, "Smoke preset manual command must request manual open.");
AssertPathEndsWith(smoke.VideoPath, "srcTest/260102_jp_10.mp4", "smoke video");
AssertPathEndsWith(smoke.YoloModelPath, ".tmp/models/YoloV5Face.onnx", "smoke YOLO model");

var explicitOptions = AppStartupOptions.Parse(new[]
{
    "--video", "srcTest/260102_jp_10.mp4",
    "--detector", "yolo",
    "--yolo-model-type", "yolov8",
    "--yolo-model", ".tmp/models/yolov8n-face-lindevs.onnx",
    "--open-auto",
    "--no-auto-export",
    "--frame", "12"
});
Assert(explicitOptions.DetectorBackend == FaceDetectorBackend.YoloFaceOnnx, "Explicit detector must select YOLO backend.");
Assert(explicitOptions.YoloModelType == YoloFaceModelType.YoloV8Face, "Explicit model type must select YOLOv8.");
Assert(explicitOptions.OpenMode == WorkspaceMode.Auto, "Explicit command must request auto open.");
Assert(explicitOptions.AutoExportAfter == false, "Explicit command must disable auto export.");
Assert(explicitOptions.FrameIndex == 12, "Explicit command must apply startup frame index.");
AssertPathEndsWith(explicitOptions.VideoPath, "srcTest/260102_jp_10.mp4", "explicit video");
AssertPathEndsWith(explicitOptions.YoloModelPath, ".tmp/models/yolov8n-face-lindevs.onnx", "explicit YOLO model");

AppBuilder.Configure<FaceShield.App>()
    .UsePlatformDetect()
    .WithInterFont()
    .SetupWithoutStarting();

var stateStore = new WorkspaceStateStore();
var home = new HomePageViewModel(_ => { }, () => { }, stateStore);
home.ApplyStartupOptions(smoke);
Assert(home.SelectedVideoPath == smoke.VideoPath, "Home startup video path was not applied.");
Assert(home.SelectedAutoDetectorBackendOption?.Backend == FaceDetectorBackend.YoloFaceOnnx, "Home startup backend was not applied.");
Assert(home.SelectedYoloModelTypeOption?.ModelType == YoloFaceModelType.Yolo5Face, "Home startup YOLO model type was not applied.");
Assert(home.AutoYoloModelPath == smoke.YoloModelPath, "Home startup YOLO model path was not applied.");
Assert(home.IsYoloDetectorSelected, "Home startup state must expose YOLO as selected.");
Assert(home.CanStartWorkspace, "Home startup state must be ready to start workspace.");

var noExportHome = new HomePageViewModel(_ => { }, () => { }, stateStore);
noExportHome.ApplyStartupOptions(explicitOptions);
Assert(noExportHome.AutoExportAfter == false, "Home startup no-auto-export option was not applied.");

var main = new MainWindowViewModel(new[] { "--yolo-smoke", "--open-manual" });
Assert(main.ShouldOpenStartupWorkspace, "Main window startup options must request workspace open.");
Assert(main.CurrentPage is HomePageViewModel, "Main window should keep Home as current page before posted startup open.");
var startupHome = (HomePageViewModel)main.CurrentPage!;
Assert(startupHome.SelectedVideoPath == smoke.VideoPath, "Main window startup video path was not applied.");
Assert(startupHome.SelectedAutoDetectorBackendOption?.Backend == FaceDetectorBackend.YoloFaceOnnx, "Main window startup backend was not applied.");
Assert(startupHome.SelectedYoloModelTypeOption?.ModelType == YoloFaceModelType.Yolo5Face, "Main window startup YOLO type was not applied.");
Assert(startupHome.AutoYoloModelPath == smoke.YoloModelPath, "Main window startup YOLO model path was not applied.");
Assert(startupHome.CanStartWorkspace, "Main window startup Home must be ready to start workspace.");

Console.WriteLine($"[YoloStartupSmokeVerify] presetBackend={smoke.DetectorBackend}, presetModel={smoke.YoloModelType}, openMode={smoke.OpenMode}, canStart={startupHome.CanStartWorkspace}");
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project
