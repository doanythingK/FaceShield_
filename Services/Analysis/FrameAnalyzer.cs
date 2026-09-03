using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public sealed class FrameAnalyzer : IFrameAnalyzer
    {
        private readonly IFaceDetector _detector;

        public FrameAnalyzer(IFaceDetector detector)
        {
            _detector = detector;
        }

        public async Task<IReadOnlyList<FrameAnalysisResult>> AnalyzeAsync(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            VideoMetadataInfo metadata = VideoMetadataReader.Read(videoPath, ct);
            double fps = metadata.Fps;
            int totalFrames = metadata.GetFrameCountEstimate();

            if (fps <= 0 || totalFrames <= 0)
                return Array.Empty<FrameAnalysisResult>();

            var list = new List<FrameAnalysisResult>();

            using var extractor = new FfFrameExtractor(videoPath, cancellationToken: ct);

            await Task.Run(() =>
            {
                for (int idx = 0; idx < totalFrames; idx++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var bmp = extractor.GetFrameByIndex(idx, ct);
                    if (bmp == null) continue;

                    var faces = _detector.DetectFaces(bmp);
                    bool hasFace = faces.Count > 0;

                    Rect? first = hasFace ? faces[0].Bounds : null;

                    double timestampSec = extractor.TryGetCachedFrameTimestampSeconds(
                        idx,
                        out double decodedTimestampSec)
                            ? decodedTimestampSec
                            : idx / fps;

                    list.Add(new FrameAnalysisResult
                    {
                        FrameIndex = idx,
                        TimestampSec = timestampSec,
                        HasFace = hasFace,
                        Confidence = hasFace ? 1.0f : 0.0f, // 점수는 실제 detector에서 추출 가능하면 교체
                        FaceBounds = first
                        // 필요하면 FrameAnalysisResult에 리스트 필드 추가해서 전체 bounds 보내도 됨
                    });

                    progress?.Report((int)(idx * 100.0 / Math.Max(1, totalFrames - 1)));
                }

                progress?.Report(100);
            }, ct);

            return list;
        }

    }
}
