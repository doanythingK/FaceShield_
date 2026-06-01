param(
    [string]$DraftReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-gt-review-draft.csv",
    [string]$DraftFullFrameReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-frame-review-draft.csv",
    [string]$OutputDir = ".tmp\yolo-pseudo-gt\review-visual",
    [string]$FrameSourceDir = ".tmp\yolo-full-gt\review-package-smoke\frames",
    [string]$VideoPath = "",
    [double]$PaddingRatio = 0.65,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [string]$FfmpegPath = "",
    [string]$FfprobePath = "",
    [switch]$Force,
    [switch]$Verify,
    [switch]$RequireAllVisuals
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

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "$Name is not a file: $resolved"
    }

    if ($item.Length -le 0) {
        throw "$Name is empty: $resolved"
    }

    return $resolved
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

function Get-CsvValue {
    param(
        [object]$Row,
        [string]$Column
    )

    if ($null -eq $Row -or $null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = Get-CsvValue $Row $Name
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

    $value = Get-CsvValue $Row $Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0
    }

    return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-MediaSize {
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

    if ($ImageWidth -le 0 -or $ImageHeight -le 0) {
        throw "Invalid image size: $($ImageWidth)x$($ImageHeight)"
    }

    $padX = $W * $Padding
    $padY = $H * $Padding
    $left = [Math]::Min($ImageWidth - 1, [Math]::Max(0, [Math]::Floor($X - $padX)))
    $top = [Math]::Min($ImageHeight - 1, [Math]::Max(0, [Math]::Floor($Y - $padY)))
    $right = [Math]::Max($left + 1, [Math]::Min($ImageWidth, [Math]::Ceiling($X + $W + $padX)))
    $bottom = [Math]::Max($top + 1, [Math]::Min($ImageHeight, [Math]::Ceiling($Y + $H + $padY)))

    [pscustomobject]@{
        X = [int]$left
        Y = [int]$top
        W = [Math]::Max(1, [int]($right - $left))
        H = [Math]::Max(1, [int]($bottom - $top))
    }
}

function Get-VisualSourceSize {
    param(
        [string]$SourceFrame,
        [int]$FallbackWidth,
        [int]$FallbackHeight
    )

    $sourceSize = Get-MediaSize $SourceFrame
    if ($null -ne $sourceSize -and $sourceSize.Width -gt 0 -and $sourceSize.Height -gt 0) {
        return $sourceSize
    }

    if ($FallbackWidth -gt 0 -and $FallbackHeight -gt 0) {
        return [pscustomobject]@{
            Width = $FallbackWidth
            Height = $FallbackHeight
        }
    }

    throw "Could not determine frame size. Pass -FrameWidth and -FrameHeight."
}

function Get-ClampedBoxRect {
    param(
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [int]$ImageWidth,
        [int]$ImageHeight
    )

    if ($ImageWidth -le 0 -or $ImageHeight -le 0) {
        throw "Invalid image size: $($ImageWidth)x$($ImageHeight)"
    }

    $left = [Math]::Min($ImageWidth - 1, [Math]::Max(0, [Math]::Floor($X)))
    $top = [Math]::Min($ImageHeight - 1, [Math]::Max(0, [Math]::Floor($Y)))
    $right = [Math]::Max($left + 1, [Math]::Min($ImageWidth, [Math]::Ceiling($X + $W)))
    $bottom = [Math]::Max($top + 1, [Math]::Min($ImageHeight, [Math]::Ceiling($Y + $H)))

    [pscustomobject]@{
        X = [int]$left
        Y = [int]$top
        W = [Math]::Max(1, [int]($right - $left))
        H = [Math]::Max(1, [int]($bottom - $top))
    }
}

function Convert-ToSafeName {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return "blank"
    }

    return $Text -replace '[^A-Za-z0-9_.-]', '_'
}

function Convert-ToHtmlText {
    param([string]$Text)

    return [System.Net.WebUtility]::HtmlEncode($Text)
}

