from pathlib import Path


def read_exact(path: str) -> str:
    with Path(path).open('r', encoding='utf-8', newline='') as f:
        return f.read()


def write_exact(path: str, text: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('w', encoding='utf-8', newline='') as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read_exact(path)
    candidates = [(old, new)]
    if '\n' in old:
        candidates.append((old.replace('\n', '\r\n'), new.replace('\n', '\r\n')))
    for before, after in candidates:
        count = text.count(before)
        if count == 1:
            write_exact(path, text.replace(before, after, 1))
            return
        if count > 1:
            raise RuntimeError(f'Expected one match in {path}, found {count}: {old[:120]!r}')
    raise RuntimeError(f'Patch target not found in {path}: {old[:160]!r}')


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    text = read_exact(path)
    sep = '\r\n' if '\r\n' in text else '\n'
    start_key = start_marker.replace('\n', sep)
    end_key = end_marker.replace('\n', sep)
    start = text.find(start_key)
    if start < 0:
        raise RuntimeError(f'Start marker not found in {path}: {start_marker!r}')
    end = text.find(end_key, start)
    if end < 0:
        raise RuntimeError(f'End marker not found in {path}: {end_marker!r}')
    write_exact(path, text[:start] + replacement.replace('\n', sep) + text[end:])


write_exact('Controls/TimelineFrameStripRequestCoordinator.cs', '''using Avalonia.Threading;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Controls;

internal sealed class TimelineFrameStripRequestCoordinator
{
    private readonly Func<TimelineThumbnailProvider?> _getProvider;
    private readonly Func<int> _getSelectedFrameIndex;
    private readonly Action<int> _setSelectedFrameIndex;
    private readonly Action _invalidateVisual;

    private readonly object _pendingThumbnailSync = new();
    private readonly HashSet<long> _pendingThumbnails = new();
    private CancellationTokenSource _thumbnailRequestCts = new();
    private CancellationTokenSource? _selectionRequestCts;
    private CancellationTokenSource? _selectedPtsRequestCts;
    private TimelineThumbnailProvider? _selectedPtsRequestProvider;
    private int _selectedPtsRequestFrame = -1;
    private CancellationTokenSource? _issueViewportRequestCts;
    private TimelineThumbnailProvider? _issueViewportRequestProvider;
    private double _issueViewportRequestSeconds = double.NaN;
    private double _issueViewportFailedSeconds = double.NaN;
    private CancellationTokenSource? _issueFrameRequestCts;
    private TimelineThumbnailProvider? _issueFrameRequestProvider;
    private int _issueFrameRequestIndex = -1;
    private int _issueFrameFailedIndex = -1;
    private TimelineThumbnailProvider? _thumbnailRequestProvider;
    private double _thumbnailRequestStart = double.NaN;
    private double _thumbnailRequestEnd = double.NaN;

    internal TimelineFrameStripRequestCoordinator(
        Func<TimelineThumbnailProvider?> getProvider,
        Func<int> getSelectedFrameIndex,
        Action<int> setSelectedFrameIndex,
        Action invalidateVisual)
    {
        _getProvider = getProvider ?? throw new ArgumentNullException(nameof(getProvider));
        _getSelectedFrameIndex = getSelectedFrameIndex ?? throw new ArgumentNullException(nameof(getSelectedFrameIndex));
        _setSelectedFrameIndex = setSelectedFrameIndex ?? throw new ArgumentNullException(nameof(setSelectedFrameIndex));
        _invalidateVisual = invalidateVisual ?? throw new ArgumentNullException(nameof(invalidateVisual));
    }

    internal void EnsureThumbnailRequestScope(
        TimelineThumbnailProvider provider,
        double startSec,
        double endSec)
    {
        CancellationTokenSource previous;
        lock (_pendingThumbnailSync)
        {
            bool changed =
                !ReferenceEquals(_thumbnailRequestProvider, provider) ||
                !double.IsFinite(_thumbnailRequestStart) ||
                Math.Abs(_thumbnailRequestStart - startSec) > 0.001 ||
                Math.Abs(_thumbnailRequestEnd - endSec) > 0.001;
            if (!changed)
                return;

            previous = _thumbnailRequestCts;
            _thumbnailRequestCts = new CancellationTokenSource();
            _thumbnailRequestProvider = provider;
            _thumbnailRequestStart = startSec;
            _thumbnailRequestEnd = endSec;
            _pendingThumbnails.Clear();
        }

        previous.Cancel();
        previous.Dispose();
    }

    internal void RequestThumbnail(
        TimelineThumbnailProvider provider,
        double timestampSeconds)
    {
        long requestKey = Math.Max(
            0,
            (long)Math.Round(timestampSeconds * 1000.0));
        CancellationToken token;
        lock (_pendingThumbnailSync)
        {
            if (!_pendingThumbnails.Add(requestKey))
                return;

            token = _thumbnailRequestCts.Token;
        }

        _ = Task.Run(() =>
            {
                try
                {
                    return provider.GetThumbnailAtTime(
                        timestampSeconds,
                        token) != null;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            }, token)
            .ContinueWith(task =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    lock (_pendingThumbnailSync)
                        _pendingThumbnails.Remove(requestKey);

                    if (!token.IsCancellationRequested &&
                        task.Status == TaskStatus.RanToCompletion &&
                        task.Result)
                    {
                        _invalidateVisual();
                    }
                });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal void RequestExactFrameSelection(
        TimelineThumbnailProvider provider,
        double timestampSeconds,
        int totalFrames,
        int baselineSelectedFrameIndex,
        Func<int, int> normalizeFrameIndex)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _selectionRequestCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _ = ResolveExactFrameSelectionAsync(
            provider,
            timestampSeconds,
            totalFrames,
            baselineSelectedFrameIndex,
            normalizeFrameIndex,
            cts);
    }

    private async Task ResolveExactFrameSelectionAsync(
        TimelineThumbnailProvider provider,
        double timestampSeconds,
        int totalFrames,
        int baselineSelectedFrameIndex,
        Func<int, int> normalizeFrameIndex,
        CancellationTokenSource cts)
    {
        try
        {
            (bool resolved, int frameIndex) = await Task.Run(() =>
            {
                bool ok = provider.TryResolveFrameIndexAtTimestamp(
                    timestampSeconds,
                    cts.Token,
                    out int resolvedIndex);
                return (ok, resolvedIndex);
            }, cts.Token);

            if (cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested ||
                    !ReferenceEquals(_selectionRequestCts, cts) ||
                    !ReferenceEquals(_getProvider(), provider) ||
                    _getSelectedFrameIndex() != baselineSelectedFrameIndex)
                {
                    return;
                }

                if (!resolved)
                    return;

                _setSelectedFrameIndex(normalizeFrameIndex(frameIndex));
                _invalidateVisual();
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_selectionRequestCts, cts))
                Interlocked.CompareExchange(ref _selectionRequestCts, null, cts);
            cts.Dispose();
        }
    }

    internal void RequestSelectedFrameTimestamp(
        TimelineThumbnailProvider provider,
        int frameIndex)
    {
        if (provider.OperationsSuspended)
            return;
        if (ReferenceEquals(_selectedPtsRequestProvider, provider) &&
            _selectedPtsRequestFrame == frameIndex &&
            _selectedPtsRequestCts != null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _selectedPtsRequestCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _selectedPtsRequestProvider = provider;
        _selectedPtsRequestFrame = frameIndex;
        _ = ResolveSelectedFrameTimestampAsync(provider, frameIndex, cts);
    }

    private async Task ResolveSelectedFrameTimestampAsync(
        TimelineThumbnailProvider provider,
        int frameIndex,
        CancellationTokenSource cts)
    {
        try
        {
            bool resolved = await Task.Run(
                () => provider.TryResolveFrameTimestampSeconds(
                    frameIndex,
                    cts.Token,
                    out _),
                cts.Token);

            if (!resolved || cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested ||
                    !ReferenceEquals(_selectedPtsRequestCts, cts) ||
                    !ReferenceEquals(_getProvider(), provider) ||
                    _getSelectedFrameIndex() != frameIndex)
                {
                    return;
                }

                _invalidateVisual();
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_selectedPtsRequestCts, cts))
            {
                Interlocked.CompareExchange(
                    ref _selectedPtsRequestCts,
                    null,
                    cts);
                _selectedPtsRequestProvider = null;
                _selectedPtsRequestFrame = -1;
            }

            cts.Dispose();
        }
    }

    internal bool RequestIssueViewportMapping(
        TimelineThumbnailProvider provider,
        double timestampSeconds)
    {
        if (provider.OperationsSuspended ||
            !double.IsFinite(timestampSeconds) ||
            timestampSeconds < 0)
        {
            return false;
        }

        if (ReferenceEquals(_issueViewportRequestProvider, provider))
        {
            if (_issueViewportRequestCts != null &&
                Math.Abs(_issueViewportRequestSeconds - timestampSeconds) <= 0.001)
            {
                return true;
            }

            if (double.IsFinite(_issueViewportFailedSeconds) &&
                Math.Abs(_issueViewportFailedSeconds - timestampSeconds) <= 0.001)
            {
                return false;
            }
        }

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _issueViewportRequestCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _issueViewportRequestProvider = provider;
        _issueViewportRequestSeconds = timestampSeconds;
        _issueViewportFailedSeconds = double.NaN;
        _ = ResolveIssueViewportMappingAsync(
            provider,
            timestampSeconds,
            cts);
        return true;
    }

    private async Task ResolveIssueViewportMappingAsync(
        TimelineThumbnailProvider provider,
        double timestampSeconds,
        CancellationTokenSource cts)
    {
        bool resolved = false;
        try
        {
            resolved = await Task.Run(
                () => provider.TryResolveFrameIndexAtTimestamp(
                    timestampSeconds,
                    cts.Token,
                    out _),
                cts.Token);

            if (cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested ||
                    !ReferenceEquals(_issueViewportRequestCts, cts) ||
                    !ReferenceEquals(_getProvider(), provider))
                {
                    return;
                }

                if (!resolved && !provider.OperationsSuspended)
                    _issueViewportFailedSeconds = timestampSeconds;

                _invalidateVisual();
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_issueViewportRequestCts, cts))
            {
                Interlocked.CompareExchange(
                    ref _issueViewportRequestCts,
                    null,
                    cts);
            }

            cts.Dispose();
        }
    }

    internal void RequestIssueFrameMapping(
        TimelineThumbnailProvider provider,
        int frameIndex)
    {
        if (provider.OperationsSuspended || frameIndex < 0)
            return;

        if (ReferenceEquals(_issueFrameRequestProvider, provider))
        {
            if (_issueFrameRequestCts != null &&
                _issueFrameRequestIndex == frameIndex)
            {
                return;
            }

            if (_issueFrameFailedIndex == frameIndex)
                return;
        }

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _issueFrameRequestCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _issueFrameRequestProvider = provider;
        _issueFrameRequestIndex = frameIndex;
        _issueFrameFailedIndex = -1;
        _ = ResolveIssueFrameMappingAsync(provider, frameIndex, cts);
    }

    private async Task ResolveIssueFrameMappingAsync(
        TimelineThumbnailProvider provider,
        int frameIndex,
        CancellationTokenSource cts)
    {
        bool resolved = false;
        try
        {
            resolved = await Task.Run(
                () => provider.TryResolveFrameTimestampSeconds(
                    frameIndex,
                    cts.Token,
                    out _),
                cts.Token);

            if (cts.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested ||
                    !ReferenceEquals(_issueFrameRequestCts, cts) ||
                    !ReferenceEquals(_getProvider(), provider))
                {
                    return;
                }

                if (!resolved && !provider.OperationsSuspended)
                    _issueFrameFailedIndex = frameIndex;

                _invalidateVisual();
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_issueFrameRequestCts, cts))
            {
                Interlocked.CompareExchange(
                    ref _issueFrameRequestCts,
                    null,
                    cts);
            }

            cts.Dispose();
        }
    }
}
''')

replace_once('Controls/TimelineFrameStrip.cs', 'using System.Threading;\n', '')
replace_once('Controls/TimelineFrameStrip.cs', 'using System.Threading.Tasks;\n', '')
replace_once('Controls/TimelineFrameStrip.cs', 'using Avalonia.Threading;\n', '')

replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''        private double _hoverSeconds = double.NaN;\n        private readonly object _pendingThumbnailSync = new();\n        private readonly HashSet<long> _pendingThumbnails = new();\n        private CancellationTokenSource _thumbnailRequestCts = new();\n        private CancellationTokenSource? _selectionRequestCts;\n        private CancellationTokenSource? _selectedPtsRequestCts;\n        private TimelineThumbnailProvider? _selectedPtsRequestProvider;\n        private int _selectedPtsRequestFrame = -1;\n        private CancellationTokenSource? _issueViewportRequestCts;\n        private TimelineThumbnailProvider? _issueViewportRequestProvider;\n        private double _issueViewportRequestSeconds = double.NaN;\n        private double _issueViewportFailedSeconds = double.NaN;\n        private CancellationTokenSource? _issueFrameRequestCts;\n        private TimelineThumbnailProvider? _issueFrameRequestProvider;\n        private int _issueFrameRequestIndex = -1;\n        private int _issueFrameFailedIndex = -1;\n        private TimelineThumbnailProvider? _thumbnailRequestProvider;\n        private double _thumbnailRequestStart = double.NaN;\n        private double _thumbnailRequestEnd = double.NaN;\n\n        static TimelineFrameStrip()\n''',
    '''        private double _hoverSeconds = double.NaN;\n        private readonly TimelineFrameStripRequestCoordinator _requests;\n\n        public TimelineFrameStrip()\n        {\n            _requests = new TimelineFrameStripRequestCoordinator(\n                () => ThumbnailProvider,\n                () => SelectedFrameIndex,\n                frameIndex => SetCurrentValue(SelectedFrameIndexProperty, frameIndex),\n                InvalidateVisual);\n        }\n\n        static TimelineFrameStrip()\n''')

replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''                RequestExactFrameSelection(\n                    provider,\n                    seconds,\n                    total,\n                    SelectedFrameIndex);\n''',
    '''                _requests.RequestExactFrameSelection(\n                    provider,\n                    seconds,\n                    total,\n                    SelectedFrameIndex,\n                    frameIndex => NormalizeResolvedFrameIndex(frameIndex, total));\n''')

replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''                    RequestSelectedFrameTimestamp(\n                        timelineProvider,\n                        SelectedFrameIndex);\n''',
    '''                    _requests.RequestSelectedFrameTimestamp(\n                        timelineProvider,\n                        SelectedFrameIndex);\n''')

replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''            EnsureThumbnailRequestScope(provider, startSec, endSec);\n''',
    '''            _requests.EnsureThumbnailRequestScope(provider, startSec, endSec);\n''')
replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''                        RequestThumbnail(provider, sec);\n''',
    '''                        _requests.RequestThumbnail(provider, sec);\n''')
replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''                    if (RequestIssueViewportMapping(provider, centerSec))\n''',
    '''                    if (_requests.RequestIssueViewportMapping(provider, centerSec))\n''')
replace_once(
    'Controls/TimelineFrameStrip.cs',
    '''                    RequestIssueFrameMapping(provider, nextMissingFrame);\n''',
    '''                    _requests.RequestIssueFrameMapping(provider, nextMissingFrame);\n''')

replace_between(
    'Controls/TimelineFrameStrip.cs',
    '''        private void EnsureThumbnailRequestScope(\n''',
    '''        private static void DrawGridLines(\n''',
    '')

print('Phase C6 TimelineFrameStrip request coordinator extraction applied.')
