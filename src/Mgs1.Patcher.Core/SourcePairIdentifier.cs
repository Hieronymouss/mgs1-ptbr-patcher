namespace Mgs1.Patcher.Core;

/// <summary>
/// The exact relationship of a selected BIN/CUE pair to a loaded manifest.
/// </summary>
public enum SourcePairRecognitionKind
{
    ExactPair,
    MixedDiscPair,
    Unsupported,
}

public enum SourcePairIdentificationPhase
{
    ValidatingBin,
    ValidatingCue,
    Completed,
}

public sealed record SourcePairIdentificationProgress(
    SourcePairIdentificationPhase Phase,
    long CompletedBytes,
    long TotalBytes);

/// <summary>
/// The read-only, exact-hash identity of a selected source pair. A null member
/// identity means that member is not an exact source artifact in the manifest.
/// </summary>
public sealed record SourcePairIdentification(
    SourcePairRecognitionKind Kind,
    string? DiscId,
    string? BinDiscId,
    string? CueDiscId,
    FileDigest BinDigest,
    FileDigest CueDigest);

/// <summary>
/// Identifies source pairs without opening an output path or accessing payloads.
/// </summary>
public static class SourcePairIdentifier
{
    public static async Task<SourcePairIdentification> IdentifyAsync(
        ReleaseManifest manifest,
        string sourceBinPath,
        string sourceCuePath,
        IProgress<SourcePairIdentificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string binPath = NormalizePath(sourceBinPath, "clean BIN");
        string cuePath = NormalizePath(sourceCuePath, "clean CUE");
        if (PathsEqual(binPath, cuePath))
        {
            throw new PatcherSafetyException("Clean BIN and CUE paths must be distinct.");
        }

        FileDigest bin = await FileIntegrity.DigestAsync(
            binPath,
            PatchApplyOptions.DefaultIoBufferSize,
            (completed, total) => progress?.Report(new SourcePairIdentificationProgress(
                SourcePairIdentificationPhase.ValidatingBin,
                completed,
                total)),
            cancellationToken).ConfigureAwait(false);
        FileDigest cue = await FileIntegrity.DigestAsync(
            cuePath,
            PatchApplyOptions.DefaultIoBufferSize,
            (completed, total) => progress?.Report(new SourcePairIdentificationProgress(
                SourcePairIdentificationPhase.ValidatingCue,
                completed,
                total)),
            cancellationToken).ConfigureAwait(false);

        string? binDiscId = FindDiscId(manifest, bin, static disc => disc.Source.Bin);
        string? cueDiscId = FindDiscId(manifest, cue, static disc => disc.Source.Cue);
        SourcePairRecognitionKind kind;
        string? discId = null;
        if (binDiscId is not null && string.Equals(binDiscId, cueDiscId, StringComparison.Ordinal))
        {
            kind = SourcePairRecognitionKind.ExactPair;
            discId = binDiscId;
        }
        else if (binDiscId is not null && cueDiscId is not null)
        {
            kind = SourcePairRecognitionKind.MixedDiscPair;
        }
        else
        {
            kind = SourcePairRecognitionKind.Unsupported;
        }

        progress?.Report(new SourcePairIdentificationProgress(
            SourcePairIdentificationPhase.Completed,
            checked(bin.Size + cue.Size),
            checked(bin.Size + cue.Size)));
        return new SourcePairIdentification(kind, discId, binDiscId, cueDiscId, bin, cue);
    }

    private static string? FindDiscId(
        ReleaseManifest manifest,
        FileDigest digest,
        Func<DiscSpec, ArtifactSpec> artifact)
    {
        foreach ((string id, DiscSpec disc) in manifest.Discs)
        {
            ArtifactSpec expected = artifact(disc);
            if (expected.Size == digest.Size
                && string.Equals(expected.Sha256, digest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private static string NormalizePath(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatcherSafetyException($"Invalid {label} path: {path}", exception);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
