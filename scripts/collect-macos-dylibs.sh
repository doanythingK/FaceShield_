#!/usr/bin/env bash
set -euo pipefail

app_path="${1:-}"
if [[ -z "$app_path" || ! -d "$app_path" ]]; then
  echo "Usage: $0 /path/to/FaceShield.app"
  exit 1
fi

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$(cd "$(dirname "$app_path")" && pwd)"
frameworks_dir="$app_path/Contents/Frameworks"
macos_dir="$app_path/Contents/MacOS"

mkdir -p "$frameworks_dir"

copy_if_exists() {
  local src="$1"
  if [[ -f "$src" ]]; then
    cp -aL "$src" "$frameworks_dir/"
    local dest="$frameworks_dir/$(basename "$src")"
    if [[ -f "$dest" ]]; then
      chmod u+w "$dest" || true
    fi
  fi
}

# Copy FFmpeg dylibs tracked in the repo (if present).
if [[ -d "$root_dir/FFmpeg/osx-arm64" ]]; then
  for lib in "$root_dir/FFmpeg/osx-arm64"/*.dylib; do
    [[ -f "$lib" ]] || continue
    copy_if_exists "$lib"
  done
fi

# Copy ONNX Runtime dylibs from publish output (if present).
while IFS= read -r -d '' lib; do
  copy_if_exists "$lib"
done < <(find "$publish_dir" -maxdepth 1 -name "*onnxruntime*.dylib" -print0 2>/dev/null || true)

add_rpath() {
  local file="$1"
  install_name_tool -add_rpath "@loader_path/../Frameworks" "$file" 2>/dev/null || true
}

should_copy_dep() {
  case "$1" in
    /opt/homebrew/*|/usr/local/*|/Library/Frameworks/*) return 0 ;;
    *) return 1 ;;
  esac
}

deps_for() {
  otool -L "$1" | tail -n +2 | awk '{print $1}'
}

resolve_dep_source() {
  local dep="$1"
  local name="$2"
  if [[ -f "$dep" ]]; then
    echo "$dep"
    return 0
  fi
  local candidate
  candidate="$(find "$publish_dir" -name "$name" -print -quit 2>/dev/null || true)"
  if [[ -n "$candidate" && -f "$candidate" ]]; then
    echo "$candidate"
    return 0
  fi
  local nuget_root="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
  if [[ -d "$nuget_root" ]]; then
    candidate="$(find "$nuget_root" -path "*/runtimes/osx-arm64/native/$name" -print -quit 2>/dev/null || true)"
    if [[ -n "$candidate" && -f "$candidate" ]]; then
      echo "$candidate"
      return 0
    fi
    candidate="$(find "$nuget_root" -path "*/runtimes/osx/native/$name" -print -quit 2>/dev/null || true)"
    if [[ -n "$candidate" && -f "$candidate" ]]; then
      echo "$candidate"
      return 0
    fi
  fi
  return 1
}

seen_files=()
queue=()
missing_deps=0

seen_contains() {
  local f="$1"
  local s
  for s in "${seen_files[@]-}"; do
    if [[ "$s" == "$f" ]]; then
      return 0
    fi
  done
  return 1
}

add_file() {
  local f="$1"
  [[ -f "$f" ]] || return
  if ! seen_contains "$f"; then
    seen_files+=("$f")
    queue+=("$f")
  fi
}

# Seed scan with executables and any pre-copied dylibs.
if [[ -d "$macos_dir" ]]; then
  while IFS= read -r -d '' f; do
    add_file "$f"
  done < <(find "$macos_dir" -maxdepth 1 -type f -print0)
fi

if [[ -d "$frameworks_dir" ]]; then
  while IFS= read -r -d '' f; do
    add_file "$f"
  done < <(find "$frameworks_dir" -maxdepth 1 -type f -name "*.dylib" -print0)
fi

while ((${#queue[@]})); do
  file="${queue[0]}"
  queue=("${queue[@]:1}")

  add_rpath "$file"

  while IFS= read -r dep; do
    [[ -n "$dep" ]] || continue
    if should_copy_dep "$dep"; then
      name="$(basename "$dep")"
      dest="$frameworks_dir/$name"
      src="$(resolve_dep_source "$dep" "$name" || true)"
      if [[ -z "$src" ]]; then
        echo "Missing dependency on runner: $dep" >&2
        missing_deps=1
        continue
      fi
      if [[ ! -f "$dest" ]]; then
        cp -aL "$src" "$dest"
        if [[ -f "$dest" ]]; then
          chmod u+w "$dest" || true
        fi
        install_name_tool -id "@rpath/$name" "$dest" 2>/dev/null || true
        add_file "$dest"
      fi

      install_name_tool -change "$dep" "@rpath/$name" "$file" 2>/dev/null || true
    fi
  done < <(deps_for "$file")
done

if [[ "$missing_deps" -ne 0 ]]; then
  echo "Failed due to missing dylib dependencies on the runner." >&2
  exit 1
fi

echo "Collected dylibs into $frameworks_dir"
