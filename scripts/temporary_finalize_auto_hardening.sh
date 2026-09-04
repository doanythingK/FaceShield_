#!/usr/bin/env bash
# Triggered only after the finalize workflow exists on the branch.
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

# The confirmed production scope excludes the standard Workspace video-export path.
# Stop the staged patch after Preview/Workspace persistence so VideoFrameProcessingPolicy
# and verifier/harness files remain untouched by this source-fix commit.
export_marker = '# Export owns the per-frame stored-mask clone via a using declaration.'
if export_marker not in text:
    raise RuntimeError('Expected export patch marker was not found.')
text = text[:text.index(export_marker)].rstrip() + '\n'

path.write_text(text)
PY

python3 scripts/temporary_apply_auto_hardening.py

python3 - <<'PY'
from pathlib import Path


def read_lines(path):
    return Path(path).read_bytes().decode('utf-8-sig').splitlines(keepends=True)


def write_lines(path, lines):
    Path(path).write_bytes(''.join(lines).encode('utf-8'))


def eol(line):
    return '\r\n' if line.endswith('\r\n') else '\n'


# Make sparse materialization transactional: work on an isolated provider snapshot,
# then atomically commit face masks only after cancellation-free completion.
path = 'Services/Analysis/AutoMaskGenerator.cs'
lines = read_lines(path)
call_hits = [i for i, line in enumerate(lines) if 'var materialized = SparseTrackingMaterializer.Materialize(' in line]
if len(call_hits) != 1:
    raise RuntimeError(f'Expected one sparse materialize call, found {len(call_hits)}')
call = call_hits[0]
indent = lines[call][:len(lines[call]) - len(lines[call].lstrip())]
nl = eol(lines[call])

if not any('sparseMaterializationProvider' in line for line in lines[max(0, call - 8):call]):
    snapshot = [
        indent + 'using var sparseMaterializationProvider = _maskProvider.CreateSnapshot(' + nl,
        indent + '    out long sparseMaterializationSourceVersion,' + nl,
        indent + '    pipelineToken);' + nl,
    ]
    lines[call:call] = snapshot
    call += len(snapshot)

provider_arg = None
for i in range(call + 1, min(len(lines), call + 12)):
    if lines[i].strip() == '_maskProvider,':
        provider_arg = i
        break
if provider_arg is None:
    if not any(line.strip() == 'sparseMaterializationProvider,' for line in lines[call + 1:call + 12]):
        raise RuntimeError('Sparse materializer provider argument was not found.')
else:
    lines[provider_arg] = lines[provider_arg].replace('_maskProvider,', 'sparseMaterializationProvider,')

close = None
for i in range(call + 1, min(len(lines), call + 20)):
    if lines[i].strip() == 'pipelineToken);':
        close = i
        break
if close is None:
    raise RuntimeError('Sparse materialize cancellation argument terminator was not found.')

if not any('CommitFaceMasksFrom(' in line for line in lines[close + 1:close + 12]):
    commit = [
        indent + 'pipelineToken.ThrowIfCancellationRequested();' + nl,
        indent + '_maskProvider.CommitFaceMasksFrom(' + nl,
        indent + '    sparseMaterializationProvider,' + nl,
        indent + '    sparseMaterializationSourceVersion,' + nl,
        indent + '    pipelineToken);' + nl,
    ]
    lines[close + 1:close + 1] = commit

write_lines(path, lines)

# Record the confirmed production-code scope without claiming broader runtime coverage.
doc = Path('QUALITY_STABILIZATION_REVIEW_FIXES.md')
existing = doc.read_bytes().decode('utf-8-sig')
heading = '## Follow-up: Auto cancellation and stored-mask ownership (2026-09-04)'
if heading not in existing:
    nl = '\r\n' if '\r\n' in existing[-2000:] else '\n'
    section = f'''\n---\n\n{heading}\n\nConfirmed production-code fixes on `docs/manual-blur-player-architecture`:\n\n- `AutoMaskGenerator.GenerateAsync()` rethrows caller cancellation so Workspace cannot treat a cancelled Auto transaction as a successful return.\n- Sparse materialization accepts cancellation, performs work against an isolated `FrameMaskProvider` snapshot, and commits face masks to the live provider only after successful completion and version validation.\n- Sequential, single-pipeline, and parallel-pipeline bulk writers check cancellation immediately before `SetFaceRects()` provider writes.\n- `FrameMaskProvider` now provides state-gated independent stored-mask clone/snapshot APIs; Preview and Workspace persistence use those owned copies instead of live provider-owned bitmap references.\n- The standard Workspace video-export path is intentionally unchanged because it already exports from `FrameMaskProvider.CreateSnapshot()`.\n'''
    doc.write_text(existing.rstrip() + section.replace('\n', nl) + nl, encoding='utf-8')
PY

git diff --check

python3 - <<'PY'
from pathlib import Path

def text(path):
    return Path(path).read_text(encoding='utf-8-sig')

auto = text('Services/Analysis/AutoMaskGenerator.cs')
sparse = text('Services/Analysis/SparseTrackingMaterializer.cs')
provider = text('Services/Video/FrameMaskProvider.cs')
preview = text('ViewModels/Workspace/FramePreviewViewModel.cs')
workspace = text('Services/Workspace/WorkspaceStateStore.cs')
export_policy = text('Services/Video/VideoFrameProcessingPolicy.cs')

assert 'catch (OperationCanceledException) when (ct.IsCancellationRequested)\n            {\n                throw;' in auto.replace('\r\n', '\n')
assert 'CancellationToken cancellationToken = default)' in sparse
assert 'sparseMaterializationProvider = _maskProvider.CreateSnapshot(' in auto
assert '_maskProvider.CommitFaceMasksFrom(' in auto
assert 'ct.ThrowIfCancellationRequested();\n                    _maskProvider.SetFaceRects(' in auto.replace('\r\n', '\n')
assert auto.count('pipelineToken.ThrowIfCancellationRequested();') >= 3
assert 'public bool TryCloneStoredMask(' in provider
assert 'public IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetStoredMaskSnapshot(' in provider
assert 'provider.TryCloneStoredMask(frameIndex, out var stored)' in preview
assert 'var entries = maskProvider.GetStoredMaskSnapshot();' in workspace
assert 'provider.TryGetStoredMask(decodedFrameOrdinal, out var stored)' in export_policy
assert 'TryCloneStoredMask(decodedFrameOrdinal' not in export_policy
PY

dotnet build FaceShield.csproj -c Release -p:UseAppHost=false --nologo

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git rm -f --ignore-unmatch temporary-patch-error.txt
git rm -f scripts/temporary_apply_auto_hardening.py scripts/temporary_finalize_auto_hardening.sh
git add Services/Analysis/AutoMaskGenerator.cs \
        Services/Analysis/SparseTrackingMaterializer.cs \
        Services/Video/FrameMaskProvider.cs \
        Services/Workspace/WorkspaceStateStore.cs \
        ViewModels/Workspace/FramePreviewViewModel.cs \
        QUALITY_STABILIZATION_REVIEW_FIXES.md
git diff --cached --check
git commit -m "Harden Auto cancellation and stored mask ownership"
git push origin HEAD:docs/manual-blur-player-architecture
