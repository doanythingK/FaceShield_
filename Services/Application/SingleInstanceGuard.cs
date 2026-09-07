using System;
using System.IO;

namespace FaceShield.Services.Application;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lockStream;
    private bool _disposed;

    private SingleInstanceGuard(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    internal static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        string rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield");
        Directory.CreateDirectory(rootDir);

        string lockPath = Path.Combine(rootDir, "FaceShield.instance.lock");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                options: FileOptions.None);

            if (stream.Length == 0)
            {
                stream.SetLength(1);
                stream.Flush(flushToDisk: true);
            }

            // The OS releases this lock automatically when the process exits,
            // including an abnormal process termination.
            stream.Lock(0, 1);
            guard = new SingleInstanceGuard(stream);
            stream = null;
            return true;
        }
        catch (IOException)
        {
            stream?.Dispose();
            guard = null;
            return false;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _lockStream.Unlock(0, 1);
        }
        catch (IOException)
        {
            // The OS releases the lock during stream/process cleanup.
        }
        finally
        {
            _lockStream.Dispose();
        }
    }
}
