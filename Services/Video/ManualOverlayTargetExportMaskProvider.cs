using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

/// <summary>
/// Combines the existing Auto/legacy export snapshot with each target's exact
/// keyframe or validated sample. Missing motion is never replaced by a held
/// mask or silently emitted as an unblurred face. Empty explicit keyframes
/// mean the user has marked that target absent from that frame onward.
/// </summary>
internal sealed class ManualOverlayTargetExportMaskProvider : IFrameMaskProvider
{
    private readonly IFrameMaskProvider _legacyAndAuto;
    private readonly ManualOverlayTargetWorkspace _workspace;
    private readonly PixelSize _size;

    private ManualOverlayTargetExportMaskProvider(
        IFrameMaskProvider legacyAndAuto,
        ManualOverlayTargetWorkspace workspace,
        PixelSize size)
    {
        _legacyAndAuto = legacyAndAuto;
        _workspace = workspace;
        _size = size;
    }

    internal static IFrameMaskProvider WrapIfPresent(
        string videoPath, IFrameMaskProvider legacyAndAuto)
    {
        ArgumentNullException.ThrowIfNull(legacyAndAuto);
        ManualOverlayTargetWorkspace workspace = ManualOverlayTargetWorkspace.Open(videoPath);
        IReadOnlyList<ManualOverlayStoredTarget> targets = workspace.Snapshot();
        if (targets.Count == 0 || targets.All(target => target.Keyframes.Count == 0))
            return legacyAndAuto;

        foreach (ManualOverlayStoredTarget target in targets)
            foreach (ManualMaskTrackSegment segment in target.Segments ??
                         Array.Empty<ManualMaskTrackSegment>())
            {
                if (!segment.StoppedByFailure) continue;
                bool correctedAtFailure = segment.StopFrame.HasValue &&
                    target.Keyframes.Any(keyframe =>
                        keyframe.FrameIndex == segment.StopFrame.Value);
                if (!correctedAtFailure)
                    throw new InvalidDataException(
                        $"수동 얼굴 {target.Id}의 {segment.StopFrame} 프레임 추적 실패가 미해결 상태입니다. " +
                        "해당 프레임에서 같은 얼굴을 보정한 뒤 다시 내보내세요.");
            }

        ManualOverlayStoredKeyframe? first = targets.SelectMany(target => target.Keyframes)
            .FirstOrDefault();
        if (first == null) return legacyAndAuto;
        var size = new PixelSize(first.Width, first.Height);
        if (targets.SelectMany(target => target.Keyframes)
            .Any(keyframe => keyframe.Width != size.Width || keyframe.Height != size.Height))
            throw new InvalidDataException("수동 얼굴별 키프레임의 영상 크기가 서로 다릅니다.");
        return new ManualOverlayTargetExportMaskProvider(legacyAndAuto, workspace, size);
    }

    public WriteableBitmap? GetFinalMask(int frameIndex)
    {
        WriteableBitmap? legacy = _legacyAndAuto.GetFinalMask(frameIndex);
        try
        {
            if (legacy != null && legacy.PixelSize != _size)
                throw new InvalidDataException("내보내기 Auto/기존 수동 마스크의 크기가 대상별 마스크와 다릅니다.");
            int rowBytes = checked(_size.Width * 4);
            byte[]? automaticAndLegacy = legacy == null ? null : ReadPixels(legacy, rowBytes);

            // Strict composition resolves each target once. An absent face is
            // skipped, but any missing sample for a present face raises an error.
            // No per-frame keyframe sort, preflight transform or target BGRA list.
            if (!_workspace.TryComposeFrame(frameIndex, _size.Width, _size.Height,
                    automaticAndLegacy, rowBytes, out ManualOverlayMask merged,
                    requireCompleteTargets: true))
                return null;

            var bitmap = new WriteableBitmap(_size, new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            try
            {
                using var buffer = bitmap.Lock();
                for (int y = 0; y < _size.Height; y++)
                    Marshal.Copy(merged.Pixels, checked(y * merged.RowBytes),
                        IntPtr.Add(buffer.Address, checked(y * buffer.RowBytes)), rowBytes);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            legacy?.Dispose();
        }
    }

    public void SetMask(int frameIndex, WriteableBitmap mask)
        => throw new NotSupportedException("The export mask provider is read-only.");

    private byte[] ReadPixels(WriteableBitmap bitmap, int rowBytes)
    {
        byte[] pixels = new byte[checked(rowBytes * _size.Height)];
        using var buffer = bitmap.Lock();
        for (int y = 0; y < _size.Height; y++)
            Marshal.Copy(IntPtr.Add(buffer.Address, checked(y * buffer.RowBytes)),
                pixels, checked(y * rowBytes), rowBytes);
        return pixels;
    }
}
