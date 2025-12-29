using FaceShield.Models.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public interface IFrameAnalyzer
    {
        Task<IReadOnlyList<FrameAnalysisResult>> AnalyzeAsync(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct);
    }
}
