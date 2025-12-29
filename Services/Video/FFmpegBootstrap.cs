using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceShield.Services.Video
{
    public static class FFmpegBootstrap
    {
        public static void Initialize()
        {
            try
            {
                ffmpeg.avcodec_version();
                Console.WriteLine("[FFmpeg] Native libraries loaded successfully.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "FFmpeg native libraries not loaded. Check DLL placement.",
                    ex);
            }
        }
    }
}
