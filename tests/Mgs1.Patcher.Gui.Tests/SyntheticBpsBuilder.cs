using System.Buffers.Binary;

namespace Mgs1.Patcher.Gui.Tests;

internal static class SyntheticBpsBuilder
{
    private const int TargetRead = 1;

    internal static byte[] CreateTargetRead(byte[] source, byte[] target)
    {
        using var body = new MemoryStream();
        body.Write("BPS1"u8);
        WriteNumber(body, (ulong)source.Length);
        WriteNumber(body, (ulong)target.Length);
        WriteNumber(body, 0);
        WriteNumber(body, ((ulong)(target.Length - 1) << 2) | TargetRead);
        body.Write(target);
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

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint current = uint.MaxValue;
        foreach (byte value in data)
        {
            current ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0 ? 0xedb88320U ^ (current >> 1) : current >> 1;
            }
        }

        return current ^ uint.MaxValue;
    }
}
