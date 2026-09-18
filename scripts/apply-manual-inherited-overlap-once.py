#!/usr/bin/env python3
"""One-shot, guarded source patch. CI commits only after production regressions pass."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def replace_once(relative, old, new):
    path = ROOT / relative
    original = path.read_text(encoding="utf-8")
    count = original.count(old)
    if count != 1:
        raise RuntimeError(f"{relative}: expected exactly one patch anchor, found {count}")
    path.write_text(original.replace(old, new, 1), encoding="utf-8")
    print(f"patched {relative}")


timeline = "Services/Video/ManualMaskKeyframeTimeline.cs"
replace_once(timeline,
    "    internal static ExportMaskLease CreateExportMaskLease(FrameMaskProvider source)\n",
    """    /// <summary>
    /// Resolve ONLY the stored manual raster and its validated tracked samples.
    /// Never subtract Auto from a composite: overlapping manual pixels cannot
    /// be recovered from that lossy union. The caller owns the result.
    /// </summary>
    internal static bool TryCloneEffectiveManualMask(FrameMaskProvider provider,
        int frameIndex, out WriteableBitmap mask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mask = null!;
        if (!IsEnabled(provider) || frameIndex < 0) return false;
        int[] manualKeyframes = Sort(provider.GetStoredMaskFrameIndices());
        int sourceKeyframe = FindFloorKeyframe(manualKeyframes, frameIndex);
        if (sourceKeyframe < 0 ||
            !provider.TryCloneStoredMask(sourceKeyframe, out WriteableBitmap sourceMask))
            return false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameIndex == sourceKeyframe)
            {
                mask = sourceMask;
                sourceMask = null!;
                return true;
            }

            if (!TryResolveCurrentSegment(provider, sourceKeyframe,
                    out ManualMaskTrackSegment? segment, out bool blocked) ||
                blocked || segment == null || frameIndex >= segment.EndExclusive ||
                !HasAllSamples(segment, frameIndex))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            return TryCreateTransformedMask(sourceMask, segment, frameIndex, out mask);
        }
        finally
        {
            sourceMask?.Dispose();
        }
    }

    internal static ExportMaskLease CreateExportMaskLease(FrameMaskProvider source)
""")

provider = "Services/Video/FrameMaskProvider.cs"
replace_once(provider,
    """    public void SetMask(int frameIndex, WriteableBitmap mask)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        lock (_stateGate)
        {
            if (_faceMasks.TryGetValue(frameIndex, out FaceMaskData automatic))
                ManualMaskLayerIsolation.StripAutomaticCoverage(mask, automatic);

            if (_masks.TryRemove(frameIndex, out var previous) &&
                !ReferenceEquals(previous, mask))
            {
                RetireBitmapLocked(previous);
            }

            _masks[frameIndex] = mask;
            _ = _faceMasks.ContainsKey(frameIndex);
            _version++;
        }
    }
""",
    """    public void SetMask(int frameIndex, WriteableBitmap mask)
        => SetMaskCore(frameIndex, mask, editorComposite: true);

    // Only validated manual-only rasters may use this path. The editor's
    // composite SetMask must continue separating pre-existing Auto coverage.
    internal void SetIndependentManualMask(int frameIndex, WriteableBitmap mask)
        => SetMaskCore(frameIndex, mask, editorComposite: false);

    private void SetMaskCore(int frameIndex, WriteableBitmap mask, bool editorComposite)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        lock (_stateGate)
        {
            if (editorComposite &&
                _faceMasks.TryGetValue(frameIndex, out FaceMaskData automatic))
                ManualMaskLayerIsolation.StripAutomaticCoverage(mask, automatic);

            if (_masks.TryRemove(frameIndex, out var previous) &&
                !ReferenceEquals(previous, mask))
            {
                RetireBitmapLocked(previous);
            }

            _masks[frameIndex] = mask;
            _version++;
        }
    }
