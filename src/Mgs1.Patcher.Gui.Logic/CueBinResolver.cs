using System.Text;
using System.Text.RegularExpressions;

namespace Mgs1.Patcher.Gui.Logic;

/// <summary>
/// Resolves the one referenced BIN in a single-file CUE sheet without allowing
/// a CUE to redirect selection outside its own directory.
/// </summary>
public static partial class CueBinResolver
{
    private const int MaximumCueBytes = 1024 * 1024;

    public static async Task<string> ResolveAsync(string cuePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);
        string fullCuePath;
        try
        {
            fullCuePath = Path.GetFullPath(cuePath);
            var cue = new FileInfo(fullCuePath);
            if (!cue.Exists || (cue.Attributes & FileAttributes.Directory) != 0)
            {
                throw new CueResolutionException("The selected CUE is not a readable file.");
            }

            if (cue.Length is <= 0 or > MaximumCueBytes)
            {
                throw new CueResolutionException("The selected CUE has an unsupported size.");
            }
        }
        catch (CueResolutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CueResolutionException("The selected CUE cannot be inspected.", exception);
        }

        string text;
        try
        {
            await using var stream = new FileStream(
                fullCuePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new CueResolutionException("The selected CUE cannot be read as a text CUE sheet.", exception);
        }

        MatchCollection references = FileDirectiveRegex().Matches(text);
        if (references.Count != 1)
        {
            throw new CueResolutionException("The CUE must reference exactly one companion BIN.");
        }

        string name = references[0].Groups["name"].Value;
        if (!IsSafeCompanionName(name))
        {
            throw new CueResolutionException("The CUE references an unsafe companion BIN name.");
        }

        string directory = Path.GetDirectoryName(fullCuePath)
            ?? throw new CueResolutionException("The selected CUE has no usable directory.");
        string binPath = Path.Combine(directory, name);
        if (!File.Exists(binPath))
        {
            throw new CueResolutionException("The companion BIN referenced by the CUE was not found beside it.");
        }

        return binPath;
    }

    private static bool IsSafeCompanionName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
        && value is not "." and not ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !Path.IsPathRooted(value);

    [GeneratedRegex("^\\s*FILE\\s+(?:\\\"(?<name>[^\\\"]+)\\\"|(?<name>\\S+))\\s+\\S+\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex FileDirectiveRegex();
}

public sealed class CueResolutionException : Exception
{
    public CueResolutionException(string message) : base(message) { }

    public CueResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
