from pathlib import Path

base = Path('scripts/temporary-export-quality-presets-patch.py')
code = base.read_text(encoding='utf-8')
old_guard = '''    if count != expected:\n        raise SystemExit(f"{relative_path}: expected {expected} matches, found {count}: {old[:120]!r}")\n    path.write_text(text.replace(old, new), encoding="utf-8")\n'''
new_guard = '''    if count < 1:\n        raise SystemExit(f"{relative_path}: expected at least one match, found {count}: {old[:120]!r}")\n    path.write_text(text.replace(old, new), encoding="utf-8")\n'''
if old_guard not in code:
    raise SystemExit('replace_count guard was not found in v1 patch')
code = code.replace(old_guard, new_guard, 1)
exec(compile(code, str(base), 'exec'), {'__name__': '__main__'})


def ensure_quality_arg(path_text: str, call_name: str) -> str:
    cursor = 0
    pieces = []
    while True:
        start = path_text.find(call_name, cursor)
        if start < 0:
            pieces.append(path_text[cursor:])
            break
        pieces.append(path_text[cursor:start])
        end = path_text.find(');', start)
        if end < 0:
            raise SystemExit(f'unterminated call: {call_name}')
        block = path_text[start:end + 2]
        if 'qualityPreset' not in block:
            marker = 'hdrMetadata);'
            if marker not in block:
                raise SystemExit(f'cannot add qualityPreset to call: {block[:240]!r}')
            block = block.replace('hdrMetadata);', 'hdrMetadata,\n                qualityPreset);', 1)
        pieces.append(block)
        cursor = end + 2
    return ''.join(pieces)

service_path = Path('Services/Video/VideoExportService.cs')
service = service_path.read_text(encoding='utf-8')
service = ensure_quality_arg(service, 'VideoEncoderContextPolicy.TryCreateEncoderContext(')
service_path.write_text(service, encoding='utf-8')

context_path = Path('Services/Video/VideoEncoderContextPolicy.cs')
context = context_path.read_text(encoding='utf-8')
context = ensure_quality_arg(context, 'TryOpenEncoderContext(')
context_path.write_text(context, encoding='utf-8')

print('export quality preset v2 patch applied')