function Convert-ToRelativeLink {
    param(
        [string]$BaseDir,
        [string]$TargetPath
    )

    if ([string]::IsNullOrWhiteSpace($TargetPath) -or -not (Test-Path $TargetPath)) {
        return ""
    }

    $trimChars = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedBaseDir = (Resolve-Path $BaseDir).Path.TrimEnd($trimChars) + [IO.Path]::DirectorySeparatorChar
    $resolvedTargetPath = (Resolve-Path $TargetPath).Path
    $baseUri = [Uri]$resolvedBaseDir
    $targetUri = [Uri]$resolvedTargetPath
    return $baseUri.MakeRelativeUri($targetUri).ToString()
}

function Add-OutputProperty {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Target,
        [string]$Name,
        [string]$Value
    )

    if ($Target.Contains($Name)) {
        $Target[$Name] = $Value
    }
    else {
        $Target.Add($Name, $Value)
    }
}

function Copy-RowToDictionary {
    param([object]$Row)

    $values = [ordered]@{}
    foreach ($property in $Row.PSObject.Properties) {
        $values[$property.Name] = [string]$property.Value
    }

    return $values
}

function Get-FrameImagePath {
    param(
        [int]$Frame,
        [string]$FrameDir,
        [hashtable]$DraftFrameMap,
        [string]$OutputFrameDir,
        [string]$ResolvedVideo,
        [object]$Ffmpeg
    )

    $frameKey = "$Frame"
    if ($DraftFrameMap.ContainsKey($frameKey)) {
        foreach ($column in @("frameImagePath", "overlayFrameImagePath")) {
            $candidate = Resolve-RepoPath (Get-CsvValue $DraftFrameMap[$frameKey] $column)
            if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
                return $candidate
            }
        }
    }

    foreach ($name in @(
        ("frame-{0:D6}.png" -f $Frame),
        ("frame-{0}.png" -f $Frame),
        ("frame-$Frame.png")
    )) {
        $candidate = Join-Path $FrameDir $name
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ResolvedVideo)) {
        New-Item -ItemType Directory -Force -Path $OutputFrameDir | Out-Null
        $extracted = Join-Path $OutputFrameDir ("frame-{0:D6}.png" -f $Frame)
        $filter = "select=eq(n\,$Frame)"
        Invoke-Tool $Ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $ResolvedVideo, "-vf", $filter, "-frames:v", "1", $extracted) | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $extracted)) {
            return $extracted
        }
    }

    return ""
}

