using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Rtf;

internal static partial class ProseLayout
{
    private const int DenseLineLength = 240;
    private const int PreferredBreakLength = 145;
    private const int MinimumTailLength = 75;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dr", "Mr", "Mrs", "Ms", "Prof", "St", "Sr", "Jr", "vs", "etc", "Fig", "No",
        "гг", "ул", "им", "стр", "рис", "проф", "см", "напр", "руб", "тыс", "млн", "млрд"
    };

    [GeneratedRegex(@"(?<=[.!?…])[\p{Zs}\t]+(?=[\p{Lu}\p{Lt}])|(?<=[。！？])(?=\p{Lo})")]
    private static partial Regex SentenceGaps();

    internal static string AddParagraphBreaks(string text)
    {
        StringBuilder? display = null;
        int copiedThrough = 0;
        var lines = text.AsSpan().SplitAny("\r\n");
        foreach (var range in lines)
        {
            var (start, length) = range.GetOffsetAndLength(text.Length);
            var line = text.AsSpan(start, length);
            if (length < DenseLineLength || char.IsWhiteSpace(line[0]))
                continue;

            int groupStart = 0;
            foreach (var gap in SentenceGaps().EnumerateMatches(line))
            {
                if (gap.Index - groupStart < PreferredBreakLength ||
                    length - gap.Index - gap.Length < MinimumTailLength || IsAbbreviation(line, gap.Index))
                    continue;

                int position = start + gap.Index;
                display ??= new StringBuilder(text.Length + 16);
                display.Append(text.AsSpan(copiedThrough, position - copiedThrough)).Append("\n\n");
                copiedThrough = position + gap.Length;
                groupStart = gap.Index + gap.Length;
            }
        }

        return display is null ? text : display.Append(text.AsSpan(copiedThrough)).ToString();
    }

    private static bool IsAbbreviation(ReadOnlySpan<char> line, int gap)
    {
        if (line[gap - 1] != '.')
            return false;

        int end = gap - 1;
        int start = end;
        while (start > 0 && (char.IsLetter(line[start - 1]) || line[start - 1] == '.'))
            start--;

        var word = line[start..end];
        // Skip initials, dotted abbreviations and sentences whose final period may belong to a number.
        return word.Length <= 1 || word.Contains('.') || Abbreviations.Contains(word.ToString());
    }
}
