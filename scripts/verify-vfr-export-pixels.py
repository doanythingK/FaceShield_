"""Generate synthetic marker/texture frames and verify real encoded pixels and PTS."""
import json
import pathlib
import statistics
import subprocess
import sys

WIDTH, HEIGHT = 160, 120
FRAME_BYTES = WIDTH * HEIGHT * 3
PTS = [0.0, 0.1, 1.0, 1.1]

def generate(root):
    for i, marker in enumerate((24, 80, 152, 224)):
        data = bytearray()
        for y in range(HEIGHT):
            for x in range(WIDTH):
                value = marker if x < 16 and y < 16 else (30 if (x + y) % 2 == 0 else 226)
                data.extend((value, value, value))
        (root / f"vfr-{i:03}.ppm").write_bytes(
            f"P6\n{WIDTH} {HEIGHT}\n255\n".encode("ascii") + data)

def raw(tool, path):
    result = subprocess.check_output([tool, "-v", "error", "-i", str(path),
        "-map", "0:v:0", "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
    assert len(result) == 4 * FRAME_BYTES, f"{path}: incorrect decoded frame count"
    return [result[i:i + FRAME_BYTES] for i in range(0, len(result), FRAME_BYTES)]

def pts(tool, path):
    output = subprocess.check_output([tool, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", str(path)])
    return [float(f["best_effort_timestamp_time"]) for f in json.loads(output)["frames"]]

def region(frame, x0, y0, x1, y1):
    return [frame[(y * WIDTH + x) * 3] for y in range(y0, y1) for x in range(x0, x1)]

def verify(ffmpeg, ffprobe, source, output):
    a, b = raw(ffmpeg, source), raw(ffmpeg, output)
    for path in (source, output):
        stamps = pts(ffprobe, path)
        assert len(stamps) == len(PTS) and all(abs(x - y) < 0.0011 for x, y in zip(stamps, PTS)), (path, stamps)
    for i in (0, 1, 3):
        assert a[i] == b[i], f"Untargeted RGB frame {i} changed"
    for label, rect in (("Auto", (32, 42, 48, 66)), ("Manual", (112, 42, 128, 66))):
        before, after = region(a[2], *rect), region(b[2], *rect)
        assert statistics.pstdev(before) > 80, f"{label}: input texture missing"
        assert statistics.pstdev(after) < 20, f"{label}: output texture not sufficiently blurred"
        assert sum(abs(x - y) for x, y in zip(before, after)) / len(before) > 60, f"{label}: output pixels unchanged"
    assert region(a[2], 72, 42, 88, 66) == region(b[2], 72, 42, 88, 66), "Unmasked gap changed"
    assert region(a[2], 0, 0, 16, 16) == region(b[2], 0, 0, 16, 16), "Ordinal marker changed"
    print("PASS: real encoded RGB pixels, both independent masks, untouched frames/gap, VFR PTS")

if __name__ == "__main__":
    if sys.argv[1] == "generate":
        generate(pathlib.Path(sys.argv[2]))
    elif sys.argv[1] == "verify":
        verify(*sys.argv[2:])
    else:
        raise ValueError("Expected generate or verify")
