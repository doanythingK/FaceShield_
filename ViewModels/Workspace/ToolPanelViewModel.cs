using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace;
using System;

namespace FaceShield.ViewModels.Workspace
{
    public partial class ToolPanelViewModel : ViewModelBase
    {
        private const int DefaultBrushDiameter = 16;
        private const int MaxBrushDiameterValue = DefaultBrushDiameter * 5;
        private const int DefaultBlurRadius = 28;
        private const int MinBlurRadiusValue = 6;
        private const int MaxBlurRadiusValue = 40;

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
