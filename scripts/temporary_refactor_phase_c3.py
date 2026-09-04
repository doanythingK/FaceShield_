from pathlib import Path


def read_exact(p: Path) -> str:
    with p.open('r', encoding='utf-8', newline='') as f:
        return f.read()


def write_exact(p: Path, text: str) -> None:
    with p.open('w', encoding='utf-8', newline='') as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = read_exact(p)
    candidates = [(old, new)]
    if '\n' in old:
        candidates.append((old.replace('\n', '\r\n'), new.replace('\n', '\r\n')))
    for a, b in candidates:
        count = text.count(a)
        if count == 1:
            write_exact(p, text.replace(a, b, 1))
            return
        if count > 1:
            raise RuntimeError(f'Expected one match in {path}, found {count}')
    raise RuntimeError(f'Patch target not found in {path}: {old[:120]!r}')


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    p = Path(path)
    text = read_exact(p)
    sep = '\r\n' if '\r\n' in text else '\n'
    sm = start_marker.replace('\n', sep)
    em = end_marker.replace('\n', sep)
    start = text.find(sm)
    if start < 0:
        raise RuntimeError(f'Start marker not found in {path}: {start_marker!r}')
    end = text.find(em, start)
    if end < 0:
        raise RuntimeError(f'End marker not found in {path}: {end_marker!r}')
    write_exact(p, text[:start] + replacement.replace('\n', sep) + text[end:])


