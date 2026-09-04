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
    raise RuntimeError(f'Patch target not found in {path}: {old[:160]!r}')


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


write_exact(Path('ViewModels/Workspace/AutoMaskRunCoordinator.cs'), '''using Avalonia.Threading;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Analysis;
using FaceShield.Services.Diagnostics;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

internal sealed record AutoMaskRunStateSnapshot(
    int ResumeIndex,
    bool Completed,
    string? RunSignature,
    string? ExecutionSignature,
    int LastProcessedFrame,
    DateTime LastProcessedAtUtc);

internal sealed class AutoMaskRunCoordinator : IDisposable
{
    private const float LowConfidenceMargin = 0.05f;
    private const int AutoDetectionCompletionTailToleranceFrames = 0;

    private readonly WorkspaceMode _mode;
    private readonly FrameMaskProvider _maskProvider;
    private readonly FrameListViewModel _frameList;
    private readonly FramePreviewViewModel _framePreview;
    private readonly ToolPanelViewModel _toolPanel;
    private readonly IssueReviewCoordinator _issueReview;
    private readonly WorkspaceExportCoordinator _exportCoordinator;
    private readonly Func<AutoMaskOptions> _getAutoOptions;
    private readonly Func<FaceOnnxDetectorOptions> _getDetectorOptions;
    private readonly Func<FaceDetectorFactoryOptions> _getDetectorFactoryOptions;
    private readonly Func<bool> _getHideResolvedIssues;
    private readonly Func<bool> _tryBeginLifetimeOperation;
    private readonly Action _endLifetimeOperation;
    private readonly Action<bool> _persistWorkspaceState;

    private CancellationTokenSource? _autoCts;
    private long _lastPreviewTick;
    private bool _previewNeedsExactRefresh;
    private bool _disposed;

    internal AutoMaskRunCoordinator(
        WorkspaceMode mode,
        FrameMaskProvider maskProvider,
        FrameListViewModel frameList,
        FramePreviewViewModel framePreview,
        ToolPanelViewModel toolPanel,
        IssueReviewCoordinator issueReview,
        WorkspaceExportCoordinator exportCoordinator,
        Func<AutoMaskOptions> getAutoOptions,
        Func<FaceOnnxDetectorOptions> getDetectorOptions,
        Func<FaceDetectorFactoryOptions> getDetectorFactoryOptions,
        Func<bool> getHideResolvedIssues,
        Func<bool> tryBeginLifetimeOperation,
        Action endLifetimeOperation,
        Action<bool> persistWorkspaceState)
    {
        _mode = mode;
        _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
        _frameList = frameList ?? throw new ArgumentNullException(nameof(frameList));
        _framePreview = framePreview ?? throw new ArgumentNullException(nameof(framePreview));
        _toolPanel = toolPanel ?? throw new ArgumentNullException(nameof(toolPanel));
        _issueReview = issueReview ?? throw new ArgumentNullException(nameof(issueReview));
        _exportCoordinator = exportCoordinator ?? throw new ArgumentNullException(nameof(exportCoordinator));
        _getAutoOptions = getAutoOptions ?? throw new ArgumentNullException(nameof(getAutoOptions));
        _getDetectorOptions = getDetectorOptions ?? throw new ArgumentNullException(nameof(getDetectorOptions));
        _getDetectorFactoryOptions = getDetectorFactoryOptions ?? throw new ArgumentNullException(nameof(getDetectorFactoryOptions));
        _getHideResolvedIssues = getHideResolvedIssues ?? throw new ArgumentNullException(nameof(getHideResolvedIssues));
        _tryBeginLifetimeOperation = tryBeginLifetimeOperation ?? throw new ArgumentNullException(nameof(tryBeginLifetimeOperation));
        _endLifetimeOperation = endLifetimeOperation ?? throw new ArgumentNullException(nameof(endLifetimeOperation));
        _persistWorkspaceState = persistWorkspaceState ?? throw new ArgumentNullException(nameof(persistWorkspaceState));
    }

    internal bool IsRunning { get; private set; }
    internal int ResumeIndex { get; private set; }
    internal bool Completed { get; private set; }
    internal string? RunSignature { get; private set; }
    internal string? ExecutionSignature { get; private set; }
    internal int LastProcessedFrame { get; private set; } = -1;
    internal DateTime LastProcessedAtUtc { get; private set; } = DateTime.MinValue;
    internal string? ExecutionProviderLabel { get; private set; }
    internal string? ExecutionProviderError { get; private set; }

    internal AutoMaskRunStateSnapshot CreateStateSnapshot()
        => new(
            ResumeIndex,
            Completed,
            RunSignature,
            ExecutionSignature,
            LastProcessedFrame,
            LastProcessedAtUtc);

    internal void RestoreState(
        int resumeIndex,
        bool completed,
        string? runSignature,
        string? executionSignature)
    {
        ResumeIndex = Math.Max(0, resumeIndex);
        Completed = completed;
        RunSignature = runSignature;
        ExecutionSignature = executionSignature;
    }

    internal bool NeedsResumePrompt()
    {
        if (_mode != WorkspaceMode.Auto || Completed || ResumeIndex <= 0)
            return false;

        AutoMaskOptions options = _getAutoOptions();
        FaceOnnxDetectorOptions detectorOptions = _getDetectorOptions();
        FaceDetectorFactoryOptions factoryOptions = _getDetectorFactoryOptions();
        string intentSignature = AutoRunSignaturePolicy.BuildIntentSignature(
            options,
            detectorOptions,
            factoryOptions);
        return !AutoRunSignaturePolicy.RequiresCompleteTimeline(options, factoryOptions) &&
               AutoMaskGenerator.CanResumeFromFrame(options, ResumeIndex) &&
               IsResumeSignatureCurrent(intentSignature);
    }

    internal void MarkPreviewNeedsExactRefresh()
        => _previewNeedsExactRefresh = true;

    internal Task<bool> RunAsync(
        bool exportAfter,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? exportProgress = null)
    {
        ThrowIfDisposed();
        if (IsRunning || !_tryBeginLifetimeOperation())
            return Task.FromResult(false);

        try
        {
            _framePreview.PersistCurrentMask();
            ExecutionProviderLabel = null;
            ExecutionProviderError = null;
            IsRunning = true;
            _autoCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();

            _toolPanel.IsAutoRunning = true;
            if (!exportAfter)
                _toolPanel.AutoProgress = 0;

            return RunTrackedAsync(exportAfter, progress, exportProgress);
        }
        catch
        {
            _autoCts?.Dispose();
            _autoCts = null;
            IsRunning = false;
            _toolPanel.IsAutoRunning = false;
            _endLifetimeOperation();
            throw;
        }
    }

    private async Task<bool> RunTrackedAsync(
        bool exportAfter,
        IProgress<int>? progress,
        IProgress<ExportProgress>? exportProgress)
    {
        try
        {
            return await RunCoreAsync(exportAfter, progress, exportProgress);
        }
        finally
        {
            _endLifetimeOperation();
        }
    }

    private async Task<bool> RunCoreAsync(
        bool exportAfter,
        IProgress<int>? progress,
        IProgress<ExportProgress>? exportProgress)
    {
        bool persisted = false;
        bool postProcessCommitted = false;
        bool autoAnalysisCompleted = false;
        bool exactFrameOperationsSuspended = false;
        bool timelineOperationsSuspended = false;
        bool restorePlaybackEnabled = _frameList.IsPlaybackEnabled;
        string runId = $"auto-{Guid.NewGuid():N}";
        _frameList.SetPlaybackEnabled(false);

        try
        {
            await _framePreview.StopPlaybackAndWaitAsync();
            await _framePreview.SuspendExactFrameOperationsAndWaitAsync();
            exactFrameOperationsSuspended = true;
            await _frameList.SuspendTimelineOperationsAndWaitAsync();
            timelineOperationsSuspended = true;

            _framePreview.PersistCurrentMask();
            _previewNeedsExactRefresh = false;

            FaceOnnxDetectorOptions detectorOptions = _getDetectorOptions();
            AutoMaskOptions effectiveAutoOptions = _getAutoOptions().ResolveProcessingMode();
            FaceDetectorFactoryOptions configuredFactoryOptions = _getDetectorFactoryOptions();
            FaceDetectorFactoryOptions detectorFactoryOptions = AutoRunSignaturePolicy.ResolveDetectorFactoryOptions(
                effectiveAutoOptions,
                detectorOptions,
                configuredFactoryOptions,
                out FaceOnnxDetectorOptions? yoloSecondaryOptions);
            string runSignature = AutoRunSignaturePolicy.BuildRunSignature(
                effectiveAutoOptions,
                detectorFactoryOptions);

            int tunedSessions = Math.Max(1, effectiveAutoOptions.ParallelDetectorCount);
            if (configuredFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx &&
                detectorOptions.AllowAutoTune != false)
            {
                CancellationToken tuneToken = _autoCts?.Token ?? CancellationToken.None;
                var tuneResult = await Task.Run(() =>
                {
                    bool tuned = DetectorAutoTuner.TryTune(
                        _frameList.VideoPath,
                        effectiveAutoOptions.DownscaleRatio,
                        effectiveAutoOptions.DownscaleQuality,
                        detectorOptions,
                        tunedSessions,
                        detectorOptions.AllowAutoGpu == true,
                        tuneToken,
                        out var tunedOptions,
                        out var tunedCount,
                        out var tuneLabel);
                    return (tuned, tunedOptions, tunedCount, tuneLabel);
                }, tuneToken);

                if (tuneToken.IsCancellationRequested)
                    return false;

                if (tuneResult.tuned)
                {
                    detectorOptions = tuneResult.tunedOptions;
                    tunedSessions = Math.Max(1, tuneResult.tunedCount);
                    System.Diagnostics.Debug.WriteLine($"[AutoTune] applied {tuneResult.tuneLabel}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AutoTune] skipped; using configured detector options.");
                }

                detectorFactoryOptions = detectorFactoryOptions.WithFaceOnnxOptions(detectorOptions);
            }

            AutoMaskOptions runOptions = BuildRunOptions(
                effectiveAutoOptions,
                detectorOptions,
                detectorFactoryOptions,
                yoloSecondaryOptions,
                tunedSessions,
                runId);
            var detectorFactory = new FaceDetectorFactory(detectorFactoryOptions);
            using IFaceDetector detector = detectorFactory.CreateDetector();
            ExecutionProviderLabel = AutoRunSignaturePolicy.GetExecutionProviderLabel(detector);
            ExecutionProviderError = detector switch
            {
                FaceOnnxDetector faceOnnx => faceOnnx.ExecutionProviderError,
                YoloFaceOnnxDetector yoloOnnx => yoloOnnx.ExecutionProviderError,
                _ => null
            };

            string sourceEvidenceId = AutoRunSignaturePolicy.BuildSourceEvidenceId(_frameList.VideoPath);
            string executionSignature = AutoRunSignaturePolicy.BuildExecutionSignature(
                runOptions,
                detectorFactoryOptions,
                AutoRunSignaturePolicy.GetExecutionProviderLabel(detector),
                sourceEvidenceId);
            RunMetricsLog.AppendRunLines(
                runId,
                $"[AutoRunConfig] runId={runId}, sourceId={sourceEvidenceId}, totalFrames={_frameList.TotalFrames}, signature={AutoRunSignaturePolicy.BuildEvidenceSignature(runOptions, detectorFactoryOptions)}, executionSignature={executionSignature}");

            var generator = new AutoMaskGenerator(
                detector,
                _maskProvider,
                runOptions,
                detectorFactory);
            ResetStaleResumeIfRunChanged(runSignature, executionSignature);
            RunSignature = runSignature;
            ExecutionSignature = executionSignature;
            Completed = false;
            int lastProcessed = Math.Max(0, ResumeIndex);
            if ((AutoRunSignaturePolicy.RequiresCompleteTimeline(runOptions, detectorFactoryOptions) ||
                 !AutoMaskGenerator.CanResumeFromFrame(runOptions, lastProcessed)) &&
                lastProcessed > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AutoMaskResumeReset] reason=resume-requires-complete-timeline resumeIndex={lastProcessed}");
                lastProcessed = 0;
                ResumeIndex = 0;
            }

            _exportCoordinator.ApplyGateState(
                WorkspaceExportGatePolicy.Begin(WorkspaceExportCoordinator.HybridCopyDisabledReason));
            ResetAutoFaceMasksForRun(lastProcessed);

            var effectiveProgress = new Progress<int>(p =>
            {
                progress?.Report(p);
                if (!exportAfter)
                    _toolPanel.AutoProgress = p;
            });
            CancellationToken token = _autoCts?.Token ?? CancellationToken.None;
            await generator.GenerateAsync(
                _frameList.VideoPath,
                effectiveProgress,
                token,
                startFrameIndex: lastProcessed,
                onFrameProcessed: idx =>
                {
                    lastProcessed = idx;
                    ResumeIndex = idx;
                    LastProcessedFrame = idx;
                    LastProcessedAtUtc = DateTime.UtcNow;
                    if (!exportAfter)
                        TryUpdatePreview(idx);
                });

            if (generator.LastRunSummary != null)
                System.Diagnostics.Debug.WriteLine($"[WorkspaceAuto] {generator.LastRunSummary.ToLogLine()}");
            SynchronizeFrameListWithDecodedTimeline(generator.LastRunSummary);

            if (!IsDetectionRunComplete(generator.LastRunSummary, lastProcessed, _frameList.TotalFrames))
            {
                Completed = false;
                ResumeIndex = Math.Clamp(lastProcessed, 0, Math.Max(0, _frameList.TotalFrames - 1));
                System.Diagnostics.Debug.WriteLine(
                    $"[AutoMaskPostProcessSkipped] reason=incomplete totalFrames={_frameList.TotalFrames} startFrame={generator.LastRunSummary?.StartFrameIndex ?? lastProcessed} processed={generator.LastRunSummary?.ProcessedFrames ?? 0} decoded={generator.LastRunSummary?.DecodedFrames ?? 0} decodeEof={generator.LastRunSummary?.ReachedDecoderEof.ToString().ToLowerInvariant() ?? "false"} decodeCancelled={generator.LastRunSummary?.DecodeCancelled.ToString().ToLowerInvariant() ?? "false"} decodeError={generator.LastRunSummary?.DecodeError ?? "summary-missing"} lastProcessed={lastProcessed} resumeIndex={ResumeIndex}");
                _persistWorkspaceState(!exportAfter);
                persisted = true;
                return false;
            }

            postProcessCommitted = true;
            ResumeIndex = 0;
            token.ThrowIfCancellationRequested();
            _exportCoordinator.ApplyGateState(
                WorkspaceExportGatePolicy.Complete(
                    runOptions,
                    generator.LastRunSummary,
                    WorkspaceExportCoordinator.HybridCopyDisabledReason));
            RefreshPreviewAfterPostProcess(exportAfter);

            if (!exportAfter)
                await BuildAnomaliesAsync(token);
            token.ThrowIfCancellationRequested();

            Completed = true;
            ResumeIndex = 0;
            autoAnalysisCompleted = true;

            if (exportAfter)
            {
                string? cascadeFailure = WorkspaceExportGatePolicy.GetRequiredYoloCascadeFailure(
                    runOptions,
                    generator.LastRunSummary);
                if (cascadeFailure != null)
                {
                    string cascadeError = generator.LastRunSummary?.YoloCascadeError ?? "summary-missing";
                    string line = $"[AutoExportBlocked] runId={runId}, reason={cascadeFailure}, cascadeError={cascadeError}";
                    System.Diagnostics.Debug.WriteLine(line);
                    RunMetricsLog.AppendRunLines(runId, line);
                    _persistWorkspaceState(false);
                    persisted = true;
                    return false;
                }

                bool exported = await _exportCoordinator.ExportAsync(
                    _frameList.VideoPath,
                    _toolPanel.BlurRadius,
                    exportProgress,
                    _autoCts?.Token ?? CancellationToken.None,
                    updateToolPanel: false,
                    runId: runId,
                    autoRunSummary: generator.LastRunSummary,
                    autoRunOptions: runOptions);
                if (!exported)
                {
                    _persistWorkspaceState(false);
                    persisted = true;
                    return false;
                }
            }

            _persistWorkspaceState(!exportAfter);
            persisted = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            Completed = autoAnalysisCompleted;
            if (postProcessCommitted)
                ResumeIndex = 0;
            _persistWorkspaceState(!exportAfter);
            persisted = true;
            return false;
        }
        finally
        {
            _autoCts?.Dispose();
            _autoCts = null;
            IsRunning = false;
            _toolPanel.IsAutoRunning = false;
            if (timelineOperationsSuspended)
            {
                _frameList.ResumeTimelineOperations();
                if (!exportAfter)
                {
                    _issueReview.RefreshTimes(
                        _frameList.ThumbnailProvider,
                        _frameList.SelectedFrameIndex,
                        _frameList.Fps,
                        _frameList.SecondsPerScreen);
                }
            }
            if (exactFrameOperationsSuspended)
                _framePreview.ResumeExactFrameOperations();
            _frameList.SetPlaybackEnabled(restorePlaybackEnabled);
            if (!exportAfter &&
                _previewNeedsExactRefresh &&
                _frameList.SelectedFrameIndex >= 0)
            {
                _previewNeedsExactRefresh = false;
                _framePreview.OnFrameIndexChanged(_frameList.SelectedFrameIndex);
            }
            if (!persisted)
                _persistWorkspaceState(!exportAfter);
        }
    }

    internal Task<bool> RunSingleFrameAsync()
    {
        ThrowIfDisposed();
        int frameIndex = _frameList.SelectedFrameIndex;
        if (frameIndex < 0 || !_tryBeginLifetimeOperation())
            return Task.FromResult(false);
        return RunTrackedSingleFrameAsync(frameIndex);
    }

    private async Task<bool> RunTrackedSingleFrameAsync(int frameIndex)
    {
        try
        {
            return await RunSingleFrameCoreAsync(frameIndex);
        }
        finally
        {
            _endLifetimeOperation();
        }
    }

    private async Task<bool> RunSingleFrameCoreAsync(int frameIndex)
    {
        if (IsRunning)
            return false;

        _framePreview.PersistCurrentMask();
        ExecutionProviderLabel = null;
        ExecutionProviderError = null;
        IsRunning = true;
        _autoCts = new CancellationTokenSource();
        bool refreshPreviewAfterAuto = false;
        bool exactFrameOperationsSuspended = false;
        bool timelineOperationsSuspended = false;
        bool restorePlaybackEnabled = _frameList.IsPlaybackEnabled;

        _toolPanel.IsAutoRunning = true;
        _toolPanel.AutoProgress = 0;
        _frameList.SetPlaybackEnabled(false);
        try
        {
            await _framePreview.StopPlaybackAndWaitAsync();
            await _framePreview.SuspendExactFrameOperationsAndWaitAsync();
            exactFrameOperationsSuspended = true;
            await _frameList.SuspendTimelineOperationsAndWaitAsync();
            timelineOperationsSuspended = true;

            var detectorFactory = new FaceDetectorFactory(_getDetectorFactoryOptions());
            using IFaceDetector detector = detectorFactory.CreateDetector();
            var generator = new AutoMaskGenerator(
                detector,
                _maskProvider,
                _getAutoOptions(),
                detectorFactory);
            var effectiveProgress = new Progress<int>(p => _toolPanel.AutoProgress = p);
            CancellationToken token = _autoCts?.Token ?? CancellationToken.None;
            bool generated = await generator.GenerateFrameAsync(
                _frameList.VideoPath,
                frameIndex,
                effectiveProgress,
                token);
            if (!generated || token.IsCancellationRequested)
            {
                _persistWorkspaceState(true);
                return false;
            }

            refreshPreviewAfterAuto = true;
            LastProcessedFrame = frameIndex;
            LastProcessedAtUtc = DateTime.UtcNow;
            _persistWorkspaceState(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            _persistWorkspaceState(true);
            return false;
        }
        finally
        {
            _autoCts?.Dispose();
            _autoCts = null;
            IsRunning = false;
            _toolPanel.IsAutoRunning = false;
            if (timelineOperationsSuspended)
                _frameList.ResumeTimelineOperations();
            if (exactFrameOperationsSuspended)
                _framePreview.ResumeExactFrameOperations();
            _frameList.SetPlaybackEnabled(restorePlaybackEnabled);
            if (refreshPreviewAfterAuto)
                _framePreview.OnFrameIndexChanged(frameIndex);
            _persistWorkspaceState(true);
        }
    }

    internal void Cancel()
    {
        try { Volatile.Read(ref _autoCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private AutoMaskOptions BuildRunOptions(
        AutoMaskOptions effective,
        FaceOnnxDetectorOptions detectorOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions,
        FaceOnnxDetectorOptions? yoloSecondaryOptions,
        int tunedSessions,
        string runId)
    {
        bool useAllPostProcessModules = effective.ProcessingMode == AutoMaskProcessingMode.Full;
        return new AutoMaskOptions
        {
            ProcessingMode = effective.ProcessingMode,
            DownscaleRatio = effective.DownscaleRatio,
            DownscaleQuality = effective.DownscaleQuality,
            EnablePostProcessing = effective.EnablePostProcessing,
            EnableRoiPostProcess = useAllPostProcessModules || effective.EnableRoiPostProcess,
            EnableYoloWeakIsolatedCleanup = useAllPostProcessModules || effective.EnableYoloWeakIsolatedCleanup,
            EnableYoloGapFill = useAllPostProcessModules || effective.EnableYoloGapFill,
            EnableYoloSceneCutCarryCleanup = useAllPostProcessModules || effective.EnableYoloSceneCutCarryCleanup,
            EnableYoloTemporalSmoothing = useAllPostProcessModules || effective.EnableYoloTemporalSmoothing,
            EnableYoloRiskCascade = effective.EnableYoloRiskCascade,
            YoloStrongFullScanIntervalSeconds = effective.YoloStrongFullScanIntervalSeconds,
            YoloRiskLowConfidenceThreshold = effective.YoloRiskLowConfidenceThreshold,
            YoloRiskSmallFaceAreaRatio = effective.YoloRiskSmallFaceAreaRatio,
            YoloRiskEdgeMarginRatio = effective.YoloRiskEdgeMarginRatio,
            YoloRiskMaxTrackGapFrames = effective.YoloRiskMaxTrackGapFrames,
            YoloStrongConfirmationFrames = effective.YoloStrongConfirmationFrames,
            EnableYoloPrimaryRoiShortcut = effective.EnableYoloPrimaryRoiShortcut,
            YoloSecondaryDetectorOptions = yoloSecondaryOptions,
            RoiRefinerDetectorOptions = useAllPostProcessModules || effective.EnableRoiPostProcess
                ? detectorOptions
                : null,
            UseFaceOnnxRoiRefiner = detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx,
            UseTracking = effective.UseTracking,
            DetectEveryNFrames = effective.DetectEveryNFrames,
            ParallelDetectorCount = tunedSessions,
            RunId = runId,
            FilterProfile = effective.FilterProfile,
            DumpDetectionDiagnostics = effective.DumpDetectionDiagnostics
        }.ResolveProcessingMode();
    }

    private async Task BuildAnomaliesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoMaskOptions options = _getAutoOptions();
        await _issueReview.BuildAsync(
            _frameList.TotalFrames,
            GetLowConfidenceCutoff(),
            options.FilterProfile,
            _frameList.ThumbnailProvider,
            _frameList.SelectedFrameIndex,
            _frameList.Fps,
            _frameList.SecondsPerScreen,
            _getHideResolvedIssues(),
            cancellationToken);
    }

    private float GetLowConfidenceCutoff()
    {
        var defaults = FaceOnnxDetector.GetDefaultThresholds();
        FaceDetectorFactoryOptions factory = _getDetectorFactoryOptions();
        FaceOnnxDetectorOptions detector = _getDetectorOptions();
        float baseThreshold = factory.Backend switch
        {
            FaceDetectorBackend.YoloFaceOnnx when factory.YoloFaceOnnxOptions != null =>
                factory.YoloFaceOnnxOptions.ConfidenceThreshold,
            FaceDetectorBackend.ScrfdOnnx when factory.ScrfdOnnxOptions != null =>
                factory.ScrfdOnnxOptions.ConfidenceThreshold,
            FaceDetectorBackend.YuNetOnnx when factory.YuNetOnnxOptions != null =>
                factory.YuNetOnnxOptions.ConfidenceThreshold,
            FaceDetectorBackend.FaceOnnx when factory.FaceOnnxOptions != null =>
                factory.FaceOnnxOptions.ConfidenceThreshold ?? defaults.Confidence,
            _ => detector.ConfidenceThreshold ?? defaults.Confidence
        };
        return Math.Clamp(baseThreshold + LowConfidenceMargin, 0.0f, 0.99f);
    }

    private bool IsResumeSignatureCurrent(string currentSignature)
        => !string.IsNullOrWhiteSpace(RunSignature) &&
           string.Equals(RunSignature, currentSignature, StringComparison.Ordinal);

    private void ResetStaleResumeIfRunChanged(
        string currentRunSignature,
        string currentExecutionSignature)
    {
        string? reason = AutoRunSignaturePolicy.GetResumeResetReason(
            ResumeIndex,
            RunSignature,
            currentRunSignature,
            ExecutionSignature,
            currentExecutionSignature);
        if (reason == null)
            return;

        System.Diagnostics.Debug.WriteLine(
            $"[AutoMaskResumeReset] reason={reason} resumeIndex={ResumeIndex}");
        ResumeIndex = 0;
        Completed = false;
    }

    private void ResetAutoFaceMasksForRun(int startFrameIndex)
    {
        if (startFrameIndex <= 0)
        {
            _maskProvider.ClearFaceMasks();
            return;
        }
        int removed = _maskProvider.RemoveFaceMasksFrom(startFrameIndex);
        if (removed > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskResumeReset] start={startFrameIndex} removedStaleFaceMasks={removed}");
        }
    }

    private static bool IsDetectionRunComplete(
        AutoMaskRunSummary? summary,
        int lastProcessedFrame,
        int workspaceTotalFrames)
    {
        if (summary == null ||
            !summary.ReachedDecoderEof ||
            summary.DecodeCancelled ||
            !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int totalFrames = summary.TotalFrames > 0 ? summary.TotalFrames : workspaceTotalFrames;
        if (totalFrames <= 0)
            return false;
        int completionFrame = Math.Max(0, totalFrames - 1 - AutoDetectionCompletionTailToleranceFrames);
        if (lastProcessedFrame >= completionFrame)
            return true;
        if (summary.ProcessedFrames <= 0)
            return false;
        return summary.StartFrameIndex + summary.ProcessedFrames - 1 >= completionFrame;
    }

    private void SynchronizeFrameListWithDecodedTimeline(AutoMaskRunSummary? summary)
    {
        if (summary == null ||
            !summary.ReachedDecoderEof ||
            summary.DecodeCancelled ||
            !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int previousTotalFrames = _frameList.TotalFrames;
        _frameList.UpdateActualTotalFrames(summary.TotalFrames);
        if (previousTotalFrames != _frameList.TotalFrames)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskTimelineAdjusted] reported={previousTotalFrames} actual={_frameList.TotalFrames}");
        }
    }

    private void TryUpdatePreview(int frameIndex)
    {
        if (_mode != WorkspaceMode.Auto)
            return;
        long now = Environment.TickCount64;
        if (now - _lastPreviewTick < 200)
            return;
        _lastPreviewTick = now;

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsRunning || _frameList.SelectedFrameIndex == frameIndex)
                return;
            _frameList.SelectedFrameIndex = frameIndex;
        });
    }

    private void RefreshPreviewAfterPostProcess(bool exportAfter)
    {
        if (exportAfter || _frameList.SelectedFrameIndex < 0)
            return;
        _previewNeedsExactRefresh = false;
        _framePreview.OnFrameIndexChanged(_frameList.SelectedFrameIndex);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AutoMaskRunCoordinator));
    }
}
''')

