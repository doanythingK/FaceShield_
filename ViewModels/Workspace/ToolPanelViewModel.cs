using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;

namespace FaceShield.ViewModels.Workspace
{
    public sealed record ExportQualityChoice(
        VideoExportQualityPreset Preset,
        string Label,
        string Description)
    {
        public override string ToString() => Label;
    }

    public partial class ToolPanelViewModel : ViewModelBase
    {
        private const int DefaultBrushDiameter = 16;
        private const int MaxBrushDiameterValue = DefaultBrushDiameter * 5;
        private const int DefaultBlurRadius = 28;
        private const int MinBlurRadiusValue = 6;
        private const int MaxBlurRadiusValue = 40;

        private static readonly ExportQualityChoice SizePriorityExportQuality = new(
            VideoExportQualityPreset.SizePriority,
            "용량 우선",
            "원본 영상 bitrate와 비슷한 수준(1.00×)으로 제한합니다.");
        private static readonly ExportQualityChoice BalancedExportQuality = new(
            VideoExportQualityPreset.Balanced,
            "균형 (권장)",
            "재인코딩 화질 여유를 위해 원본 영상 bitrate의 최대 1.20×를 사용합니다.");
        private static readonly ExportQualityChoice QualityPriorityExportQuality = new(
            VideoExportQualityPreset.QualityPriority,
            "화질 우선",
            "복잡한 장면의 재인코딩 손실을 줄이기 위해 원본 영상 bitrate의 최대 1.40×를 사용합니다.");

        public IReadOnlyList<ExportQualityChoice> ExportQualityChoices { get; } = new[]
        {
            SizePriorityExportQuality,
            BalancedExportQuality,
            QualityPriorityExportQuality
        };

        [ObservableProperty]
        private ExportQualityChoice selectedExportQuality = BalancedExportQuality;

        public VideoExportQualityPreset ExportQualityPreset => SelectedExportQuality.Preset;

        [ObservableProperty]
        private EditMode currentMode = EditMode.None;

        [ObservableProperty]
        private int autoProgress;

        [ObservableProperty]
        private bool isAutoRunning;

        [ObservableProperty]
        private bool isExportRunning;

        [ObservableProperty]
        private int exportProgress;

        [ObservableProperty]
        private string? exportEtaText;

        [ObservableProperty]
        private string? exportStatusText;

        [ObservableProperty]
        private int brushDiameter = DefaultBrushDiameter;

        [ObservableProperty]
        private int blurRadius = DefaultBlurRadius;

        public int MinBrushDiameter => DefaultBrushDiameter;
        public int MaxBrushDiameter => MaxBrushDiameterValue;
        public int MinBlurRadius => MinBlurRadiusValue;
        public int MaxBlurRadius => MaxBlurRadiusValue;

        public bool ShowBrushSize =>
            CurrentMode == EditMode.Brush || CurrentMode == EditMode.Eraser;

        public bool ShowAutoProgress => IsAutoRunning && !IsExportRunning;
        public bool CanEditWorkspace => !IsExportRunning && !IsAutoRunning;


        partial void OnCurrentModeChanged(EditMode value)
        {
            OnPropertyChanged(nameof(ShowBrushSize));
        }

        partial void OnIsAutoRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowAutoProgress));
            OnPropertyChanged(nameof(CanEditWorkspace));
        }

        partial void OnIsExportRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowAutoProgress));
            OnPropertyChanged(nameof(CanEditWorkspace));
        }

        partial void OnSelectedExportQualityChanged(ExportQualityChoice value)
        {
            OnPropertyChanged(nameof(ExportQualityPreset));
        }


        public event Action? UndoRequested;
        public event Action? SaveRequested;

        // 🔹 새 이벤트: 자동 분석 요청
        public event Action? AutoRequested;
        public event Action? AutoCancelRequested;
        public event Action? ExportCancelRequested;

        [RelayCommand]
        private void SetAuto()
        {
            CurrentMode = EditMode.Auto;
            AutoRequested?.Invoke();
        }

        [RelayCommand]
        private void SetManual()
        {
            if (!CanEditWorkspace) return;
            CurrentMode = EditMode.Manual;
        }

        [RelayCommand]
        private void SetBrush()
        {
            if (!CanEditWorkspace) return;
            CurrentMode = EditMode.Brush;
        }

        [RelayCommand]
        private void SetEraser()
        {
            if (!CanEditWorkspace) return;
            CurrentMode = EditMode.Eraser;
        }

        [RelayCommand]
        private void Undo()
        {
            if (!CanEditWorkspace) return;
            UndoRequested?.Invoke();
        }

        [RelayCommand]
        private void Save()
        {
            if (!CanEditWorkspace) return;
            SaveRequested?.Invoke();
        }

        [RelayCommand]
        private void CancelAuto() => AutoCancelRequested?.Invoke();

        [RelayCommand]
        private void CancelExport() => ExportCancelRequested?.Invoke();

    }
}
