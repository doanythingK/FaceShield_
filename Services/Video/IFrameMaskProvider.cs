using Avalonia.Media.Imaging;

namespace FaceShield.Services.Video
{
    public interface IFrameMaskProvider
    {
        /// <summary>
        /// Export 시 사용할 최종 마스크.
        /// 프레임마다 없을 수도 있음(null).
        /// 반환된 bitmap은 호출자가 소유하며 사용 후 Dispose해야 합니다.
        /// </summary>
        WriteableBitmap? GetFinalMask(int frameIndex);

        /// <summary>
        /// 프레임별 마스크 저장/갱신.
        /// (프리뷰에서 만든 마스크, 추후 자동 분석에서 생성한 마스크 등)
        /// 호출이 성공하면 bitmap 소유권은 provider로 이전됩니다.
        /// 호출자는 이후 bitmap을 수정하거나 Dispose하면 안 됩니다.
        /// </summary>
        void SetMask(int frameIndex, WriteableBitmap mask);
    }
}
