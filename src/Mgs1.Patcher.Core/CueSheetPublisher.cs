using System.Text;

namespace Mgs1.Patcher.Core;

internal static class CueSheetPublisher
{
    private const int MaximumCueBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Preamble = Encoding.UTF8.GetPreamble();

    internal static async Task RewriteCompanionNameAsync(
        string cuePath,
        string expectedBinFileName,
        string publishedBinFileName,
        CancellationToken cancellationToken)
    {
        if (string.Equals(expectedBinFileName, publishedBinFileName, StringComparison.Ordinal))
        {
            return;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(cuePath);
            if (!info.Exists || info.Length is <= 0 or > MaximumCueBytes)
            {
                throw new PatcherIntegrityException("The verified target CUE has an unsupported size.");
            }

            bytes = await File.ReadAllBytesAsync(cuePath, cancellationToken).ConfigureAwait(false);
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
            throw new PatcherIntegrityException("The verified target CUE cannot be read for publication.", exception);
        }

        bool hasPreamble = bytes.AsSpan().StartsWith(Utf8Preamble);
        int textOffset = hasPreamble ? Utf8Preamble.Length : 0;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, textOffset, bytes.Length - textOffset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PatcherIntegrityException("The verified target CUE is not valid UTF-8 text.", exception);
        }

        List<FileDirective> directives = FindFileDirectives(text);
        if (directives.Count != 1)
        {
            throw new PatcherIntegrityException("The verified target CUE must contain exactly one FILE directive.");
        }

        FileDirective directive = directives[0];
        if (!string.Equals(directive.Name, expectedBinFileName, StringComparison.Ordinal))
        {
            throw new PatcherIntegrityException("The verified target CUE does not reference the manifest target BIN.");
        }

        string rewritten = text[..directive.TokenStart]
            + '"' + publishedBinFileName + '"'
            + text[directive.TokenEnd..];
        byte[] body = StrictUtf8.GetBytes(rewritten);
        byte[] published = new byte[(hasPreamble ? Utf8Preamble.Length : 0) + body.Length];
        if (hasPreamble)
        {
            Utf8Preamble.CopyTo(published, 0);
        }

        body.CopyTo(published, hasPreamble ? Utf8Preamble.Length : 0);
        try
        {
            await File.WriteAllBytesAsync(cuePath, published, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PatcherIntegrityException("The verified target CUE could not be prepared for publication.", exception);
        }
    }

    private static List<FileDirective> FindFileDirectives(string text)
    {
        var directives = new List<FileDirective>();
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline >= 0 ? newline : text.Length;
            int contentEnd = lineEnd > lineStart && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            FileDirective? directive = ParseLine(text, lineStart, contentEnd);
            if (directive is not null)
            {
                directives.Add(directive);
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return directives;
    }

    private static FileDirective? ParseLine(string text, int lineStart, int lineEnd)
    {
        int index = SkipHorizontalWhitespace(text, lineStart, lineEnd);
        const string keyword = "FILE";
        if (index + keyword.Length > lineEnd
            || !text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        index += keyword.Length;
        if (index >= lineEnd || !IsHorizontalWhitespace(text[index]))
        {
            return null;
        }

        index = SkipHorizontalWhitespace(text, index, lineEnd);
        int tokenStart = index;
        string name;
        if (index < lineEnd && text[index] == '"')
        {
            int closingQuote = text.IndexOf('"', index + 1, lineEnd - index - 1);
            if (closingQuote < 0)
            {
                return null;
            }

            name = text[(index + 1)..closingQuote];
            index = closingQuote + 1;
        }
        else
        {
            int nameStart = index;
            while (index < lineEnd && !IsHorizontalWhitespace(text[index]))
            {
                index++;
            }

            name = text[nameStart..index];
        }

        int tokenEnd = index;
        if (name.Length == 0 || index >= lineEnd || !IsHorizontalWhitespace(text[index]))
        {
            return null;
        }

        index = SkipHorizontalWhitespace(text, index, lineEnd);
        int typeStart = index;
        while (index < lineEnd && !IsHorizontalWhitespace(text[index]))
        {
            index++;
        }

        return index == typeStart ? null : new FileDirective(tokenStart, tokenEnd, name);
    }

    private static int SkipHorizontalWhitespace(string text, int index, int end)
    {
        while (index < end && IsHorizontalWhitespace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsHorizontalWhitespace(char value) => value is ' ' or '\t';

    private sealed record FileDirective(int TokenStart, int TokenEnd, string Name);
}