write_exact(Path('ViewModels/Workspace/WorkspaceExportCoordinator.cs'), '''using FaceShield.Services.Analysis;
using FaceShield.Services.Diagnostics;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

internal sealed class WorkspaceExportCoordinator : IDisposable
{
    internal const string HybridCopyDisabledReason = "bitstream-compatibility-unverified";

    private readonly FrameMaskProvider _maskProvider;
    private readonly ToolPanelViewModel _toolPanel;
    private readonly Func<bool> _isAutoRunning;
    private readonly Func<bool> _tryBeginLifetimeOperation;
    private readonly Action _endLifetimeOperation;
    private readonly Func<string, Task<(string? Path, bool AllowOverwrite)>> _resolveOutputPathAsync;
    private readonly Action<AutoMaskRunSummary?, ExportRunSummary, bool, IReadOnlyList<string>?> _logQualityGate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<(DateTime Timestamp, int FrameIndex)> _etaSamples = new();
    private (DateTime Timestamp, int FrameIndex) _lastEtaSample;
    private CancellationTokenSource? _exportCts;
    private WorkspaceAutoExportGateState _gateState = new(
        Required: false,
        Passed: false,
        Failure: null,
        CompletedRunSummary: null,
        HybridPolicyAvailable: false,
        AllowHybridCopy: false,
        HybridDisableReasons: HybridCopyDisabledReason);
    private bool _disposed;

    internal WorkspaceExportCoordinator(
        FrameMaskProvider maskProvider,
        ToolPanelViewModel toolPanel,
        Func<bool> isAutoRunning,
        Func<bool> tryBeginLifetimeOperation,
        Action endLifetimeOperation,
        Func<string, Task<(string? Path, bool AllowOverwrite)>> resolveOutputPathAsync,
        Action<AutoMaskRunSummary?, ExportRunSummary, bool, IReadOnlyList<string>?> logQualityGate)
    {
        _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
        _toolPanel = toolPanel ?? throw new ArgumentNullException(nameof(toolPanel));
        _isAutoRunning = isAutoRunning ?? throw new ArgumentNullException(nameof(isAutoRunning));
        _tryBeginLifetimeOperation = tryBeginLifetimeOperation ?? throw new ArgumentNullException(nameof(tryBeginLifetimeOperation));
        _endLifetimeOperation = endLifetimeOperation ?? throw new ArgumentNullException(nameof(endLifetimeOperation));
        _resolveOutputPathAsync = resolveOutputPathAsync ?? throw new ArgumentNullException(nameof(resolveOutputPathAsync));
        _logQualityGate = logQualityGate ?? throw new ArgumentNullException(nameof(logQualityGate));
    }

    internal WorkspaceAutoExportGateState GateState => _gateState;

    internal void ApplyGateState(WorkspaceAutoExportGateState state)
        => _gateState = state ?? throw new ArgumentNullException(nameof(state));

    internal async Task<bool> ExportAsync(
        string input,
        int blurRadius,
        IProgress<ExportProgress>? exportProgress = null,
        CancellationToken cancellationToken = default,
        bool updateToolPanel = true,
        string? runId = null,
        AutoMaskRunSummary? autoRunSummary = null,
        AutoMaskOptions? autoRunOptions = null)
    {
        ThrowIfDisposed();
        if (!_tryBeginLifetimeOperation())
            return false;

        bool entered = false;
        try
        {
            entered = await _gate.WaitAsync(0).ConfigureAwait(false);
            if (!entered)
                return false;

            return await ExportCoreAsync(
                input,
                blurRadius,
                exportProgress,
                cancellationToken,
                updateToolPanel,
                runId,
                autoRunSummary,
                autoRunOptions).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                _gate.Release();
            _endLifetimeOperation();
        }
    }

    private async Task<bool> ExportCoreAsync(
        string input,
        int blurRadius,
        IProgress<ExportProgress>? exportProgress,
        CancellationToken cancellationToken,
        bool updateToolPanel,
        string? runId,
        AutoMaskRunSummary? autoRunSummary,
        AutoMaskOptions? autoRunOptions)
    {
        string exportRunId = string.IsNullOrWhiteSpace(runId)
            ? $"export-{Guid.NewGuid():N}"
            : runId;

        if (_isAutoRunning() && autoRunOptions == null)
        {
            const string reason = "auto-analysis-in-progress";
            string line = $"[ExportBlocked] runId={exportRunId}, reason={reason}";
            System.Diagnostics.Debug.WriteLine(line);
            RunMetricsLog.AppendRunLines(exportRunId, line);
            throw new InvalidOperationException(
                "자동 분석이 진행 중이므로 내보내기를 중단했습니다. 분석 완료 후 다시 시도해 주세요.");
        }

        AutoMaskRunSummary? effectiveAutoRunSummary =
            autoRunSummary ?? _gateState.CompletedRunSummary;
        string? cascadeFailure = null;
        string cascadeError = "n/a";
        if (autoRunOptions != null)
        {
            cascadeFailure = WorkspaceExportGatePolicy.GetRequiredYoloCascadeFailure(
                autoRunOptions,
                autoRunSummary);
            cascadeError = autoRunSummary?.YoloCascadeError ?? "summary-missing";
        }
        if (cascadeFailure == null && _gateState.Required && !_gateState.Passed)
        {
            cascadeFailure = string.IsNullOrWhiteSpace(_gateState.Failure)
                ? "persisted-auto-export-gate-failed"
                : _gateState.Failure;
            cascadeError = "persisted-gate";
        }
        if (cascadeFailure != null)
        {
            string line = $"[ExportBlocked] runId={exportRunId}, reason={cascadeFailure}, cascadeError={cascadeError}";
            System.Diagnostics.Debug.WriteLine(line);
            RunMetricsLog.AppendRunLines(exportRunId, line);
            throw new InvalidOperationException(
                $"자동 분석 품질 검증이 완료되지 않아 내보내기를 중단했습니다. reason={cascadeFailure}");
        }

        string output = BuildDefaultExportPath(input);
        (string? resolvedOutput, bool allowOutputOverwrite) =
            await _resolveOutputPathAsync(output);
        if (string.IsNullOrWhiteSpace(resolvedOutput))
            return false;
        output = resolvedOutput;

        using var exportMaskProvider = _maskProvider.CreateSnapshot();
        var exporter = new VideoExportService(exportMaskProvider);

        if (updateToolPanel)
        {
            _toolPanel.IsExportRunning = true;
            _toolPanel.ExportProgress = 0;
            _toolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
            _toolPanel.ExportStatusText = null;
        }
        _etaSamples.Clear();

        var progress = new Progress<ExportProgress>(p =>
        {
            exportProgress?.Report(p);
            if (!updateToolPanel)
                return;

            _toolPanel.ExportProgress = Math.Clamp(p.Percent, 0, 100);
            UpdateEta(DateTime.UtcNow, p.FrameIndex, p.TotalFrames);
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                _toolPanel.ExportStatusText = p.StatusMessage;
        });

        var exportCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        Interlocked.Exchange(ref _exportCts, exportCts);
        CancellationToken exportToken = exportCts.Token;

        try
        {
            (bool allowHybridCopy, IReadOnlyList<string> disableReasons) hybridPolicy = (
                false,
                new[] { HybridCopyDisabledReason });
            System.Diagnostics.Debug.WriteLine(
                $"[WorkspaceExportPolicy] runId={exportRunId}, autoRunSummary={(effectiveAutoRunSummary?.RunId ?? "n/a")}, persistedPolicy={(_gateState.HybridPolicyAvailable && effectiveAutoRunSummary == null).ToString().ToLowerInvariant()}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");
            RunMetricsLog.AppendRunLines(
                exportRunId,
                $"[ExportRunConfig] runId={exportRunId}, blurRadius={blurRadius}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");

            await Task.Run(() =>
            {
                exporter.Export(
                    input,
                    output,
                    blurRadius,
                    progress,
                    exportToken,
                    exportRunId,
                    allowHybridCopy: hybridPolicy.allowHybridCopy,
                    allowOutputOverwrite: allowOutputOverwrite);
            }, exportToken).ConfigureAwait(false);

            if (exporter.LastExportSummary != null)
            {
                System.Diagnostics.Debug.WriteLine($"[WorkspaceExport] {exporter.LastExportSummary.ToLogLine()}");
                _logQualityGate(
                    effectiveAutoRunSummary,
                    exporter.LastExportSummary,
                    hybridPolicy.allowHybridCopy,
                    hybridPolicy.disableReasons);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (updateToolPanel)
            {
                _toolPanel.IsExportRunning = false;
                _toolPanel.ExportProgress = 0;
                _toolPanel.ExportEtaText = null;
                _toolPanel.ExportStatusText = null;
            }
            Interlocked.CompareExchange(ref _exportCts, null, exportCts);
            exportCts.Dispose();
        }
    }

    internal void Cancel()
    {
        try { Volatile.Read(ref _exportCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void UpdateEta(DateTime timestamp, int frameIndex, int totalFrames)
    {
        if (totalFrames <= 0 || frameIndex <= 0)
        {
            if (string.IsNullOrWhiteSpace(_toolPanel.ExportEtaText))
                _toolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
            return;
        }
        if (frameIndex >= totalFrames)
        {
            _toolPanel.ExportEtaText = null;
            return;
        }
        if (_etaSamples.Count > 0 && frameIndex <= _lastEtaSample.FrameIndex)
            return;

        _etaSamples.Enqueue((timestamp, frameIndex));
        _lastEtaSample = (timestamp, frameIndex);
        while (_etaSamples.Count > 0 && (timestamp - _etaSamples.Peek().Timestamp).TotalSeconds > 10)
            _etaSamples.Dequeue();
        if (_etaSamples.Count < 2)
        {
            _toolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
            return;
        }

        var first = _etaSamples.Peek();
        double elapsedSeconds = (_lastEtaSample.Timestamp - first.Timestamp).TotalSeconds;
        int progressed = _lastEtaSample.FrameIndex - first.FrameIndex;
        if (elapsedSeconds <= 0 || progressed <= 0)
            return;
        double remaining = (totalFrames - frameIndex) / (progressed / elapsedSeconds);
        if (remaining >= 0)
            _toolPanel.ExportEtaText = $"예상 남은 시간: {FormatEta(TimeSpan.FromSeconds(remaining))}";
    }

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}시간 {Math.Max(0, remaining.Minutes)}분 {Math.Max(0, remaining.Seconds)}초";
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}분 {Math.Max(0, remaining.Seconds)}초";
        return $"{Math.Max(0, (int)remaining.TotalSeconds)}초";
    }

    private static string BuildDefaultExportPath(string inputPath)
    {
        string extension = Path.GetExtension(inputPath);
        string normalizedExtension = extension.ToLowerInvariant();
        if (normalizedExtension is not (".mp4" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".webm"))
            extension = ".mp4";
        string directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{baseName}_blur{extension}");
    }

    private static string FormatTextListForLog(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "none";
        const int maxValues = 12;
        string text = string.Join("|", values.Take(maxValues));
        return values.Count > maxValues ? $"{text}|...(+{values.Count - maxValues})" : text;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _gate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceExportCoordinator));
    }
}
''')

