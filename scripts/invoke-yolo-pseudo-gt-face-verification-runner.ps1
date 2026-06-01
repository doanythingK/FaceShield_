param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv,
    [Parameter(Mandatory = $true)]
    [string]$ModelPath,
    [ValidateSet("Scrfd", "YuNet")]
    [string]$Detector = "Scrfd",
    [double]$ConfidenceThreshold = 0.55,
    [double]$NmsThreshold = 0.45,
    [int]$InputSize = 640,
    [string]$EvidenceModel = "",
    [string]$EvidenceRunner = "",
    [switch]$ForceBuild
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

$resolvedManifest = Resolve-RepoPath $ManifestCsv
$resolvedOutput = Resolve-RepoPath $OutputCsv
$resolvedModel = Resolve-RepoPath $ModelPath

if (-not (Test-Path $resolvedManifest)) {
    throw "ManifestCsv not found: $resolvedManifest"
}

if (-not (Test-Path $resolvedModel)) {
    throw "ModelPath not found: $resolvedModel"
}

if ([string]::IsNullOrWhiteSpace($EvidenceModel)) {
    $EvidenceModel = [IO.Path]::GetFileName($resolvedModel)
}

if ([string]::IsNullOrWhiteSpace($EvidenceRunner)) {
    $EvidenceRunner = "FaceShieldPseudoGtFaceVerificationRunner/$Detector"
}

$runnerDir = Join-Path $repo ".tmp\yolo-pseudo-gt-face-verification-runner"
$projectPath = Join-Path $runnerDir "YoloPseudoGtFaceVerificationRunner.csproj"
$programPath = Join-Path $runnerDir "Program.cs"
New-Item -ItemType Directory -Force -Path $runnerDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null

$projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo/FaceShield.csproj" />
  </ItemGroup>
</Project>
"@

$programText = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using Microsoft.VisualBasic.FileIO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

static string Require(Dictionary<string, string> row, params string[] names)
{
    foreach (string name in names)
    {
        if (row.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
            return value;
    }

    throw new InvalidOperationException("CSV row missing required value: " + string.Join("/", names));
}

static string Optional(Dictionary<string, string> row, params string[] names)
{
    foreach (string name in names)
    {
        if (row.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
            return value;
    }

    return "";
}

static int ReadInt(Dictionary<string, string> row, params string[] names)
{
    return int.Parse(Require(row, names), CultureInfo.InvariantCulture);
}

static double ReadDouble(Dictionary<string, string> row, params string[] names)
{
    return double.Parse(Require(row, names), CultureInfo.InvariantCulture);
}

static string Csv(string value)
{
    if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    return value;
}

static IReadOnlyList<Dictionary<string, string>> ReadCsv(string path)
{
    var rows = new List<Dictionary<string, string>>();
    using var parser = new TextFieldParser(path);
    parser.TextFieldType = FieldType.Delimited;
    parser.SetDelimiters(",");
    parser.HasFieldsEnclosedInQuotes = true;

    string[]? headers = parser.ReadFields();
    if (headers == null || headers.Length == 0)
        throw new InvalidOperationException("CSV has no header: " + path);

    while (!parser.EndOfData)
    {
        string[]? fields = parser.ReadFields();
        if (fields == null)
            continue;

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            row[headers[i]] = i < fields.Length ? fields[i] : "";
        rows.Add(row);
    }

    return rows;
}

static string ResolvePath(string baseDir, string path)
{
    if (Path.IsPathRooted(path))
        return path;
    return Path.GetFullPath(Path.Combine(baseDir, path));
}

static byte[] LoadBgra(string path, out int width, out int height)
{
    using Image<Rgba32> image = Image.Load<Rgba32>(path);
    width = image.Width;
    height = image.Height;
    int imageWidth = image.Width;
    int imageHeight = image.Height;
    byte[] bgra = new byte[imageWidth * imageHeight * 4];

    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            Span<Rgba32> row = accessor.GetRowSpan(y);
            int offset = y * imageWidth * 4;
            for (int x = 0; x < imageWidth; x++)
            {
                Rgba32 p = row[x];
                bgra[offset + x * 4 + 0] = p.B;
                bgra[offset + x * 4 + 1] = p.G;
                bgra[offset + x * 4 + 2] = p.R;
                bgra[offset + x * 4 + 3] = p.A;
            }
        }
    });

    return bgra;
}

static double Iou(double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh)
{
    double x1 = Math.Max(ax, bx);
    double y1 = Math.Max(ay, by);
    double x2 = Math.Min(ax + aw, bx + bw);
    double y2 = Math.Min(ay + ah, by + bh);
    double iw = Math.Max(0, x2 - x1);
    double ih = Math.Max(0, y2 - y1);
    double intersection = iw * ih;
    double union = Math.Max(1e-6, aw * ah + bw * bh - intersection);
    return intersection / union;
}

