from pathlib import Path

for path in (
    Path('Views/Pages/WorkspaceView.axaml'),
    Path('Views/Workspace/FramePreviewView.axaml'),
):
    data = path.read_bytes()
    old = b'Panel.ZIndex="'
    count = data.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one Panel.ZIndex, found {count}')
    path.write_bytes(data.replace(old, b'ZIndex="', 1))

print('manual player Avalonia ZIndex syntax fixed')