# Linq is needed by coordinator's log formatter.
p = Path('ViewModels/Workspace/WorkspaceExportCoordinator.cs')
text = read_exact(p)
text = text.replace('using System.IO;\n', 'using System.IO;\nusing System.Linq;\n')
write_exact(p, text)

# Fields: add coordinator and remove export-owned state.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private readonly IssueReviewCoordinator _issueReview;\n''',
    '''        private readonly IssueReviewCoordinator _issueReview;\n        private readonly WorkspaceExportCoordinator _exportCoordinator;\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _exportEtaSamples = new();\n        private (DateTime Timestamp, int FrameIndex) _exportLastSample;\n\n''',
    '')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private CancellationTokenSource? _exportCts;\n''',
    '')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private readonly SemaphoreSlim _exportGate = new(1, 1);\n        private bool _autoExportGateRequired;\n        private bool _autoExportGatePassed;\n        private string? _autoExportGateFailure;\n        private AutoMaskRunSummary? _lastCompletedAutoRunSummary;\n        private bool _autoExportHybridPolicyAvailable;\n        private const string HybridCopyDisabledReason = "bitstream-compatibility-unverified";\n        private bool _autoExportAllowHybridCopy;\n        private string? _autoExportHybridDisableReasons;\n''',
    '')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            _issueReview.StateChanged += ApplyIssueReviewState;\n            if (!deferSessionInit)\n''',
    '''            _issueReview.StateChanged += ApplyIssueReviewState;\n            _exportCoordinator = new WorkspaceExportCoordinator(\n                _maskProvider,\n                ToolPanel,\n                () => _isAutoRunning,\n                TryBeginLifetimeOperation,\n                EndLifetimeOperation,\n                ResolveExportOutputPathAsync,\n                LogExportQualityGate);\n            if (!deferSessionInit)\n''')

# Replace export execution methods; keep static quality logging below.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private async Task<bool> SaveVideoAsync(\n''',
    '''        private static void LogExportQualityGate(\n''',
    '''        private Task<bool> SaveVideoAsync(\n            IProgress<ExportProgress>? exportProgress = null,\n            CancellationToken cancellationToken = default,\n            bool updateToolPanel = true,\n            string? runId = null,\n            AutoMaskRunSummary? autoRunSummary = null,\n            AutoMaskOptions? autoRunOptions = null)\n        {\n            return _exportCoordinator.ExportAsync(\n                FrameList.VideoPath,\n                ToolPanel.BlurRadius,\n                exportProgress,\n                cancellationToken,\n                updateToolPanel,\n                runId,\n                autoRunSummary,\n                autoRunOptions);\n        }\n\n''')

