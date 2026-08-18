using Mgs1.Patcher.Core;

namespace Mgs1.Patcher.Gui.Logic;

public sealed record OutputPairNames(string BinFileName, string CueFileName);

/// <summary>
/// Derives recognizable output names from the CUE selected by the user while
/// keeping the generated pair inside the chosen destination directory.
/// </summary>
public static class OutputNamePolicy
{
    private const string TranslationSuffix = " (PT-BR)";
    private const int MaximumOutputStemLength = 240;

    public static OutputPairNames FromCuePath(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);
        string sourceStem;
        try
        {
            sourceStem = Path.GetFileNameWithoutExtension(Path.GetFileName(cuePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new PatcherSafetyException("The selected CUE has an unsafe output name.", exception);
        }

        string sanitized = SanitizeStem(sourceStem);
        string baseStem = sanitized.EndsWith(TranslationSuffix, StringComparison.OrdinalIgnoreCase)
            ? sanitized[..^TranslationSuffix.Length]
            : sanitized;
        int maximumBaseLength = MaximumOutputStemLength - TranslationSuffix.Length;
        if (baseStem.Length > maximumBaseLength)
        {
            baseStem = baseStem[..maximumBaseLength];
        }

        baseStem = baseStem.Trim().TrimEnd('.');
        if (baseStem.Length == 0)
        {
            throw new PatcherSafetyException("The selected CUE has no usable output name.");
        }

        string outputStem = baseStem + TranslationSuffix;
        return new OutputPairNames(outputStem + ".bin", outputStem + ".cue");
    }

    public static bool Collides(OutputPairNames first, OutputPairNames second) =>
        string.Equals(first.BinFileName, second.BinFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(first.CueFileName, second.CueFileName, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeStem(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] sanitized = value
            .Select(character => character == '\0' || char.IsControl(character) || invalid.Contains(character)
                ? '_'
                : character)
            .ToArray();
        return new string(sanitized).Trim().TrimEnd('.');
    }
}
