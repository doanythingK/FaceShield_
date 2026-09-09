using System;

namespace FaceShield.ViewModels.Workspace;

/// <summary>
/// Owns the workspace operation/disposal boundary. New operations stop once
/// disposal is requested, while already-started operations are allowed to
/// drain before the owning view model disposes shared resources.
/// </summary>
internal sealed class WorkspaceOperationLifetime
{
    private readonly object _sync = new();
    private readonly Action _onOperationsDrained;
    private int _activeOperations;
    private bool _admissionClosed;
    private bool _disposeRequested;
    private bool _disposeClaimed;

    internal WorkspaceOperationLifetime(Action onOperationsDrained)
    {
        _onOperationsDrained = onOperationsDrained
            ?? throw new ArgumentNullException(nameof(onOperationsDrained));
    }

    internal bool TryBegin()
    {
        lock (_sync)
        {
            if (_admissionClosed)
                return false;

            _activeOperations++;
            return true;
        }
    }

    /// <summary>
    /// Stops new lifetime operations without claiming resource disposal. This is
    /// used by terminal shutdown so cancellation and final persistence can run
    /// while already-started operations drain.
    /// </summary>
    internal bool CloseAdmission()
    {
        lock (_sync)
        {
            if (_admissionClosed)
                return false;

            _admissionClosed = true;
            return true;
        }
    }

    internal void End()
    {
        bool notifyDrained = false;
        lock (_sync)
        {
            if (_activeOperations > 0)
                _activeOperations--;

            if (_disposeRequested &&
                _activeOperations == 0 &&
                !_disposeClaimed)
            {
                _disposeClaimed = true;
                notifyDrained = true;
            }
        }

        if (notifyDrained)
            _onOperationsDrained();
    }

    /// <summary>
    /// Atomically closes admission for new operations. The caller owns the
    /// cancellation-before-disposal ordering and schedules resource disposal
    /// when <paramref name="disposeNow"/> is true.
    /// </summary>
    internal bool RequestDispose(out bool disposeNow)
    {
        lock (_sync)
        {
            if (_disposeRequested)
            {
                disposeNow = false;
                return false;
            }

            _admissionClosed = true;
            _disposeRequested = true;
            disposeNow = _activeOperations == 0 && !_disposeClaimed;
            if (disposeNow)
                _disposeClaimed = true;

            return true;
        }
    }
}
