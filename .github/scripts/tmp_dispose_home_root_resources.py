from pathlib import Path

HOME = Path('ViewModels/Pages/HomePageViewModel.cs')
MAIN = Path('ViewModels/MainWindowViewModel.cs')
STATUS = Path('OWNERSHIP_WORKSPACE_HARDENING_STATUS.md')


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'[{label}] expected exactly one match, found {count}')
    return text.replace(old, new, 1)


home = HOME.read_text(encoding='utf-8')

home = replace_once(
    home,
    '    public partial class HomePageViewModel : ViewModelBase\n',
    '    public partial class HomePageViewModel : ViewModelBase, IDisposable\n',
    'home-idisposable')

home = replace_once(
    home,
    '''        private DispatcherTimer? _autoStatusTimer;\n        private bool _autoRestartRequested;\n''',
    '''        private DispatcherTimer? _autoStatusTimer;\n        private int _rootResourcesDisposed;\n        private bool _autoRestartRequested;\n''',
    'dispose-field')

old_regen = '''        private void RegenerateBlurExamples()\n        {\n            const int w = 120;\n            const int h = 90;\n            var percents = new[] { 1.0, 3.0, 5.0, 12.0 };\n            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];\n\n            var list = new List<BlurExampleItem>(percents.Length);\n            foreach (var p in percents)\n            {\n                var face = BuildCenteredFaceRect(w, h, p);\n                var src = CreatePatternImage(w, h);\n                var mask = FrameMaskProvider.CreateMaskFromFaceRects(new PixelSize(w, h), new[] { face });\n                var preview = PreviewBlurProcessor.CreateBlurPreview(src, mask, BlurRadius, new[] { face });\n                string label = BuildBlurLabel(p, resolution.Width, resolution.Height);\n                list.Add(new BlurExampleItem(p, label, preview));\n\n                src.Dispose();\n                mask.Dispose();\n            }\n\n            if (BlurExamples != null)\n            {\n                foreach (var item in BlurExamples)\n                    item.Image.Dispose();\n            }\n\n            BlurExamples = list;\n        }\n'''
new_regen = '''        private void RegenerateBlurExamples()\n        {\n            if (IsShutdownRequested || Volatile.Read(ref _rootResourcesDisposed) != 0)\n                return;\n\n            const int w = 120;\n            const int h = 90;\n            var percents = new[] { 1.0, 3.0, 5.0, 12.0 };\n            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];\n            var list = new List<BlurExampleItem>(percents.Length);\n            bool published = false;\n            try\n            {\n                foreach (var p in percents)\n                {\n                    var face = BuildCenteredFaceRect(w, h, p);\n                    using var src = CreatePatternImage(w, h);\n                    using var mask = FrameMaskProvider.CreateMaskFromFaceRects(\n                        new PixelSize(w, h),\n                        new[] { face });\n                    var preview = PreviewBlurProcessor.CreateBlurPreview(\n                        src,\n                        mask,\n                        BlurRadius,\n                        new[] { face });\n                    string label = BuildBlurLabel(p, resolution.Width, resolution.Height);\n                    list.Add(new BlurExampleItem(p, label, preview));\n                }\n\n                IReadOnlyList<BlurExampleItem> previous = BlurExamples;\n                BlurExamples = list;\n                published = true;\n                DisposeBlurExampleImages(previous);\n            }\n            finally\n            {\n                if (!published)\n                    DisposeBlurExampleImages(list);\n            }\n        }\n'''
home = replace_once(home, old_regen, new_regen, 'regen-blur-examples')

home = replace_once(
    home,
    '''        public BlurPreviewPayload? BuildBlurPreview(double percent)\n        {\n            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];\n            int w = resolution.Width;\n            int h = resolution.Height;\n\n            var face = BuildCenteredFaceRect(w, h, percent);\n            var src = CreatePatternImage(w, h);\n            var mask = FrameMaskProvider.CreateMaskFromFaceRects(new PixelSize(w, h), new[] { face });\n            var preview = PreviewBlurProcessor.CreateBlurPreview(src, mask, BlurRadius, new[] { face });\n\n            src.Dispose();\n            mask.Dispose();\n            int faceW = (int)Math.Round(face.Width);\n''',
    '''        public BlurPreviewPayload? BuildBlurPreview(double percent)\n        {\n            if (IsShutdownRequested || Volatile.Read(ref _rootResourcesDisposed) != 0)\n                return null;\n\n            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];\n            int w = resolution.Width;\n            int h = resolution.Height;\n\n            var face = BuildCenteredFaceRect(w, h, percent);\n            using var src = CreatePatternImage(w, h);\n            using var mask = FrameMaskProvider.CreateMaskFromFaceRects(\n                new PixelSize(w, h),\n                new[] { face });\n            var preview = PreviewBlurProcessor.CreateBlurPreview(src, mask, BlurRadius, new[] { face });\n\n            int faceW = (int)Math.Round(face.Width);\n''',
    'build-preview')

home = replace_once(
    home,
    '''                _autoStatusTimer = new DispatcherTimer\n                {\n                    Interval = TimeSpan.FromSeconds(1)\n                };\n                _autoStatusTimer.Tick += (_, _) => UpdateAutoStatusText();\n''',
    '''                _autoStatusTimer = new DispatcherTimer\n                {\n                    Interval = TimeSpan.FromSeconds(1)\n                };\n                _autoStatusTimer.Tick += OnAutoStatusTimerTick;\n''',
    'timer-subscription')