static double CenterDistanceRatio(double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh, double normW, double normH)
{
    double acx = ax + aw / 2.0;
    double acy = ay + ah / 2.0;
    double bcx = bx + bw / 2.0;
    double bcy = by + bh / 2.0;
    double norm = Math.Max(1e-6, Math.Sqrt(normW * normW + normH * normH));
    return Math.Sqrt((acx - bcx) * (acx - bcx) + (acy - bcy) * (acy - bcy)) / norm;
}

static IBgraFaceDetector CreateDetector(
    string detector,
    string modelPath,
    float confidenceThreshold,
    float nmsThreshold,
    int inputSize)
{
    if (detector.Equals("YuNet", StringComparison.OrdinalIgnoreCase))
    {
        return new YuNetOnnxDetector(new YuNetOnnxDetectorOptions
        {
            ModelPath = modelPath,
            ConfidenceThreshold = confidenceThreshold,
            NmsThreshold = nmsThreshold,
            UseTiling = false
        });
    }

    return new ScrfdOnnxDetector(new ScrfdOnnxDetectorOptions
    {
        ModelPath = modelPath,
        ConfidenceThreshold = confidenceThreshold,
        NmsThreshold = nmsThreshold,
        InputWidth = inputSize,
        InputHeight = inputSize,
        UseOrtOptimization = true,
        UseGpu = false
    });
}

static string GetArg(string[] args, string name, string defaultValue = "")
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return defaultValue;
}

string manifestCsv = GetArg(args, "--manifest");
string outputCsv = GetArg(args, "--output");
string modelPath = GetArg(args, "--model");
string detectorName = GetArg(args, "--detector", "Scrfd");
string evidenceModel = GetArg(args, "--evidence-model", Path.GetFileName(modelPath));
string evidenceRunner = GetArg(args, "--evidence-runner", "FaceShieldPseudoGtFaceVerificationRunner/" + detectorName);
float confidenceThreshold = float.Parse(GetArg(args, "--confidence", "0.55"), CultureInfo.InvariantCulture);
float nmsThreshold = float.Parse(GetArg(args, "--nms", "0.45"), CultureInfo.InvariantCulture);
int inputSize = int.Parse(GetArg(args, "--input-size", "640"), CultureInfo.InvariantCulture);

if (string.IsNullOrWhiteSpace(manifestCsv) || string.IsNullOrWhiteSpace(outputCsv) || string.IsNullOrWhiteSpace(modelPath))
    throw new InvalidOperationException("--manifest, --output, and --model are required.");

string manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestCsv)) ?? Directory.GetCurrentDirectory();
IReadOnlyList<Dictionary<string, string>> manifestRows = ReadCsv(manifestCsv);
var verifications = new List<VerificationRow>();

using IBgraFaceDetector detector = CreateDetector(detectorName, modelPath, confidenceThreshold, nmsThreshold, inputSize);

