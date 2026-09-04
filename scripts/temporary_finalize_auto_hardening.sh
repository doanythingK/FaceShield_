#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
from pathlib import Path
path = Path('scripts/temporary_apply_auto_hardening.py')
text = path.read_text()

replacements = [
    ('catch_idx = find(lines, "catch (OperationCanceledException) when (ct.IsCancellationRequested)")', 'generate_async = find(lines, "public async Task GenerateAsync(")\ncatch_idx = find_next(lines, "catch (OperationCanceledException) when (ct.IsCancellationRequested)", generate_async, 500)'),
    ('bridge_call = find(lines, "CanBridgeSparseResults(")', 'bridge_call = find_next(lines, "CanBridgeSparseResults(", next_call, 40)'),
    ('find_loop = find_next(lines, "for (int i = startKeyIndex;", find_next_method, 50)', 'find_loop = find_next(lines, "for (int i = startIndex;", find_next_method, 50)'),
    ('can_sig_end = find_next(lines, "double sceneCutDifferenceThreshold)", can_bridge_method, 20)', 'can_sig_end = find_next(lines, "double sceneCutThreshold)", can_bridge_method, 20)'),
    ('lines[can_sig_end] = lines[can_sig_end].replace("double sceneCutDifferenceThreshold)", "double sceneCutDifferenceThreshold,")', 'lines[can_sig_end] = lines[can_sig_end].replace("double sceneCutThreshold)", "double sceneCutThreshold,")'),
    ('after_entries = find_next(lines, "public IReadOnlyCollection<KeyValuePair<int, FaceMaskData>> GetFaceMaskEntries()", get_entries, 80)', 'after_entries = find_next(lines, "public FrameMaskProvider CreateSnapshot()", get_entries, 80)'),
]
for old, new in replacements:
    if text.count(old) != 1:
        raise RuntimeError(f'Expected one patch locator, found {text.count(old)} for: {old}')
    text = text.replace(old, new)

start = text.index('if any(line.strip() for line in lines[brace_idx + 1 : close_idx]):')
end = text.index('\n\nseq_start =', start)
replacement = '''if not any("throw;" in line for line in lines[brace_idx + 1 : close_idx]):
    indent = lines[brace_idx][: len(lines[brace_idx]) - len(lines[brace_idx].lstrip())] + "    "
    lines[brace_idx + 1 : close_idx] = [indent + "throw;" + eol(lines[brace_idx])]'''
text = text[:start] + replacement + text[end:]

old = '''stop_if = find(lines, "if (!canBridge &&")
if "ThrowIfCancellationRequested" not in lines[stop_if - 1]:
    insert_before(lines, stop_if, "cancellationToken.ThrowIfCancellationRequested();")'''
new = '''scene_cut_probe = find(lines, "int nextKey =")
if "ThrowIfCancellationRequested" not in lines[scene_cut_probe - 1]:
    insert_before(lines, scene_cut_probe, "cancellationToken.ThrowIfCancellationRequested();")'''
if text.count(old) != 1:
    raise RuntimeError(f'Expected one stale scene-cut insertion block, found {text.count(old)}')
text = text.replace(old, new)

old = '''interp = find(lines, "? InterpolateSparseResult(current, nextPositive!, frame)")
lines[interp] = lines[interp].replace(
    "InterpolateSparseResult(current, nextPositive!, frame)",
    "InterpolateSparseResult(current, nextPositive!, frame, cancellationToken)",
)'''
new = '''interp_call = find(lines, "? InterpolateSparseResult(")
interp_frame = find_next(lines, "frame)", interp_call, 8)
nl = eol(lines[interp_frame])
indent = lines[interp_frame][: len(lines[interp_frame]) - len(lines[interp_frame].lstrip())]
lines[interp_frame] = lines[interp_frame].replace("frame)", "frame,")
lines.insert(interp_frame + 1, indent + "cancellationToken)" + nl)'''
if text.count(old) != 1:
    raise RuntimeError(f'Expected one stale interpolation call block, found {text.count(old)}')