# Add coordinator field and remove auto-owned fields/state.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private readonly WorkspaceExportCoordinator _exportCoordinator;\n''',
    '''        private readonly WorkspaceExportCoordinator _exportCoordinator;\n        private readonly AutoMaskRunCoordinator _autoRunCoordinator;\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private const float LowConfidenceMargin = 0.05f;\n        private const int AutoDetectionCompletionTailToleranceFrames = 0;\n        private int _autoResumeIndex;\n        private bool _autoCompleted;\n        private string? _autoRunSignature;\n        private string? _autoExecutionSignature;\n        private int _autoLastProcessedFrame = -1;\n        private DateTime _autoLastProcessedAtUtc = DateTime.MinValue;\n''',
    '')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        // 🔹 자동 분석 상태 관리용 (최소한 재진입 방지)\n        private bool _isAutoRunning;\n        private long _autoLastPreviewTick;\n        private bool _autoPreviewNeedsExactRefresh;\n        private CancellationTokenSource? _autoCts;\n        private CancellationTokenSource? _sessionInitCts;\n''',
    '''        private CancellationTokenSource? _sessionInitCts;\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        public bool NeedsAutoResumePrompt =>\n            Mode == WorkspaceMode.Auto &&\n            !_autoCompleted &&\n            _autoResumeIndex > 0 &&\n            !AutoRunSignaturePolicy.RequiresCompleteTimeline(_autoOptions, _detectorFactoryOptions) &&\n            AutoMaskGenerator.CanResumeFromFrame(_autoOptions, _autoResumeIndex) &&\n            IsAutoResumeSignatureCurrent(AutoRunSignaturePolicy.BuildIntentSignature(\n                _autoOptions,\n                _detectorOptions,\n                _detectorFactoryOptions));\n\n        public int AutoLastProcessedFrame => _autoLastProcessedFrame;\n        public DateTime AutoLastProcessedAtUtc => _autoLastProcessedAtUtc;\n        public string? AutoExecutionProviderLabel { get; private set; }\n        public string? AutoExecutionProviderError { get; private set; }\n''',
    '''        public bool NeedsAutoResumePrompt => _autoRunCoordinator.NeedsResumePrompt();\n\n        public int AutoLastProcessedFrame => _autoRunCoordinator.LastProcessedFrame;\n        public DateTime AutoLastProcessedAtUtc => _autoRunCoordinator.LastProcessedAtUtc;\n        public string? AutoExecutionProviderLabel => _autoRunCoordinator.ExecutionProviderLabel;\n        public string? AutoExecutionProviderError => _autoRunCoordinator.ExecutionProviderError;\n''')

