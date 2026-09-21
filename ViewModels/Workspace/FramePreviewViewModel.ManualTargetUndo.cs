using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private const long ManualTargetUndoBudgetBytes = 128L * 1024 * 1024;
    private readonly Dictionary<(Guid TargetId, int Frame), byte[][]> _manualTargetUndoArchives = new();
    private readonly LinkedList<(Guid TargetId, int Frame)> _manualTargetUndoOrder = new();
    private long _manualTargetUndoBytes;

    // Called before switching faces. Snapshot arrays are owned by the undo
    // stack; moving them between contexts preserves the original per-frame
    // undo semantics without copying full-resolution masks yet again.
    internal void PreserveManualTargetUndo()
    {
        if (_selectedManualTarget == null || _currentFrameIndex < 0)
            return;
        var key = (_selectedManualTarget.Id, _currentFrameIndex);
        RemoveArchivedUndo(key);
        byte[][] snapshots = _maskUndo.ToArray(); // newest first
        if (snapshots.Length == 0) return;
        long bytes = snapshots.Sum(static snapshot => (long)snapshot.Length);
        if (bytes > ManualTargetUndoBudgetBytes) return;
        while (_manualTargetUndoBytes + bytes > ManualTargetUndoBudgetBytes &&
               _manualTargetUndoOrder.First != null)
            RemoveArchivedUndo(_manualTargetUndoOrder.First.Value);
        _manualTargetUndoArchives[key] = snapshots;
        _manualTargetUndoOrder.AddLast(key);
        _manualTargetUndoBytes += bytes;
    }

    // Called after ReplaceEditorWithSelectedTarget clears the shared stack.
    // An Undo from face A can therefore never restore pixels into face B.
    internal void RestoreManualTargetUndo()
    {
        _maskUndo.Clear();
        if (_selectedManualTarget == null || _currentFrameIndex < 0)
            return;
        var key = (_selectedManualTarget.Id, _currentFrameIndex);
        if (!_manualTargetUndoArchives.TryGetValue(key, out byte[][]? newestFirst))
            return;
        for (int i = newestFirst.Length - 1; i >= 0; i--)
            _maskUndo.Push(newestFirst[i]);
    }

    private void RemoveArchivedUndo((Guid TargetId, int Frame) key)
    {
        if (_manualTargetUndoArchives.Remove(key, out byte[][]? snapshots))
            _manualTargetUndoBytes -= snapshots.Sum(static snapshot => (long)snapshot.Length);
        _manualTargetUndoOrder.Remove(key);
    }
}