text = text.replace(old, new)

old = 'block_end = find_next(lines, "}", stored + 1, 12)'
new = 'face_data = find_next(lines, "if (provider.TryGetFaceMaskData(frameIndex, out var faceData))", stored, 24)\nblock_end = face_data'
if text.count(old) != 1:
    raise RuntimeError(f'Expected one preview block locator, found {text.count(old)}')
text = text.replace(old, new)
old = 'lines[stored : block_end + 1] = replacement'
new = 'lines[stored:block_end] = replacement'
if text.count(old) != 1:
    raise RuntimeError(f'Expected one preview replacement slice, found {text.count(old)}')
text = text.replace(old, new)

path.write_text(text)
PY

python3 scripts/temporary_apply_auto_hardening.py

python3 - <<'PY'
from pathlib import Path
path = Path('scripts/verify-runtime-hardening.ps1')
text = path.read_text()
replacements = [
    (
        "Assert-Match \"aborted bitmap snapshot copy disposes its uncommitted bitmap\" $frameMaskProvider 'CloneBitmap\\([\\s\\S]{0,1200}catch[\\s\\S]{0,120}copy\\.Dispose\\(\\)[\\s\\S]{0,80}throw;'",
        "Assert-Match \"aborted bitmap snapshot copy disposes its uncommitted bitmap\" $frameMaskProvider 'private\\s+static\\s+WriteableBitmap\\s+CloneBitmap\\([\\s\\S]{0,1800}catch[\\s\\S]{0,160}copy\\.Dispose\\(\\)[\\s\\S]{0,120}throw;'"
    ),
    (
        "Assert-Match \"blocking frame reads use the shared AVIO interrupt guard\" ($extractor + $videoIoInterrupt) 'VideoIoInterruptGuard[\\s\\S]*ConfigureIoInterrupt\\([\\s\\S]*_ioInterrupt\\.Configure\\(format\\)[\\s\\S]*BeginIoInterrupt\\([\\s\\S]*_ioInterrupt\\.Begin\\(cancellationToken\\)'",
        "Assert-Match \"blocking frame reads use the shared AVIO interrupt guard\" ($extractor + $videoIoInterrupt) 'VideoIoInterruptGuard[\\s\\S]*ConfigureIoInterrupt\\([\\s\\S]{0,700}var\\s+ioInterrupt\\s*=\\s*_ioInterrupt[\\s\\S]{0,500}ioInterrupt\\.Configure\\(format\\)[\\s\\S]{0,1400}BeginIoInterrupt\\([\\s\\S]{0,700}var\\s+ioInterrupt\\s*=\\s*_ioInterrupt[\\s\\S]{0,500}ioInterrupt\\.Begin\\(cancellationToken\\)'"
    ),
]
for old, new in replacements:
    if text.count(old) != 1:
        raise RuntimeError(f'Expected one verifier assertion, found {text.count(old)} for: {old[:80]}')
    text = text.replace(old, new)
path.write_text(text)
PY

git diff --check
pwsh -File ./scripts/verify-runtime-hardening.ps1
pwsh -File ./scripts/verify-automask-sparse-materialize-scene-cut.ps1

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git rm -f --ignore-unmatch temporary-patch-error.txt
git rm -f scripts/temporary_apply_auto_hardening.py scripts/temporary_finalize_auto_hardening.sh
git add Services/Analysis/AutoMaskGenerator.cs \
        Services/Analysis/SparseTrackingMaterializer.cs \
        Services/Video/FrameMaskProvider.cs \
        Services/Video/VideoFrameProcessingPolicy.cs \
        Services/Workspace/WorkspaceStateStore.cs \
        ViewModels/Workspace/FramePreviewViewModel.cs \
        scripts/verify-runtime-hardening.ps1 \
        scripts/verify-automask-sparse-materialize-scene-cut.ps1 \
        QUALITY_STABILIZATION_REVIEW_FIXES.md
git diff --cached --check
git commit -m "Harden Auto cancellation and stored mask reads"
git push origin HEAD:docs/manual-blur-player-architecture