# Remove duplicate path builder now owned by coordinator; keep conflict dialog resolver and unique naming.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private static string BuildDefaultExportPath(string inputPath)\n''',
    '''        private IFaceDetectorFactory CreateFaceDetectorFactory()\n''',
    '''''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private void OnExportCancelRequested()\n        {\n            _exportCts?.Cancel();\n        }\n''',
    '''        private void OnExportCancelRequested()\n        {\n            _exportCoordinator.Cancel();\n        }\n''')

# Remove ETA helpers from VM.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private void UpdateExportEta(DateTime timestamp, int frameIndex, int totalFrames)\n''',
    '''        [RelayCommand]\n        private async Task GoBack()\n''',
    '''''')

# Gate state now belongs to export coordinator.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private void ApplyAutoExportGateState(\n            WorkspaceAutoExportGateState state)\n        {\n            _autoExportGateRequired = state.Required;\n            _autoExportGatePassed = state.Passed;\n            _autoExportGateFailure = state.Failure;\n            _lastCompletedAutoRunSummary = state.CompletedRunSummary;\n            _autoExportHybridPolicyAvailable = state.HybridPolicyAvailable;\n            _autoExportAllowHybridCopy = state.AllowHybridCopy;\n            _autoExportHybridDisableReasons = state.HybridDisableReasons;\n        }\n''',
    '''        private void ApplyAutoExportGateState(\n            WorkspaceAutoExportGateState state)\n        {\n            _exportCoordinator.ApplyGateState(state);\n        }\n''')

# Snapshot capture reads persisted export state from the coordinator.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            var state = new WorkspaceStateCapture(\n                FrameList.VideoPath,\n''',
    '''            WorkspaceAutoExportGateState exportState = _exportCoordinator.GateState;\n            var state = new WorkspaceStateCapture(\n                FrameList.VideoPath,\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                _autoExportGateRequired,\n                _autoExportGatePassed,\n                _autoExportGateFailure,\n                _autoExportHybridPolicyAvailable,\n                _autoExportAllowHybridCopy,\n                _autoExportHybridDisableReasons);\n''',
    '''                exportState.Required,\n                exportState.Passed,\n                exportState.Failure,\n                exportState.HybridPolicyAvailable,\n                exportState.AllowHybridCopy,\n                exportState.HybridDisableReasons);\n''')

# Replace hybrid constant references throughout VM.
p = Path('ViewModels/Pages/WorkspaceViewModel.cs')
text = read_exact(p)
text = text.replace('HybridCopyDisabledReason', 'WorkspaceExportCoordinator.HybridCopyDisabledReason')
# Dispose/cancel ownership moves to coordinator.
text = text.replace('            try { _exportCts?.Cancel(); }\n            catch { }\n', '            _exportCoordinator.Cancel();\n')
text = text.replace('            _exportGate.Dispose();\n', '            _exportCoordinator.Dispose();\n')
write_exact(p, text)

print('Phase C3 export coordinator extraction applied.')
