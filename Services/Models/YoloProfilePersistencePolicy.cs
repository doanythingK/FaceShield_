using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Workspace;
using System;

namespace FaceShield.Services.Models;

internal sealed class YoloProfileState
{
    internal string? ModelPath { get; init; }
    internal double ObjectnessThreshold { get; init; }
    internal double ConfidenceThreshold { get; init; }
    internal double NmsThreshold { get; init; }
    internal int InputSize { get; init; }
    internal bool UseTiling { get; init; }
    internal bool TileOnly { get; init; }
    internal int TileColumns { get; init; }
    internal int TileRows { get; init; }
    internal double TileOverlapRatio { get; init; }
    internal double DownscaleRatio { get; init; }
    internal DownscaleQuality DownscaleQuality { get; init; }
    internal bool AutoTrackingEnabled { get; init; }
    internal int AutoDetectEveryNFrames { get; init; }
    internal int ParallelSessionCount { get; init; }
}

internal static class YoloProfilePersistencePolicy
{
    internal static YoloProfileState CreateDefault(
        YoloFaceModelType modelType,
        string? defaultModelPath,
        int defaultAutoDetectEveryNFrames,
        double yolo5ObjectnessThreshold,
        double yolo5ConfidenceThreshold,
        double defaultNmsThreshold)
    {
        return new YoloProfileState
        {
            ModelPath = NormalizeModelPath(defaultModelPath),
            ObjectnessThreshold =
                modelType == YoloFaceModelType.Yolo5Face
                    ? yolo5ObjectnessThreshold
                    : 0.25,
            ConfidenceThreshold =
                modelType == YoloFaceModelType.Yolo5Face
                    ? yolo5ConfidenceThreshold
                    : 0.35,
            NmsThreshold = defaultNmsThreshold,
            InputSize = 640,
            UseTiling = false,
            TileOnly = false,
            TileColumns = 2,
            TileRows = 2,
            TileOverlapRatio = 0.15,
            DownscaleRatio = 1.0,
            DownscaleQuality = DownscaleQuality.BalancedBilinear,
            AutoTrackingEnabled = true,
            AutoDetectEveryNFrames = defaultAutoDetectEveryNFrames,
            ParallelSessionCount = 2
        };
    }

