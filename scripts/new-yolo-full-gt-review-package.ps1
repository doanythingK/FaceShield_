param(
    [string]$VideoPath,
    [string]$TemplateCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [string]$OutputDir = ".tmp\yolo-full-gt\review-package",
    [double]$PaddingRatio = 0.65,
    [int]$MaxRows = 0,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [switch]$IncludeFullFrameReview,
    [int]$FullFrameEveryN = 1,
    [int]$MaxFullFrameRows = 0,
    [int]$VideoFrameCount = 0,
    [int]$FullFrameScaleWidth = 0,
    [bool]$IncludeCandidateFramesInFullFrameReview = $true,
    [string]$RequiredFullFrameNumbers = "",
    [string]$FfmpegPath = "",
    [string]$FfprobePath = "",
    [switch]$Force,
    [switch]$RefreshIndexOnly
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0.0
    }

    return [double]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-IntValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0
    }

    return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-VideoSize {
    param([string]$Path)

    $ffprobe = Resolve-Tool -PreferredPath $FfprobePath -ToolName "ffprobe"
    if ($null -eq $ffprobe) {
        return $null
    }

    $sizeText = Invoke-Tool $ffprobe @("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", $Path)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sizeText)) {
        return $null
    }

    $parts = $sizeText.Trim().Split("x")
    if ($parts.Count -ne 2) {
        return $null
    }

    [pscustomobject]@{
        Width = [int]$parts[0]
        Height = [int]$parts[1]
    }
}

function Get-VideoFrameCount {
    param([string]$Path)

    $ffprobe = Resolve-Tool -PreferredPath $FfprobePath -ToolName "ffprobe"
    if ($null -eq $ffprobe) {
        return 0
    }

    $frameText = Invoke-Tool $ffprobe @("-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames,nb_frames", "-of", "default=nokey=1:noprint_wrappers=1", $Path)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($frameText)) {
        return 0
    }

    foreach ($line in @($frameText -split "`r?`n")) {
        $value = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($value) -or $value -eq "N/A") {
            continue
        }

        $parsed = 0
        if ([int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
            return $parsed
        }
    }

    return 0
}

function Get-CropRect {
    param(
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [int]$ImageWidth,
        [int]$ImageHeight,
        [double]$Padding
    )

    $padX = $W * $Padding
    $padY = $H * $Padding
    $left = [Math]::Max(0, [Math]::Floor($X - $padX))
    $top = [Math]::Max(0, [Math]::Floor($Y - $padY))
    $right = [Math]::Min($ImageWidth, [Math]::Ceiling($X + $W + $padX))
    $bottom = [Math]::Min($ImageHeight, [Math]::Ceiling($Y + $H + $padY))
    $cropW = [Math]::Max(1, [int]($right - $left))
    $cropH = [Math]::Max(1, [int]($bottom - $top))

    [pscustomobject]@{
        X = [int]$left
        Y = [int]$top
        W = $cropW
        H = $cropH
    }
}

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Resolve-Tool {
    param(
        [string]$PreferredPath,
        [string]$ToolName
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $resolved = Resolve-RepoPath $PreferredPath
        if (-not (Test-Path $resolved)) {
            throw "$ToolName not found: $resolved"
        }

        return [pscustomobject]@{
            Command = $resolved
            ToolName = ""
            UseWsl = $false
        }
    }

    $native = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $native) {
        return [pscustomobject]@{
            Command = $native.Source
            ToolName = ""
            UseWsl = $false
        }
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -ne $wsl) {
        return [pscustomobject]@{
            Command = $wsl.Source
            ToolName = $ToolName
            UseWsl = $true
        }
    }

    return $null
}

function Invoke-Tool {
    param(
        [object]$Tool,
        [string[]]$Arguments
    )

    if ($Tool.UseWsl) {
        $converted = @()
        foreach ($arg in $Arguments) {
            if ($arg -match '^[A-Za-z]:\\') {
                $converted += Convert-ToWslPath $arg
            }
            else {
                $converted += $arg
            }
        }

        return & $Tool.Command --exec $Tool.ToolName @converted
    }

    return & $Tool.Command @Arguments
}

