using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
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
                ConfigureRootPath();
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

        private static void ConfigureRootPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var macFrameworks = Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks"));
            if (Directory.Exists(macFrameworks))
            {
                ffmpeg.RootPath = macFrameworks;
                return;
            }

            var ffmpegDir = Path.Combine(baseDir, "FFmpeg");
            if (Directory.Exists(ffmpegDir))
            {
                ffmpeg.RootPath = ffmpegDir;
            }
        }
    }
}
