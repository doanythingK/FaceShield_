function Get-YoloDefaultModelFileNames {
    param(
        [string]$YoloModelType = "Yolo5Face"
    )

    if ($YoloModelType -eq "Yolo5Face") {
        return @("YoloV5Face.onnx", "Yolo5Face.onnx")
    }

    return @(
        "yolov8n-face-lindevs.onnx",
        "yolov8s-face-lindevs.onnx",
        "yolov8m-face-lindevs.onnx",
        "yolov8l-face-lindevs.onnx"
    )
}

function Resolve-YoloModelPath {
    param(
        [string]$Repo,
        [string]$YoloModelPath = "",
        [string]$YoloModelType = "Yolo5Face",
        [switch]$Require
    )

    if ([string]::IsNullOrWhiteSpace($Repo)) {
        throw "Repo path is required"
    }

    if (-not [string]::IsNullOrWhiteSpace($YoloModelPath)) {
        $explicit = if ([IO.Path]::IsPathRooted($YoloModelPath)) { $YoloModelPath } else { Join-Path $Repo $YoloModelPath }
        if ($Require -and -not (Test-Path $explicit)) {
            throw "YOLO model not found: $explicit"
        }

        return $explicit
    }

    $directories = @(
        (Join-Path $Repo "Models\Yolo"),
        (Join-Path $Repo ".tmp\models")
    )
    $fileNames = Get-YoloDefaultModelFileNames -YoloModelType $YoloModelType

    foreach ($directory in $directories) {
        foreach ($fileName in $fileNames) {
            $candidate = Join-Path $directory $fileName
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    if ($Require) {
        $relativeNames = ($fileNames | ForEach-Object { "Models/Yolo/$_" }) -join ", "
        throw "YOLO model not found. Put a model at $relativeNames or pass -YoloModelPath."
    }

    return ""
}
