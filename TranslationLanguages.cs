using System;
using System.Text.RegularExpressions;

namespace Rtf;

internal static partial class TranslationLanguages
{
    internal sealed record Choice(string Code, string Label);

    internal static readonly Choice[] All =
    [
        new("", "Automatic"), new("ru", "Russian"), new("en", "English"), new("de", "German"),
        new("fr", "French"), new("es", "Spanish"), new("it", "Italian"), new("pt", "Portuguese"),
        new("uk", "Ukrainian"), new("pl", "Polish"), new("tr", "Turkish"), new("zh", "Chinese"),
        new("ja", "Japanese"), new("ko", "Korean"), new("ar", "Arabic"), new("hi", "Hindi"),
        new("nl", "Dutch"), new("sv", "Swedish"), new("vi", "Vietnamese"), new("he", "Hebrew"),
        new("el", "Greek"), new("cs", "Czech"), new("fa", "Persian")
    ];

    internal static string Label(string code) =>
        Array.Find(All, item => item.Code == code)?.Label ?? code.ToUpperInvariant();

    internal static string TargetFor(string? source, string fallback) => source?.Split('-')[0].ToLowerInvariant() switch
    {
        "ru" => "en",
        "en" => "ru",
        _ => fallback
    };

    // A local suggestion for the UI. The provider supplies the actual detection.
    internal static string? PredictSource(string text)
    {
        int cyrillic = 0, letters = 0;
        bool ukrainian = false;
        foreach (char c in text.AsSpan(0, Math.Min(text.Length, 1200)))
        {
            if (char.IsLetter(c))
                letters++;
            if (c is >= '\u0400' and <= '\u052f')
            {
                cyrillic++;
                ukrainian |= "іїєґІЇЄҐ".Contains(c);
            }
        }
        if (letters < 6)
            return null;
        if (cyrillic > letters / 2)
            return ukrainian ? "uk" : "ru";
        return EnglishWords().Matches(text[..Math.Min(text.Length, 1200)]).Count >= 2 ? "en" : null;
    }

    [GeneratedRegex(@"\b(?:the|this|that|with|from|and|for|you|are|was|have|will|not|can|should|is|to|in)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWords();
}
