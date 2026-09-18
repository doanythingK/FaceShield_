#!/usr/bin/env python3
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
tracker = repo / "Services/Video/ManualMaskTrackingService.cs"
text = tracker.read_text(encoding="utf-8")

old = """                    nextFeatures[c] = inliers;\n                    nextTransforms[c] = cumulative;\n                    confidences[c] = confidence;\n"""
new = """                    // Re-detect strong points inside the CURRENT transformed manual\n                    // mask. Carrying only surviving optical-flow points makes the feature\n                    // set monotonically shrink and eventually fail after blur/occlusion.\n                    // The source mask still defines the allowed target region, so refreshed\n                    // points cannot be seeded on unrelated background outside that mask.\n                    List<ManualPoint> refreshed = SelectFeaturesInTransformedMask(\n                        sourceMask, component.Source, cumulative, current, currentStride,\n                        width, height, cancellationToken);\n                    nextFeatures[c] = refreshed.Count >= MinimumFeatures ? refreshed : inliers;\n                    nextTransforms[c] = cumulative;\n                    confidences[c] = confidence;\n"""
if text.count(old) != 1:
    raise RuntimeError(f"expected one tracking feature commit block, found {text.count(old)}")
text = text.replace(old, new, 1)

anchor = """    private static double Luma(byte[] frame, int stride, int x, int y)\n    {\n        int i = y * stride + x * 4;\n        return frame[i] * 0.114 + frame[i + 1] * 0.587 + frame[i + 2] * 0.299;\n    }\n"""
helper = r'''    private static List<ManualPoint> SelectFeaturesInTransformedMask(
        WriteableBitmap sourceMask, Bounds source, ManualMotion motion,
        byte[] frame, int stride, int width, int height, CancellationToken ct)
    {
        double determinant = motion.A * motion.A + motion.B * motion.B;
        if (!double.IsFinite(determinant) || determinant < 1e-8)
            return new List<ManualPoint>();

        ManualPoint[] corners =
        {
            motion.Apply(new ManualPoint(source.X0, source.Y0)),
            motion.Apply(new ManualPoint(source.X1, source.Y0)),
            motion.Apply(new ManualPoint(source.X0, source.Y1)),
            motion.Apply(new ManualPoint(source.X1, source.Y1))
        };
        if (corners.Any(static p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            return new List<ManualPoint>();

        double left = corners.Min(static p => p.X);
        double right = corners.Max(static p => p.X);
        double top = corners.Min(static p => p.Y);
        double bottom = corners.Max(static p => p.Y);
        int x0 = Math.Max(4, (int)Math.Floor(left));
        int x1 = Math.Min(width - 5, (int)Math.Ceiling(right));
        int y0 = Math.Max(4, (int)Math.Floor(top));
        int y1 = Math.Min(height - 5, (int)Math.Ceiling(bottom));
        if (x0 > x1 || y0 > y1)
            return new List<ManualPoint>();

        var ranked = new List<(double Strength, ManualPoint Location)>();
        using var fb = sourceMask.Lock();
        unsafe
        {
            byte* origin = (byte*)fb.Address;
            for (int y = y0; y <= y1; y += 2)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = x0; x <= x1; x += 2)
                {
                    double dx = x - motion.X;
                    double dy = y - motion.Y;
                    double sourceX = (motion.A * dx + motion.B * dy) / determinant;
                    double sourceY = (-motion.B * dx + motion.A * dy) / determinant;
                    if (sourceX < source.X0 || sourceX > source.X1 ||
                        sourceY < source.Y0 || sourceY > source.Y1)
                        continue;

                    int sx = Math.Clamp(
                        (int)Math.Round((sourceX + 0.5) * fb.Size.Width / width - 0.5),
                        0, fb.Size.Width - 1);
                    int sy = Math.Clamp(
                        (int)Math.Round((sourceY + 0.5) * fb.Size.Height / height - 0.5),
                        0, fb.Size.Height - 1);
                    byte* row = origin + sy * fb.RowBytes;
                    if (row[sx * 4 + 3] <= 24)
                        continue;

                    double gx = Luma(frame, stride, x + 2, y) -
                                Luma(frame, stride, x - 2, y);
                    double gy = Luma(frame, stride, x, y + 2) -
                                Luma(frame, stride, x, y - 2);
                    double strength = Math.Abs(gx) + Math.Abs(gy);
                    if (strength >= 6)
                        ranked.Add((strength, new ManualPoint(x, y)));
                }
            }
        }

        ranked.Sort(static (a, b) => b.Strength.CompareTo(a.Strength));
        var chosen = new List<ManualPoint>(Math.Min(MaxFeaturesPerComponent, ranked.Count));
        // Keep coverage across the target instead of letting one high-contrast patch
        // monopolize all 64 points. This lets the tracker survive local blur/occlusion.
        const int grid = 4;
        const int perCellLimit = MaxFeaturesPerComponent / (grid * grid);
        var perCell = new int[grid * grid];
        double spanX = Math.Max(1.0, right - left + 1.0);
        double spanY = Math.Max(1.0, bottom - top + 1.0);
        foreach ((double strength, ManualPoint location) in ranked)
        {
            if ((chosen.Count & 15) == 0)
                ct.ThrowIfCancellationRequested();
            if (strength < 16 && chosen.Count >= MinimumFeatures)
                break;
            if (chosen.Any(point => DistanceSquared(point, location) < 16))
                continue;

            int cellX = Math.Clamp((int)((location.X - left) * grid / spanX), 0, grid - 1);
            int cellY = Math.Clamp((int)((location.Y - top) * grid / spanY), 0, grid - 1);
            int cell = cellY * grid + cellX;
            if (perCell[cell] >= perCellLimit)
                continue;

            chosen.Add(location);
            perCell[cell]++;
            if (chosen.Count >= MaxFeaturesPerComponent)
                break;
        }
        return chosen;
    }

'''
if text.count(anchor) != 1:
    raise RuntimeError(f"expected one Luma anchor, found {text.count(anchor)}")
