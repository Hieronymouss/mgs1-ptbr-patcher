using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace Mgs1.Patcher.Core;

public sealed record BpsPatchInfo(
    long SourceSize,
    long TargetSize,
    string Metadata,
    uint SourceCrc32,
    uint TargetCrc32,
    uint PatchCrc32,
    long PatchSize);

public sealed record BpsActionCounts(
    long SourceReadActions,
    long SourceReadBytes,
    long TargetReadActions,
    long TargetReadBytes,
    long SourceCopyActions,
    long SourceCopyBytes,
    long TargetCopyActions,
    long TargetCopyBytes);

public sealed record BpsApplyResult(
    long OutputSize,
    string OutputSha256,
    uint OutputCrc32,
    BpsActionCounts Actions,
    string Metadata);

public static class BpsPatchReader
{
    private const int FooterSize = 12;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumVarintBytes = 10;

    public static async Task<BpsPatchInfo> InspectAsync(
        string patchPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        try
        {
            await using var patch = new FileStream(
                patchPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long patchSize = patch.Length;
            if (patchSize < 19)
            {
                throw new PatcherIntegrityException("BPS payload is too small.");
            }

            var patchCrc = new Crc32Accumulator();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(PatchApplyOptions.DefaultIoBufferSize);
            try
            {
                long remaining = patchSize - sizeof(uint);
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = await patch.ReadAsync(
                        buffer.AsMemory(0, wanted),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new PatcherIntegrityException("Truncated BPS payload.");
                    }

                    patchCrc.Append(buffer.AsSpan(0, read));
                    remaining -= read;
                }

                byte[] storedPatchCrcBytes = new byte[sizeof(uint)];
                await ReadExactlyAsync(patch, storedPatchCrcBytes, cancellationToken).ConfigureAwait(false);
                uint storedPatchCrc = BinaryPrimitives.ReadUInt32LittleEndian(storedPatchCrcBytes);
                if (patchCrc.Value != storedPatchCrc)
                {
                    throw new PatcherIntegrityException(
                        $"BPS patch CRC32 mismatch: expected {storedPatchCrc:x8}, got {patchCrc.Value:x8}.");
                }

                patch.Position = 0;
                RequireMagic(patch);
                long actionEnd = patchSize - FooterSize;
                long sourceSize = DecodeSize(patch, actionEnd, "source size");
                long targetSize = DecodeSize(patch, actionEnd, "target size");
                long metadataSize = DecodeSize(patch, actionEnd, "metadata size");
                if (metadataSize > MaximumMetadataBytes || metadataSize > actionEnd - patch.Position)
                {
                    throw new PatcherIntegrityException("BPS metadata overlaps actions or footer.");
                }

                byte[] metadataBytes = GC.AllocateUninitializedArray<byte>((int)metadataSize);
                ReadExactly(patch, metadataBytes);
                string metadata;
                try
                {
                    metadata = new UTF8Encoding(false, true).GetString(metadataBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new PatcherIntegrityException("BPS metadata is not valid UTF-8.", exception);
                }

                patch.Position = patchSize - FooterSize;
                Span<byte> footer = stackalloc byte[FooterSize];
                ReadExactly(patch, footer);
                uint sourceCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer);
                uint targetCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
                uint footerPatchCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
                if (footerPatchCrc != storedPatchCrc)
                {
                    throw new PatcherIntegrityException("BPS footer patch CRC32 is inconsistent.");
                }

                return new BpsPatchInfo(
                    sourceSize,
                    targetSize,
                    metadata,
                    sourceCrc,
                    targetCrc,
                    storedPatchCrc,
                    patchSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
            throw new PatcherIntegrityException(
                $"Cannot inspect BPS payload {patchPath}: {exception.Message}",
                exception);
        }
    }

    internal static ulong DecodeNumber(Stream stream, long limit)
    {
        ulong value = 0;
        ulong shift = 1;
        try
        {
            for (int index = 0; index < MaximumVarintBytes; index++)
            {
                if (stream.Position >= limit)
                {
                    throw new PatcherIntegrityException("Truncated BPS variable-length number.");
                }

                int raw = stream.ReadByte();
                if (raw < 0)
                {
                    throw new PatcherIntegrityException("Truncated BPS variable-length number.");
                }

                byte valueByte = (byte)raw;
                checked
                {
                    value += (ulong)(valueByte & 0x7f) * shift;
                    if ((valueByte & 0x80) != 0)
                    {
                        return value;
                    }

                    shift <<= 7;
                    value += shift;
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new PatcherIntegrityException("BPS variable-length number exceeds 64 bits.", exception);
        }

        throw new PatcherIntegrityException("BPS variable-length number exceeds 64 bits.");
    }

    internal static void RequireMagic(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        ReadExactly(stream, magic);
        if (!magic.SequenceEqual("BPS1"u8))
        {
            throw new PatcherIntegrityException("Unsupported patch format; expected BPS1.");
        }
    }

    internal static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw new PatcherIntegrityException("Truncated BPS payload.");
            }

            offset += read;
        }
    }

    private static long DecodeSize(Stream stream, long limit, string field)
    {
        ulong value = DecodeNumber(stream, limit);
        if (value > long.MaxValue)
        {
            throw new PatcherIntegrityException($"BPS {field} exceeds the supported 64-bit range.");
        }

        return (long)value;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new PatcherIntegrityException("Truncated BPS payload.");
            }

            offset += read;
        }
    }
}

