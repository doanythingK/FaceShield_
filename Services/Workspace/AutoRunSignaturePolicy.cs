using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FaceShield.Services.Workspace;

internal static class AutoRunSignaturePolicy
{
    internal static string? GetResumeResetReason(
        int resumeIndex,
        string? storedRunSignature,
        string currentRunSignature,
        string? storedExecutionSignature,
        string currentExecutionSignature)
    {
        if (resumeIndex <= 0)
            return null;
        if (string.IsNullOrWhiteSpace(storedRunSignature) ||
            !string.Equals(storedRunSignature, currentRunSignature, StringComparison.Ordinal))
        {
            return "settings-changed";
        }
        if (string.IsNullOrWhiteSpace(storedExecutionSignature) ||
            string.IsNullOrWhiteSpace(currentExecutionSignature))
        {
            return "execution-signature-missing";
        }
        if (!string.Equals(storedExecutionSignature, currentExecutionSignature, StringComparison.Ordinal))
            return "execution-changed";

        return null;
    }

    internal static string BuildRunSignature(
        AutoMaskOptions autoOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions)
    {
        autoOptions = (autoOptions ?? new AutoMaskOptions()).ResolveProcessingMode();
        detectorFactoryOptions ??= FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions());
        bool useLegacyCompatibleSignature =
            autoOptions.ProcessingMode == AutoMaskProcessingMode.Legacy &&
            !autoOptions.EnableYoloRiskCascade &&
            autoOptions.EnableYoloPrimaryRoiShortcut;

        var parts = new List<string>
        {
            useLegacyCompatibleSignature ? "v3" : "v6",
            $"backend={detectorFactoryOptions.Backend}",
            $"profile={autoOptions.FilterProfile}",
            $"downscale={FormatNumber(autoOptions.DownscaleRatio)}",
            $"quality={autoOptions.DownscaleQuality}",
            $"post={autoOptions.EnablePostProcessing}",
            $"roi={autoOptions.EnableRoiPostProcess}",
            $"iso={autoOptions.EnableYoloWeakIsolatedCleanup}",
            $"gap={autoOptions.EnableYoloGapFill}",
            $"scene={autoOptions.EnableYoloSceneCutCarryCleanup}",
            $"smooth={autoOptions.EnableYoloTemporalSmoothing}"
        };
        if (!useLegacyCompatibleSignature)
        {
            parts.Add($"processingMode={autoOptions.ProcessingMode}");
            parts.Add($"riskCascade={autoOptions.EnableYoloRiskCascade}");
            parts.Add($"strongInterval={FormatNumber(autoOptions.YoloStrongFullScanIntervalSeconds)}");
            parts.Add($"riskConfidence={FormatNumber(autoOptions.YoloRiskLowConfidenceThreshold)}");
            parts.Add($"riskArea={FormatNumber(autoOptions.YoloRiskSmallFaceAreaRatio)}");
            parts.Add($"riskEdge={FormatNumber(autoOptions.YoloRiskEdgeMarginRatio)}");
            parts.Add($"riskGap={autoOptions.YoloRiskMaxTrackGapFrames}");
            parts.Add($"strongConfirm={autoOptions.YoloStrongConfirmationFrames}");
            parts.Add($"primaryRoi={autoOptions.EnableYoloPrimaryRoiShortcut}");
        }

        parts.AddRange(new[]
        {
            $"tracking={autoOptions.UseTracking}",
            $"everyN={autoOptions.DetectEveryNFrames}",
            $"parallel={autoOptions.ParallelDetectorCount}",
            $"dump={autoOptions.DumpDetectionDiagnostics}"
        });

        switch (detectorFactoryOptions.Backend)
        {
            case FaceDetectorBackend.YoloFaceOnnx:
                AppendYoloSignature(parts, detectorFactoryOptions.YoloFaceOnnxOptions);
                if (autoOptions.EnableYoloRiskCascade)
                    AppendSecondaryFaceOnnxSignature(parts, detectorFactoryOptions.FaceOnnxOptions);
                break;
            case FaceDetectorBackend.FaceOnnx:
                AppendFaceOnnxSignature(parts, detectorFactoryOptions.FaceOnnxOptions);
                break;
            case FaceDetectorBackend.ScrfdOnnx:
                AppendScrfdSignature(parts, detectorFactoryOptions.ScrfdOnnxOptions);
                break;
            case FaceDetectorBackend.YuNetOnnx:
                AppendYuNetSignature(parts, detectorFactoryOptions.YuNetOnnxOptions);
                break;
            default:
                parts.Add($"detector={detectorFactoryOptions.Backend}");
                break;
        }

