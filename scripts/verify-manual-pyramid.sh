#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

# Keep the test project outside the repository so its Program.cs is never
# picked up by FaceShield.csproj's default recursive Compile glob.
cp "$repo_root/Services/Video/ManualMaskVision.cs" "$test_dir/ManualMaskVision.cs"
cp "$repo_root/Services/Video/ManualTranslationEstimator.cs" "$test_dir/ManualTranslationEstimator.cs"
cp "$repo_root/scripts/manual-pyramid-regression.cs.txt" "$test_dir/Program.cs"
cat > "$test_dir/ManualPyramidRegression.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
XML

dotnet run --project "$test_dir/ManualPyramidRegression.csproj" --configuration Release --nologo
cp "$repo_root/scripts/manual-translation-regression.cs.txt" "$test_dir/Program.cs"
dotnet run --project "$test_dir/ManualPyramidRegression.csproj" --configuration Release --nologo
