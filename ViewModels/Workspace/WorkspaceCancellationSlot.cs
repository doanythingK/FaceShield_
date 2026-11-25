using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FaceShield.ViewModels.Workspace;

/// <summary>
/// Registers a cancelable operation under the same lock as lifetime admission.
/// CloseAdmission followed by Cancel cannot miss an admitted operation.
/// Superseded session initializations remain registered until they drain.
/// </summary>
internal sealed class WorkspaceCancellationSlot
{
    private readonly object _gate = new();
    private readonly HashSet<Registration> _active = new();
    private Registration? _current;

    internal Registration? Current
    {
        get { lock (_gate) return _current; }
    }

    internal bool TryBegin(Func<bool> tryBeginLifetime,
        CancellationToken cancellationToken, out Registration registration,
        bool replaceExisting = false)
    {
        Registration? previous;
        lock (_gate)
        {
            registration = null!;
            if (!replaceExisting && _current != null)
                return false;

            var candidate = new Registration(cancellationToken);
            previous = _current;
            bool admitted = false;
            try
            {
                _active.Add(candidate);
                _current = candidate;
                admitted = tryBeginLifetime();
                if (!admitted)
                    return false;

                registration = candidate;
            }
            finally
            {
                if (!admitted)
                {
                    _active.Remove(candidate);
                    _current = previous;
                    candidate.Finish();
                }
            }
        }

        // Do not execute token callbacks under the admission lock.
        previous?.Cancel();
        return true;
    }

    internal void Cancel()
    {
        Registration[] pending;
        lock (_gate)
        {
            pending = new Registration[_active.Count];
            _active.CopyTo(pending);
        }
        foreach (var registration in pending)
            registration.Cancel();
    }

    internal void Finish(Registration registration)
    {
        lock (_gate)
        {
            _active.Remove(registration);
            if (ReferenceEquals(_current, registration))
                _current = null;
        }
        registration.Finish();
    }

    internal sealed class Registration
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private int _cancelers;
        private bool _finished;

        internal Registration(CancellationToken token)
        {
            _source = token.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : new CancellationTokenSource();
            Token = _source.Token;
        }

        internal CancellationToken Token { get; }

        internal void Cancel()
        {
            lock (_gate)
            {
                if (_finished) return;
                _cancelers++;
            }
            try
            {
                _source.Cancel();
            }
            catch (AggregateException ex)
            {
                // All callbacks have been attempted. A faulty callback must
                // not prevent cancellation of other admitted operations.
                Debug.WriteLine($"[WorkspaceCancellation] callback failed: {ex}");
            }
            finally
            {
                lock (_gate)
                {
                    _cancelers--;
                    if (_finished && _cancelers == 0)
                        _source.Dispose();
                }
            }
        }

        internal void Finish()
        {
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                // Cancel callbacks may synchronously complete an operation.
                // Defer disposal until those callbacks have returned.
                if (_cancelers == 0)
                    _source.Dispose();
            }
        }
    }
}
