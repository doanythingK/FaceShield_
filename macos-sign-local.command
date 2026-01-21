#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app_path="${1:-}"
log_path="$script_dir/macos-sign-local.log"

exec > >(tee -a "$log_path") 2>&1

on_error() {
  echo ""
  echo "Signing failed. See log: $log_path"
  read -r -p "Press Enter to close..."
}
trap on_error ERR

if [[ -z "$app_path" ]]; then
  mapfile -t apps < <(find "$script_dir" -maxdepth 1 -name "*.app" -print)
  if (( ${#apps[@]} == 0 )); then
    echo "No .app found in: $script_dir"
    echo "Usage: $(basename "$0") /path/to/App.app"
    exit 1
  elif (( ${#apps[@]} > 1 )); then
    echo "Multiple .app found. Pass the app path explicitly."
    printf '  %s\n' "${apps[@]}"
    exit 1
  fi
  app_path="${apps[0]}"
fi

if [[ ! -d "$app_path" ]]; then
  echo "App not found: $app_path"
  exit 1
fi

xattr -dr com.apple.quarantine "$app_path"
codesign --force --deep --sign - "$app_path"
echo "Signed: $app_path"
echo "Log saved: $log_path"
read -r -p "Press Enter to close..."
