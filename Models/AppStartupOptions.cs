using FaceShield.Enums.Workspace;
using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.IO;

namespace FaceShield.Models
{
    public sealed class AppStartupOptions
    {
        public string? VideoPath { get; private init; }
        public FaceDetectorBackend? DetectorBackend { get; private init; }
        public YoloFaceModelType? YoloModelType { get; private init; }
        public string? YoloModelPath { get; private init; }
        public WorkspaceMode? OpenMode { get; private init; }
        public bool? AutoExportAfter { get; private init; }
        public int? FrameIndex { get; private init; }

        public bool HasValues =>
            !string.IsNullOrWhiteSpace(VideoPath) ||
            DetectorBackend.HasValue ||
            YoloModelType.HasValue ||
            !string.IsNullOrWhiteSpace(YoloModelPath) ||
            OpenMode.HasValue ||
            AutoExportAfter.HasValue ||
            FrameIndex.HasValue;

        public static AppStartupOptions Parse(IEnumerable<string>? args)
        {
            string? videoPath = null;
            FaceDetectorBackend? detectorBackend = null;
            YoloFaceModelType? yoloModelType = null;
            string? yoloModelPath = null;
            WorkspaceMode? openMode = null;
            bool? autoExportAfter = null;
            int? frameIndex = null;

            var tokens = args is null ? Array.Empty<string>() : [.. args];
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                switch (token.Trim().ToLowerInvariant())
                {
                    case "--yolo-smoke":
                        detectorBackend = FaceDetectorBackend.YoloFaceOnnx;
                        yoloModelType ??= YoloFaceModelType.Yolo5Face;
                        videoPath ??= ResolveExistingPath("srcTest/260102_jp_10.mp4");
                        yoloModelPath ??= ResolveExistingPath(".tmp/models/YoloV5Face.onnx");
                        break;
                    case "--video":
                        videoPath = ReadValue(tokens, ref i, token);
                        break;
                    case "--detector":
                        detectorBackend = ParseDetectorBackend(ReadValue(tokens, ref i, token));
                        break;
                    case "--yolo-model-type":
                        yoloModelType = ParseYoloModelType(ReadValue(tokens, ref i, token));
                        break;
                    case "--yolo-model":
                        yoloModelPath = ReadValue(tokens, ref i, token);
                        break;
                    case "--open-manual":
                        openMode = WorkspaceMode.Manual;
                        break;
                    case "--open-auto":
                        openMode = WorkspaceMode.Auto;
                        break;
                    case "--no-auto-export":
                        autoExportAfter = false;
                        break;
                    case "--auto-export":
                        autoExportAfter = true;
                        break;
                    case "--frame":
                        frameIndex = ParseFrameIndex(ReadValue(tokens, ref i, token));
                        break;
                }
            }

            return new AppStartupOptions
            {
                VideoPath = NormalizePath(videoPath),
                DetectorBackend = detectorBackend,
                YoloModelType = yoloModelType,
                YoloModelPath = NormalizePath(yoloModelPath),
                OpenMode = openMode,
                AutoExportAfter = autoExportAfter,
                FrameIndex = frameIndex
            };
        }

        private static string ReadValue(IReadOnlyList<string> tokens, ref int index, string optionName)
        {
            if (index + 1 >= tokens.Count || tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{optionName} requires a value.");

            index++;
            return tokens[index];
        }

        private static FaceDetectorBackend ParseDetectorBackend(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "faceonnx" or "onnx" => FaceDetectorBackend.FaceOnnx,
                "yolo" or "yolofaceonnx" or "yolo-face-onnx" => FaceDetectorBackend.YoloFaceOnnx,
                _ => throw new ArgumentException($"Unsupported detector backend: {value}")
            };
        }

        private static YoloFaceModelType ParseYoloModelType(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "yolo5" or "yolov5" or "yolo5face" or "yolov5face" => YoloFaceModelType.Yolo5Face,
                "yolo8" or "yolov8" or "yolov8face" or "yolov8-face" => YoloFaceModelType.YoloV8Face,
                _ => throw new ArgumentException($"Unsupported YOLO model type: {value}")
            };
        }

        private static int ParseFrameIndex(string value)
        {
            if (!int.TryParse(value, out int frameIndex) || frameIndex < 0)
                throw new ArgumentException($"Unsupported frame index: {value}");

            return frameIndex;
        }

        private static string? ResolveExistingPath(string relativePath)
        {
            string path = NormalizePath(relativePath) ?? relativePath;
            return File.Exists(path) ? path : null;
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return Path.GetFullPath(path);
        }
    }
}