text = text.replace(anchor, helper + anchor, 1)
tracker.write_text(text, encoding="utf-8")

verify = repo / "scripts/verify-manual-tracking-integration.sh"
text = verify.read_text(encoding="utf-8")
old_ffmpeg = """ffmpeg_prefix=\"$(brew --prefix ffmpeg@8 2>/dev/null || true)\"\nif [[ -z \"$ffmpeg_prefix\" || ! -x \"$ffmpeg_prefix/bin/ffmpeg\" ]]; then\n    brew install ffmpeg@8\n    ffmpeg_prefix=\"$(brew --prefix ffmpeg@8)\"\nfi\nffmpeg_cmd=\"$ffmpeg_prefix/bin/ffmpeg\"\nffprobe_cmd=\"$ffmpeg_prefix/bin/ffprobe\"\nif [[ ! -x \"$ffmpeg_cmd\" || ! -x \"$ffprobe_cmd\" ]]; then\n    echo 'ERROR: the FFmpeg 8 command-line tools were not installed' >&2\n    exit 1\nfi\n"""
new_ffmpeg = """ffmpeg_prefix=\"$(brew --prefix ffmpeg@8 2>/dev/null || true)\"\nif [[ -z \"$ffmpeg_prefix\" || ! -x \"$ffmpeg_prefix/bin/ffmpeg\" ]]; then\n    ffmpeg_prefix=\"$(brew --prefix ffmpeg 2>/dev/null || true)\"\n    if [[ -z \"$ffmpeg_prefix\" || ! -x \"$ffmpeg_prefix/bin/ffmpeg\" ]]; then\n        brew install ffmpeg\n        ffmpeg_prefix=\"$(brew --prefix ffmpeg)\"\n    fi\nfi\nffmpeg_cmd=\"$ffmpeg_prefix/bin/ffmpeg\"\nffprobe_cmd=\"$ffmpeg_prefix/bin/ffprobe\"\nif [[ ! -x \"$ffmpeg_cmd\" || ! -x \"$ffprobe_cmd\" ]]; then\n    echo 'ERROR: FFmpeg command-line tools were not installed' >&2\n    exit 1\nfi\nffmpeg_version=\"$($ffmpeg_cmd -version | head -n 1)\"\nif [[ ! \"$ffmpeg_version\" =~ ffmpeg\\ version\\ 8([.[:space:]]|$) ]]; then\n    echo \"ERROR: manual tracking integration requires FFmpeg major 8, got: $ffmpeg_version\" >&2\n    exit 1\nfi\n"""
if text.count(old_ffmpeg) != 1:
    raise RuntimeError(f"expected one FFmpeg setup block, found {text.count(old_ffmpeg)}")
text = text.replace(old_ffmpeg, new_ffmpeg, 1)

old_fixture = """                sx = x - 40 - frame\n                sy = y - 28 - frame\n                if 0 <= sx < 56 and 0 <= sy < 56:\n                    value = int(125 + 45*math.sin(sx*.19) +\n                                35*math.cos(sy*.21) +\n                                30*math.sin((sx+sy)*.14))\n                    value = max(35, min(220, value))\n                else:\n                    value = 28\n"""
new_fixture = """                sx = x - 40 - frame\n                sy = y - 28 - frame\n                if 0 <= sx < 56 and 0 <= sy < 56:\n                    # Frame 0 has useful texture only on the left half. From frame 1\n                    # the right half gains texture, and from frame 8 the left half\n                    # becomes flat. A tracker that only carries its original points\n                    # loses the target; per-frame feature refresh can hand off support.\n                    if sx < 28 and frame < 8:\n                        value = int(125 + 55*math.sin(sx*.41) +\n                                    45*math.cos(sy*.37) +\n                                    30*math.sin((sx+sy)*.29))\n                    elif sx >= 28 and frame >= 1:\n                        value = int(125 + 38*math.sin(sx*.33) +\n                                    34*math.cos(sy*.31) +\n                                    24*math.sin((sx+sy)*.23))\n                    else:\n                        value = 125\n                    value = max(35, min(220, value))\n                else:\n                    value = 125\n"""
if text.count(old_fixture) != 1:
    raise RuntimeError(f"expected one moving fixture block, found {text.count(old_fixture)}")
text = text.replace(old_fixture, new_fixture, 1)
verify.write_text(text, encoding="utf-8")
print("PASS: staged transformed-mask feature refresh, FFmpeg 8 resolver, and texture-handoff regression")