    internal static YoloProfileState ReadSavedProfile(
        AutoSettingsState saved,
        YoloFaceModelType modelType,
        YoloFaceModelType selectedModelType,
        YoloProfileState defaults)
    {
        bool useLegacyActiveProfile = selectedModelType == modelType;
        string? legacyModelPath = useLegacyActiveProfile
            ? NormalizeModelPath(saved.YoloModelPath)
            : null;

        if (modelType == YoloFaceModelType.Yolo5Face)
        {
            return new YoloProfileState
            {
                ModelPath =
                    NormalizeModelPath(saved.Yolo5ModelPath) ??
                    legacyModelPath ??
                    defaults.ModelPath,
                ObjectnessThreshold =
                    saved.Yolo5ObjectnessThreshold ??
                    (useLegacyActiveProfile
                        ? saved.YoloObjectnessThreshold
                        : null) ??
                    defaults.ObjectnessThreshold,
                ConfidenceThreshold =
                    saved.Yolo5ConfidenceThreshold ??
                    (useLegacyActiveProfile
                        ? saved.YoloConfidenceThreshold
                        : null) ??
                    defaults.ConfidenceThreshold,
                NmsThreshold =
                    saved.Yolo5NmsThreshold ??
                    (useLegacyActiveProfile
                        ? saved.YoloNmsThreshold
                        : null) ??
                    defaults.NmsThreshold,
                InputSize = Math.Clamp(
                    saved.Yolo5InputSize ??
                    (useLegacyActiveProfile
                        ? saved.YoloInputSize
                        : null) ??
                    defaults.InputSize,
                    64,
                    2048),
                UseTiling =
                    saved.Yolo5UseTiling ??
                    (useLegacyActiveProfile
                        ? (bool?)saved.YoloUseTiling
                        : null) ??
                    defaults.UseTiling,
                TileOnly =
                    saved.Yolo5TileOnly ??
                    (useLegacyActiveProfile
                        ? (bool?)saved.YoloTileOnly
                        : null) ??
                    defaults.TileOnly,
                TileColumns = Math.Clamp(
                    saved.Yolo5TileColumns ??
                    (useLegacyActiveProfile
                        ? saved.YoloTileColumns
                        : null) ??
                    defaults.TileColumns,
                    1,
                    8),
                TileRows = Math.Clamp(
                    saved.Yolo5TileRows ??
                    (useLegacyActiveProfile
                        ? saved.YoloTileRows
                        : null) ??
                    defaults.TileRows,
                    1,
                    8),
                TileOverlapRatio = Math.Clamp(
                    saved.Yolo5TileOverlapRatio ??
                    (useLegacyActiveProfile
                        ? saved.YoloTileOverlapRatio
                        : null) ??
                    defaults.TileOverlapRatio,
                    0.0,
                    0.45),
                DownscaleRatio = ResolveSavedDownscaleRatio(
                    saved.Yolo5DownscaleRatio,
                    useLegacyActiveProfile
                        ? saved.DownscaleRatio
                        : null,
                    defaults.DownscaleRatio),
                DownscaleQuality = ResolveSavedDownscaleQuality(
                    saved.Yolo5DownscaleQuality,
                    useLegacyActiveProfile
                        ? saved.DownscaleQuality
                        : null,
                    defaults.DownscaleQuality),
                AutoTrackingEnabled =
                    saved.Yolo5AutoTrackingEnabled ??
                    (useLegacyActiveProfile
                        ? (bool?)saved.AutoTrackingEnabled
                        : null) ??
                    defaults.AutoTrackingEnabled,
                AutoDetectEveryNFrames = Math.Max(
                    1,
                    saved.Yolo5AutoDetectEveryNFrames ??
                    (useLegacyActiveProfile
                        ? (int?)saved.AutoDetectEveryNFrames
                        : null) ??
                    defaults.AutoDetectEveryNFrames),
                ParallelSessionCount = Math.Max(
                    1,
                    saved.Yolo5ParallelSessionCount ??
                    (useLegacyActiveProfile
                        ? (int?)saved.ParallelSessionCount
                        : null) ??
                    defaults.ParallelSessionCount)
            };
        }

        return new YoloProfileState
        {
            ModelPath =
                NormalizeModelPath(saved.YoloV8ModelPath) ??
                legacyModelPath ??
                defaults.ModelPath,
            ObjectnessThreshold =
                saved.YoloV8ObjectnessThreshold ??
                (useLegacyActiveProfile
                    ? saved.YoloObjectnessThreshold
                    : null) ??
                defaults.ObjectnessThreshold,
            ConfidenceThreshold =
                saved.YoloV8ConfidenceThreshold ??
                (useLegacyActiveProfile
                    ? saved.YoloConfidenceThreshold
                    : null) ??
                defaults.ConfidenceThreshold,
            NmsThreshold =
                saved.YoloV8NmsThreshold ??
                (useLegacyActiveProfile
                    ? saved.YoloNmsThreshold
                    : null) ??
                defaults.NmsThreshold,
            InputSize = Math.Clamp(
                saved.YoloV8InputSize ??
                (useLegacyActiveProfile
                    ? saved.YoloInputSize
                    : null) ??
                defaults.InputSize,
                64,
                2048),
            UseTiling =
                saved.YoloV8UseTiling ??
                (useLegacyActiveProfile
                    ? (bool?)saved.YoloUseTiling
                    : null) ??
                defaults.UseTiling,
            TileOnly =
                saved.YoloV8TileOnly ??
                (useLegacyActiveProfile
                    ? (bool?)saved.YoloTileOnly
                    : null) ??
                defaults.TileOnly,
            TileColumns = Math.Clamp(
                saved.YoloV8TileColumns ??
                (useLegacyActiveProfile
                    ? saved.YoloTileColumns
                    : null) ??
                defaults.TileColumns,
                1,
                8),
            TileRows = Math.Clamp(
                saved.YoloV8TileRows ??
                (useLegacyActiveProfile
                    ? saved.YoloTileRows
                    : null) ??
                defaults.TileRows,
                1,
                8),
            TileOverlapRatio = Math.Clamp(
                saved.YoloV8TileOverlapRatio ??
                (useLegacyActiveProfile
                    ? saved.YoloTileOverlapRatio
                    : null) ??
                defaults.TileOverlapRatio,
                0.0,
                0.45),
            DownscaleRatio = ResolveSavedDownscaleRatio(
                saved.YoloV8DownscaleRatio,
                useLegacyActiveProfile
                    ? saved.DownscaleRatio
                    : null,
                defaults.DownscaleRatio),
            DownscaleQuality = ResolveSavedDownscaleQuality(
                saved.YoloV8DownscaleQuality,
                useLegacyActiveProfile
                    ? saved.DownscaleQuality
                    : null,
                defaults.DownscaleQuality),
            AutoTrackingEnabled =
                saved.YoloV8AutoTrackingEnabled ??
                (useLegacyActiveProfile
                    ? (bool?)saved.AutoTrackingEnabled
                    : null) ??
                defaults.AutoTrackingEnabled,
            AutoDetectEveryNFrames = Math.Max(
                1,
                saved.YoloV8AutoDetectEveryNFrames ??
                (useLegacyActiveProfile
                    ? (int?)saved.AutoDetectEveryNFrames
                    : null) ??
                defaults.AutoDetectEveryNFrames),
            ParallelSessionCount = Math.Max(
                1,
                saved.YoloV8ParallelSessionCount ??
                (useLegacyActiveProfile
                    ? (int?)saved.ParallelSessionCount
                    : null) ??
                defaults.ParallelSessionCount)
        };
    }

    private static double ResolveSavedDownscaleRatio(
        double? savedValue,
        double? legacyValue,
        double defaultValue)
    {
        double value =
            savedValue ??
            legacyValue ??
            defaultValue;

        return value is 1.0 or 0.75 or 0.5 or 0.33
            ? value
            : defaultValue;
    }

    private static DownscaleQuality ResolveSavedDownscaleQuality(
        int? savedValue,
        int? legacyValue,
        DownscaleQuality defaultValue)
    {
        int value =
            savedValue ??
            legacyValue ??
            (int)defaultValue;

        return Enum.IsDefined(typeof(DownscaleQuality), value)
            ? (DownscaleQuality)value
            : defaultValue;
    }

    private static string? NormalizeModelPath(string? modelPath)
    {
        return string.IsNullOrWhiteSpace(modelPath)
            ? null
            : modelPath;
    }
}