function Write-PlaceholderImage {
    param(
        [object]$Tool,
        [string]$Path
    )

    Invoke-Tool $Tool @(
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "lavfi",
        "-i",
        "color=c=black:s=640x360",
        "-frames:v",
        "1",
        $Path
    ) | Out-Null

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Path)) {
        throw "ffmpeg placeholder crop failed for $Path with exit code $LASTEXITCODE"
    }
}

function Convert-ToHtmlText {
    param([string]$Text)

    return [System.Net.WebUtility]::HtmlEncode($Text)
}

function Get-DrawBoxFilter {
    param([object[]]$Rects)

    $segments = New-Object System.Collections.Generic.List[string]
    foreach ($rect in $Rects) {
        $x = [int]([Math]::Max(0, [Math]::Floor([double]$rect.X)))
        $y = [int]([Math]::Max(0, [Math]::Floor([double]$rect.Y)))
        $w = [int]([Math]::Max(1, [Math]::Ceiling([double]$rect.W)))
        $h = [int]([Math]::Max(1, [Math]::Ceiling([double]$rect.H)))
        $segments.Add(("drawbox=x={0}:y={1}:w={2}:h={3}:color=lime:t=4" -f $x, $y, $w, $h)) | Out-Null
    }

    return ($segments.ToArray() -join ",")
}

function Convert-ToRelativeImagePath {
    param(
        [string]$BaseDir,
        [string]$Path
    )

    $trimChars = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedBaseDir = (Resolve-Path $BaseDir).Path.TrimEnd($trimChars) + [IO.Path]::DirectorySeparatorChar
    $resolvedPath = (Resolve-Path $Path).Path
    $baseUri = [Uri]$resolvedBaseDir
    $pathUri = [Uri]$resolvedPath
    return $baseUri.MakeRelativeUri($pathUri).ToString()
}

