using System.Buffers.Binary;
using System.Text;

namespace Lyceum.Services;

/// <summary>
/// Reads the duration of an MP4/MOV file from its 'moov/mvhd' box without decoding anything.
/// Only reads a few dozen bytes per box header, so it is fast even for large files.
/// </summary>
public static class Mp4Duration
{
    public static double? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            var moov = FindBox(fs, 0, fs.Length, "moov");
            if (moov is null) return null;
            var mvhd = FindBox(fs, moov.Value.dataStart, moov.Value.end, "mvhd");
            if (mvhd is null) return null;

            fs.Position = mvhd.Value.dataStart;
            Span<byte> buf = stackalloc byte[32];
            if (fs.Read(buf) < 32) return null;
            var version = buf[0];
            ulong timescale, duration;
            if (version == 1)
            {
                // version(1) flags(3) created(8) modified(8) timescale(4) duration(8)
                timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[20..]);
                duration = BinaryPrimitives.ReadUInt64BigEndian(buf[24..]);
            }
            else
            {
                // version(1) flags(3) created(4) modified(4) timescale(4) duration(4)
                timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[12..]);
                duration = BinaryPrimitives.ReadUInt32BigEndian(buf[16..]);
            }
            return timescale == 0 ? null : (double)duration / timescale;
        }
        catch
        {
            return null;
        }
    }

    private static (long dataStart, long end)? FindBox(FileStream fs, long start, long end, string type)
    {
        Span<byte> header = stackalloc byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(header[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var boxType = Encoding.ASCII.GetString(header[4..8]);
            long headerLen = 8;
            if (size == 1)
            {
                if (fs.Read(header[8..16]) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerLen) return null;
            if (boxType == type) return (pos + headerLen, Math.Min(pos + size, end));
            pos += size;
        }
        return null;
    }
}
