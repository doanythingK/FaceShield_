#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

manual_tracking="$repo_root/ViewModels/Workspace/FramePreviewViewModel.ManualTracking.cs"
if grep -q 'RemoveFaceMasksRange' "$manual_tracking"; then
    echo 'ERROR: manual tracking rollback must not delete the combined Auto/manual frame range' >&2
    exit 1
fi

echo 'PASS: manual tracking rollback does not delete Auto mask ranges'

# The editor must never persist an Auto/manual composite as an independent
# manual keyframe. The headless regression below verifies alpha preservation;
# these assertions also guard the actual view-model connection to that path.
editor_view="$repo_root/ViewModels/Workspace/FramePreviewViewModel.cs"
keyframe_view="$repo_root/ViewModels/Workspace/FramePreviewViewModel.ManualKeyframes.cs"
for connection in \
    'ManualMaskEditorLayer.CreateEditableMask(' \
    'ManualMaskEditorLayer.ComposeWithAutomatic(' \
    'manualProvider.SetIndependentManualMask('; do
    if ! grep -Fq "$connection" "$editor_view"; then
        echo "ERROR: manual-only editor connection missing: $connection" >&2
        exit 1
    fi
done
if ! grep -Fq 'ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(' "$keyframe_view"; then
    echo 'ERROR: inherited editable mask must resolve the manual-only layer' >&2
    exit 1
fi
echo 'PASS: editor stores manual-only alpha and composites Auto only for preview'

cp "$repo_root/scripts/manual-overlay-regression.cs.txt" "$test_dir/Program.cs"
cat > "$test_dir/ManualOverlayRegression.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$repo_root/Services/Workspace/WorkspacePathIdentity.cs" Link="WorkspacePathIdentity.cs" />
    <Compile Include="$repo_root/Services/Video/ManualOverlayCore.cs" Link="ManualOverlayCore.cs" />
    <Compile Include="$repo_root/Services/Video/ManualOverlayStateStore.cs" Link="ManualOverlayStateStore.cs" />
    <Compile Include="$repo_root/Services/Video/ManualOverlayWorkspaceStore.cs" Link="ManualOverlayWorkspaceStore.cs" />
    <Compile Include="$repo_root/Services/Video/ManualMaskTrackModels.cs" Link="ManualMaskTrackModels.cs" />
  </ItemGroup>
</Project>
XML

dotnet run --project "$test_dir/ManualOverlayRegression.csproj" -c Release
cp "$repo_root/scripts/manual-overlay-workspace-regression.cs.txt" "$test_dir/Program.cs"
dotnet run --project "$test_dir/ManualOverlayRegression.csproj" -c Release
cp "$repo_root/scripts/manual-track-continuity-regression.cs.txt" "$test_dir/Program.cs"
dotnet run --project "$test_dir/ManualOverlayRegression.csproj" -c Release

# Exercise the production FrameMaskProvider through the built application assembly.
cp "$repo_root/scripts/frame-mask-layer-regression.cs.txt" "$test_dir/Program.cs"
cat > "$test_dir/FrameMaskLayerRegression.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo_root/FaceShield.csproj" />
    <PackageReference Include="Avalonia.Headless" Version="11.3.9" />
  </ItemGroup>
</Project>
XML

dotnet restore "$test_dir/FrameMaskLayerRegression.csproj" -r osx-arm64
dotnet run --project "$test_dir/FrameMaskLayerRegression.csproj" -c Release -r osx-arm64 --no-restore