function Write-ReviewIndexHtml {
    param(
        [string]$OutputDir,
        [object[]]$CropRows,
        [object[]]$FrameRows
    )

    $htmlPath = Join-Path $OutputDir "review-index.html"
    $builder = New-Object System.Text.StringBuilder

    [void]$builder.AppendLine("<!doctype html>")
    [void]$builder.AppendLine("<html lang=""en"">")
    [void]$builder.AppendLine("<head>")
    [void]$builder.AppendLine("<meta charset=""utf-8"">")
    [void]$builder.AppendLine("<meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
    [void]$builder.AppendLine("<title>YOLO full GT review package</title>")
    [void]$builder.AppendLine("<style>")
    [void]$builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;background:#f7f7f7;color:#111}h1,h2{margin:0 0 12px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px;margin-bottom:28px}.item{background:#fff;border:1px solid #ddd;padding:8px}.item img{width:100%;height:180px;object-fit:contain;background:#eee}.frame img{height:280px}.meta{font-size:12px;line-height:1.45;word-break:break-word}.muted{color:#555}.rules{background:#fff;border:1px solid #ddd;margin:16px 0 24px;padding:12px}.rules ul{margin:8px 0 0 20px;padding:0}.rules li{margin:4px 0}.status{font-size:12px;font-weight:600;margin:6px 0 4px}.pending{color:#9a3412}.done{color:#166534}.template{font-family:Consolas,monospace;background:#f3f4f6;padding:4px;margin-top:4px}")
    [void]$builder.AppendLine("</style>")
    [void]$builder.AppendLine("</head>")
    [void]$builder.AppendLine("<body>")
    [void]$builder.AppendLine("<h1>YOLO full GT review package</h1>")
    [void]$builder.AppendLine("<p class=""muted"">Fill full-gt-review.csv for detection crops, then full-frame-review.csv for missed-face scan evidence.</p>")
    [void]$builder.AppendLine("<section class=""rules"">")
    [void]$builder.AppendLine("<h2>Input Rules</h2>")
    [void]$builder.AppendLine("<ul>")
    [void]$builder.AppendLine("<li><strong>full-gt-review.csv label</strong>: use <code>face</code> for a real visible face, <code>nonface</code> for detector false positives, and <code>miss</code> for manual missed-face rows added from the full-frame scan.</li>")
    [void]$builder.AppendLine("<li><strong>reviewStatus</strong>: use <code>pass</code> only after reviewing the crop or frame, and fill <code>evidenceNotes</code>.</li>")
    [void]$builder.AppendLine("<li><strong>full-frame-review.csv missedFaceCount</strong>: count visible faces not covered by detection crop rows.</li>")
    [void]$builder.AppendLine("<li><strong>missedFaceRowsAdded</strong>: add the same number of manual missed-face rows to <code>full-gt-review.csv</code>.</li>")
    [void]$builder.AppendLine("<li>For missed faces, add <code>label=miss rows</code> with empty <code>sourcePredictionId</code>; do not use <code>miss</code> for detection crop rows.</li>")
    [void]$builder.AppendLine("<li>Each card shows its CSV row key, current pending fields, and suggested entry pattern.</li>")
    [void]$builder.AppendLine("</ul>")
    [void]$builder.AppendLine("</section>")

    [void]$builder.AppendLine("<h2>Detection crops</h2>")
    [void]$builder.AppendLine("<div class=""grid"">")
    $displayCropRows = @($CropRows | Where-Object {
        $null -ne $_.PSObject.Properties["cropPath"] -and
            -not [string]::IsNullOrWhiteSpace([string]$_.cropPath)
    })
    foreach ($row in $displayCropRows) {
        $relativePath = Convert-ToRelativeImagePath -BaseDir $OutputDir -Path $row.cropPath
        $missing = @()
        foreach ($column in @("label", "reviewStatus", "evidenceNotes")) {
            if ($null -eq $row.PSObject.Properties[$column] -or [string]::IsNullOrWhiteSpace([string]$row.PSObject.Properties[$column].Value)) {
                $missing += $column
            }
        }

        $statusClass = if ($missing.Count -eq 0) { "done" } else { "pending" }
        $statusText = if ($missing.Count -eq 0) { "complete" } else { "pending: $($missing -join ',')" }
        [void]$builder.AppendLine("<div class=""item"">")
        [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText $relativePath)"" alt=""crop frame $(Convert-ToHtmlText $row.frame)"">")
        [void]$builder.AppendLine("<div class=""status $statusClass"">$(Convert-ToHtmlText $statusText)</div>")
        [void]$builder.AppendLine("<div class=""meta"">frame=$(Convert-ToHtmlText $row.frame), pred=$(Convert-ToHtmlText $row.sourcePredictionId), conf=$(Convert-ToHtmlText $row.sourceConfidence)</div>")
        [void]$builder.AppendLine("<div class=""meta muted"">x=$(Convert-ToHtmlText $row.x), y=$(Convert-ToHtmlText $row.y), w=$(Convert-ToHtmlText $row.w), h=$(Convert-ToHtmlText $row.h)</div>")
        [void]$builder.AppendLine("<div class=""meta template"">CSV key: full-gt-review.csv frame=$(Convert-ToHtmlText $row.frame), sourcePredictionId=$(Convert-ToHtmlText $row.sourcePredictionId)</div>")
        [void]$builder.AppendLine("<div class=""meta template"">Set: label=face|nonface; reviewStatus=pass; evidenceNotes=visual reason</div>")
        [void]$builder.AppendLine("</div>")
    }
    [void]$builder.AppendLine("</div>")

    if ($FrameRows.Count -gt 0) {
        [void]$builder.AppendLine("<h2>Full-frame missed-face scan</h2>")
        [void]$builder.AppendLine("<div class=""grid"">")
        foreach ($row in $FrameRows) {
            $relativePath = Convert-ToRelativeImagePath -BaseDir $OutputDir -Path $row.frameImagePath
            $overlayRelativePath = ""
            if ($null -ne $row.PSObject.Properties["overlayFrameImagePath"] -and -not [string]::IsNullOrWhiteSpace($row.overlayFrameImagePath)) {
                $overlayRelativePath = Convert-ToRelativeImagePath -BaseDir $OutputDir -Path $row.overlayFrameImagePath
            }

            $missing = @()
            foreach ($column in @("missedFaceCount", "missedFaceRowsAdded", "reviewStatus", "evidenceNotes")) {
                if ($null -eq $row.PSObject.Properties[$column] -or [string]::IsNullOrWhiteSpace([string]$row.PSObject.Properties[$column].Value)) {
                    $missing += $column
                }
            }

            $statusClass = if ($missing.Count -eq 0) { "done" } else { "pending" }
            $statusText = if ($missing.Count -eq 0) { "complete" } else { "pending: $($missing -join ',')" }
            [void]$builder.AppendLine("<div class=""item frame"">")
            if (-not [string]::IsNullOrWhiteSpace($overlayRelativePath)) {
                [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText $overlayRelativePath)"" alt=""overlay frame $(Convert-ToHtmlText $row.frame)"">")
            }
            [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText $relativePath)"" alt=""full frame $(Convert-ToHtmlText $row.frame)"">")
            [void]$builder.AppendLine("<div class=""status $statusClass"">$(Convert-ToHtmlText $statusText)</div>")
            [void]$builder.AppendLine("<div class=""meta"">frame=$(Convert-ToHtmlText $row.frame), detectedCandidateCount=$(Convert-ToHtmlText $row.detectedCandidateCount)</div>")
            if ($null -ne $row.PSObject.Properties["candidateSummary"] -and -not [string]::IsNullOrWhiteSpace($row.candidateSummary)) {
                [void]$builder.AppendLine("<div class=""meta muted"">$(Convert-ToHtmlText $row.candidateSummary)</div>")
            }
            [void]$builder.AppendLine("<div class=""meta template"">CSV key: full-frame-review.csv frame=$(Convert-ToHtmlText $row.frame)</div>")
            [void]$builder.AppendLine("<div class=""meta template"">Set: missedFaceCount=N; missedFaceRowsAdded=N; reviewStatus=pass; evidenceNotes=frame scan reason</div>")
            [void]$builder.AppendLine("</div>")
        }
        [void]$builder.AppendLine("</div>")
    }

    [void]$builder.AppendLine("</body>")
    [void]$builder.AppendLine("</html>")

    $builder.ToString() | Set-Content -Encoding UTF8 -Path $htmlPath
    return $htmlPath
}

if (-not $RefreshIndexOnly -and [string]::IsNullOrWhiteSpace($VideoPath)) {
    throw "VideoPath is required."
}

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedTemplate = Resolve-RepoPath $TemplateCsv
$resolvedOutputDir = Resolve-RepoPath $OutputDir

if ($RefreshIndexOnly) {
    $reviewCsv = Join-Path $resolvedOutputDir "full-gt-review.csv"
    $frameReviewCsv = Join-Path $resolvedOutputDir "full-frame-review.csv"
    if (-not (Test-Path $reviewCsv)) {
        throw "Review CSV not found for index refresh: $reviewCsv"
    }

    $reviewRows = @(Import-Csv $reviewCsv)
    $frameReviewRows = @()
    if (Test-Path $frameReviewCsv) {
        $frameReviewRows = @(Import-Csv $frameReviewCsv)
    }

    $indexHtml = Write-ReviewIndexHtml -OutputDir $resolvedOutputDir -CropRows $reviewRows -FrameRows $frameReviewRows
    Write-Host "[YoloFullGtReviewIndex] refreshed path=$indexHtml"
    return
}

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

if (-not (Test-Path $resolvedTemplate)) {
    throw "Template CSV not found: $resolvedTemplate"
}

$ffmpeg = Resolve-Tool -PreferredPath $FfmpegPath -ToolName "ffmpeg"
if ($null -eq $ffmpeg) {
    throw "ffmpeg not found on PATH and wsl.exe is unavailable. Pass -FfmpegPath explicitly."
}

if ($FrameWidth -le 0 -or $FrameHeight -le 0) {
    $size = Get-VideoSize $resolvedVideo
    if ($null -eq $size) {
        throw "Could not determine video size. Pass -FrameWidth and -FrameHeight."
    }

    $FrameWidth = $size.Width
    $FrameHeight = $size.Height
}

$rows = @(Import-Csv $resolvedTemplate)
if ($rows.Count -eq 0) {
    throw "Template CSV has no rows: $resolvedTemplate"
}

if ($MaxRows -gt 0) {
    $rows = @($rows | Select-Object -First $MaxRows)
}

$cropDir = Join-Path $resolvedOutputDir "crops"
$frameDir = Join-Path $resolvedOutputDir "frames"
$reviewCsv = Join-Path $resolvedOutputDir "full-gt-review.csv"
$frameReviewCsv = Join-Path $resolvedOutputDir "full-frame-review.csv"
$reviewIndex = Join-Path $resolvedOutputDir "review-index.html"
$existingOutputs = @($reviewCsv, $frameReviewCsv, $reviewIndex, $cropDir, $frameDir) | Where-Object { Test-Path $_ }
if ($existingOutputs.Count -gt 0 -and -not $Force) {
    throw "Review package output already exists in $resolvedOutputDir. Pass -Force to overwrite it."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
if (Test-Path $cropDir) {
    Remove-Item -Recurse -Force -Path $cropDir
}
New-Item -ItemType Directory -Force -Path $cropDir | Out-Null

$reviewRows = New-Object System.Collections.Generic.List[object]
$index = 0
foreach ($row in $rows) {
    $frame = Read-IntValue $row "frame"
    $x = Read-DoubleValue $row "x"
    $y = Read-DoubleValue $row "y"
    $w = Read-DoubleValue $row "w"
    $h = Read-DoubleValue $row "h"
    $rect = Get-CropRect -X $x -Y $y -W $w -H $h -ImageWidth $FrameWidth -ImageHeight $FrameHeight -Padding $PaddingRatio
    $sourcePredictionId = $row.sourcePredictionId
    if ([string]::IsNullOrWhiteSpace($sourcePredictionId)) {
        $sourcePredictionId = $index.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    $cropFileName = "frame-{0:D6}-pred-{1}.png" -f $frame, ($sourcePredictionId -replace '[^A-Za-z0-9_.-]', '_')
    $cropPath = Join-Path $cropDir $cropFileName
    $filter = "select=eq(n\,$frame),crop=$($rect.W):$($rect.H):$($rect.X):$($rect.Y)"
    Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $resolvedVideo, "-vf", $filter, "-frames:v", "1", $cropPath) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg crop failed for frame $frame with exit code $LASTEXITCODE"
    }

    $cropAvailable = Test-Path $cropPath
    $notes = $row.notes
    if (-not $cropAvailable) {
        Write-PlaceholderImage -Tool $ffmpeg -Path $cropPath
        $notes = ([string]$notes).Trim()
        $unavailableNote = "Crop extraction unavailable for frame $frame; the video may report more frames than can be decoded. Review the full-frame evidence or source video before using this row as GT."
        $notes = if ([string]::IsNullOrWhiteSpace($notes)) { $unavailableNote } else { "$notes $unavailableNote" }
    }

    $reviewRows.Add([pscustomobject]@{
        frame = $row.frame
        gtId = $row.gtId
        label = $row.label
        x = $row.x
        y = $row.y
        w = $row.w
        h = $row.h
        sourcePredictionId = $row.sourcePredictionId
        sourceConfidence = $row.sourceConfidence
        source = $row.source
        cropPath = $cropPath
        cropX = $rect.X
        cropY = $rect.Y
        cropW = $rect.W
        cropH = $rect.H
        reviewStatus = ""
        evidenceNotes = ""
        notes = $notes
    }) | Out-Null
    $index++
}

$reviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

$frameReviewRows = New-Object System.Collections.Generic.List[object]
if ($IncludeFullFrameReview) {
    if ($FullFrameEveryN -le 0) {
        throw "FullFrameEveryN must be greater than zero."
    }

    if ($VideoFrameCount -le 0) {
        $VideoFrameCount = Get-VideoFrameCount $resolvedVideo
    }

    if ($VideoFrameCount -le 0) {
        throw "Could not determine video frame count. Pass -VideoFrameCount explicitly."
    }

    if (Test-Path $frameDir) {
        Remove-Item -Recurse -Force -Path $frameDir
    }
    New-Item -ItemType Directory -Force -Path $frameDir | Out-Null

    $candidateCountByFrame = @{}
    $candidateRectsByFrame = @{}
    $candidateSummaryByFrame = @{}
    foreach ($row in $rows) {
        $frame = Read-IntValue $row "frame"
        if (-not $candidateCountByFrame.ContainsKey($frame)) {
            $candidateCountByFrame[$frame] = 0
        }

        $candidateCountByFrame[$frame]++

        if (-not $candidateRectsByFrame.ContainsKey($frame)) {
            $candidateRectsByFrame[$frame] = New-Object System.Collections.Generic.List[object]
        }

        $candidateRectsByFrame[$frame].Add([pscustomobject]@{
            X = Read-DoubleValue $row "x"
            Y = Read-DoubleValue $row "y"
            W = Read-DoubleValue $row "w"
            H = Read-DoubleValue $row "h"
        }) | Out-Null

        if (-not $candidateSummaryByFrame.ContainsKey($frame)) {
            $candidateSummaryByFrame[$frame] = New-Object System.Collections.Generic.List[string]
        }

        $candidateSummaryByFrame[$frame].Add(("pred={0},conf={1},box={2}/{3}/{4}/{5}" -f $row.sourcePredictionId, $row.sourceConfidence, $row.x, $row.y, $row.w, $row.h)) | Out-Null
    }

    $candidateFrameNumbers = New-Object System.Collections.Generic.List[int]
    $candidateSeenFrames = @{}
    if ($IncludeCandidateFramesInFullFrameReview) {
        foreach ($row in $rows) {
            $candidateFrame = Read-IntValue $row "frame"
            if ($candidateFrame -ge 0 -and $candidateFrame -lt $VideoFrameCount -and -not $candidateSeenFrames.ContainsKey($candidateFrame)) {
                $candidateSeenFrames[$candidateFrame] = $true
                $candidateFrameNumbers.Add($candidateFrame) | Out-Null
            }
        }
    }

    $intervalFrameNumbers = New-Object System.Collections.Generic.List[int]
    $intervalSeenFrames = @{}
    for ($frame = 0; $frame -lt $VideoFrameCount; $frame += $FullFrameEveryN) {
        if (-not $candidateSeenFrames.ContainsKey($frame) -and -not $intervalSeenFrames.ContainsKey($frame)) {
            $intervalSeenFrames[$frame] = $true
            $intervalFrameNumbers.Add($frame) | Out-Null
        }
    }

    $selectedFrames = New-Object System.Collections.Generic.List[int]
    $selectedSeenFrames = @{}
    $requiredFullFrames = @(
        $RequiredFullFrameNumbers -split "," |
            Where-Object { $_ -match '^\d+$' } |
            ForEach-Object { [int]$_ } |
            Sort-Object -Unique
    )
    foreach ($frame in $requiredFullFrames) {
        if ($frame -ge 0 -and
            ($VideoFrameCount -le 0 -or $frame -lt $VideoFrameCount) -and
            -not $selectedSeenFrames.ContainsKey($frame)) {
            $selectedSeenFrames[$frame] = $true
            $selectedFrames.Add($frame) | Out-Null
        }
    }

    foreach ($frame in @($candidateFrameNumbers | Sort-Object)) {
        if ($MaxFullFrameRows -gt 0 -and $selectedFrames.Count -ge $MaxFullFrameRows) {
            break
        }

        if ($selectedSeenFrames.ContainsKey($frame)) {
            continue
        }

        $selectedSeenFrames[$frame] = $true
        $selectedFrames.Add($frame) | Out-Null
    }

    foreach ($frame in @($intervalFrameNumbers | Sort-Object)) {
        if ($MaxFullFrameRows -gt 0 -and $selectedFrames.Count -ge $MaxFullFrameRows) {
            break
        }

        if ($selectedSeenFrames.ContainsKey($frame)) {
            continue
        }

        $selectedSeenFrames[$frame] = $true
        $selectedFrames.Add($frame) | Out-Null
    }

    foreach ($frame in @($selectedFrames | Sort-Object)) {
        $frameFileName = "frame-{0:D6}.png" -f $frame
        $overlayFrameFileName = "frame-{0:D6}-overlay.png" -f $frame
        $framePath = Join-Path $frameDir $frameFileName
        $overlayFramePath = Join-Path $frameDir $overlayFrameFileName
        $filter = "select=eq(n\,$frame)"
        if ($FullFrameScaleWidth -gt 0) {
            $filter = "$filter,scale=$($FullFrameScaleWidth):-2"
        }

        Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $resolvedVideo, "-vf", $filter, "-frames:v", "1", $framePath) | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "ffmpeg full-frame extraction failed for frame $frame with exit code $LASTEXITCODE"
        }
        $frameAvailable = Test-Path $framePath
        if (-not $frameAvailable) {
            Write-PlaceholderImage -Tool $ffmpeg -Path $framePath
        }

        $overlayFilter = "select=eq(n\,$frame)"
        if ($candidateRectsByFrame.ContainsKey($frame)) {
            $drawBoxFilter = Get-DrawBoxFilter -Rects ($candidateRectsByFrame[$frame]).ToArray()
            if (-not [string]::IsNullOrWhiteSpace($drawBoxFilter)) {
                $overlayFilter = "$overlayFilter,$drawBoxFilter"
            }
        }

        if ($FullFrameScaleWidth -gt 0) {
            $overlayFilter = "$overlayFilter,scale=$($FullFrameScaleWidth):-2"
        }

        Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $resolvedVideo, "-vf", $overlayFilter, "-frames:v", "1", $overlayFramePath) | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "ffmpeg full-frame overlay extraction failed for frame $frame with exit code $LASTEXITCODE"
        }
        if (-not (Test-Path $overlayFramePath)) {
            Write-PlaceholderImage -Tool $ffmpeg -Path $overlayFramePath
        }

        $candidateCount = 0
        $candidateSummary = ""
        if ($candidateCountByFrame.ContainsKey($frame)) {
            $candidateCount = $candidateCountByFrame[$frame]
        }
        if ($candidateSummaryByFrame.ContainsKey($frame)) {
            $candidateSummary = ($candidateSummaryByFrame[$frame].ToArray() -join "; ")
        }

        $notes = "Review this full frame for visible faces that are not covered by detection crop rows. Add missed face rows to full-gt-review.csv."
        if (-not $frameAvailable) {
            $notes = "Full-frame extraction unavailable for frame $frame; the video may report more frames than can be decoded. Review the source video before using this row as GT."
        }

        $frameReviewRows.Add([pscustomobject]@{
            frame = $frame
            frameImagePath = $framePath
            overlayFrameImagePath = $overlayFramePath
            detectedCandidateCount = $candidateCount
            candidateSummary = $candidateSummary
            missedFaceCount = ""
            missedFaceRowsAdded = ""
            reviewStatus = ""
            evidenceNotes = ""
            notes = $notes
        }) | Out-Null
    }

    $frameReviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameReviewCsv
    Write-Host "[YoloFullGtFrameReviewPackage] wrote rows=$($frameReviewRows.Count), frameReviewCsv=$frameReviewCsv, frameDir=$frameDir"
    Write-Host "[YoloFullGtFrameReviewPackage] label instructions: set missedFaceCount, reviewStatus, and evidenceNotes after reviewing frameImagePath; add label=miss rows to full-gt-review.csv for each missed face."
}

$indexHtml = Write-ReviewIndexHtml -OutputDir $resolvedOutputDir -CropRows $reviewRows.ToArray() -FrameRows $frameReviewRows.ToArray()
Write-Host "[YoloFullGtReviewIndex] wrote path=$indexHtml"
Write-Host "[YoloFullGtReviewPackage] wrote rows=$($reviewRows.Count), reviewCsv=$reviewCsv, cropDir=$cropDir"
Write-Host "[YoloFullGtReviewPackage] label instructions: set label=face/nonface/miss and evidenceNotes after reviewing cropPath images; add label=miss rows for visible missed faces."
