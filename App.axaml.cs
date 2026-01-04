using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FaceShield.ViewModels;
using FaceShield.Views;
using FaceShield.Views.Dialogs;

namespace FaceShield
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            RegisterGlobalExceptionHandlers();

            // ✅ FFmpeg는 UI/VM 생성 전에 초기화 (기능 불능 예방)
            try
            {
                FaceShield.Services.Video.FFmpegBootstrap.Initialize();
            }
            catch (Exception ex)
            {
                ShowStartupErrorAndExit(ex);
                return;
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                var mainVm = new MainWindowViewModel();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainVm,
                };

                desktop.Exit += (_, _) => mainVm.PersistAppState();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    HandleUnhandledException(ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                HandleUnhandledException(e.Exception);
                e.SetObserved();
            };

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                HandleUnhandledException(e.Exception);
                e.Handled = true;
            };
        }

        private void HandleUnhandledException(Exception ex)
        {
            try
            {
                string message = BuildExceptionMessage(ex);
                WriteCrashLog(message, ex);
                Dispatcher.UIThread.Post(() => ShowGlobalErrorDialog(message));
            }
            catch
            {
                // Last-resort: swallow to avoid recursive crashes.
            }
        }

        private static string BuildExceptionMessage(Exception ex)
        {
            if (ex is AggregateException agg)
                ex = agg.Flatten().InnerException ?? ex;

            if (ex is System.IO.FileNotFoundException fnf && !string.IsNullOrWhiteSpace(fnf.FileName))
                return $"{fnf.Message}\n누락 파일: {fnf.FileName}";

            return ex.Message;
        }

        private void ShowGlobalErrorDialog(string message)
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var owner = desktop.MainWindow;
            var dialog = new ErrorDialog("예기치 않은 오류", message);
            if (owner == null)
            {
                dialog.Show();
                return;
            }
            _ = dialog.ShowDialog(owner);
        }

        private void ShowStartupErrorAndExit(Exception ex)
        {
            string message = BuildExceptionMessage(ex);
            WriteCrashLog(message, ex);
            Console.Error.WriteLine(message);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var dialog = new ErrorDialog("시작 실패", message);
                dialog.Closed += (_, _) => desktop.Shutdown(-1);
                desktop.MainWindow = dialog;
                dialog.Show();
            }
        }

        private static void WriteCrashLog(string message, Exception ex)
        {
            try
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FaceShield");
                Directory.CreateDirectory(baseDir);

                string path = Path.Combine(baseDir, "crash.log");
                string payload = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(path, payload);
            }
            catch
            {
                // Ignore logging failures.
            }
        }
    }
}
