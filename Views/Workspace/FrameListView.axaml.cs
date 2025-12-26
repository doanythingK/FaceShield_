// FILE: D:\WorkSpace\FaceShield\Views\Workspace\FrameListView.axaml.cs
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FaceShield.ViewModels.Workspace;
using System;

namespace FaceShield.Views.Workspace;

public partial class FrameListView : UserControl
{
    private DispatcherTimer? _playTimer;

    public FrameListView()
    {
        InitializeComponent();

        // 워크스페이스 열리면 포커스 확보
        AttachedToVisualTree += (_, _) => Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not FrameListViewModel vm)
            return;

        if (vm.TotalFrames <= 0)
            return;

        switch (e.Key)
        {
            // ─────────────────────────
            // 프레임 단위 이동
            // ─────────────────────────
            case Key.Left:
            case Key.Right:
                MoveFrame(vm, e.Key == Key.Right, e.KeyModifiers);
                e.Handled = true;
                break;

            // ─────────────────────────
            // 키프레임 단위 이동
            // (현 구조상: 1초 단위로 대체)
            // ─────────────────────────
            case Key.Up:
                MoveBySeconds(vm, +1);
                e.Handled = true;
                break;

            case Key.Down:
                MoveBySeconds(vm, -1);
                e.Handled = true;
                break;

            // ─────────────────────────
            // 처음 / 끝
            // ─────────────────────────
            case Key.Home:
                vm.SelectedFrameIndex = 0;
                e.Handled = true;
                break;

            case Key.End:
                vm.SelectedFrameIndex = vm.TotalFrames - 1;
                e.Handled = true;
                break;

            // ─────────────────────────
            // 재생 / 정지
            // ─────────────────────────
            case Key.Space:
                TogglePlay(vm);
                e.Handled = true;
                break;
        }
    }

    // ─────────────────────────────
    // helpers
    // ─────────────────────────────

    private static void MoveFrame(FrameListViewModel vm, bool forward, KeyModifiers mods)
    {
        int step = mods.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        int delta = forward ? step : -step;

        int next = Math.Clamp(
            vm.SelectedFrameIndex + delta,
            0,
            vm.TotalFrames - 1);

        vm.SelectedFrameIndex = next;
    }

    private static void MoveBySeconds(FrameListViewModel vm, int seconds)
    {
        if (vm.Fps <= 0) return;

        int deltaFrames = (int)Math.Round(seconds * vm.Fps);

        int next = Math.Clamp(
            vm.SelectedFrameIndex + deltaFrames,
            0,
            vm.TotalFrames - 1);

        vm.SelectedFrameIndex = next;
    }

    private void TogglePlay(FrameListViewModel vm)
    {
        if (_playTimer == null)
        {
            _playTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, vm.Fps))
            };

            _playTimer.Tick += (_, _) =>
            {
                if (vm.SelectedFrameIndex >= vm.TotalFrames - 1)
                {
                    StopPlay();
                    return;
                }

                vm.SelectedFrameIndex++;
            };
        }

        if (_playTimer.IsEnabled)
            StopPlay();
        else
            _playTimer.Start();
    }

    private void StopPlay()
    {
        if (_playTimer != null)
            _playTimer.Stop();
    }
}
