from pathlib import Path
p = Path('scripts/tmp-fix-workspace-path-identity.py')
text = p.read_text(encoding='utf-8')
old = '''if "FilePathComparison" in home or "FilePathComparer" in home:\n    raise RuntimeError("home still contains duplicate path identity policy")\nhome_path.write_text(home, encoding="utf-8")'''
new = '''for i, line in enumerate(home.splitlines(), start=1):\n    if "FilePathComparison" in line or "FilePathComparer" in line:\n        print(f"[HOME-REMAINDER] {i}: {line}")\nhome_path.write_text(home, encoding="utf-8")'''
if old not in text:
    raise RuntimeError('diagnostic target not found')
p.write_text(text.replace(old, new, 1), encoding='utf-8')
