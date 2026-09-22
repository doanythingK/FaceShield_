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
    private bool _exclusiveProcessing;

    internal WorkspaceOperationLifetime(Action onOperationsDrained)
    {
        _onOperationsDrained = onOperationsDrained
            ?? throw new ArgumentNullException(nameof(onOperationsDrained));
    }

    internal event Action? AdmissionClosed;

    // Normal operations (including Auto's subsequent export) retain their
    // independent lifetime lease. They never reacquire the processing gate.
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
    /// Atomically admits only one Auto or manual-tracking operation. This is
    /// separate from TryBegin so an Auto-owned export can obtain a regular
    /// lifetime lease without deadlocking on its parent's exclusive lease.
    /// </summary>
    internal bool TryBeginExclusiveProcessing()
    {
        lock (_sync)
        {
            if (_admissionClosed || _exclusiveProcessing)
                return false;
            _exclusiveProcessing = true;
            _activeOperations++;
            return true;
        }
    }

    internal void EndExclusiveProcessing()
    {
        lock (_sync)
        {
            if (!_exclusiveProcessing)
                throw new InvalidOperationException("No exclusive workspace operation is active.");
            _exclusiveProcessing = false;
        }
        End();
    }

    /// <summary>
    /// Stops new lifetime operations without claiming resource disposal. This is
    /// used by terminal shutdown so cancellation and final persistence can run
    /// while already-started operations drain.
    /// </summary>
    internal bool CloseAdmission()
    {
        bool notify = false;
        lock (_sync)
        {
            if (_admissionClosed)
                return false;

            _admissionClosed = true;
            notify = true;
        }

        if (notify)
            AdmissionClosed?.Invoke();
        return true;
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
        bool notify = false;
        lock (_sync)
        {
            if (_disposeRequested)
            {
                disposeNow = false;
                return false;
            }

            notify = !_admissionClosed;
            _admissionClosed = true;
            _disposeRequested = true;
            disposeNow = _activeOperations == 0 && !_disposeClaimed;
            if (disposeNow)
                _disposeClaimed = true;
        }

        if (notify)
            AdmissionClosed?.Invoke();
        return true;
    }
}
