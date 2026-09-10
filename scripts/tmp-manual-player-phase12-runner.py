from pathlib import Path

script_path = Path('scripts/tmp-manual-player-phase12.py')
source = script_path.read_text(encoding='utf-8')
old_helpers = '''def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)
'''
new_helpers = '''_newline_styles = {}


def read(path):
    data = (ROOT / path).read_bytes()
    _newline_styles[path] = '\\r\\n' if b'\\r\\n' in data else '\\n'
    return data.decode('utf-8').replace('\\r\\n', '\\n')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    newline = _newline_styles.get(path, '\\n')
    if newline == '\\r\\n':
        text = text.replace('\\n', '\\r\\n')
    p.write_bytes(text.encode('utf-8'))


def replace_once(text, old, new, label):
    count = text.count(old)
    if count < 1:
        raise RuntimeError(f'{label}: expected at least 1 occurrence, found {count}')
    return text.replace(old, new, 1)
'''
if source.count(old_helpers) != 1:
    raise RuntimeError('patch helper definitions not found exactly once')
source = source.replace(old_helpers, new_helpers, 1)
exec(compile(source, str(script_path), 'exec'), {'__name__': '__main__'})
