using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private const long ManualTargetUndoBudgetBytes = 128L * 1024 * 1024;

    // A correction changes both pixels and the validated tracking intervals.
    // Legacy/global masks use an entry without target state.
    private sealed class MaskUndoEntry
    {
        internal MaskUndoEntry(byte[] pixels, ManualOverlayStoredTarget? targetBefore,
            Guid targetId, int frame)
        {
            Pixels = pixels;
            TargetBefore = targetBefore;
            TargetId = targetId;
            Frame = frame;
        }

        internal byte[] Pixels { get; }
        internal ManualOverlayStoredTarget? TargetBefore { get; }
        internal ManualOverlayStoredTarget? TargetAfter { get; set; }
        internal Guid TargetId { get; }
        internal int Frame { get; }
    }

    private readonly Dictionary<(Guid TargetId, int Frame), MaskUndoEntry[]>
        _manualTargetUndoArchives = new();
    private readonly LinkedList<(Guid TargetId, int Frame)> _manualTargetUndoOrder = new();
    private long _manualTargetUndoBytes;

    private ManualOverlayStoredTarget SnapshotSelectedManualTarget()
    {
        if (_manualTargets == null || _selectedManualTarget == null)
            throw new InvalidOperationException("A manual face must be selected.");
        return _manualTargets.Snapshot().First(target => target.Id == _selectedManualTarget.Id);
    }

    private MaskUndoEntry CreateMaskUndoEntry(byte[] pixels)
    {
        if (_manualTargets == null || _selectedManualTarget == null || _currentFrameIndex < 0)
            return new MaskUndoEntry(pixels, null, Guid.Empty, -1);
        return new MaskUndoEntry(pixels, SnapshotSelectedManualTarget(),
            _selectedManualTarget.Id, _currentFrameIndex);
    }

    // Record the post-commit state only after disk publication succeeded.
    private void RecordCommittedManualTargetUndo()
    {
        if (_maskUndo.Count == 0 || _selectedManualTarget == null)
            return;
        MaskUndoEntry latest = _maskUndo.Peek();
        if (latest.TargetBefore == null || latest.TargetAfter != null ||
            latest.TargetId != _selectedManualTarget.Id ||
            latest.Frame != _currentFrameIndex)
            return;
        latest.TargetAfter = SnapshotSelectedManualTarget();
    }

    private void RestoreSelectedManualTargetForUndo(MaskUndoEntry entry)
    {
        if (entry.TargetBefore == null || _manualTargets == null ||
            _selectedManualTarget == null || _selectedManualTarget.Id != entry.TargetId ||
            _currentFrameIndex != entry.Frame)
            throw new InvalidOperationException("The manual face Undo context has changed.");

        // An unsaved correction leaves the persisted face state unchanged.
        // A committed edit must restore keyframes and motion together, not
        // repaint old pixels as a fresh correction.
        ManualOverlayTargetEditCommitter.Restore(
            _manualTargetsVideoPath
                ?? throw new InvalidOperationException("Manual face video path is missing."),
            _manualTargets, entry.TargetId, entry.TargetBefore,
            entry.TargetAfter ?? entry.TargetBefore);
    }

    // Per-face and per-frame history survives preview refresh and switching.
    internal void PreserveManualTargetUndo(bool clearWhenEmpty = false)
    {
        if (_selectedManualTarget == null || _currentFrameIndex < 0)
            return;
        var key = (_selectedManualTarget.Id, _currentFrameIndex);
        MaskUndoEntry[] snapshots = _maskUndo.ToArray(); // newest first
        if (snapshots.Length == 0)
        {
            if (clearWhenEmpty)
                RemoveArchivedUndo(key);
            return;
        }
        RemoveArchivedUndo(key);
        long bytes = snapshots.Sum(static snapshot => (long)snapshot.Pixels.Length);
        if (bytes > ManualTargetUndoBudgetBytes) return;
        while (_manualTargetUndoBytes + bytes > ManualTargetUndoBudgetBytes &&
               _manualTargetUndoOrder.First != null)
            RemoveArchivedUndo(_manualTargetUndoOrder.First.Value);
        _manualTargetUndoArchives[key] = snapshots;
        _manualTargetUndoOrder.AddLast(key);
        _manualTargetUndoBytes += bytes;
    }

    internal void RestoreManualTargetUndo()
    {
        if (_selectedManualTarget == null || _currentFrameIndex < 0)
            return;
        _maskUndo.Clear();
        var key = (_selectedManualTarget.Id, _currentFrameIndex);
        if (!_manualTargetUndoArchives.TryGetValue(key, out MaskUndoEntry[]? newestFirst))
            return;
        for (int i = newestFirst.Length - 1; i >= 0; i--)
            _maskUndo.Push(newestFirst[i]);
    }

    private void RemoveArchivedUndo((Guid TargetId, int Frame) key)
    {
        if (_manualTargetUndoArchives.Remove(key, out MaskUndoEntry[]? snapshots))
            _manualTargetUndoBytes -= snapshots.Sum(static snapshot => (long)snapshot.Pixels.Length);
        _manualTargetUndoOrder.Remove(key);
    }
}