function Write-ReviewIndexHtml {
    param(
        [string]$Path,
        [object[]]$Rows,
        [string]$VisualDraftCsv,
        [string]$VisualFrameCsv,
        [int]$MissingVisualRows,
        [string[]]$MissingVisualFrames,
        [string[]]$MissingVisualCandidateIds,
        [string]$SuggestedVideoRerunCommand
    )

    $baseDir = Split-Path -Parent $Path
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine("<!doctype html>")
    [void]$builder.AppendLine("<html lang=""en"">")
    [void]$builder.AppendLine("<head>")
    [void]$builder.AppendLine("<meta charset=""utf-8"">")
    [void]$builder.AppendLine("<meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
    [void]$builder.AppendLine("<title>YOLO pseudo-GT visual review package</title>")
    [void]$builder.AppendLine("<style>")
    [void]$builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;background:#f7f7f7;color:#111;line-height:1.45}h1,h2{margin:0 0 12px}.notice,.item{background:#fff;border:1px solid #ddd;padding:12px;margin-bottom:12px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:12px}.item img{width:100%;height:220px;object-fit:contain;background:#eee;margin:4px 0}.meta{font-size:12px;word-break:break-word}.muted{color:#555}.badge{display:inline-block;font-size:12px;font-weight:700;padding:2px 6px;border:1px solid #ccc;background:#f3f4f6;margin-right:4px}.warn{color:#9a3412;font-weight:700}code{background:#f3f4f6;padding:1px 3px}")
    [void]$builder.AppendLine("</style>")
    [void]$builder.AppendLine("</head>")
    [void]$builder.AppendLine("<body>")
    [void]$builder.AppendLine("<h1>YOLO pseudo-GT visual review package</h1>")
    [void]$builder.AppendLine("<section class=""notice"">")
    [void]$builder.AppendLine("<p>This package is test-only visual review evidence. It does not finalize face/nonface/miss labels and is not part of the app runtime path.</p>")
    [void]$builder.AppendLine("<p>Fill <code>label</code>, <code>reviewStatus</code>, and <code>evidenceNotes</code> only after human visual confirmation. Do not copy <code>suggestedLabel</code> blindly.</p>")
    [void]$builder.AppendLine("<p>Visual draft: <code>$(Convert-ToHtmlText (Convert-ToRelativeLink -BaseDir $baseDir -TargetPath $VisualDraftCsv))</code></p>")
    [void]$builder.AppendLine("<p>Full-frame draft: <code>$(Convert-ToHtmlText (Convert-ToRelativeLink -BaseDir $baseDir -TargetPath $VisualFrameCsv))</code></p>")
    [void]$builder.AppendLine("<p class=""warn"">missingVisualRows=$MissingVisualRows</p>")
    if ($MissingVisualRows -gt 0) {
        [void]$builder.AppendLine("<p class=""warn"">missingVisualFrames=$(Convert-ToHtmlText (($MissingVisualFrames | Select-Object -First 80) -join ","))</p>")
        [void]$builder.AppendLine("<p class=""warn"">missingVisualCandidateIds=$(Convert-ToHtmlText (($MissingVisualCandidateIds | Select-Object -First 80) -join ","))</p>")
        [void]$builder.AppendLine("<p>To fill missing visuals, rerun with the short problem clip: <code>$(Convert-ToHtmlText $SuggestedVideoRerunCommand)</code></p>")
    }
    [void]$builder.AppendLine("</section>")
    [void]$builder.AppendLine("<div class=""grid"">")
    foreach ($row in $Rows) {
        $crop = Get-CsvValue $row "cropPath"
        $overlay = Get-CsvValue $row "visualOverlayPath"
        $frameImage = Get-CsvValue $row "visualFramePath"
        [void]$builder.AppendLine("<article class=""item"">")
        [void]$builder.AppendLine("<div><span class=""badge"">$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_candidateType"))</span><span class=""badge"">suggested=$(Convert-ToHtmlText (Get-CsvValue $row "suggestedLabel"))</span></div>")
        if (-not [string]::IsNullOrWhiteSpace($crop) -and (Test-Path $crop)) {
            [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText (Convert-ToRelativeLink -BaseDir $baseDir -TargetPath $crop))"" alt=""crop"">")
        }
        if (-not [string]::IsNullOrWhiteSpace($overlay) -and (Test-Path $overlay)) {
            [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText (Convert-ToRelativeLink -BaseDir $baseDir -TargetPath $overlay))"" alt=""overlay"">")
        }
        elseif (-not [string]::IsNullOrWhiteSpace($frameImage) -and (Test-Path $frameImage)) {
            [void]$builder.AppendLine("<img src=""$(Convert-ToHtmlText (Convert-ToRelativeLink -BaseDir $baseDir -TargetPath $frameImage))"" alt=""frame"">")
        }
        else {
            [void]$builder.AppendLine("<p class=""warn"">No visual source found for this candidate.</p>")
        }
        [void]$builder.AppendLine("<div class=""meta"">frame=$(Convert-ToHtmlText (Get-CsvValue $row "frame")), pred=$(Convert-ToHtmlText (Get-CsvValue $row "sourcePredictionId")), gtId=$(Convert-ToHtmlText (Get-CsvValue $row "gtId"))</div>")
        [void]$builder.AppendLine("<div class=""meta muted"">x=$(Convert-ToHtmlText (Get-CsvValue $row "x")), y=$(Convert-ToHtmlText (Get-CsvValue $row "y")), w=$(Convert-ToHtmlText (Get-CsvValue $row "w")), h=$(Convert-ToHtmlText (Get-CsvValue $row "h"))</div>")
        [void]$builder.AppendLine("<div class=""meta muted"">tileFaceConfidence=$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_tileFaceConfidence")), faceVerificationConfidence=$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_faceVerificationConfidence")), fpProbability=$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_fpProbability")), missProbability=$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_missProbability"))</div>")
        [void]$builder.AppendLine("<div class=""meta"">reason=$(Convert-ToHtmlText (Get-CsvValue $row "pseudoGt_pseudoGtReason"))</div>")
        [void]$builder.AppendLine("</article>")
    }
    [void]$builder.AppendLine("</div>")
    [void]$builder.AppendLine("</body>")
    [void]$builder.AppendLine("</html>")

    $builder.ToString() | Set-Content -Encoding UTF8 -Path $Path
}

