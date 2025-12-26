using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace;
using System;

namespace FaceShield.ViewModels.Workspace
{
    public partial class ToolPanelViewModel : ViewModelBase
    {
        [ObservableProperty]
        private EditMode currentMode = EditMode.None;

        public event Action? UndoRequested;
        public event Action? SaveRequested;

        [RelayCommand]
        private void SetAuto() => CurrentMode = EditMode.Auto;

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
    }
}
