#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app_path="${1:-}"
log_path="$script_dir/macos-run.log"

exec > >(tee -a "$log_path") 2>&1

if [[ -z "$app_path" ]]; then
  apps=()
  while IFS= read -r app; do
    apps+=("$app")
  done < <(find "$script_dir" -maxdepth 1 -name "*.app" -print)
  if (( ${#apps[@]} == 0 )); then
    echo "No .app found in: $script_dir"
    echo "Usage: $(basename "$0") /path/to/App.app"
    read -r -p "Press Enter to close..."
    exit 1
  elif (( ${#apps[@]} > 1 )); then
    echo "Multiple .app found. Pass the app path explicitly."
    printf '  %s\n' "${apps[@]}"
    read -r -p "Press Enter to close..."
    exit 1
  fi
  app_path="${apps[0]}"
fi

if [[ ! -d "$app_path" ]]; then
  echo "App not found: $app_path"
  read -r -p "Press Enter to close..."
  exit 1
fi

plist="$app_path/Contents/Info.plist"
if [[ ! -f "$plist" ]]; then
  echo "Info.plist not found: $plist"
  read -r -p "Press Enter to close..."
  exit 1
fi

exe_name="$(/usr/libexec/PlistBuddy -c 'Print CFBundleExecutable' "$plist" 2>/dev/null || true)"
if [[ -z "$exe_name" ]]; then
  echo "CFBundleExecutable not found in: $plist"
  read -r -p "Press Enter to close..."
  exit 1
fi

exe_path="$app_path/Contents/MacOS/$exe_name"
if [[ ! -x "$exe_path" ]]; then
  echo "Executable not found: $exe_path"
  read -r -p "Press Enter to close..."
  exit 1
fi

echo "Launching: $exe_path"
echo "Log: $log_path"
echo ""

set +e
"$exe_path"
exit_code=$?
set -e

echo ""
echo "Exit code: $exit_code"
read -r -p "Press Enter to close..."
