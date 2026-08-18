using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mgs1.Patcher.Core;

public static class ManifestLoader
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumDiscs = 16;
    private const int MaximumFileNameLength = 180;
    private const int MaximumTextLength = 4096;
    private const char WindowsSeparator = (char)92;

    private static readonly Regex DiscIdPattern = new(
        "^[a-z0-9][a-z0-9-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "release-candidate",
        "released",
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static async Task<ReleaseManifest> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(manifestPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatcherManifestException($"Invalid release manifest path: {manifestPath}", exception);
        }

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumManifestBytes)
            {
                throw new PatcherManifestException(
                    $"Release manifest must be between 1 and {MaximumManifestBytes} bytes.");
            }

            bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (PatcherManifestException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PatcherManifestException($"Cannot read release manifest {fullPath}: {exception.Message}", exception);
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            return Parse(fullPath, document.RootElement);
        }
        catch (PatcherManifestException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PatcherManifestException(
                $"Cannot parse release manifest {fullPath}: {exception.Message}",
                exception);
        }
    }

    private static ReleaseManifest Parse(string path, JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "manifest");
        RejectUnknown(root, "manifest", "$schema", "schema_version", "release_id", "status", "patch_format", "discs");
        if (root.TryGetProperty("$schema", out JsonElement schema)
            && (schema.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(schema.GetString())
                || schema.GetString()!.Length > MaximumTextLength))
        {
            throw new PatcherManifestException("$schema must be a non-empty bounded string when present.");
        }

        int schemaVersion = RequireInt32(root, "schema_version", "schema_version");
        if (schemaVersion != 1)
        {
            throw new PatcherManifestException("schema_version must be 1.");
        }

        string releaseId = RequireString(root, "release_id", "release_id", MaximumTextLength);
        string status = RequireString(root, "status", "status", 64);
        if (!AllowedStatuses.Contains(status))
        {
            throw new PatcherManifestException("status is not an allowed release state.");
        }

        JsonElement patchFormat = RequireProperty(root, "patch_format", "patch_format");
        RequireKind(patchFormat, JsonValueKind.Object, "patch_format");
        RejectUnknown(patchFormat, "patch_format", "id", "profile", "specification", "implementation_license");
        if (RequireString(patchFormat, "id", "patch_format.id", 32) != "BPS1")
        {
            throw new PatcherManifestException("patch_format.id must be BPS1.");
        }

        if (RequireString(patchFormat, "profile", "patch_format.profile", 64) != "linear-streaming-v1")
        {
            throw new PatcherManifestException("patch_format.profile must be linear-streaming-v1.");
        }

        string specification = RequireString(
            patchFormat,
            "specification",
            "patch_format.specification",
            MaximumTextLength);
        if (!Uri.TryCreate(specification, UriKind.Absolute, out _))
        {
            throw new PatcherManifestException("patch_format.specification must be an absolute URI.");
        }

        if (RequireString(
                patchFormat,
                "implementation_license",
                "patch_format.implementation_license",
                64) != "MIT")
        {
            throw new PatcherManifestException("patch_format.implementation_license must be MIT.");
        }

        JsonElement rawDiscs = RequireProperty(root, "discs", "discs");
        RequireKind(rawDiscs, JsonValueKind.Array, "discs");
        int discCount = rawDiscs.GetArrayLength();
        if (discCount is < 1 or > MaximumDiscs)
        {
            throw new PatcherManifestException($"discs must contain between 1 and {MaximumDiscs} entries.");
        }

        var discs = new Dictionary<string, DiscSpec>(discCount, StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement rawDisc in rawDiscs.EnumerateArray())
        {
            string field = $"discs[{index}]";
            DiscSpec disc = ParseDisc(rawDisc, field);
            if (!discs.TryAdd(disc.Id, disc))
            {
                throw new PatcherManifestException($"Duplicate disc id: {disc.Id}");
            }

            index++;
        }

        return new ReleaseManifest(path, schemaVersion, releaseId, status, discs);
    }

    private static DiscSpec ParseDisc(JsonElement rawDisc, string field)
    {
        RequireKind(rawDisc, JsonValueKind.Object, field);
        RejectUnknown(
            rawDisc,
            field,
            "id",
            "display_name",
            "authority_date",
            "acceptance_scope",
            "source",
            "target",
            "patches");

        string id = RequireString(rawDisc, "id", $"{field}.id", 128);
        if (!DiscIdPattern.IsMatch(id))
        {
            throw new PatcherManifestException($"{field}.id is not a stable lowercase identifier.");
        }

        string displayName = RequireString(rawDisc, "display_name", $"{field}.display_name", MaximumTextLength);
        string authorityDate = RequireString(rawDisc, "authority_date", $"{field}.authority_date", 10);
        if (!DateOnly.TryParseExact(
                authorityDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new PatcherManifestException($"{field}.authority_date must use YYYY-MM-DD.");
        }

        string acceptanceScope = RequireString(
            rawDisc,
            "acceptance_scope",
            $"{field}.acceptance_scope",
            MaximumTextLength);
        ArtifactPair source = ParseArtifactPair(
            RequireProperty(rawDisc, "source", $"{field}.source"),
            $"{field}.source");
        ArtifactPair target = ParseArtifactPair(
            RequireProperty(rawDisc, "target", $"{field}.target"),
            $"{field}.target");
        PatchPair patches = ParsePatchPair(
            RequireProperty(rawDisc, "patches", $"{field}.patches"),
            $"{field}.patches");

        string[] artifactNames = [
            source.Bin.FileName,
            source.Cue.FileName,
            target.Bin.FileName,
            target.Cue.FileName,
        ];
        if (artifactNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifactNames.Length)
        {
            throw new PatcherManifestException($"{field} clean and output file names must be distinct.");
        }

        if (string.Equals(patches.Bin.File, patches.Cue.File, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherManifestException($"{field} BIN and CUE patch paths must be distinct.");
        }

        return new DiscSpec(id, displayName, authorityDate, acceptanceScope, source, target, patches);
    }

    private static ArtifactPair ParseArtifactPair(JsonElement element, string field)
    {
        RequireKind(element, JsonValueKind.Object, field);
        RejectUnknown(element, field, "bin", "cue");
        return new ArtifactPair(
            ParseArtifact(RequireProperty(element, "bin", $"{field}.bin"), $"{field}.bin"),
            ParseArtifact(RequireProperty(element, "cue", $"{field}.cue"), $"{field}.cue"));
    }

    private static PatchPair ParsePatchPair(JsonElement element, string field)
    {
        RequireKind(element, JsonValueKind.Object, field);
        RejectUnknown(element, field, "bin", "cue");
        return new PatchPair(
            ParsePatch(RequireProperty(element, "bin", $"{field}.bin"), $"{field}.bin"),
            ParsePatch(RequireProperty(element, "cue", $"{field}.cue"), $"{field}.cue"));
    }

    private static ArtifactSpec ParseArtifact(JsonElement element, string field)
    {
        RequireKind(element, JsonValueKind.Object, field);
        RejectUnknown(element, field, "file_name", "size", "sha256");
        return new ArtifactSpec(
            RequireSafeFileName(element, "file_name", $"{field}.file_name"),
            RequireSize(element, "size", $"{field}.size"),
            RequireSha256(element, "sha256", $"{field}.sha256"));
    }

    private static PatchSpec ParsePatch(JsonElement element, string field)
    {
        RequireKind(element, JsonValueKind.Object, field);
        RejectUnknown(element, field, "file", "size", "sha256");
        string relative = RequireString(element, "file", $"{field}.file", MaximumTextLength);
        ValidateSafeRelativePath(relative, $"{field}.file");
        return new PatchSpec(
            relative,
            RequireSize(element, "size", $"{field}.size"),
            RequireSha256(element, "sha256", $"{field}.sha256"));
    }

    private static string RequireSafeFileName(JsonElement element, string property, string field)
    {
        string value = RequireString(element, property, field, MaximumFileNameLength);
        ValidateSafeFileName(value, field);
        return value;
    }

    private static void ValidateSafeRelativePath(string value, string field)
    {
        if (value.Contains(WindowsSeparator))
        {
            throw new PatcherManifestException($"{field} must use forward slashes.");
        }

        string[] parts = value.Split('/', StringSplitOptions.None);
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new PatcherManifestException($"{field} must be a safe relative path.");
        }

        foreach (string part in parts)
        {
            ValidateSafeFileName(part, field);
        }
    }

    private static void ValidateSafeFileName(string value, string field)
    {
        if (value.Length > MaximumFileNameLength
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/')
            || value.Contains(WindowsSeparator)
            || value.EndsWith(' ')
            || value.EndsWith('.'))
        {
            throw new PatcherManifestException($"{field} must be a Windows-safe plain file name.");
        }

        string stem = value.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(stem))
        {
            throw new PatcherManifestException($"{field} uses a reserved Windows file name.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string property, string field)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            throw new PatcherManifestException($"{field} is required.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement element,
        string property,
        string field,
        int maximumLength)
    {
        JsonElement value = RequireProperty(element, property, field);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PatcherManifestException($"{field} must be a string.");
        }

        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumLength)
        {
            throw new PatcherManifestException(
                $"{field} must be a non-empty string no longer than {maximumLength} characters.");
        }

        return result;
    }

    private static string RequireSha256(JsonElement element, string property, string field)
    {
        string value = RequireString(element, property, field, 64);
        if (!Sha256Pattern.IsMatch(value))
        {
            throw new PatcherManifestException($"{field} must be exactly 64 hexadecimal characters.");
        }

        return value.ToLowerInvariant();
    }

    private static long RequireSize(JsonElement element, string property, string field)
    {
        JsonElement value = RequireProperty(element, property, field);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < 0)
        {
            throw new PatcherManifestException($"{field} must be a non-negative 64-bit integer.");
        }

        return result;
    }

    private static int RequireInt32(JsonElement element, string property, string field)
    {
        JsonElement value = RequireProperty(element, property, field);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw new PatcherManifestException($"{field} must be an integer.");
        }

        return result;
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string field)
    {
        if (element.ValueKind != expected)
        {
            throw new PatcherManifestException($"{field} must be {expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static void RejectUnknown(JsonElement element, string field, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw new PatcherManifestException($"{field} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new PatcherManifestException("Release manifest was truncated while being read.");
            }

            offset += read;
        }
    }
}
