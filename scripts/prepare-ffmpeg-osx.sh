#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="$root_dir/FFmpeg/osx-arm64"

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is required. Install it first: https://brew.sh/"
  exit 1
fi

ffmpeg_prefix="$(brew --prefix ffmpeg 2>/dev/null || true)"
if [[ -z "$ffmpeg_prefix" ]]; then
  echo "ffmpeg is not installed. Run: brew install ffmpeg"
  exit 1
fi

lib_dir="$ffmpeg_prefix/lib"
if [[ ! -d "$lib_dir" ]]; then
  echo "ffmpeg lib directory not found: $lib_dir"
  exit 1
fi

mkdir -p "$out_dir"

rm -f "$out_dir"/libav*.dylib "$out_dir"/libsw*.dylib

for lib_name in libavcodec libavformat libavutil libswscale libswresample libavfilter; do
  if ls "$lib_dir/$lib_name"*.dylib >/dev/null 2>&1; then
    cp -a "$lib_dir/$lib_name"*.dylib "$out_dir/"
  fi
done

echo "FFmpeg dylibs copied to $out_dir"