        return string.Join("|", parts);
    }

    internal static string BuildIntentSignature(
        AutoMaskOptions autoOptions,
        FaceOnnxDetectorOptions detectorOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions)
    {
        AutoMaskOptions effectiveAutoOptions = (autoOptions ?? new AutoMaskOptions()).ResolveProcessingMode();
        FaceDetectorFactoryOptions effectiveFactoryOptions = ResolveDetectorFactoryOptions(
            effectiveAutoOptions,
            detectorOptions,
            detectorFactoryOptions,
            out _);
        return BuildRunSignature(effectiveAutoOptions, effectiveFactoryOptions);
    }

    internal static FaceDetectorFactoryOptions ResolveDetectorFactoryOptions(
        AutoMaskOptions autoOptions,
        FaceOnnxDetectorOptions detectorOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions,
        out FaceOnnxDetectorOptions? yoloSecondaryOptions)
    {
        detectorOptions ??= new FaceOnnxDetectorOptions();
        detectorFactoryOptions ??= FaceDetectorFactoryOptions.ForOnnx(detectorOptions);
        yoloSecondaryOptions = null;
        if (detectorFactoryOptions.Backend != FaceDetectorBackend.YoloFaceOnnx ||
            !autoOptions.EnableYoloRiskCascade)
        {
            return detectorFactoryOptions;
        }

        yoloSecondaryOptions = CreateYoloSecondaryDetectorOptions(detectorOptions);
        return detectorFactoryOptions.WithFaceOnnxOptions(yoloSecondaryOptions);
    }

    internal static string BuildEvidenceSignature(
        AutoMaskOptions autoOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions)
    {
        string signature = BuildRunSignature(autoOptions, detectorFactoryOptions);
        string? modelPath = GetDetectorModelPath(detectorFactoryOptions);
        if (string.IsNullOrWhiteSpace(modelPath))
            return signature;

        string normalizedPath = NormalizePath(modelPath);
        string modelIdentity;
        try
        {
            var modelFile = new FileInfo(normalizedPath);
            modelIdentity = $"{modelFile.Name}:{modelFile.Length}:{modelFile.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            modelIdentity = Path.GetFileName(modelPath);
        }

        return signature.Replace(
            $"model={normalizedPath}",
            $"model={modelIdentity}",
            StringComparison.Ordinal);
    }

    internal static string BuildExecutionSignature(
        AutoMaskOptions autoOptions,
        FaceDetectorFactoryOptions detectorFactoryOptions,
        string executionProviderLabel,
        string sourceEvidenceId)
    {
        string provider = DetectorExecutionProviderIdentity.NormalizeLabel(executionProviderLabel);
        string source = string.IsNullOrWhiteSpace(sourceEvidenceId)
            ? "unavailable"
            : sourceEvidenceId.Trim();
        return $"exec-v1|{BuildEvidenceSignature(autoOptions, detectorFactoryOptions)}|provider={provider}|source={source}";
    }

    internal static string GetExecutionProviderLabel(IFaceDetector detector)
        => DetectorExecutionProviderIdentity.GetCanonicalLabel(detector);

    private static FaceOnnxDetectorOptions CreateYoloSecondaryDetectorOptions(
        FaceOnnxDetectorOptions? configured)
    {
        configured ??= new FaceOnnxDetectorOptions();
        return new FaceOnnxDetectorOptions
        {
            UseOrtOptimization = configured.UseOrtOptimization,
            UseGpu = false,
            IntraOpNumThreads = configured.IntraOpNumThreads,
            InterOpNumThreads = configured.InterOpNumThreads,
            UseParallelExecution = false,
            DetectionThreshold = null,
            ConfidenceThreshold = null,
            NmsThreshold = null,
            EnablePreprocessParallelism = configured.EnablePreprocessParallelism,
            AllowAutoTune = false,
            AllowAutoGpu = false
        };
    }

    private static void AppendFaceOnnxSignature(List<string> parts, FaceOnnxDetectorOptions? options)
    {
        options ??= new FaceOnnxDetectorOptions();
        parts.Add($"ort={options.UseOrtOptimization}");
        parts.Add($"gpu={options.UseGpu}");
        parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
        parts.Add($"preprocess={options.EnablePreprocessParallelism?.ToString() ?? "null"}");
        parts.Add($"autoTune={options.AllowAutoTune?.ToString() ?? "null"}");
        parts.Add($"autoGpu={options.AllowAutoGpu?.ToString() ?? "null"}");
        parts.Add($"detect={FormatNumber(options.DetectionThreshold)}");
        parts.Add($"conf={FormatNumber(options.ConfidenceThreshold)}");
        parts.Add($"nms={FormatNumber(options.NmsThreshold)}");
    }

    private static void AppendSecondaryFaceOnnxSignature(List<string> parts, FaceOnnxDetectorOptions? options)
    {
        options ??= new FaceOnnxDetectorOptions();
        parts.Add($"secondaryOrt={options.UseOrtOptimization}");
        parts.Add($"secondaryGpu={options.UseGpu}");
        parts.Add($"secondaryIntra={options.IntraOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"secondaryInter={options.InterOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"secondaryParallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
        parts.Add($"secondaryPreprocess={options.EnablePreprocessParallelism?.ToString() ?? "null"}");
        parts.Add($"secondaryAutoTune={options.AllowAutoTune?.ToString() ?? "null"}");
        parts.Add($"secondaryAutoGpu={options.AllowAutoGpu?.ToString() ?? "null"}");
        parts.Add($"secondaryDetect={FormatNumber(options.DetectionThreshold)}");
        parts.Add($"secondaryConf={FormatNumber(options.ConfidenceThreshold)}");
        parts.Add($"secondaryNms={FormatNumber(options.NmsThreshold)}");
    }

    private static void AppendYoloSignature(List<string> parts, YoloFaceOnnxDetectorOptions? options)
    {
        if (options == null)
        {
            parts.Add("yolo=null");
            return;
        }

        parts.Add($"model={NormalizePath(options.ModelPath)}");
        parts.Add($"type={options.ModelType}");
        parts.Add($"ort={options.UseOrtOptimization}");
        parts.Add($"gpu={options.UseGpu}");
        parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
        if (options.EnableCoreMl)
            parts.Add("coreMl=True");
        parts.Add($"input={options.InputWidth?.ToString() ?? "null"}x{options.InputHeight?.ToString() ?? "null"}");
        parts.Add($"obj={FormatNumber(options.ObjectnessThreshold)}");
        parts.Add($"conf={FormatNumber(options.ConfidenceThreshold)}");
        parts.Add($"nms={FormatNumber(options.NmsThreshold)}");
        parts.Add($"max={options.MaxDetections}");
        parts.Add($"tiling={options.UseTiling}");
        parts.Add($"tileOnly={!options.IncludeFullFrameWhenTiling}");
        parts.Add($"tiles={options.TileColumns}x{options.TileRows}");
        parts.Add($"tileOverlap={FormatNumber(options.TileOverlapRatio)}");
        parts.Add($"letterbox={options.UseLetterboxResize}");
        parts.Add($"centerPad={options.CenterLetterboxPadding}");
        parts.Add($"padValue={FormatNumber(options.LetterboxPaddingValue)}");
        parts.Add($"rgb={options.UseRgbInput}");
        parts.Add($"inputScale={FormatNumber(options.InputScale)}");
        parts.Add($"lowPos={options.UseLowConfidencePositionFilter}:{FormatNumber(options.LowConfidencePositionMaxConfidence)}:{FormatNumber(options.LowConfidencePositionMinCenterYRatio)}");
        parts.Add($"small={options.UseSmallAreaFilter}:{FormatNumber(options.SmallAreaMaxAreaRatio)}");
        parts.Add($"aspect={options.UseAspectRatioFilter}:{FormatNumber(options.MinAspectRatio)}:{FormatNumber(options.MaxAspectRatio)}");
        parts.Add($"topSmall={options.UseTopSmallLowConfidenceFilter}:{FormatNumber(options.TopSmallLowConfidenceMaxConfidence)}:{FormatNumber(options.TopSmallLowConfidenceMaxCenterYRatio)}:{FormatNumber(options.TopSmallLowConfidenceMaxAreaRatio)}");
        parts.Add($"largeScale={FormatNumber(options.LargeBoxWidthScale)}:{FormatNumber(options.LargeBoxHeightScale)}:{FormatNumber(options.LargeBoxMinAreaRatio)}");
        parts.Add($"landmark={options.UseYolo5LandmarkBoxRefine}:{FormatNumber(options.Yolo5LandmarkBoxMinAreaRatio)}:{FormatNumber(options.Yolo5LandmarkBoxWidthScale)}:{FormatNumber(options.Yolo5LandmarkBoxHeightScale)}:{FormatNumber(options.Yolo5LandmarkBoxCenterYOffsetRatio)}:{FormatNumber(options.Yolo5LandmarkBoxMinOriginalIou)}");
    }

    private static void AppendScrfdSignature(List<string> parts, ScrfdOnnxDetectorOptions? options)
    {
        if (options == null)
        {
            parts.Add("scrfd=null");
            return;
        }

        parts.Add($"model={NormalizePath(options.ModelPath)}");
        parts.Add($"ort={options.UseOrtOptimization}");
        parts.Add($"gpu={options.UseGpu}");
        parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
        parts.Add($"conf={FormatNumber(options.ConfidenceThreshold)}");
        parts.Add($"nms={FormatNumber(options.NmsThreshold)}");
        parts.Add($"input={options.InputWidth?.ToString() ?? "null"}x{options.InputHeight?.ToString() ?? "null"}");
        parts.Add($"normalize={FormatNumber(options.InputMean)}:{FormatNumber(options.InputStd)}");
        parts.Add($"bboxStride={options.MultiplyBboxByStride}");
        parts.Add($"anchorOffset={FormatNumber(options.AnchorCenterOffset)}");
        parts.Add($"letterbox={options.UseLetterboxResize}");
        parts.Add($"centerPad={options.CenterLetterboxPadding}");
        parts.Add($"padValue={FormatNumber(options.LetterboxPaddingValue)}");
        parts.Add($"rgb={options.UseRgbInput}");
    }

    private static void AppendYuNetSignature(List<string> parts, YuNetOnnxDetectorOptions? options)
    {
        if (options == null)
        {
            parts.Add("yunet=null");
            return;
        }

        parts.Add($"model={NormalizePath(options.ModelPath)}");
        parts.Add($"ort={options.UseOrtOptimization}");
        parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
        parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
        parts.Add($"conf={FormatNumber(options.ConfidenceThreshold)}");
        parts.Add($"nms={FormatNumber(options.NmsThreshold)}");
        parts.Add($"topK={options.TopK}");
        parts.Add($"tiling={options.UseTiling}");
        parts.Add($"tileOnly={!options.IncludeFullFrameWhenTiling}");
        parts.Add($"tiles={options.TileColumns}x{options.TileRows}");
        parts.Add($"tileOverlap={FormatNumber(options.TileOverlapRatio)}");
    }

    private static string? GetDetectorModelPath(FaceDetectorFactoryOptions options)
        => options.Backend switch
        {
            FaceDetectorBackend.YoloFaceOnnx => options.YoloFaceOnnxOptions?.ModelPath,
            FaceDetectorBackend.ScrfdOnnx => options.ScrfdOnnxOptions?.ModelPath,
            FaceDetectorBackend.YuNetOnnx => options.YuNetOnnxOptions?.ModelPath,
            _ => null
        };

    private static string FormatNumber(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatNumber(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatNumber(float? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "null";

        try
        {
            return Path.GetFullPath(path).Trim();
        }
        catch
        {
            return path.Trim();
        }
    }
}
