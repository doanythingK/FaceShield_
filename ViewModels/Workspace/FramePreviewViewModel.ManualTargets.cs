using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Services.Video;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public sealed record ManualTargetChoice(Guid Id, string Label);

public partial class FramePreviewViewModel
{
    private ManualOverlayTargetWorkspace? _manualTargets;
    private string? _manualTargetsVideoPath;
    private ManualTargetChoice? _selectedManualTarget;
    private bool _replacingTargetMask;
    private bool _composingTargetPreview;
    private bool _manualTargetEventsAttached;

    public ObservableCollection<ManualTargetChoice> ManualTargetChoices { get; } = new();
    public bool HasManualTargetWorkspace => _manualTargets != null;
    public bool HasSelectedManualTarget => _selectedManualTarget != null;
    public bool CanTrackSelectedManualTarget => CanTrackForward && _manualTargets != null &&
        _selectedManualTarget != null && HasSelectedExplicitKeyframe();

    public ManualTargetChoice? SelectedManualTarget
    {
        get => _selectedManualTarget;
        set
        {
            if (value == null || _manualTargets == null ||
                !ManualTargetChoices.Any(choice => choice.Id == value.Id) ||
                _selectedManualTarget?.Id == value.Id || !CanSwitchManualTarget())
                return;
            CommitSelectedManualTargetEdit();
            PreserveManualTargetUndo();
            _manualTargets.SelectTarget(value.Id);
            _selectedManualTarget = ManualTargetChoices.First(choice => choice.Id == value.Id);
            OnPropertyChanged(nameof(SelectedManualTarget));
            OnPropertyChanged(nameof(HasSelectedManualTarget));
            ReplaceEditorWithSelectedTarget();
            RestoreManualTargetUndo();
            PersistManualTargetSelection();
            OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
        }
    }

    private bool CanSwitchManualTarget() => !_disposed && !_isPlaying && !IsManualTracking &&
        !IsFrameLoading && !_isDrawing && _toolPanel.CanEditWorkspace;

    internal void ConfigureManualTargets(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || _disposed)
            return;
        if (_manualTargets != null &&
            string.Equals(_manualTargetsVideoPath, videoPath, StringComparison.Ordinal))
            return;

