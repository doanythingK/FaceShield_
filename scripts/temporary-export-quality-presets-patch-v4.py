from pathlib import Path

v3 = Path('scripts/temporary-export-quality-presets-patch-v3.py')
code = v3.read_text(encoding='utf-8')
needle = ".replace('\\n', '\\r\\n').encode('utf-8')"
count = code.count(needle)
if count != 2:
    raise SystemExit(f'expected two XAML CRLF conversion markers, found {count}')
code = code.replace(needle, ".encode('utf-8')")
exec(compile(code, str(v3), 'exec'), {'__name__': '__main__'})
print('export quality preset v4 patch applied')
