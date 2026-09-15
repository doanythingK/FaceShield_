using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Collections.Generic;

namespace FaceShield.ViewModels.Workspace;

internal static class ManualTrackingShutdownRegistry
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<FramePreviewViewModel>> Registrations = new();
    private static IClassicDesktopStyleApplicationLifetime? _lifetime;

    internal static void Register(FramePreviewViewModel viewModel)
    {
        if (viewModel == null)
            throw new ArgumentNullException(nameof(viewModel));

        lock (Gate)
        {
            bool alreadyRegistered = false;
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                if (!Registrations[i].TryGetTarget(out FramePreviewViewModel? existing))
                {
                    Registrations.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(existing, viewModel))
                    alreadyRegistered = true;
            }

            if (!alreadyRegistered)
                Registrations.Add(new WeakReference<FramePreviewViewModel>(viewModel));

            if (_lifetime != null)
                return;

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return;

            _lifetime = lifetime;
            lifetime.ShutdownRequested += OnShutdownRequested;
        }
    }

    private static void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        FramePreviewViewModel[] live;
        lock (Gate)
        {
            var result = new List<FramePreviewViewModel>(Registrations.Count);
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                if (!Registrations[i].TryGetTarget(out FramePreviewViewModel? viewModel))
                {
                    Registrations.RemoveAt(i);
                    continue;
                }
                result.Add(viewModel);
            }
            live = result.ToArray();
        }

        foreach (FramePreviewViewModel viewModel in live)
            viewModel.CancelManualTrackingForShutdown();
    }
}
