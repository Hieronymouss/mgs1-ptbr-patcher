using System.Buffers.Binary;
using System.Text;

namespace Mgs1.Patcher.Core.Tests;

internal static class SyntheticBpsBuilder
{
    private const int SourceRead = 0;
    private const int TargetRead = 1;
    private const int SourceCopy = 2;
    private const int TargetCopy = 3;

    internal static byte[] CreateTargetRead(byte[] source, byte[] target)
    {
        using var body = Begin(source.Length, target.Length);
        WriteNumber(body, ((ulong)(target.Length - 1) << 2) | TargetRead);
        body.Write(target);
        return Finish(body, source, target);
    }

    internal static byte[] CreateAllModes(byte[] source, out byte[] target)
    {
        target = Encoding.ASCII.GetBytes("abcXYfgXYfgXYfg");
        using var body = Begin(source.Length, target.Length);
        WriteNumber(body, ((ulong)(3 - 1) << 2) | SourceRead);
        WriteNumber(body, ((ulong)(2 - 1) << 2) | TargetRead);
        body.Write("XY"u8);
        WriteNumber(body, ((ulong)(2 - 1) << 2) | SourceCopy);
        WriteNumber(body, EncodeRelative(5));
        WriteNumber(body, ((ulong)(8 - 1) << 2) | TargetCopy);
        WriteNumber(body, EncodeRelative(3));
        return Finish(body, source, target);
    }

    internal static byte[] WithInvalidTargetCrc(byte[] patch)
    {
        byte[] changed = (byte[])patch.Clone();
        int targetCrcOffset = changed.Length - 8;
        changed[targetCrcOffset] ^= 1;
        uint patchCrc = ComputeCrc32(changed.AsSpan(0, changed.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(changed.Length - 4), patchCrc);
        return changed;
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint current = uint.MaxValue;
        foreach (byte value in data)
        {
            current ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? 0xedb88320U ^ (current >> 1)
                    : current >> 1;
            }
        }

        return current ^ uint.MaxValue;
    }

    private static MemoryStream Begin(int sourceSize, int targetSize)
    {
        var body = new MemoryStream();
        body.Write("BPS1"u8);
        WriteNumber(body, (ulong)sourceSize);
        WriteNumber(body, (ulong)targetSize);
        WriteNumber(body, 0);
        return body;
    }

    private static byte[] Finish(MemoryStream body, byte[] source, byte[] target)
    {
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, ComputeCrc32(source));
        body.Write(crc);
        BinaryPrimitives.WriteUInt32LittleEndian(crc, ComputeCrc32(target));
        body.Write(crc);
        uint patchCrc = ComputeCrc32(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(crc, patchCrc);
        body.Write(crc);
        return body.ToArray();
    }

    private static ulong EncodeRelative(long delta) =>
        delta < 0 ? ((ulong)(-delta) << 1) | 1 : (ulong)delta << 1;

    private static void WriteNumber(Stream stream, ulong value)
    {
        while (true)
        {
            byte next = (byte)(value & 0x7f);
            value >>= 7;
            if (value == 0)
            {
                stream.WriteByte((byte)(next | 0x80));
                return;
            }

            stream.WriteByte(next);
            value--;
        }
    }
}
