using System.Collections.ObjectModel;

namespace Mgs1.Patcher.Core;

public sealed record ArtifactSpec(string FileName, long Size, string Sha256);

public sealed record PatchSpec(string File, long Size, string Sha256);

public sealed record ArtifactPair(ArtifactSpec Bin, ArtifactSpec Cue)
{
    internal ArtifactSpec Get(FileKind kind) => kind == FileKind.Bin ? Bin : Cue;
}

public sealed record PatchPair(PatchSpec Bin, PatchSpec Cue)
{
    internal PatchSpec Get(FileKind kind) => kind == FileKind.Bin ? Bin : Cue;
}

public sealed record DiscSpec(
    string Id,
    string DisplayName,
    string AuthorityDate,
    string AcceptanceScope,
    ArtifactPair Source,
    ArtifactPair Target,
    PatchPair Patches);

public sealed class ReleaseManifest
{
    internal ReleaseManifest(
        string manifestPath,
        int schemaVersion,
        string releaseId,
        string status,
        IReadOnlyDictionary<string, DiscSpec> discs)
    {
        ManifestPath = manifestPath;
        SchemaVersion = schemaVersion;
        ReleaseId = releaseId;
        Status = status;
        Discs = new ReadOnlyDictionary<string, DiscSpec>(
            new Dictionary<string, DiscSpec>(discs, StringComparer.Ordinal));
    }

    public string ManifestPath { get; }

    public int SchemaVersion { get; }

    public string ReleaseId { get; }

    public string Status { get; }

    public IReadOnlyDictionary<string, DiscSpec> Discs { get; }
}

internal enum FileKind
{
    Bin,
    Cue,
}

internal static class FileKindExtensions
{
    internal static readonly FileKind[] All = [FileKind.Bin, FileKind.Cue];

    internal static string Label(this FileKind kind) => kind == FileKind.Bin ? "BIN" : "CUE";

    internal static string JsonName(this FileKind kind) => kind == FileKind.Bin ? "bin" : "cue";
}
