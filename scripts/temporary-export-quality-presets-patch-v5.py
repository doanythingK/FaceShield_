from pathlib import Path

exec(compile(
    Path('scripts/temporary-export-quality-presets-patch-v4.py').read_text(encoding='utf-8'),
    'scripts/temporary-export-quality-presets-patch-v4.py',
    'exec'), {'__name__': '__main__'})

path = Path('Services/Video/VideoExportQualityPreset.cs')
text = path.read_text(encoding='utf-8')
old = '''        return VideoExportFidelityPolicy.ClampBitrate(\n            (long)Math.Round(scaled, MidpointRounding.AwayFromZero));\n'''
new = '''        return VideoExportFidelityPolicy.ClampBitrate(\n            (long)System.Math.Round(\n                scaled,\n                System.MidpointRounding.AwayFromZero));\n'''
if text.count(old) != 1:
    raise SystemExit('preset rounding expression not found exactly once')
path.write_text(text.replace(old, new, 1), encoding='utf-8', newline='\n')
print('export quality preset v5 patch applied')
