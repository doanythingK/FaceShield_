// FILE: D:\WorkSpace\FaceShield\Services\Video\FrameImageProvider.cs
using Avalonia.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video
{
    /// <summary>
    /// 프레임 추출 Provider
    /// - 동일 영상에 대해 Extractor 세션 재사용(매 클릭마다 open/close 금지)
    /// - 연속 클릭 시 이전 요청 cancel -> OperationCanceledException은 정상 흐름으로 무시
    /// - 끝 프레임(EOF)은 null 반환하고, 호출부에서 마지막 프레임/이전 프레임을 쓰도록 처리
    /// </summary>
    public sealed class FrameImageProvider : IDisposable
    {
        private readonly ConcurrentDictionary<string, FfFrameExtractor> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public Task<WriteableBitmap?> GetFrameAsync(string videoPath, int frameIndex, CancellationToken ct)
        {
            // UI를 막지 않도록 백그라운드
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var ex = _sessions.GetOrAdd(videoPath, p => new FfFrameExtractor(p));
                return ex.GetFrameByIndex(frameIndex);

            }, ct);
        }

        public void Dispose()
        {
            foreach (var kv in _sessions)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }
}
