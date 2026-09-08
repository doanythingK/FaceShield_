from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VM_PATH = ROOT / "ViewModels" / "Pages" / "WorkspaceViewModel.cs"
COORD_PATH = ROOT / "ViewModels" / "Workspace" / "WorkspaceExportCoordinator.cs"
LOG_PATH = ROOT / "Services" / "Diagnostics" / "RunMetricsLog.cs"
STATUS_PATH = ROOT / "OWNERSHIP_WORKSPACE_HARDENING_STATUS.md"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 exact match, found {count}")
    return text.replace(old, new, 1)


vm = VM_PATH.read_text(encoding="utf-8-sig")
method_start = "        private static void LogExportQualityGate(\n"
next_method = "        private static string? SerializeAutoExportHybridDisableReasons(\n"
start = vm.find(method_start)
end = vm.find(next_method, start + 1)
if start < 0 or end < 0 or end <= start:
    raise RuntimeError("workspace export quality diagnostics block not found")

quality_block = vm[start:end]
if "private static string FormatTextListForLog(" not in quality_block:
    raise RuntimeError("quality diagnostics helper was not captured with the block")
quality_block = quality_block.replace(
    "        private static void LogExportQualityGate(\n",
    "        public static void AppendExportQualityGate(\n",
    1,
)

vm = vm[:start] + vm[end:]
vm = replace_once(
    vm,
    "                EndLifetimeOperation,\n                ResolveExportOutputPathAsync,\n                LogExportQualityGate);",
    "                EndLifetimeOperation,\n                ResolveExportOutputPathAsync);",
    "workspace export coordinator construction",
)
VM_PATH.write_text(vm, encoding="utf-8")

coord = COORD_PATH.read_text(encoding="utf-8-sig")
coord = replace_once(
    coord,
    "    private readonly Func<string, Task<(string? Path, bool AllowOverwrite)>> _resolveOutputPathAsync;\n    private readonly Action<AutoMaskRunSummary?, ExportRunSummary, bool, IReadOnlyList<string>?> _logQualityGate;\n",
    "    private readonly Func<string, Task<(string? Path, bool AllowOverwrite)>> _resolveOutputPathAsync;\n",
    "remove export diagnostics delegate field",
)
coord = replace_once(
    coord,
    "        Action endLifetimeOperation,\n        Func<string, Task<(string? Path, bool AllowOverwrite)>> resolveOutputPathAsync,\n        Action<AutoMaskRunSummary?, ExportRunSummary, bool, IReadOnlyList<string>?> logQualityGate)",
    "        Action endLifetimeOperation,\n        Func<string, Task<(string? Path, bool AllowOverwrite)>> resolveOutputPathAsync)",
    "remove export diagnostics constructor parameter",
)
coord = replace_once(
    coord,
    "        _resolveOutputPathAsync = resolveOutputPathAsync ?? throw new ArgumentNullException(nameof(resolveOutputPathAsync));\n        _logQualityGate = logQualityGate ?? throw new ArgumentNullException(nameof(logQualityGate));\n",
    "        _resolveOutputPathAsync = resolveOutputPathAsync ?? throw new ArgumentNullException(nameof(resolveOutputPathAsync));\n",
    "remove export diagnostics delegate assignment",
)
coord = replace_once(
    coord,
    "                _logQualityGate(\n",
    "                RunMetricsLog.AppendExportQualityGate(\n",
    "delegate export quality diagnostics directly to diagnostics service",
)
COORD_PATH.write_text(coord, encoding="utf-8")

log = LOG_PATH.read_text(encoding="utf-8-sig")
if "using FaceShield.Services.Analysis;" not in log:
    log = "using FaceShield.Services.Analysis;\nusing FaceShield.Services.Video;\n" + log
insert_before = "        private static string NormalizeLine(string value)\n"
if insert_before not in log:
    raise RuntimeError("RunMetricsLog insertion anchor not found")
log = log.replace(insert_before, quality_block + insert_before, 1)
LOG_PATH.write_text(log, encoding="utf-8")

status = STATUS_PATH.read_text(encoding="utf-8-sig")
status = replace_once(
    status,
    "Status: **IN PROGRESS**\n\nResume the historical ownership/workspace hardening sequence by continuing responsibility/policy extraction from `WorkspaceViewModel` and its coordinators. The next concrete target is export quality-gate diagnostics: export result risk calculation/logging belongs with export diagnostics rather than in the page ViewModel and should not be injected back into `WorkspaceExportCoordinator` as a ViewModel callback.\n",
    "Status: **COMPLETED — export quality-gate diagnostics extracted**\n\n- [x] Move export quality/risk calculation and logging out of `WorkspaceViewModel` into `RunMetricsLog`.\n- [x] Remove the quality-log callback dependency from `WorkspaceExportCoordinator`; the coordinator now calls the diagnostics service directly.\n- [x] Keep export behavior and log payloads unchanged while reducing page ViewModel responsibility.\n\nNext: continue the historical responsibility/policy-boundary hardening sequence without reopening deferred PATH edge cases unless a reproduced failure requires it.\n",
    "hardening status active block",
)
STATUS_PATH.write_text(status, encoding="utf-8")

# Structural verification after applying the patch.
vm_check = VM_PATH.read_text(encoding="utf-8")
coord_check = COORD_PATH.read_text(encoding="utf-8")
log_check = LOG_PATH.read_text(encoding="utf-8")
if "LogExportQualityGate" in vm_check:
    raise RuntimeError("WorkspaceViewModel still owns export quality diagnostics")
if "private static string FormatTextListForLog(" in vm_check:
    raise RuntimeError("WorkspaceViewModel still owns export quality log formatting")
if "_logQualityGate" in coord_check or "logQualityGate" in coord_check:
    raise RuntimeError("WorkspaceExportCoordinator still carries the ViewModel diagnostics callback")
if "RunMetricsLog.AppendExportQualityGate(" not in coord_check:
    raise RuntimeError("WorkspaceExportCoordinator does not call RunMetricsLog directly")
if "public static void AppendExportQualityGate(" not in log_check:
    raise RuntimeError("RunMetricsLog export quality diagnostics method missing")
if "private static string FormatTextListForLog(" not in log_check:
    raise RuntimeError("RunMetricsLog quality formatting helper missing")

print("[ExportQualityDiagnosticsExtraction] PASS")