# Export coordinator callback no longer depends on VM auto field.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                () => _isAutoRunning,\n''',
    '''                () => _autoRunCoordinator?.IsRunning == true,\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                ResolveExportOutputPathAsync,\n                LogExportQualityGate);\n            if (!deferSessionInit)\n''',
    '''                ResolveExportOutputPathAsync,\n                LogExportQualityGate);\n            _autoRunCoordinator = new AutoMaskRunCoordinator(\n                Mode,\n                _maskProvider,\n                FrameList,\n                FramePreview,\n                ToolPanel,\n                _issueReview,\n                _exportCoordinator,\n                () => _autoOptions,\n                () => _detectorOptions,\n                () => _detectorFactoryOptions,\n                () => HideResolvedIssues,\n                TryBeginLifetimeOperation,\n                EndLifetimeOperation,\n                PersistWorkspaceState);\n            if (!deferSessionInit)\n''')

# Selection change only marks exact refresh while coordinator is active.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                    if (_isAutoRunning && Mode == WorkspaceMode.Auto)\n                    {\n                        // 분석 중에는 타임라인 번호만 갱신합니다. 정확 프레임 seek는\n                        // 종료 시 한 번 수행하고, FramePreview의 편집 대상 인덱스는\n                        // 현재 표시 중인 exact 프레임에 그대로 유지합니다.\n                        _autoPreviewNeedsExactRefresh = true;\n                    }\n''',
    '''                    if (_autoRunCoordinator.IsRunning && Mode == WorkspaceMode.Auto)\n                    {\n                        // 분석 중에는 타임라인 번호만 갱신합니다. 정확 프레임 seek는\n                        // 종료 시 한 번 수행하고, FramePreview의 편집 대상 인덱스는\n                        // 현재 표시 중인 exact 프레임에 그대로 유지합니다.\n                        _autoRunCoordinator.MarkPreviewNeedsExactRefresh();\n                    }\n''')

