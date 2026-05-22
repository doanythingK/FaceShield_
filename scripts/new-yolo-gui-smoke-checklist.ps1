param(
    [string]$OutputCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputPath = if ([IO.Path]::IsPathRooted($OutputCsv)) { $OutputCsv } else { Join-Path $repo $OutputCsv }
$outputDir = Split-Path -Parent $outputPath
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$rows = @(
    [pscustomobject]@{
        stepId = "open-video"
        status = ""
        evidence = ""
        notes = "Open a short srcTest video from the Home screen and confirm Workspace opens with preview frames."
    },
    [pscustomobject]@{
        stepId = "select-yolo-backend"
        status = ""
        evidence = ""
        notes = "Select YOLO Face ONNX, choose YOLO5Face or YOLOv8-Face, set a model path, and confirm FaceONNX threshold controls remain separate."
    },
    [pscustomobject]@{
        stepId = "run-yolo-auto-detect"
        status = ""
        evidence = ""
        notes = "Run automatic mosaic with YOLO selected and confirm progress/status completes without crashing."
    },
    [pscustomobject]@{
        stepId = "preview-result"
        status = ""
        evidence = ""
        notes = "Scrub or play preview frames and confirm detected faces are masked without obvious flicker on the tested clip."
    },
    [pscustomobject]@{
        stepId = "manual-edit"
        status = ""
        evidence = ""
        notes = "Use manual/brush/eraser or undo workflow on at least one frame and confirm the preview reflects the edit."
    },
    [pscustomobject]@{
        stepId = "export"
        status = ""
        evidence = ""
        notes = "Export the edited YOLO workspace and confirm output file is created and playable."
    },
    [pscustomobject]@{
        stepId = "reopen-state"
        status = ""
        evidence = ""
        notes = "Reopen the same workspace and confirm YOLO model/profile settings and mask state are restored."
    }
)

$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
Write-Host "[YoloGuiSmokeChecklist] wrote rows=$($rows.Count), output=$OutputCsv"
Write-Host "[YoloGuiSmokeChecklist] Fill status=pass for each row after manual GUI verification. Use status=fail with notes for failures."