internal static class BpsPatchApplier
{
    private const int FooterSize = 12;
    private const int SourceRead = 0;
    private const int TargetRead = 1;
    private const int SourceCopy = 2;
    private const int TargetCopy = 3;

    internal static async Task<BpsApplyResult> ApplyAsync(
        string sourcePath,
        string patchPath,
        string outputPath,
        FileDigest sourceDigest,
        ArtifactSpec expectedTarget,
        BpsPatchInfo info,
        int bufferSize,
        PatchProgressPhase phase,
        IProgress<PatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (info.SourceSize != sourceDigest.Size)
        {
            throw new PatcherIntegrityException("BPS source size does not match the selected input.");
        }

        if (info.SourceCrc32 != sourceDigest.Crc32)
        {
            throw new PatcherIntegrityException("BPS source CRC32 does not match the selected input.");
        }

        if (info.TargetSize != expectedTarget.Size)
        {
            throw new PatcherIntegrityException("BPS target size does not match the release manifest.");
        }

        if (File.Exists(outputPath))
        {
            throw new PatcherSafetyException($"Refusing to overwrite output: {outputPath}");
        }

        bool outputCreated = false;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using var patch = new FileStream(
                patchPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            FileStream output;
            try
            {
                output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                outputCreated = true;
            }
            catch (IOException exception) when (File.Exists(outputPath))
            {
                throw new PatcherSafetyException($"Refusing to overwrite output: {outputPath}", exception);
            }

            await using (output)
            using (var outputSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(outputPath, File.GetAttributes(outputPath) | FileAttributes.Hidden);
                }

                cancellationToken.ThrowIfCancellationRequested();
                BpsPatchReader.RequireMagic(patch);
                long actionEnd = info.PatchSize - FooterSize;
                long decodedSourceSize = DecodeLong(patch, actionEnd, "source size");
                long decodedTargetSize = DecodeLong(patch, actionEnd, "target size");
                long metadataSize = DecodeLong(patch, actionEnd, "metadata size");
                if (metadataSize > actionEnd - patch.Position)
                {
                    throw new PatcherIntegrityException("BPS metadata overlaps actions or footer.");
                }

                patch.Position += metadataSize;
                if (decodedSourceSize != info.SourceSize || decodedTargetSize != info.TargetSize)
                {
                    throw new PatcherIntegrityException("BPS header changed between inspection and application.");
                }

                var outputCrc = new Crc32Accumulator();
                long outputOffset = 0;
                long sourceRelative = 0;
                long targetRelative = 0;
                long sourceReadActions = 0;
                long sourceReadBytes = 0;
                long targetReadActions = 0;
                long targetReadBytes = 0;
                long sourceCopyActions = 0;
                long sourceCopyBytes = 0;
                long targetCopyActions = 0;
                long targetCopyBytes = 0;

                Report(progress, phase, expectedTarget.FileName, 0, info.TargetSize, cancellationToken);
                while (patch.Position < actionEnd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong encoded = BpsPatchReader.DecodeNumber(patch, actionEnd);
                    int mode = (int)(encoded & 3);
                    ulong rawLength = (encoded >> 2) + 1;
                    if (rawLength > long.MaxValue)
                    {
                        throw new PatcherIntegrityException("BPS action length exceeds the supported range.");
                    }

                    long length = (long)rawLength;
                    if (length > info.TargetSize - outputOffset)
                    {
                        throw new PatcherIntegrityException("BPS actions exceed declared target size.");
                    }

                    switch (mode)
                    {
                        case SourceRead:
                            if (length > info.SourceSize - outputOffset)
                            {
                                throw new PatcherIntegrityException("BPS SourceRead exceeds source size.");
                            }

                            await CopySourceAsync(
                                source,
                                output,
                                outputOffset,
                                outputOffset,
                                length,
                                buffer,
                                outputSha,
                                outputCrc,
                                progress,
                                phase,
                                expectedTarget.FileName,
                                info.TargetSize,
                                cancellationToken).ConfigureAwait(false);
                            sourceReadActions++;
                            sourceReadBytes = checked(sourceReadBytes + length);
                            break;

                        case TargetRead:
                            await CopyPatchAsync(
                                patch,
                                output,
                                outputOffset,
                                length,
                                actionEnd,
                                buffer,
                                outputSha,
                                outputCrc,
                                progress,
                                phase,
                                expectedTarget.FileName,
                                info.TargetSize,
                                cancellationToken).ConfigureAwait(false);
                            targetReadActions++;
                            targetReadBytes = checked(targetReadBytes + length);
                            break;

                        case SourceCopy:
                            sourceRelative = ApplyRelativeOffset(sourceRelative, patch, actionEnd, "SourceCopy");
                            if (sourceRelative < 0 || length > info.SourceSize - sourceRelative)
                            {
                                throw new PatcherIntegrityException("BPS SourceCopy exceeds source bounds.");
                            }

                            await CopySourceAsync(
                                source,
                                output,
                                sourceRelative,
                                outputOffset,
                                length,
                                buffer,
                                outputSha,
                                outputCrc,
                                progress,
                                phase,
                                expectedTarget.FileName,
                                info.TargetSize,
                                cancellationToken).ConfigureAwait(false);
                            sourceRelative = checked(sourceRelative + length);
                            sourceCopyActions++;
                            sourceCopyBytes = checked(sourceCopyBytes + length);
                            break;

                        case TargetCopy:
                            targetRelative = ApplyRelativeOffset(targetRelative, patch, actionEnd, "TargetCopy");
                            await CopyTargetAsync(
                                output,
                                targetRelative,
                                outputOffset,
                                length,
                                buffer,
                                outputSha,
                                outputCrc,
                                progress,
                                phase,
                                expectedTarget.FileName,
                                info.TargetSize,
                                cancellationToken).ConfigureAwait(false);
                            targetRelative = checked(targetRelative + length);
                            targetCopyActions++;
                            targetCopyBytes = checked(targetCopyBytes + length);
                            break;

                        default:
                            throw new PatcherIntegrityException("Unknown BPS action.");
                    }

                    outputOffset = checked(outputOffset + length);
                }

                if (outputOffset != info.TargetSize)
                {
                    throw new PatcherIntegrityException(
                        $"BPS actions produced {outputOffset} bytes; expected {info.TargetSize}.");
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                string actualSha = Convert.ToHexString(outputSha.GetHashAndReset()).ToLowerInvariant();
                if (outputCrc.Value != info.TargetCrc32)
                {
                    throw new PatcherIntegrityException(
                        $"BPS target CRC32 mismatch: expected {info.TargetCrc32:x8}, got {outputCrc.Value:x8}.");
                }

                if (!string.Equals(actualSha, expectedTarget.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PatcherIntegrityException(
                        $"Output SHA-256 mismatch: expected {expectedTarget.Sha256}, got {actualSha}.");
                }

                if (output.Length != expectedTarget.Size)
                {
                    throw new PatcherIntegrityException("Output size does not match the release manifest.");
                }

                Report(
                    progress,
                    phase,
                    expectedTarget.FileName,
                    outputOffset,
                    info.TargetSize,
                    cancellationToken);
                return new BpsApplyResult(
                    outputOffset,
                    actualSha,
                    outputCrc.Value,
                    new BpsActionCounts(
                        sourceReadActions,
                        sourceReadBytes,
                        targetReadActions,
                        targetReadBytes,
                        sourceCopyActions,
                        sourceCopyBytes,
                        targetCopyActions,
                        targetCopyBytes),
                    info.Metadata);
            }
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = outputCreated ? TryDelete(outputPath) : null;
            if (cleanupFailure is not null)
            {
                throw new PatcherSafetyException(
                    $"Patch application failed and partial output cleanup also failed: {outputPath}",
                    new AggregateException(failure, cleanupFailure));
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopySourceAsync(
        FileStream source,
        FileStream output,
        long sourceOffset,
        long outputOffset,
        long length,
        byte[] buffer,
        IncrementalHash sha256,
        Crc32Accumulator crc32,
        IProgress<PatchProgress>? progress,
        PatchProgressPhase phase,
        string item,
        long targetSize,
        CancellationToken cancellationToken)
    {
        source.Position = sourceOffset;
        output.Position = outputOffset;
        long remaining = length;
        long completed = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = (int)Math.Min(buffer.Length, remaining);
            await ReadExactlyAsync(source, buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            sha256.AppendData(buffer.AsSpan(0, take));
            crc32.Append(buffer.AsSpan(0, take));
            remaining -= take;
            completed += take;
            Report(progress, phase, item, outputOffset + completed, targetSize, cancellationToken);
        }
    }

    private static async Task CopyPatchAsync(
        FileStream patch,
        FileStream output,
        long outputOffset,
        long length,
        long actionEnd,
        byte[] buffer,
        IncrementalHash sha256,
        Crc32Accumulator crc32,
        IProgress<PatchProgress>? progress,
        PatchProgressPhase phase,
        string item,
        long targetSize,
        CancellationToken cancellationToken)
    {
        output.Position = outputOffset;
        long remaining = length;
        long completed = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (patch.Position >= actionEnd)
            {
                throw new PatcherIntegrityException("BPS TargetRead overlaps the checksum footer.");
            }

            int take = (int)Math.Min(buffer.Length, Math.Min(remaining, actionEnd - patch.Position));
            await ReadExactlyAsync(patch, buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            sha256.AppendData(buffer.AsSpan(0, take));
            crc32.Append(buffer.AsSpan(0, take));
            remaining -= take;
            completed += take;
            Report(progress, phase, item, outputOffset + completed, targetSize, cancellationToken);
        }
    }

    private static async Task CopyTargetAsync(
        FileStream output,
        long readOffset,
        long outputOffset,
        long length,
        byte[] buffer,
        IncrementalHash sha256,
        Crc32Accumulator crc32,
        IProgress<PatchProgress>? progress,
        PatchProgressPhase phase,
        string item,
        long targetSize,
        CancellationToken cancellationToken)
    {
        long remaining = length;
        long completed = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long currentOutputOffset = outputOffset + completed;
            long currentReadOffset = readOffset + completed;
            if (currentReadOffset < 0 || currentReadOffset >= currentOutputOffset)
            {
                throw new PatcherIntegrityException("BPS TargetCopy references unavailable output.");
            }

            int take = (int)Math.Min(buffer.Length, remaining);
            long available = currentOutputOffset - currentReadOffset;
            int existing = (int)Math.Min(take, available);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Position = currentReadOffset;
            await ReadExactlyAsync(output, buffer.AsMemory(0, existing), cancellationToken).ConfigureAwait(false);
            if (existing < take)
            {
                for (int index = existing; index < take; index++)
                {
                    buffer[index] = buffer[index - existing];
                }
            }

            output.Position = currentOutputOffset;
            await output.WriteAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            sha256.AppendData(buffer.AsSpan(0, take));
            crc32.Append(buffer.AsSpan(0, take));
            remaining -= take;
            completed += take;
            Report(progress, phase, item, outputOffset + completed, targetSize, cancellationToken);
        }
    }

    private static long ApplyRelativeOffset(long current, Stream patch, long actionEnd, string action)
    {
        ulong encoded = BpsPatchReader.DecodeNumber(patch, actionEnd);
        ulong rawMagnitude = encoded >> 1;
        if (rawMagnitude > long.MaxValue)
        {
            throw new PatcherIntegrityException($"BPS {action} relative offset exceeds the supported range.");
        }

        long magnitude = (long)rawMagnitude;
        long delta = (encoded & 1) != 0 ? -magnitude : magnitude;
        try
        {
            return checked(current + delta);
        }
        catch (OverflowException exception)
        {
            throw new PatcherIntegrityException($"BPS {action} relative offset overflowed.", exception);
        }
    }

    private static long DecodeLong(Stream patch, long actionEnd, string field)
    {
        ulong value = BpsPatchReader.DecodeNumber(patch, actionEnd);
        if (value > long.MaxValue)
        {
            throw new PatcherIntegrityException($"BPS {field} exceeds the supported range.");
        }

        return (long)value;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new PatcherIntegrityException("BPS action reads beyond available data.");
            }

            offset += read;
        }
    }

    private static void Report(
        IProgress<PatchProgress>? progress,
        PatchProgressPhase phase,
        string item,
        long completed,
        long total,
        CancellationToken cancellationToken)
    {
        progress?.Report(new PatchProgress(phase, item, completed, total));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Exception? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }
}