        CommitSelectedManualTargetEdit();
        PreserveManualTargetUndo();
        _manualTargets = ManualOverlayTargetWorkspace.Open(videoPath);
        _manualTargetsVideoPath = videoPath;
        ManualTargetChoices.Clear();
        foreach (Guid id in _manualTargets.GetTargetIds())
            ManualTargetChoices.Add(new ManualTargetChoice(id, $"수동 얼굴 {ManualTargetChoices.Count + 1}"));
        Guid? selected = _manualTargets.SelectedTargetId;
        _selectedManualTarget = selected.HasValue
            ? ManualTargetChoices.FirstOrDefault(choice => choice.Id == selected.Value) : null;
        OnPropertyChanged(nameof(SelectedManualTarget));
        OnPropertyChanged(nameof(HasSelectedManualTarget));
        OnPropertyChanged(nameof(HasManualTargetWorkspace));
        OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
        if (!_manualTargetEventsAttached)
        {
            PropertyChanged += OnManualTargetPreviewPropertyChanged;
            MaskEdited += OnManualTargetMaskEdited;
            _toolPanel.UndoRequested += OnManualTargetUndoRequested;
            _manualTargetEventsAttached = true;
        }
        ReplaceEditorWithSelectedTarget();
        RestoreManualTargetUndo();
    }

    [RelayCommand]
    private void CreateManualTarget()
    {
        if (!CanSwitchManualTarget() || _manualTargets == null)
            return;
        CommitSelectedManualTargetEdit();
        PreserveManualTargetUndo();
        // Creating on the live workspace first could leave an unsaved ghost
        // target/selection when the file write fails. Prepare a detached copy
        // from the just-saved target document, persist it, then adopt it.
        ManualOverlayTargetWorkspace staged = ManualOverlayTargetWorkspace.Open(
            _manualTargetsVideoPath ?? throw new InvalidOperationException("Manual target video path is missing."));
        Guid id = staged.CreateTarget();
        staged.Save();
        _manualTargets = staged;
        ManualTargetChoice choice = new(id, $"수동 얼굴 {ManualTargetChoices.Count + 1}");
        ManualTargetChoices.Add(choice);
        _selectedManualTarget = choice;
        OnPropertyChanged(nameof(SelectedManualTarget));
        OnPropertyChanged(nameof(HasSelectedManualTarget));
        ReplaceEditorWithSelectedTarget();
        RestoreManualTargetUndo();
        PersistManualTargetSelection();
        OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
    }

    private bool HasSelectedExplicitKeyframe()
    {
        if (_manualTargets == null || _selectedManualTarget == null || _currentFrameIndex < 0)
            return false;
        return _manualTargets.Snapshot().Any(target => target.Id == _selectedManualTarget.Id &&
            target.Keyframes.Any(keyframe => keyframe.FrameIndex == _currentFrameIndex &&
                keyframe.Alpha.Any(static alpha => alpha != 0)));
    }

    private void OnManualTargetPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _manualTargets == null || _selectedManualTarget == null)
            return;
        if (e.PropertyName == nameof(MaskBitmap) && !_replacingTargetMask &&
            !_isDrawing && !_maskDirty && !IsManualTracking)
        {
            ReplaceEditorWithSelectedTarget();
            OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
        }
        else if (e.PropertyName == nameof(PreviewBitmap) && !_composingTargetPreview)
            ComposeTargetPreview();
        else if (e.PropertyName == nameof(CanTrackForward))
            OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
    }

    private void ReplaceEditorWithSelectedTarget()
    {
        if (_replacingTargetMask || _manualTargets == null ||
            _selectedManualTarget == null || _frameBitmap == null ||
            _currentFrameIndex < 0 || _isDrawing || _maskDirty)
            return;
        _replacingTargetMask = true;
        _manualMaskKeyframeRefreshInProgress = true;
        try
        {
            WriteableBitmap next;
            if (_manualTargets.TryResolveTargetMask(_selectedManualTarget.Id,
                    _currentFrameIndex, out ManualOverlayStoredKeyframe mask))
            {
                if (mask.Width != _frameBitmap.PixelSize.Width ||
                    mask.Height != _frameBitmap.PixelSize.Height)
                    throw new InvalidOperationException("선택한 얼굴 마스크의 영상 크기가 일치하지 않습니다.");
                next = ToBitmap(mask);
            }
            else
                next = CreateEmptyMask(_frameBitmap.PixelSize.Width, _frameBitmap.PixelSize.Height);
            MaskBitmap = next;
            _maskUndo.Clear();
            _maskDirty = false;
            _hasDirtyRegion = false;
            RefreshPreview(force: true);
        }
        finally
        {
            _manualMaskKeyframeRefreshInProgress = false;
            _replacingTargetMask = false;
        }
    }

    private void OnManualTargetMaskEdited(int frameIndex)
    {
        if (_selectedManualTarget == null || frameIndex != _currentFrameIndex)
            return;
        TryCommitManualTargetEditWithoutDiscardingDirtyPixels();
    }

    private void OnManualTargetUndoRequested()
    {
        if (_selectedManualTarget != null && _maskDirty)
            TryCommitManualTargetEditWithoutDiscardingDirtyPixels();
    }

    private void TryCommitManualTargetEditWithoutDiscardingDirtyPixels()
    {
        try
        {
            CommitSelectedManualTargetEdit();
        }
        catch (Exception ex)
        {
            // Brush release and Undo are event callbacks: report the error
            // without allowing a failed save to clear the editable bitmap.
            Debug.WriteLine($"[ManualTarget] Correction save failed: {ex}");
            ManualTrackingStatusText = $"수동 얼굴 마스크 저장 실패: {ex.Message}";
        }
    }

    private void CommitSelectedManualTargetEdit()
    {
        if (!_maskDirty || _manualTargets == null ||
            _selectedManualTarget == null || _maskBitmap == null || _currentFrameIndex < 0)
            return;
        ManualOverlayStoredKeyframe correction = FromBitmap(_currentFrameIndex, _maskBitmap);
        // An all-zero alpha mask is an explicit absence boundary for this face.
        // Stage the complete state on disk BEFORE changing the live timeline:
        // a failed write leaves the existing verified track and dirty editor intact.
        ManualOverlayTargetEditCommitter.Commit(
            _manualTargetsVideoPath ?? throw new InvalidOperationException("Manual target video path is missing."),
            _manualTargets, _selectedManualTarget.Id, correction);
        _maskDirty = false;
        _manualTrackingPendingSourceValidationFrame = -1;
        OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
        ComposeTargetPreview();
    }

    private void ComposeTargetPreview()
    {
        if (_composingTargetPreview || _replacingTargetMask || _disposed ||
            _manualTargets == null || _selectedManualTarget == null ||
            _maskBitmap == null || _frameBitmap == null || _currentFrameIndex < 0 ||
            _maskBitmap.PixelSize != _frameBitmap.PixelSize)
            return;
        _composingTargetPreview = true;
        try
        {
            int width = _frameBitmap.PixelSize.Width;
            int height = _frameBitmap.PixelSize.Height;
            int rowBytes = checked(width * 4);
            byte[]? legacy = null;
            if (_maskProvider is FrameMaskProvider provider)
            {
                using WriteableBitmap? exact = provider.GetFinalMask(_currentFrameIndex);
                if (exact != null)
                    legacy = ReadBitmap(exact, width, height);
                if (ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                        provider, _currentFrameIndex, out WriteableBitmap inherited))
                {
                    using (inherited)
                    {
                        byte[] more = ReadBitmap(inherited, width, height);
                        legacy ??= new byte[checked(rowBytes * height)];
                        UnionAlpha(legacy, more);
                    }
                }
            }
            var layers = new System.Collections.Generic.List<ManualOverlayMask>();
            foreach (Guid id in _manualTargets.GetTargetIds())
            {
                if (id == _selectedManualTarget.Id ||
                    !_manualTargets.TryResolveTargetMask(id, _currentFrameIndex,
                        out ManualOverlayStoredKeyframe other))
                    continue;
                if (other.Width != width || other.Height != height)
                    throw new InvalidOperationException("수동 얼굴 마스크 크기가 영상과 일치하지 않습니다.");
                layers.Add(other.ToBgra(rowBytes));
            }
            layers.Add(new ManualOverlayMask(ReadBitmap(_maskBitmap, width, height), rowBytes));
            byte[] output = new byte[checked(rowBytes * height)];
            ManualOverlayMaskComposer.Compose(legacy, rowBytes, layers,
                output, rowBytes, width, height);
            using WriteableBitmap previewMask = ToBitmap(
                new ManualOverlayStoredKeyframe(_currentFrameIndex, width, height,
                    ExtractAlpha(output, width, height, rowBytes)));
            EnsureBlurredFrame();
            WriteableBitmap result = PreviewBlurProcessor.ComposeMaskedPreview(
                _frameBitmap, _blurredFrame!, previewMask, null, null);
            SetPreviewBitmap(result, ownsBitmap: true);
        }
        finally
        {
            _composingTargetPreview = false;
        }
    }

    private static byte[] ExtractAlpha(byte[] bgra, int width, int height, int rowBytes)
    {
        byte[] alpha = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = bgra[y * rowBytes + x * 4 + 3];
        return alpha;
    }

    private static void UnionAlpha(byte[] destination, byte[] source)
    {
        if (destination.Length != source.Length)
            throw new InvalidOperationException("합성할 마스크 크기가 일치하지 않습니다.");
        for (int p = 0; p < destination.Length; p += 4)
        {
            byte alpha = Math.Max(destination[p + 3], source[p + 3]);
            destination[p] = destination[p + 1] = destination[p + 2] = destination[p + 3] = alpha;
        }
    }

    private static byte[] ReadBitmap(WriteableBitmap bitmap, int width, int height)
    {
        if (bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            throw new InvalidOperationException("합성할 마스크 크기가 영상과 일치하지 않습니다.");
        int rowBytes = checked(width * 4);
        byte[] pixels = new byte[checked(rowBytes * height)];
        using var frame = bitmap.Lock();
        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(frame.Address, checked(y * frame.RowBytes)),
                pixels, checked(y * rowBytes), rowBytes);
        return pixels;
    }

    private static ManualOverlayStoredKeyframe FromBitmap(int frameIndex, WriteableBitmap bitmap)
    {
        int width = bitmap.PixelSize.Width, height = bitmap.PixelSize.Height;
        return ManualOverlayStoredKeyframe.FromBgra(frameIndex,
            ReadBitmap(bitmap, width, height), checked(width * 4), width, height);
    }

    private static WriteableBitmap ToBitmap(ManualOverlayStoredKeyframe mask)
    {
        int rowBytes = checked(mask.Width * 4);
        ManualOverlayMask bgra = mask.ToBgra(rowBytes);
        var bitmap = new WriteableBitmap(new PixelSize(mask.Width, mask.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = bitmap.Lock();
            for (int y = 0; y < mask.Height; y++)
                Marshal.Copy(bgra.Pixels, checked(y * rowBytes),
                    IntPtr.Add(frame.Address, checked(y * frame.RowBytes)), rowBytes);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    [RelayCommand]
    private async Task TrackSelectedManualTarget()
    {
        if (!CanTrackSelectedManualTarget || _manualTargets == null ||
            _selectedManualTarget == null || string.IsNullOrWhiteSpace(_manualTargetsVideoPath))
            return;
        CommitSelectedManualTargetEdit();
        Guid targetId = _selectedManualTarget.Id;
        int sourceFrame = _currentFrameIndex;
        if (!HasSelectedExplicitKeyframe()) return;
        int boundary = _manualTargets.NextBoundaryExclusive(targetId,
            sourceFrame, _manualTrackingTotalFrames);
        int expectedFrames = boundary == int.MaxValue
            ? Math.Max(1, _manualTrackingTotalFrames - sourceFrame - 1)
            : Math.Max(1, boundary - sourceFrame - 1);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        if (Interlocked.CompareExchange(ref _manualTrackingTask, operation, null) != null)
            return;
        bool lifetimeStarted = false;
        bool exactSuspended = false;
        var cts = new CancellationTokenSource();
        try
        {
            if (_tryBeginManualTrackingLifetime == null || !_tryBeginManualTrackingLifetime())
                return;
            lifetimeStarted = true;
            Interlocked.Exchange(ref _manualTrackingCts, cts)?.Dispose();
            IsManualTracking = true;
            _toolPanel.IsManualTracking = true;
            ManualTrackingProgress = 0;
            ManualTrackingStatusText = $"수동 얼굴 {ManualTargetChoices.IndexOf(_selectedManualTarget) + 1}: {sourceFrame} 프레임부터 추적 중...";
            await StopManualOperationsAndWaitAsync();
            cts.Token.ThrowIfCancellationRequested();
            await SuspendExactFrameOperationsAndWaitAsync();
            exactSuspended = true;
            var progress = new SynchronousTrackingProgress(processed =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && ReferenceEquals(_manualTrackingCts, cts))
                        ManualTrackingProgress = Math.Min(99,
                            (int)Math.Round(processed * 100.0 / expectedFrames));
                }));
            ManualMaskTrackResult result = await Task.Run(() =>
                ManualOverlayTargetTrackingService.TrackForward(
                    _manualTargetsVideoPath!, _manualTargets, targetId, sourceFrame,
                    _manualTrackingTotalFrames, progress, cts.Token), cts.Token);
            // TrackForward may have already persisted its result before a late
            // cancel request. Report that committed result rather than throwing
            // here and falsely telling the user that nothing was saved.
            ManualTrackingProgress = result.Segment.StoppedByFailure ? ManualTrackingProgress : 100;
            ManualTrackingStatusText = result.Segment.StoppedByFailure
                ? $"수동 얼굴 추적 중단: {result.Segment.StopFrame} 프레임. {result.Segment.StopReason} 해당 얼굴을 보정하고 이어서 추적하세요."
                : $"수동 얼굴 추적 완료: {result.ProcessedFrames}개 프레임 확인.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ManualTrackingStatusText = "수동 얼굴 추적이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            ManualTrackingStatusText = $"수동 얼굴 추적 실패: {ex.Message}";
        }
        finally
        {
            if (exactSuspended && !_disposed)
                ResumeExactFrameOperations();
            Interlocked.CompareExchange(ref _manualTrackingCts, null, cts);
            cts.Dispose();
            IsManualTracking = false;
            _toolPanel.IsManualTracking = false;
            if (lifetimeStarted)
                _endManualTrackingLifetime?.Invoke();
            completion.TrySetResult(true);
            Interlocked.CompareExchange(ref _manualTrackingTask, null, operation);
            OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
            if (!_disposed && _currentFrameIndex >= 0)
                OnFrameIndexChanged(_currentFrameIndex);
        }
    }
}
