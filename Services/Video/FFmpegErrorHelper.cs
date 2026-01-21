using FFmpeg.AutoGen;
using System;
using System.Text;

namespace FaceShield.Services.Video
{
    public static unsafe class FFmpegErrorHelper
    {
        public static void ThrowIfError(int error, string context)
        {
            if (error >= 0)
                return;

            string detail = GetErrorMessage(error);
            throw new InvalidOperationException($"{context} (ffmpeg: {detail}, code: {error})");
        }

        public static string GetErrorMessage(int error)
        {
            byte* buf = stackalloc byte[1024];
            ffmpeg.av_strerror(error, buf, 1024);
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0');
        }
    }
}
