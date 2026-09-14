using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video;

public readonly record struct VideoMetadataInfo(
    double Fps,
    int ContainerFrameCount,
    double DurationSeconds,
    bool HasContainerFrameCount)
{
    public int GetFrameCountEstimate(bool minimumOneWhenUnknown = false)
    {
        if (HasContainerFrameCount)
            return Math.Max(0, ContainerFrameCount);

        if (DurationSeconds > 0 && Fps > 0)
        {
            double estimate = Math.Floor(DurationSeconds * Fps);
            if (double.IsFinite(estimate) && estimate > 0)
            {
                int frames = (int)Math.Min(estimate, int.MaxValue);
                return minimumOneWhenUnknown
                    ? Math.Max(1, frames)
                    : Math.Max(0, frames);
            }
        }

        return minimumOneWhenUnknown ? 1 : 0;
    }
}

public static unsafe class VideoMetadataReader
{
    private static readonly AVIOInterruptCB_callback InterruptCallback =
        HandleInterrupt;

    private sealed class InterruptState
    {
        public int Requested;
    }

    public static VideoMetadataInfo Read(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Video path is empty.", nameof(path));

        cancellationToken.ThrowIfCancellationRequested();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

        AVFormatContext* format = ffmpeg.avformat_alloc_context();
        if (format == null)
            throw new InvalidOperationException("avformat_alloc_context failed");

        var interruptState = new InterruptState();
        GCHandle interruptHandle =
            GCHandle.Alloc(interruptState, GCHandleType.Normal);
        CancellationTokenRegistration registration = default;

        try
        {
            format->interrupt_callback.callback = InterruptCallback;
            format->interrupt_callback.opaque =
                (void*)GCHandle.ToIntPtr(interruptHandle);

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(
                    static state =>
                    {
                        if (state is InterruptState interrupt)
                            Volatile.Write(ref interrupt.Requested, 1);
                    },
                    interruptState);
            }

            if (cancellationToken.IsCancellationRequested)
                Volatile.Write(ref interruptState.Requested, 1);

            int openResult =
                ffmpeg.avformat_open_input(&format, path, null, null);
            cancellationToken.ThrowIfCancellationRequested();
            FFmpegErrorHelper.ThrowIfError(
                openResult,
                $"Failed to open video: {path}");

            int streamInfoResult =
                ffmpeg.avformat_find_stream_info(format, null);
            cancellationToken.ThrowIfCancellationRequested();
            FFmpegErrorHelper.ThrowIfError(
                streamInfoResult,
                $"Failed to read stream info: {path}");

            int videoStreamIndex =
                FFmpegStreamSelection.FindPrimaryVideoStreamIndex(format);
            AVStream* videoStream =
                videoStreamIndex >= 0
                    ? format->streams[videoStreamIndex]
                    : null;
            if (videoStream == null)
                throw new InvalidOperationException("Video stream not found.");

            double fpsValue =
                videoStream->avg_frame_rate.num != 0
                    ? ffmpeg.av_q2d(videoStream->avg_frame_rate)
                    : videoStream->r_frame_rate.num != 0
                        ? ffmpeg.av_q2d(videoStream->r_frame_rate)
                        : 30.0;
            if (!double.IsFinite(fpsValue) || fpsValue <= 0)
                fpsValue = 30.0;

            double durationSeconds;
            if (videoStream->duration > 0)
            {
                durationSeconds =
                    videoStream->duration *
                    ffmpeg.av_q2d(videoStream->time_base);
            }
            else if (format->duration > 0)
            {
                durationSeconds =
                    format->duration /
                    (double)ffmpeg.AV_TIME_BASE;
            }
            else
            {
                durationSeconds = 0;
            }

            if (!double.IsFinite(durationSeconds) ||
                durationSeconds < 0)
            {
                durationSeconds = 0;
            }

            bool hasContainerFrameCount =
                videoStream->nb_frames > 0;
            int containerFrameCount = hasContainerFrameCount
                ? (int)Math.Min(
                    videoStream->nb_frames,
                    int.MaxValue)
                : 0;

            return new VideoMetadataInfo(
                Fps: fpsValue,
                ContainerFrameCount: containerFrameCount,
                DurationSeconds: durationSeconds,
                HasContainerFrameCount: hasContainerFrameCount);
        }
        finally
        {
            if (format != null)
                ffmpeg.avformat_close_input(&format);

            registration.Dispose();
            if (interruptHandle.IsAllocated)
                interruptHandle.Free();
        }
    }

    private static int HandleInterrupt(void* opaque)
    {
        if (opaque == null)
            return 0;

        try
        {
            GCHandle handle =
                GCHandle.FromIntPtr((IntPtr)opaque);
            if (handle.Target is not InterruptState state)
                return 1;

            return Volatile.Read(ref state.Requested) != 0
                ? 1
                : 0;
        }
        catch
        {
            return 1;
        }
    }
}