home = replace_once(
    home,
    '''        private void StopAutoStatusTimer(bool clearUi = true)\n        {\n            if (_autoStatusTimer == null)\n                return;\n\n            _autoStatusTimer.Stop();\n            if (clearUi && !IsShutdownRequested)\n                UpdateAutoStatusText(clear: true);\n            _etaFrameSamples.Clear();\n        }\n\n        private void UpdateAutoStatusText(bool clear = false)\n''',
    '''        private void OnAutoStatusTimerTick(object? sender, EventArgs e)\n            => UpdateAutoStatusText();\n\n        private void StopAutoStatusTimer(bool clearUi = true)\n        {\n            if (_autoStatusTimer == null)\n                return;\n\n            _autoStatusTimer.Stop();\n            if (clearUi && !IsShutdownRequested)\n                UpdateAutoStatusText(clear: true);\n            _etaFrameSamples.Clear();\n        }\n\n        private void UpdateAutoStatusText(bool clear = false)\n''',
    'timer-handler')

# Add root resource helpers before DisposeAllWorkspaces near the class tail.
home = replace_once(
    home,
    '''        public void DisposeAllWorkspaces()\n        {\n            PrepareAllWorkspacesForShutdown();\n\n            WorkspaceViewModel[] workspaces;\n            lock (_workspaceCacheGate)\n            {\n                workspaces = _workspaceCache.Values.Distinct().ToArray();\n                _workspaceCache.Clear();\n                _deferredWorkspaceEvictions.Clear();\n            }\n\n            foreach (var workspace in workspaces)\n                workspace.Dispose();\n        }\n\n    }\n}\n''',
    '''        private static void DisposeBlurExampleImages(\n            IEnumerable<BlurExampleItem> items)\n        {\n            foreach (var item in items)\n                item.Image.Dispose();\n        }\n\n        public void DisposeAllWorkspaces()\n        {\n            PrepareAllWorkspacesForShutdown();\n\n            WorkspaceViewModel[] workspaces;\n            lock (_workspaceCacheGate)\n            {\n                workspaces = _workspaceCache.Values.Distinct().ToArray();\n                _workspaceCache.Clear();\n                _deferredWorkspaceEvictions.Clear();\n            }\n\n            foreach (var workspace in workspaces)\n                workspace.Dispose();\n        }\n\n        public void Dispose()\n        {\n            if (Interlocked.Exchange(ref _rootResourcesDisposed, 1) != 0)\n                return;\n\n            DisposeAllWorkspaces();\n\n            DispatcherTimer? timer = _autoStatusTimer;\n            _autoStatusTimer = null;\n            if (timer != null)\n            {\n                timer.Stop();\n                timer.Tick -= OnAutoStatusTimerTick;\n            }\n\n            IReadOnlyList<BlurExampleItem> blurExamples = BlurExamples;\n            BlurExamples = Array.Empty<BlurExampleItem>();\n            DisposeBlurExampleImages(blurExamples);\n            _etaFrameSamples.Clear();\n            _workspaceEtaSamples.Clear();\n            _exportEtaSamples.Clear();\n        }\n\n    }\n}\n''',
    'home-dispose')

HOME.write_text(home, encoding='utf-8')

main = MAIN.read_text(encoding='utf-8')
main = replace_once(
    main,
    '''        private readonly int? _startupFrameIndex;\n''',
    '''        private readonly int? _startupFrameIndex;\n        private int _appStatePersisted;\n''',
    'main-idempotency-field')
main = replace_once(
    main,
    '''        public void PersistAppState()\n        {\n            _home.PrepareAllWorkspacesForShutdown();\n            CurrentPage = _home;\n            try\n            {\n                _home.PersistAllWorkspaces();\n            }\n            finally\n            {\n                _home.DisposeAllWorkspaces();\n            }\n        }\n''',
    '''        public void PersistAppState()\n        {\n            if (System.Threading.Interlocked.Exchange(ref _appStatePersisted, 1) != 0)\n                return;\n\n            _home.PrepareAllWorkspacesForShutdown();\n            CurrentPage = null;\n            try\n            {\n                _home.PersistAllWorkspaces();\n            }\n            finally\n            {\n                _home.Dispose();\n            }\n        }\n''',
    'main-persist')
MAIN.write_text(main, encoding='utf-8')

status = STATUS.read_text(encoding='utf-8')
status = replace_once(
    status,
    '''## Next active block\n\nAudit Home/application-root resource disposal (generated blur-preview bitmaps, timer/event lifetime, and app-exit idempotency) and fix only resources that remain rooted or can be disposed twice across shutdown paths.\n''',
    '''### Home / application-root resource disposal\n\n- [x] Make Home root-resource disposal idempotent and route final workspace disposal through the same owner.\n- [x] Detach and release the Home `DispatcherTimer` explicitly instead of leaving an anonymous Tick subscription rooted for the remainder of application lifetime.\n- [x] Swap `BlurExamples` before disposing the prior image set and dispose the final published image set at application exit.\n- [x] Make blur-example generation and full-size preview construction exception-safe for temporary source/mask bitmaps and partially generated preview lists.\n- [x] Make application-state persistence idempotent and clear `CurrentPage` before disposing Home/workspace-owned UI resources.\n\n## Next active block\n\nAudit shutdown-time persistence failure handling and application/global exception routing so an exit-time save failure cannot bypass cleanup or enqueue user-facing UI after the desktop lifetime is already closing.\n''',
    'status-next')
STATUS.write_text(status, encoding='utf-8')

print('[HomeRootResourceDisposal] PASS')
