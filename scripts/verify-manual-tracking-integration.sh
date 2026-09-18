#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

# The repository's macOS dylibs reference a Homebrew 8.0.1 Cellar path that is
# absent from clean CI. Exercise the production managed decoder against a fresh,
# compatible FFmpeg 8 installation; do NOT claim this validates app packaging.
if [[ "$(uname -s)" != "Darwin" ]] || ! command -v brew >/dev/null 2>&1; then
    echo 'ERROR: macOS with Homebrew is required for this integration test' >&2
    exit 1
fi
ffmpeg_prefix="$(brew --prefix ffmpeg@8 2>/dev/null || true)"
if [[ -z "$ffmpeg_prefix" || ! -x "$ffmpeg_prefix/bin/ffmpeg" ]]; then
    brew install ffmpeg@8
    ffmpeg_prefix="$(brew --prefix ffmpeg@8)"
fi
ffmpeg_cmd="$ffmpeg_prefix/bin/ffmpeg"
ffprobe_cmd="$ffmpeg_prefix/bin/ffprobe"
if [[ ! -x "$ffmpeg_cmd" || ! -x "$ffprobe_cmd" ]]; then
    echo 'ERROR: the FFmpeg 8 command-line tools were not installed' >&2
    exit 1
fi

echo "Synthetic integration runtime: $ffmpeg_prefix (FFmpeg 8; NOT bundled dylib validation)"

# Deterministic textured still image: 750 stationary frames at 30 fps,
# followed by 30 black frames. No private/user footage is committed.
python3 - "$test_dir/pattern.ppm" <<'PY'
import sys
width, height = 320, 180
with open(sys.argv[1], 'wb') as output:
    output.write(f'P6\n{width} {height}\n255\n'.encode('ascii'))
    for y in range(height):
        line = bytearray()
        for x in range(width):
            noise = ((x * 73856093) ^ (y * 19349663) ^ ((x * y + 1) * 83492791)) & 255
            line.extend(((noise + 3*x + 5*y) & 255,
                         (noise*3 + 7*x + y) & 255,
                         (noise*7 + x + 11*y) & 255))
        output.write(line)
PY

video="$test_dir/scene-cut-750.mkv"
"$ffmpeg_cmd" -hide_banner -loglevel error -y \
    -loop 1 -framerate 30 -t 25 -i "$test_dir/pattern.ppm" \
    -f lavfi -i 'color=c=black:s=320x180:r=30:d=1' \
    -filter_complex '[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]concat=n=2:v=1:a=0[v]' \
    -map '[v]' -frames:v 780 -c:v ffv1 -pix_fmt yuv420p "$video"

frame_count="$("$ffprobe_cmd" -v error -count_frames -select_streams v:0 \
    -show_entries stream=nb_read_frames -of default=noprint_wrappers=1:nokey=1 "$video")"
if [[ "$frame_count" != '780' ]]; then
    echo "ERROR: expected 780 decoded fixture frames, got $frame_count" >&2
    exit 1
fi

# A temporary project avoids adding test entry points to the Avalonia app.
# Reference the real FaceShield assembly so TrackForward, FFmpeg, and mask
# bitmap creation run together rather than testing only ManualImagePyramid.
cp "$repo_root/scripts/manual-tracking-integration.cs.txt" "$test_dir/Program.cs"
cat > "$test_dir/ManualTrackingIntegration.csproj" <<XML
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

dotnet restore "$test_dir/ManualTrackingIntegration.csproj" -r osx-arm64
dotnet build "$test_dir/ManualTrackingIntegration.csproj" -c Release -r osx-arm64 --no-restore
output_dir="$test_dir/bin/Release/net8.0/osx-arm64"
# The project's stale dylibs must not shadow this isolated, installed FFmpeg 8.
# Nothing in the repository's FFmpeg/ folder is edited or replaced.
rm -f "$output_dir"/libav*.dylib "$output_dir"/libsw*.dylib
cp -L "$ffmpeg_prefix/lib/"libav*.dylib "$ffmpeg_prefix/lib/"libsw*.dylib "$output_dir/"
dotnet "$output_dir/ManualTrackingIntegration.dll" "$video"

# Independent ground-truth target: a textured 56x56 square translates exactly
# (+1,+1) pixels in each successive frame. No learned face detector is involved.
python3 - "$test_dir" <<'PY'
import math
import os
import sys
root = sys.argv[1]
width, height = 160, 120
for frame in range(16):
    path = os.path.join(root, f'moving-{frame:03d}.ppm')
    with open(path, 'wb') as output:
        output.write(f'P6\n{width} {height}\n255\n'.encode('ascii'))
        for y in range(height):
            row = bytearray()
            for x in range(width):
                sx = x - 40 - frame
                sy = y - 28 - frame
                if 0 <= sx < 56 and 0 <= sy < 56:
                    value = int(125 + 45*math.sin(sx*.19) +
                                35*math.cos(sy*.21) +
                                30*math.sin((sx+sy)*.14))
                    value = max(35, min(220, value))
                else:
                    value = 28
                row.extend((value, value, value))
            output.write(row)
PY

moving_video="$test_dir/moving-target.mkv"
"$ffmpeg_cmd" -hide_banner -loglevel error -y -framerate 30 \
    -start_number 0 -i "$test_dir/moving-%03d.ppm" \
    -frames:v 16 -c:v ffv1 -pix_fmt yuv444p "$moving_video"
move_frames="$("$ffprobe_cmd" -v error -count_frames -select_streams v:0 \
    -show_entries stream=nb_read_frames -of default=noprint_wrappers=1:nokey=1 "$moving_video")"
if [[ "$move_frames" != '16' ]]; then
    echo "ERROR: expected 16 moving fixture frames, got $move_frames" >&2
    exit 1
fi
cp "$repo_root/scripts/manual-moving-tracking-integration.cs.txt" "$test_dir/Program.cs"
dotnet build "$test_dir/ManualTrackingIntegration.csproj" -c Release -r osx-arm64 --no-restore
rm -f "$output_dir"/libav*.dylib "$output_dir"/libsw*.dylib
cp -L "$ffmpeg_prefix/lib/"libav*.dylib "$ffmpeg_prefix/lib/"libsw*.dylib "$output_dir/"
dotnet "$output_dir/ManualTrackingIntegration.dll" "$moving_video"