""")

selection = "Services/Video/ManualTrackingSourceSelection.cs"
replace_once(selection,
    """        // Only a validated, fully sampled inherited track may become a new
        // source. Do not fall back to provider.GetFinalMask(frameIndex): on an
        // Auto-only frame that method contains *only* the unrelated Auto face.
        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider, frameIndex, out WriteableBitmap inherited))
            return false;

        try
        {
            if (provider.TryGetFaceMaskData(frameIndex, out var automatic))
                ManualMaskLayerIsolation.StripAutomaticCoverage(inherited, automatic);

            if (!HasCoverage(inherited))
                return false;

            source = inherited;
            inherited = null!;
            return true;
        }
        finally
        {
            inherited?.Dispose();
        }
""",
    """        // Re-render the verified manual layer from its own source keyframe.
        // Subtracting Auto from a union destroys genuine overlapping pixels.
        // Missing or stale samples must still fail closed.
        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                provider, frameIndex, out WriteableBitmap inherited))
            return false;

        try
        {
            if (!HasCoverage(inherited))
                return false;

            source = inherited;
            inherited = null!;
            return true;
        }
        finally
        {
            inherited?.Dispose();
        }
""")

replace_once("ViewModels/Workspace/FramePreviewViewModel.ManualTracking.cs",
    "                            provider.SetMask(sourceFrame, promotedMask);\n",
    "                            provider.SetIndependentManualMask(sourceFrame, promotedMask);\n")

integration = "scripts/manual-auto-overlap-integration.cs.txt"
replace_once(integration,
    """foreach (int frame in new[] { sourceFrame - 1, sourceFrame, sourceFrame + 1, sourceFrame + 2 })
    provider.SetFaceRects(frame, new[] { new Rect(9, 10, 14, 14) }, new PixelSize(width, height));
""",
    """foreach (int frame in new[] { sourceFrame - 1, sourceFrame, sourceFrame + 1, sourceFrame + 2 })
{
    // At frame 551 Auto overlaps the manual target AND extends beyond it.
    // A lossy subtraction would erase genuine manual pixels at (185,95).
    Rect[] faces = frame == sourceFrame + 1
        ? new[] { new Rect(9, 10, 14, 14), new Rect(178, 85, 32, 30) }
        : new[] { new Rect(9, 10, 14, 14) };
    provider.SetFaceRects(frame, faces, new PixelSize(width, height));
}
""")
replace_once(integration,
    """    AssertManualOnly(continuedMask);
    Check(Array.IndexOf(provider.GetStoredMaskFrameIndices(), sourceFrame + 1) < 0,
""",
    """    AssertManualOnly(continuedMask);
    Check(AlphaAt(continuedMask, 185, 95) > 0 &&
          AlphaAt(continuedMask, 200, 95) == 0,
        "Overlapping manual pixels were subtracted or Auto-only pixels contaminated source");
    Check(Array.IndexOf(provider.GetStoredMaskFrameIndices(), sourceFrame + 1) < 0,
""")
replace_once(integration,
    """    provider.SetMask(sourceFrame + 1, continuedMask);
    continuedMask = null; // ownership transferred
    setSegment.Invoke(null, new object[] { provider, segment });
""",
    """    typeof(FrameMaskProvider).GetMethod("SetIndependentManualMask",
        BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(provider, new object[] { sourceFrame + 1, continuedMask });
    continuedMask = null; // ownership transferred
    Check(provider.TryCloneStoredMask(sourceFrame + 1, out var retained),
        "Promoted manual correction was not stored");
    using (retained)
        Check(AlphaAt(retained, 185, 95) > 0 && AlphaAt(retained, 200, 95) == 0,
            "Promotion stripped overlapping manual pixels or stored Auto-only pixels");
    setSegment.Invoke(null, new object[] { provider, segment });
""")
replace_once(integration,
    'Console.WriteLine("PASS: same-frame and inherited manual-only sources, continued FFmpeg track, Auto+manual preview/export, no untracked hold");',
    'Console.WriteLine("PASS: overlapping Auto/manual independent continuation, correction persistence, FFmpeg tracking, preview/export, no untracked hold");')

print("Guarded exact manual-only continuation patch applied; build and integration must pass before commit.")