# Replace the main auto run business block with a thin delegation.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private IFaceDetectorFactory CreateFaceDetectorFactory()\n''',
    '''        partial void OnAutoAnomalyCountChanged(int value)\n''',
    '''        public Task<bool> RunAutoAsync(\n            bool exportAfter,\n            IProgress<int>? progress = null,\n            CancellationToken cancellationToken = default,\n            IProgress<ExportProgress>? exportProgress = null)\n        {\n            return _autoRunCoordinator.RunAsync(\n                exportAfter,\n                progress,\n                cancellationToken,\n                exportProgress);\n        }\n\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private void OnAutoCancelRequested()\n        {\n            _autoCts?.Cancel();\n        }\n''',
    '''        private void OnAutoCancelRequested()\n        {\n            _autoRunCoordinator.Cancel();\n        }\n''')

replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            if (_isAutoRunning || ToolPanel.IsAutoRunning)\n                return;\n''',
    '''            if (_autoRunCoordinator.IsRunning || ToolPanel.IsAutoRunning)\n                return;\n''')

# Single-frame lifecycle is also delegated to the coordinator.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private Task<bool> RunAutoSingleFrameAsync()\n''',
    '''        [RelayCommand]\n        private void NextAutoAnomaly()\n''',
    '''        private Task<bool> RunAutoSingleFrameAsync()\n            => _autoRunCoordinator.RunSingleFrameAsync();\n\n''')

