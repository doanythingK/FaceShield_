#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

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
  </ItemGroup>
</Project>
XML

dotnet run --project "$test_dir/ManualOverlayRegression.csproj" -c Release