foreach (Dictionary<string, string> row in manifestRows)
{
    int frame = ReadInt(row, "frame", "Frame");
    string candidateId = Require(row, "candidateId", "sourceCandidateId");
    string basePredictionId = Optional(row, "basePredictionId", "predictionId", "sourcePredictionId");
    double baseX = ReadDouble(row, "x", "X");
    double baseY = ReadDouble(row, "y", "Y");
    double baseW = ReadDouble(row, "w", "W", "width", "Width");
    double baseH = ReadDouble(row, "h", "H", "height", "Height");
    double cropX = ReadDouble(row, "cropX", "CropX");
    double cropY = ReadDouble(row, "cropY", "CropY");
    double cropW = ReadDouble(row, "cropW", "CropW");
    double cropH = ReadDouble(row, "cropH", "CropH");
    string imagePath = ResolvePath(manifestDir, Require(row, "cropImagePath", "CropImagePath"));

    if (!File.Exists(imagePath))
        throw new FileNotFoundException("Crop image not found.", imagePath);

    byte[] bgra = LoadBgra(imagePath, out int width, out int height);
    GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
    try
    {
        IReadOnlyList<FaceDetectionResult> faces = detector.DetectFacesBgra(
            handle.AddrOfPinnedObject(),
            width * 4,
            width,
            height,
            1.0,
            DownscaleQuality.BalancedBilinear);

        VerificationRow? best = null;
        foreach (FaceDetectionResult face in faces)
        {
            double frameX = cropX + face.Bounds.X;
            double frameY = cropY + face.Bounds.Y;
            double frameW = face.Bounds.Width;
            double frameH = face.Bounds.Height;
            if (frameW <= 0 || frameH <= 0)
                continue;

            double iou = Iou(baseX, baseY, baseW, baseH, frameX, frameY, frameW, frameH);
            double distance = CenterDistanceRatio(baseX, baseY, baseW, baseH, frameX, frameY, frameW, frameH, cropW, cropH);
            double score = face.Confidence + iou - distance;
            var candidate = new VerificationRow(
                frame,
                candidateId,
                basePredictionId,
                "verify-" + frame.ToString(CultureInfo.InvariantCulture) + "-" + candidateId,
                frameX,
                frameY,
                frameW,
                frameH,
                face.Confidence,
                Math.Max(0, Math.Min(1, distance)),
                iou,
                evidenceModel,
                evidenceRunner,
                score);

            if (best == null || candidate.Score > best.Score)
                best = candidate;
        }

        if (best != null)
            verifications.Add(best);
    }
    finally
    {
        handle.Free();
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsv)) ?? Directory.GetCurrentDirectory());
using var writer = new StreamWriter(outputCsv);
writer.WriteLine("frame,candidateId,basePredictionId,verificationId,x,y,w,h,faceVerificationConfidence,faceVerificationDistance,verificationIou,evidenceModel,evidenceRunner");
foreach (VerificationRow row in verifications.OrderBy(c => c.Frame).ThenBy(c => c.CandidateId))
{
    writer.WriteLine(string.Join(",",
        row.Frame.ToString(CultureInfo.InvariantCulture),
        Csv(row.CandidateId),
        Csv(row.BasePredictionId),
        Csv(row.VerificationId),
        row.X.ToString("0.###", CultureInfo.InvariantCulture),
        row.Y.ToString("0.###", CultureInfo.InvariantCulture),
        row.W.ToString("0.###", CultureInfo.InvariantCulture),
        row.H.ToString("0.###", CultureInfo.InvariantCulture),
        row.Confidence.ToString("0.######", CultureInfo.InvariantCulture),
        row.Distance.ToString("0.######", CultureInfo.InvariantCulture),
        row.Iou.ToString("0.######", CultureInfo.InvariantCulture),
        Csv(row.EvidenceModel),
        Csv(row.EvidenceRunner)));
}

Console.WriteLine("[YoloPseudoGtFaceVerificationRunner] detector=" + detectorName + ", manifestRows=" + manifestRows.Count + ", verifications=" + verifications.Count + ", output=" + outputCsv);

sealed class VerificationRow
{
    public VerificationRow(int frame, string candidateId, string basePredictionId, string verificationId, double x, double y, double w, double h, float confidence, double distance, double iou, string evidenceModel, string evidenceRunner, double score)
    {
        Frame = frame;
        CandidateId = candidateId;
        BasePredictionId = basePredictionId;
        VerificationId = verificationId;
        X = x;
        Y = y;
        W = w;
        H = h;
        Confidence = confidence;
        Distance = distance;
        Iou = iou;
        EvidenceModel = evidenceModel;
        EvidenceRunner = evidenceRunner;
        Score = score;
    }

    public int Frame { get; }
    public string CandidateId { get; }
    public string BasePredictionId { get; }
    public string VerificationId { get; }
    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public float Confidence { get; }
    public double Distance { get; }
    public double Iou { get; }
    public string EvidenceModel { get; }
    public string EvidenceRunner { get; }
    public double Score { get; }
}
'@

if ($ForceBuild.IsPresent -or -not (Test-Path $projectPath) -or -not (Test-Path $programPath)) {
    $projectText | Set-Content -Encoding UTF8 -Path $projectPath
    $programText | Set-Content -Encoding UTF8 -Path $programPath
}
else {
    $projectText | Set-Content -Encoding UTF8 -Path $projectPath
    $programText | Set-Content -Encoding UTF8 -Path $programPath
}

$arguments = @(
    "run",
    "--project",
    $projectPath,
    "--",
    "--manifest",
    $resolvedManifest,
    "--output",
    $resolvedOutput,
    "--model",
    $resolvedModel,
    "--detector",
    $Detector,
    "--confidence",
    $ConfidenceThreshold.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture),
    "--nms",
    $NmsThreshold.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture),
    "--input-size",
    $InputSize.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--evidence-model",
    $EvidenceModel,
    "--evidence-runner",
    $EvidenceRunner
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT face verification runner failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $resolvedOutput)) {
    throw "Pseudo-GT face verification runner did not create output CSV: $resolvedOutput"
}
