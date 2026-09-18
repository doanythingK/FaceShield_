#!/usr/bin/env python3
from pathlib import Path

path = Path(__file__).resolve().parent / "verify-manual-tracking-integration.sh"
text = path.read_text(encoding="utf-8")
old = '''        historical_formula="$test_dir/ffmpeg.rb"
        curl -fsSL \\
          'https://raw.githubusercontent.com/Homebrew/homebrew-core/40a61ec69293671eed15d9ff8f1d677120cabcde/Formula/f/ffmpeg.rb' \\
          -o "$historical_formula"
        HOMEBREW_NO_AUTO_UPDATE=1 brew install --formula "$historical_formula"
        ffmpeg_prefix="$(brew --prefix ffmpeg)"
'''
new = '''        export HOMEBREW_NO_INSTALL_FROM_API=1
        HOMEBREW_NO_AUTO_UPDATE=1 brew tap --force homebrew/core
        core_repo="$(brew --repo homebrew/core)"
        git -C "$core_repo" reset --hard HEAD
        git -C "$core_repo" fetch --depth=1 origin 40a61ec69293671eed15d9ff8f1d677120cabcde
        git -C "$core_repo" checkout --detach 40a61ec69293671eed15d9ff8f1d677120cabcde
        HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_FROM_API=1 brew install ffmpeg
        ffmpeg_prefix="$(brew --prefix ffmpeg)"
'''
if text.count(old) != 1:
    raise RuntimeError(f"expected one historical formula install block, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
print("PASS: verification script now pins Homebrew core to FFmpeg 8.1.1 commit")
