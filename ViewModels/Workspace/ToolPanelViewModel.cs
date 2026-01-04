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

        public int MinBrushDiameter => DefaultBrushDiameter;
        public int MaxBrushDiameter => MaxBrushDiameterValue;

        public bool ShowBrushSize =>
            CurrentMode == EditMode.Brush || CurrentMode == EditMode.Eraser;

        public bool ShowAutoProgress => IsAutoRunning && !IsExportRunning;

        partial void OnCurrentModeChanged(EditMode value)
        {
            OnPropertyChanged(nameof(ShowBrushSize));
        }

        partial void OnIsAutoRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowAutoProgress));
        }

        partial void OnIsExportRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowAutoProgress));
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
        private void SetManual() => CurrentMode = EditMode.Manual;

        [RelayCommand]
        private void SetBrush() => CurrentMode = EditMode.Brush;

        [RelayCommand]
        private void SetEraser() => CurrentMode = EditMode.Eraser;

        [RelayCommand]
        private void Undo() => UndoRequested?.Invoke();

        [RelayCommand]
        private void Save() => SaveRequested?.Invoke();

        [RelayCommand]
        private void CancelAuto() => AutoCancelRequested?.Invoke();

        [RelayCommand]
        private void CancelExport() => ExportCancelRequested?.Invoke();
    }
}
