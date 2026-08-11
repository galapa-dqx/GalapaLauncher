namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// A read/write <see cref="FileStream"/> with the write/wipe helpers the SQPK
/// commands need, plus a retry loop for the transient sharing violations that crop
/// up while the game's files are being touched by other processes.
/// </summary>
public sealed class SqexFileStream : FileStream
{
    private static readonly byte[] WipeBuffer = new byte[1 << 16];

    public SqexFileStream(string path, FileMode mode)
        : base(path, mode, FileAccess.ReadWrite, FileShare.Read, 1 << 16)
    {
    }

    public static SqexFileStream WaitForStream(string path, FileMode mode, int tries = 5, int sleeptime = 1)
    {
        while (true)
        {
            try
            {
                return new SqexFileStream(path, mode);
            }
            catch (IOException)
            {
                if (tries-- <= 0)
                    throw;

                Thread.Sleep(sleeptime * 1000);
            }
        }
    }

    public void WriteFromOffset(byte[] data, long offset)
    {
        Seek(offset, SeekOrigin.Begin);
        Write(data, 0, data.Length);
    }

    public void Wipe(long length)
    {
        var numFullChunks = length / WipeBuffer.Length;
        for (var i = 0; i < numFullChunks; i++)
            Write(WipeBuffer, 0, WipeBuffer.Length);
        Write(WipeBuffer, 0, checked((int)(length - numFullChunks * WipeBuffer.Length)));
    }

    public void WipeFromOffset(long length, long offset)
    {
        Seek(offset, SeekOrigin.Begin);
        Wipe(length);
    }
}