# Anomaly building/cutoff moved into auto coordinator; timestamp helpers remain here.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private async Task BuildAutoAnomaliesAsync(\n''',
    '''        private void RefreshIssueTimesInBackground(int? preferredFrameIndex = null)\n''',
    '''''')

# Snapshot and restore source their Auto state from the coordinator.
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            WorkspaceAutoExportGateState exportState = _exportCoordinator.GateState;\n            var state = new WorkspaceStateCapture(\n''',
    '''            WorkspaceAutoExportGateState exportState = _exportCoordinator.GateState;\n            AutoMaskRunStateSnapshot autoState = _autoRunCoordinator.CreateStateSnapshot();\n            var state = new WorkspaceStateCapture(\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''                _autoResumeIndex,\n                _autoCompleted,\n                _autoRunSignature,\n                _autoExecutionSignature,\n''',
    '''                autoState.ResumeIndex,\n                autoState.Completed,\n                autoState.RunSignature,\n                autoState.ExecutionSignature,\n''')
replace_once(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''            _autoResumeIndex = state.AutoResumeIndex;\n            _autoCompleted = state.AutoCompleted;\n            _autoRunSignature = state.AutoRunSignature;\n            _autoExecutionSignature = state.AutoExecutionSignature;\n            ApplyAutoExportGateState(state.ExportGateState);\n''',
    '''            _autoRunCoordinator.RestoreState(\n                state.AutoResumeIndex,\n                state.AutoCompleted,\n                state.AutoRunSignature,\n                state.AutoExecutionSignature);\n            _exportCoordinator.ApplyGateState(state.ExportGateState);\n''')

# Remove obsolete resume helpers now owned by coordinator.
replace_between(
    'ViewModels/Pages/WorkspaceViewModel.cs',
    '''        private bool IsAutoResumeSignatureCurrent(string currentSignature)\n''',
    '''        private bool TryBeginLifetimeOperation()\n''',
    '''''')

# Cancellation/disposal ownership follows coordinator.
p = Path('ViewModels/Pages/WorkspaceViewModel.cs')
text = read_exact(p)
text = text.replace('            try { _autoCts?.Cancel(); }\n            catch { }\n', '            _autoRunCoordinator.Cancel();\n')
text = text.replace(
    '        private void DisposeOwnedResources()\n        {\n            FramePreview.Dispose();\n            FrameList.Dispose();\n            _issueReview.Dispose();\n            _workspacePersistence?.Dispose();\n            _maskProvider.Dispose();\n            _exportCoordinator.Dispose();\n        }',
    '        private void DisposeOwnedResources()\n        {\n            _autoRunCoordinator.Dispose();\n            _exportCoordinator.Dispose();\n            _issueReview.Dispose();\n            _workspacePersistence?.Dispose();\n            FramePreview.Dispose();\n            FrameList.Dispose();\n            _maskProvider.Dispose();\n        }')
write_exact(p, text)

print('Phase C4 auto run coordinator extraction applied.')
