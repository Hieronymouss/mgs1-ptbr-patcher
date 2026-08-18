namespace Mgs1.Patcher.Gui.Logic;

public enum CueDropCandidateKind
{
    Accepted,
    NoFiles,
    MultipleFiles,
    NotCue,
}

public sealed record CueDropCandidate(CueDropCandidateKind Kind, string? CuePath);

/// <summary>
/// Performs only the UI-level shape check for a drag-and-drop operation. An
/// accepted path must still pass the controller's CUE/BIN resolution and exact
/// manifest hash recognition before it can become a ready disc selection.
/// </summary>
public static class CueDropPolicy
{
    public static CueDropCandidate Evaluate(IEnumerable<string>? paths)
    {
        string[] candidates = paths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
        if (candidates.Length == 0)
        {
            return new CueDropCandidate(CueDropCandidateKind.NoFiles, null);
        }

        if (candidates.Length != 1)
        {
            return new CueDropCandidate(CueDropCandidateKind.MultipleFiles, null);
        }

        string cuePath = candidates[0];
        return string.Equals(Path.GetExtension(cuePath), ".cue", StringComparison.OrdinalIgnoreCase)
            ? new CueDropCandidate(CueDropCandidateKind.Accepted, cuePath)
            : new CueDropCandidate(CueDropCandidateKind.NotCue, null);
    }
}

public enum DestinationDropCandidateKind
{
    Accepted,
    NoPaths,
    MultiplePaths,
    NotDirectory,
}

public sealed record DestinationDropCandidate(DestinationDropCandidateKind Kind, string? DirectoryPath);

/// <summary>
/// Accepts one existing directory from Explorer. The patch workflow still
/// performs its ordinary destination safety, collision, and space preflight
/// before any output is created.
/// </summary>
public static class DestinationDropPolicy
{
    public static DestinationDropCandidate Evaluate(IEnumerable<string>? paths)
    {
        string[] candidates = paths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
        if (candidates.Length == 0)
        {
            return new DestinationDropCandidate(DestinationDropCandidateKind.NoPaths, null);
        }

        if (candidates.Length != 1)
        {
            return new DestinationDropCandidate(DestinationDropCandidateKind.MultiplePaths, null);
        }

        string directoryPath = candidates[0];
        return Directory.Exists(directoryPath)
            ? new DestinationDropCandidate(DestinationDropCandidateKind.Accepted, directoryPath)
            : new DestinationDropCandidate(DestinationDropCandidateKind.NotDirectory, null);
    }
}
