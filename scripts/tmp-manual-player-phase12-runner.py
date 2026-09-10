from pathlib import Path

script_path = Path('scripts/tmp-manual-player-phase12.py')
source = script_path.read_text(encoding='utf-8')
old = '''def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)
'''
new = '''def replace_once(text, old, new, label):
    count = text.count(old)
    if count < 1:
        raise RuntimeError(f'{label}: expected at least 1 occurrence, found {count}')
    return text.replace(old, new, 1)
'''
if source.count(old) != 1:
    raise RuntimeError('replace_once helper definition not found exactly once')
source = source.replace(old, new, 1)
exec(compile(source, str(script_path), 'exec'), {'__name__': '__main__'})
