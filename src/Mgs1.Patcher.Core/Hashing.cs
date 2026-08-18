using System.Buffers;
using System.Security.Cryptography;

namespace Mgs1.Patcher.Core;

public sealed record FileDigest(long Size, string Sha256, uint Crc32)
{
    public string Crc32Hex => Crc32.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed record FileFingerprint(long Size, long LastWriteUtcTicks, long CreationUtcTicks);

internal static class FileIntegrity
{
    internal static async Task<FileDigest> VerifyAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        string label,
        int bufferSize,
        Action<long, long>? report,
        CancellationToken cancellationToken)
    {
        FileInfo info = GetReadableFile(path, label);
        if (info.Length != expectedSize)
        {
            throw new PatcherIntegrityException(
                $"{label} size mismatch: expected {expectedSize}, got {info.Length}.");
        }

        FileDigest digest = await DigestAsync(path, bufferSize, report, cancellationToken)
            .ConfigureAwait(false);
        if (digest.Size != expectedSize)
        {
            throw new PatcherIntegrityException(
                $"{label} size changed while hashing: expected {expectedSize}, got {digest.Size}.");
        }

        if (!string.Equals(digest.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherIntegrityException(
                $"{label} SHA-256 mismatch: expected {expectedSha256.ToLowerInvariant()}, got {digest.Sha256}.");
        }

        return digest;
    }

    internal static async Task<FileDigest> DigestAsync(
        string path,
        int bufferSize,
        Action<long, long>? report,
        CancellationToken cancellationToken)
    {
        FileInfo info = GetReadableFile(path, "File");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var crc32 = new Crc32Accumulator();
            long total = 0;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, bufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                sha256.AppendData(buffer.AsSpan(0, read));
                crc32.Append(buffer.AsSpan(0, read));
                total = checked(total + read);
                report?.Invoke(total, info.Length);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new FileDigest(
                total,
                Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
                crc32.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PatcherException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PatcherIntegrityException($"Cannot read file {path}: {exception.Message}", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static FileFingerprint Fingerprint(string path, string label)
    {
        FileInfo info = GetReadableFile(path, label);
        return new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    private static FileInfo GetReadableFile(string path, string label)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            {
                throw new PatcherIntegrityException($"{label} is not a readable file: {path}");
            }

            return info;
        }
        catch (PatcherIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PatcherIntegrityException($"Cannot inspect {label} {path}: {exception.Message}", exception);
        }
    }
}

internal sealed class Crc32Accumulator
{
    private static readonly uint[] Table = BuildTable();
    private uint state = uint.MaxValue;

    internal uint Value => state ^ uint.MaxValue;

    internal void Append(ReadOnlySpan<byte> data)
    {
        uint current = state;
        foreach (byte value in data)
        {
            current = Table[(current ^ value) & 0xff] ^ (current >> 8);
        }

        state = current;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
