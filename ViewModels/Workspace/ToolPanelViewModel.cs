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
        private int brushDiameter = DefaultBrushDiameter;

        public int MinBrushDiameter => DefaultBrushDiameter;
        public int MaxBrushDiameter => MaxBrushDiameterValue;

        public bool ShowBrushSize =>
            CurrentMode == EditMode.Brush || CurrentMode == EditMode.Eraser;

        partial void OnCurrentModeChanged(EditMode value)
        {
            OnPropertyChanged(nameof(ShowBrushSize));
        }

        public event Action? UndoRequested;
        public event Action? SaveRequested;

        // 🔹 새 이벤트: 자동 분석 요청
        public event Action? AutoRequested;
        public event Action? AutoCancelRequested;

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
    }
}
