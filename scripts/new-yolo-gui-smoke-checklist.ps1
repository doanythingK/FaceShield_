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
        evidenceType = "screenshot-or-recording"
        artifactPath = ""
        evidence = ""
        notes = "Open a short srcTest video from the Home screen and confirm Workspace opens with preview frames. Attach a screenshot or recording path."
    },
    [pscustomobject]@{
        stepId = "select-yolo-backend"
        status = ""
        evidenceType = "screenshot"
        artifactPath = ""
        evidence = ""
        notes = "Select YOLO Face ONNX, choose YOLO5Face or YOLOv8-Face, set a model path, and confirm FaceONNX threshold controls remain separate. Attach a screenshot path."
    },
    [pscustomobject]@{
        stepId = "run-yolo-auto-detect"
        status = ""
        evidenceType = "screenshot-or-log"
        artifactPath = ""
        evidence = ""
        notes = "Run automatic mosaic with YOLO selected and confirm progress/status completes without crashing. Attach a completion screenshot or log path."
    },
    [pscustomobject]@{
        stepId = "preview-result"
        status = ""
        evidenceType = "screenshot-or-recording"
        artifactPath = ""
        evidence = ""
        notes = "Scrub or play preview frames and confirm detected faces are masked without obvious flicker on the tested clip. Attach a screenshot or recording path."
    },
    [pscustomobject]@{
        stepId = "manual-edit"
        status = ""
        evidenceType = "screenshot-or-recording"
        artifactPath = ""
        evidence = ""
        notes = "Use manual/brush/eraser or undo workflow on at least one frame and confirm the preview reflects the edit. Attach before/after screenshot or recording path."
    },
    [pscustomobject]@{
        stepId = "export"
        status = ""
        evidenceType = "output-file"
        artifactPath = ""
        evidence = ""
        notes = "Export the edited YOLO workspace and confirm output file is created and playable. artifactPath must point to the exported video file."
    },
    [pscustomobject]@{
        stepId = "reopen-state"
        status = ""
        evidenceType = "screenshot-or-recording"
        artifactPath = ""
        evidence = ""
        notes = "Reopen the same workspace and confirm YOLO model/profile settings and mask state are restored. Attach a screenshot or recording path."
    }
)

$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
Write-Host "[YoloGuiSmokeChecklist] wrote rows=$($rows.Count), output=$OutputCsv"
Write-Host "[YoloGuiSmokeChecklist] Fill status=pass for each row after manual GUI verification. Use status=fail with notes for failures."
