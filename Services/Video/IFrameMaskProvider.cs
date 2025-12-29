using Avalonia.Media.Imaging;

namespace FaceShield.Services.Video
{
    public interface IFrameMaskProvider
    {
        /// <summary>
        /// Export 시 사용할 최종 마스크.
        /// 프레임마다 없을 수도 있음(null).
        /// </summary>
        WriteableBitmap? GetFinalMask(int frameIndex);

        /// <summary>
        /// 프레임별 마스크 저장/갱신.
        /// (프리뷰에서 만든 마스크, 추후 자동 분석에서 생성한 마스크 등)
        /// </summary>
        void SetMask(int frameIndex, WriteableBitmap mask);
    }
}