$draftPath = Assert-FileNonEmpty "pseudo-GT draft review CSV" $DraftReviewCsv
$frameDraftPath = Resolve-RepoPath $DraftFullFrameReviewCsv
$resolvedOutputDir = Resolve-RepoPath $OutputDir
$resolvedFrameSourceDir = Resolve-RepoPath $FrameSourceDir
$resolvedVideo = Resolve-RepoPath $VideoPath

if ((Test-Path $resolvedOutputDir) -and -not $Force) {
    throw "OutputDir already exists: $resolvedOutputDir. Pass -Force to overwrite visual package outputs."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
$cropDir = Join-Path $resolvedOutputDir "crops"
$overlayDir = Join-Path $resolvedOutputDir "overlays"
$frameDir = Join-Path $resolvedOutputDir "frames"
foreach ($dir in @($cropDir, $overlayDir, $frameDir)) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force -Path $dir
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$ffmpeg = Resolve-Tool -PreferredPath $FfmpegPath -ToolName "ffmpeg"
if ($null -eq $ffmpeg) {
    throw "ffmpeg not found on PATH and wsl.exe is unavailable. Pass -FfmpegPath explicitly."
}

if (-not [string]::IsNullOrWhiteSpace($resolvedVideo) -and -not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

$draftRows = @(Import-Csv $draftPath)
if ($draftRows.Count -eq 0) {
    throw "Pseudo-GT draft review CSV has no rows: $draftPath"
}

$frameDraftRows = @()
if (-not [string]::IsNullOrWhiteSpace($frameDraftPath) -and (Test-Path $frameDraftPath)) {
    $frameDraftRows = @(Import-Csv $frameDraftPath)
}

$draftFrameMap = @{}
foreach ($frameRow in $frameDraftRows) {
    $draftFrameMap[(Get-CsvValue $frameRow "frame")] = $frameRow
}

if (($FrameWidth -le 0 -or $FrameHeight -le 0) -and -not [string]::IsNullOrWhiteSpace($resolvedVideo)) {
    $size = Get-MediaSize $resolvedVideo
    if ($null -ne $size) {
        $FrameWidth = $size.Width
        $FrameHeight = $size.Height
    }
}

$visualRows = New-Object System.Collections.Generic.List[object]
$missingVisualRows = 0
$missingVisualFrames = New-Object System.Collections.Generic.List[string]
$missingVisualCandidateIds = New-Object System.Collections.Generic.List[string]
$index = 0
foreach ($row in $draftRows) {
    $frame = Read-IntValue $row "frame"
    $x = Read-DoubleValue $row "x"
    $y = Read-DoubleValue $row "y"
    $w = Read-DoubleValue $row "w"
    $h = Read-DoubleValue $row "h"
    $candidateId = Get-CsvValue $row "pseudoGt_candidateId"
    if ([string]::IsNullOrWhiteSpace($candidateId)) {
        $candidateId = Get-CsvValue $row "sourcePredictionId"
    }
    if ([string]::IsNullOrWhiteSpace($candidateId)) {
        $candidateId = Get-CsvValue $row "gtId"
    }
    if ([string]::IsNullOrWhiteSpace($candidateId)) {
        $candidateId = "$index"
    }

    $sourceFrame = Get-FrameImagePath `
        -Frame $frame `
        -FrameDir $resolvedFrameSourceDir `
        -DraftFrameMap $draftFrameMap `
        -OutputFrameDir $frameDir `
        -ResolvedVideo $resolvedVideo `
        -Ffmpeg $ffmpeg

    $values = Copy-RowToDictionary $row
    if ([string]::IsNullOrWhiteSpace($sourceFrame) -or -not (Test-Path $sourceFrame)) {
        $missingVisualRows++
        $missingVisualFrames.Add("$frame") | Out-Null
        $missingVisualCandidateIds.Add($candidateId) | Out-Null
        Add-OutputProperty $values "visualFramePath" ""
        Add-OutputProperty $values "visualOverlayPath" ""
        Add-OutputProperty $values "visualReviewStatus" "missing-visual-source"
        $visualRows.Add([pscustomobject]$values) | Out-Null
        $index++
        continue
    }

    $sourceSize = Get-VisualSourceSize `
        -SourceFrame $sourceFrame `
        -FallbackWidth $FrameWidth `
        -FallbackHeight $FrameHeight
    $imageWidth = [int]$sourceSize.Width
    $imageHeight = [int]$sourceSize.Height

    $rect = Get-CropRect -X $x -Y $y -W $w -H $h -ImageWidth $imageWidth -ImageHeight $imageHeight -Padding $PaddingRatio
    $safeId = Convert-ToSafeName $candidateId
    $cropPath = Join-Path $cropDir ("frame-{0:D6}-candidate-{1}.png" -f $frame, $safeId)
    $overlayPath = Join-Path $overlayDir ("frame-{0:D6}-candidate-{1}-overlay.png" -f $frame, $safeId)

    $cropFilter = "crop=$($rect.W):$($rect.H):$($rect.X):$($rect.Y)"
    Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $sourceFrame, "-vf", $cropFilter, "-frames:v", "1", $cropPath) | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $cropPath)) {
        throw "ffmpeg pseudo-GT crop failed for frame $frame with exit code $LASTEXITCODE"
    }

    $box = Get-ClampedBoxRect -X $x -Y $y -W $w -H $h -ImageWidth $imageWidth -ImageHeight $imageHeight
    $boxColor = if ((Get-CsvValue $row "pseudoGt_candidateType") -eq "falsePositiveCandidate") { "0xff0000" } elseif ((Get-CsvValue $row "pseudoGt_candidateType") -eq "missCandidate") { "0xffff00" } else { "0x00ff00" }
    $overlayFilter = "drawbox=x=$($box.X):y=$($box.Y):w=$($box.W):h=$($box.H):c=$($boxColor):t=4"
    Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $sourceFrame, "-vf", $overlayFilter, "-frames:v", "1", $overlayPath) | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $overlayPath)) {
        throw "ffmpeg pseudo-GT overlay failed for frame $frame with exit code $LASTEXITCODE"
    }

    Add-OutputProperty $values "cropPath" $cropPath
    Add-OutputProperty $values "cropX" "$($rect.X)"
    Add-OutputProperty $values "cropY" "$($rect.Y)"
    Add-OutputProperty $values "cropW" "$($rect.W)"
    Add-OutputProperty $values "cropH" "$($rect.H)"
    Add-OutputProperty $values "visualFramePath" $sourceFrame
    Add-OutputProperty $values "visualOverlayPath" $overlayPath
    Add-OutputProperty $values "visualReviewStatus" "visual-ready-test-only"
    Add-OutputProperty $values "candidateRule" "test-only-reference-not-final-gt"
    Add-OutputProperty $values "humanReviewInstruction" "Use cropPath and visualOverlayPath for human review; final face/nonface/miss labels remain review CSV-owned."
    $visualRows.Add([pscustomobject]$values) | Out-Null
    $index++
}

if ($RequireAllVisuals -and $missingVisualRows -gt 0) {
    throw "Pseudo-GT visual package has missing visual rows: $missingVisualRows"
}

$visualDraftCsv = Join-Path $resolvedOutputDir "pseudo-gt-full-gt-review-visual-draft.csv"
$visualFrameCsv = Join-Path $resolvedOutputDir "pseudo-gt-full-frame-review-visual-draft.csv"
$indexHtml = Join-Path $resolvedOutputDir "pseudo-gt-review-visual-index.html"
$reportPath = Join-Path $resolvedOutputDir "pseudo-gt-review-visual-report.md"
$suggestedVideoPath = if ([string]::IsNullOrWhiteSpace($VideoPath)) { "<short problem clip path>" } else { $VideoPath }
$suggestedVideoRerunCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\new-yolo-pseudo-gt-review-visual-package.ps1 -DraftReviewCsv `"$DraftReviewCsv`" -DraftFullFrameReviewCsv `"$DraftFullFrameReviewCsv`" -FrameSourceDir `"$FrameSourceDir`" -OutputDir `"$OutputDir`" -VideoPath `"$suggestedVideoPath`" -Force -Verify -RequireAllVisuals"

$visualRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $visualDraftCsv
$frameDraftRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $visualFrameCsv
$visualRowsArray = @($visualRows | ForEach-Object { $_ })
Write-ReviewIndexHtml `
    -Path $indexHtml `
    -Rows $visualRowsArray `
    -VisualDraftCsv $visualDraftCsv `
    -VisualFrameCsv $visualFrameCsv `
    -MissingVisualRows $missingVisualRows `
    -MissingVisualFrames @($missingVisualFrames) `
    -MissingVisualCandidateIds @($missingVisualCandidateIds) `
    -SuggestedVideoRerunCommand $suggestedVideoRerunCommand

$visualReadyCount = @($visualRows | Where-Object { (Get-CsvValue $_ "visualReviewStatus") -eq "visual-ready-test-only" }).Count
$finalFilledCount = @($visualRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "label")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
    -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
}).Count

$report = @(
    "# YOLO Pseudo-GT Visual Review Package",
    "",
    "This is a test-only visual review package. It does not finalize face/nonface/miss labels and is not part of the app runtime path.",
    "",
    "## Outputs",
    "- visualDraftCsv: $visualDraftCsv",
    "- visualFrameCsv: $visualFrameCsv",
    "- reviewIndex: $indexHtml",
    "- cropDir: $cropDir",
    "- overlayDir: $overlayDir",
    "",
    "## Counts",
    "- candidateRows: $($draftRows.Count)",
    "- visualReadyRows: $visualReadyCount",
    "- missingVisualRows: $missingVisualRows",
    "- missingVisualFrames: $(@($missingVisualFrames) -join ',')",
    "- missingVisualCandidateIds: $(@($missingVisualCandidateIds) -join ',')",
    "- finalFilledRows: $finalFilledCount",
    "",
    "## Rule",
    "- test-only-reference-not-final-gt",
    "- visualReviewStatus=visual-ready-test-only only means evidence was generated.",
    "- Human review must fill label/reviewStatus/evidenceNotes before apply-yolo-pseudo-gt-review-draft.ps1.",
    "- The script does not infer labels from suggestedLabel.",
    "",
    "## Missing Visual Recovery",
    "- If missingVisualRows is greater than 0, rerun with the short problem clip, not the full original video.",
    "- suggestedVideoRerunCommand: $suggestedVideoRerunCommand"
)
$report | Set-Content -Encoding UTF8 -Path $reportPath

if ($finalFilledCount -ne 0) {
    throw "Pseudo-GT visual package unexpectedly filled final review fields: $finalFilledCount"
}

if ($Verify) {
    if (-not (Test-Path $visualDraftCsv)) {
        throw "Visual draft CSV not created: $visualDraftCsv"
    }
    if (-not (Test-Path $indexHtml)) {
        throw "Visual review index not created: $indexHtml"
    }
    if (-not (Test-Path $reportPath)) {
        throw "Visual report not created: $reportPath"
    }
    if ($RequireAllVisuals -and $visualReadyCount -ne $draftRows.Count) {
        throw "Expected all visual rows ready, ready=$visualReadyCount rows=$($draftRows.Count)"
    }
}

Write-Host "[YoloPseudoGtReviewVisual] visualDraftCsv=$visualDraftCsv"
Write-Host "[YoloPseudoGtReviewVisual] visualFrameCsv=$visualFrameCsv"
Write-Host "[YoloPseudoGtReviewVisual] reviewIndex=$indexHtml"
Write-Host "[YoloPseudoGtReviewVisual] reportPath=$reportPath"
Write-Host "[YoloPseudoGtReviewVisual] rows=$($draftRows.Count),visualReady=$visualReadyCount,missingVisualRows=$missingVisualRows"
Write-Host "[YoloPseudoGtReviewVisual] all requested checks passed"
