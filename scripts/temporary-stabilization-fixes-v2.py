from pathlib import Path

source_path = Path('scripts/temporary-stabilization-fixes.py')
source = source_path.read_text(encoding='utf-8')
start = source.index('def replace_once(')
end = source.index('\n\n# 1)', start)
helpers = r'''
def _variants(marker):
    normalized = marker.replace('\r\n', '\n')
    values = [normalized, normalized.replace('\n', '\r\n')]
    result = []
    for value in values:
        if value not in result:
            result.append(value)
    return result


def _find_unique(text, marker, label, start=0):
    found = []
    for variant in _variants(marker):
        offset = start
        while True:
            index = text.find(variant, offset)
            if index < 0:
                break
            found.append((index, variant))
            offset = index + max(1, len(variant))
    unique = {}
    for index, variant in found:
        unique[index] = variant
    if len(unique) != 1:
        raise SystemExit(f'{label}: expected one marker match, found {len(unique)}')
    index = next(iter(unique))
    return index, unique[index]


def replace_once(path, old, new, label):
    text = read_text(path)
    index, matched = _find_unique(text, old, label)
    nl = '\r\n' if '\r\n' in matched else '\n'
    replacement = adapt(new, nl)
    write_text(path, text[:index] + replacement + text[index + len(matched):])


def replace_between(path, start_marker, end_marker, replacement, label):
    text = read_text(path)
    start, matched_start = _find_unique(text, start_marker, label + '-start')
    end, matched_end = _find_unique(text, end_marker, label + '-end', start + len(matched_start))
    segment = text[start:end]
    nl = '\r\n' if segment.count('\r\n') >= max(1, segment.count('\n') // 2) else '\n'
    write_text(path, text[:start] + adapt(replacement, nl) + text[end:])
'''
source = source[:start] + helpers + source[end:]
exec(compile(source, str(source_path), 'exec'))
