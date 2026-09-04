from pathlib import Path


def read_lines(path):
    data = Path(path).read_bytes()
    had_bom = data.startswith(b"\xef\xbb\xbf")
    return data.decode("utf-8-sig").splitlines(keepends=True), had_bom


def write_lines(path, lines, had_bom):
    data = "".join(lines).encode("utf-8")
    if had_bom:
        data = b"\xef\xbb\xbf" + data
    Path(path).write_bytes(data)


def eol(line):
    return "\r\n" if line.endswith("\r\n") else "\n"


def find_one(lines, needle):
    hits = [i for i, line in enumerate(lines) if needle in line]
    if len(hits) != 1:
        raise RuntimeError(f"Expected exactly one {needle!r}, found {len(hits)}")
    return hits[0]


path = "Services/Video/FrameMaskProvider.cs"
lines, bom = read_lines(path)
anchor = find_one(lines, "internal bool TryGetStoredMaskBorrowed(int frameIndex, out WriteableBitmap mask)")
if not any("internal bool HasStoredMask(int frameIndex)" in line for line in lines):
    brace = next(i for i in range(anchor, anchor + 10) if lines[i].strip() == "{")
    close = next(i for i in range(brace + 1, brace + 12) if lines[i].strip() == "}")
    nl = eol(lines[anchor])
    indent = lines[anchor][: len(lines[anchor]) - len(lines[anchor].lstrip())]
    block = [
        nl,
        indent + "internal bool HasStoredMask(int frameIndex)" + nl,
        indent + "{" + nl,
        indent + "    lock (_stateGate)" + nl,
        indent + "        return _masks.ContainsKey(frameIndex);" + nl,
        indent + "}" + nl,
    ]
    lines[close + 1 : close + 1] = block
write_lines(path, lines, bom)

path = "Services/Analysis/YoloRiskCascadeStep.cs"
lines, bom = read_lines(path)
idx = find_one(lines, "maskProvider.TryGetStoredMask(frameIndex, out _)")
lines[idx] = lines[idx].replace(
    "maskProvider.TryGetStoredMask(frameIndex, out _)",
    "maskProvider.HasStoredMask(frameIndex)",
)
write_lines(path, lines, bom)
