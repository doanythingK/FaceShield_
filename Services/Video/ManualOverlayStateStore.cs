using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceShield.Services.Video;

/// <summary>
/// A manually confirmed mask on exactly one frame. Alpha is tightly packed,
/// one byte per pixel; it does not encode an inferred tracking result.
/// </summary>
internal sealed record ManualOverlayStoredKeyframe(int FrameIndex, int Width, int Height, byte[] Alpha)
{
    internal static ManualOverlayStoredKeyframe FromBgra(
        int frameIndex, byte[] pixels, int rowBytes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int count = ManualOverlayStateStore.CheckedPixelCount(width, height);
        if (rowBytes < checked(width * 4) || (long)rowBytes * height > pixels.Length)
            throw new ArgumentException("Invalid BGRA frame layout.", nameof(pixels));
        var alpha = new byte[count];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = pixels[y * rowBytes + x * 4 + 3];
        return new ManualOverlayStoredKeyframe(frameIndex, width, height, alpha);
    }

    internal ManualOverlayMask ToBgra(int rowBytes)
    {
        int count = ManualOverlayStateStore.CheckedPixelCount(Width, Height);
        if (Alpha == null || Alpha.Length != count || rowBytes < checked(Width * 4))
            throw new InvalidDataException("Invalid stored mask dimensions or alpha data.");
        var result = new byte[checked(rowBytes * Height)];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                byte alpha = Alpha[y * Width + x];
                int p = y * rowBytes + x * 4;
                result[p] = result[p + 1] = result[p + 2] = result[p + 3] = alpha;
            }
        return new ManualOverlayMask(result, rowBytes);
    }
}

internal sealed record ManualOverlayStoredTarget(Guid Id, IReadOnlyList<ManualOverlayStoredKeyframe> Keyframes);

/// <summary>
/// Separate, versioned persistence for manual targets. The caller supplies a
/// workspace-specific path and video source evidence. Missing files are empty;
/// existing corrupt or mismatched files throw instead of silently dropping blur.
/// The legacy automatic-mask and tracking stores are never written here.
/// This foundation is not yet wired into the editor or export.
/// </summary>
internal static class ManualOverlayStateStore
{
    private const int CurrentVersion = 1;
    private const int MaxDimension = 8192;
    private const int MaxPixels = 64_000_000;
    private static readonly object SaveGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    private sealed class FileDto
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public string SourceEvidence { get; set; } = string.Empty;
        [JsonRequired] public List<TargetDto> Targets { get; set; } = new();
    }

    private sealed class TargetDto
    {
        [JsonRequired] public Guid Id { get; set; }
        [JsonRequired] public List<KeyframeDto> Keyframes { get; set; } = new();
    }

    private sealed class KeyframeDto
    {
        [JsonRequired] public int FrameIndex { get; set; }
        [JsonRequired] public int Width { get; set; }
        [JsonRequired] public int Height { get; set; }
        [JsonRequired] public byte[] CompressedAlpha { get; set; } = Array.Empty<byte>();
    }

    internal static int CheckedPixelCount(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Unsupported mask dimensions.");
        return checked(width * height);
    }

    internal static void Save(
        string path, string sourceEvidence, IReadOnlyCollection<ManualOverlayStoredTarget> targets)
    {
        ValidateHeader(path, sourceEvidence);
        ArgumentNullException.ThrowIfNull(targets);
        var dto = new FileDto { Version = CurrentVersion, SourceEvidence = sourceEvidence };
        var seenIds = new HashSet<Guid>();
        foreach (ManualOverlayStoredTarget target in targets.OrderBy(t => t.Id))
        {
            if (target == null || target.Id == Guid.Empty || !seenIds.Add(target.Id) ||
                target.Keyframes == null)
                throw new InvalidDataException("Invalid or duplicated manual target.");
            var savedTarget = new TargetDto { Id = target.Id };
            var frames = new HashSet<int>();
            foreach (ManualOverlayStoredKeyframe keyframe in target.Keyframes.OrderBy(k => k.FrameIndex))
            {
                if (keyframe == null || keyframe.FrameIndex < 0 || !frames.Add(keyframe.FrameIndex))
                    throw new InvalidDataException("Invalid or duplicated manual keyframe.");
                int count = CheckedPixelCount(keyframe.Width, keyframe.Height);
                if (keyframe.Alpha == null || keyframe.Alpha.Length != count)
                    throw new InvalidDataException("Manual mask alpha size does not match dimensions.");
                savedTarget.Keyframes.Add(new KeyframeDto
                {
                    FrameIndex = keyframe.FrameIndex,
                    Width = keyframe.Width,
                    Height = keyframe.Height,
                    CompressedAlpha = Compress(keyframe.Alpha)
                });
            }
            dto.Targets.Add(savedTarget);
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        lock (SaveGate)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, json);
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    internal static IReadOnlyList<ManualOverlayStoredTarget> Load(string path, string sourceEvidence)
    {
        ValidateHeader(path, sourceEvidence);
        if (!File.Exists(path)) return Array.Empty<ManualOverlayStoredTarget>();
        FileDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Manual overlay file has no root object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Manual overlay file is malformed.", ex);
        }
        if (dto.Version != CurrentVersion || dto.SourceEvidence != sourceEvidence || dto.Targets == null)
            throw new InvalidDataException("Manual overlay version or video source evidence does not match.");

        var result = new List<ManualOverlayStoredTarget>(dto.Targets.Count);
        var ids = new HashSet<Guid>();
        foreach (TargetDto target in dto.Targets)
        {
            if (target == null || target.Id == Guid.Empty || !ids.Add(target.Id) || target.Keyframes == null)
                throw new InvalidDataException("Invalid or duplicated manual target.");
            var keyframes = new List<ManualOverlayStoredKeyframe>(target.Keyframes.Count);
            int lastFrame = -1;
            foreach (KeyframeDto keyframe in target.Keyframes)
            {
                if (keyframe == null || keyframe.FrameIndex <= lastFrame)
                    throw new InvalidDataException("Manual keyframes must have unique increasing frame indices.");
                int count = CheckedPixelCount(keyframe.Width, keyframe.Height);
                if (keyframe.CompressedAlpha == null)
                    throw new InvalidDataException("Manual keyframe has no alpha payload.");
                keyframes.Add(new ManualOverlayStoredKeyframe(keyframe.FrameIndex,
                    keyframe.Width, keyframe.Height, DecompressExactly(keyframe.CompressedAlpha, count)));
                lastFrame = keyframe.FrameIndex;
            }
            result.Add(new ManualOverlayStoredTarget(target.Id, keyframes));
        }
        return result;
    }

    private static void ValidateHeader(string path, string sourceEvidence)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A state file path is required.", nameof(path));
        if (string.IsNullOrWhiteSpace(sourceEvidence))
            throw new ArgumentException("Video source evidence is required.", nameof(sourceEvidence));
    }

    private static byte[] Compress(byte[] alpha)
    {
        using var output = new MemoryStream();
        using (var compression = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compression.Write(alpha);
        return output.ToArray();
    }

    private static byte[] DecompressExactly(byte[] compressed, int length)
    {
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var decompression = new ZLibStream(input, CompressionMode.Decompress);
            var alpha = new byte[length];
            decompression.ReadExactly(alpha);
            if (decompression.ReadByte() != -1)
                throw new InvalidDataException("Manual mask alpha is longer than its declared dimensions.");
            return alpha;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new InvalidDataException("Manual mask alpha payload is corrupt or truncated.", ex);
        }
    }
}
