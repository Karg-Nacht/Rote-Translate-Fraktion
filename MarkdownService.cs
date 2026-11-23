using System;
using System.Linq;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Rtf;

internal static class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    internal static (MarkdownDocument? Markdown, string PlainText) Prepare(string text, bool conservative)
    {
        // Keep pathological input selectable as plain text.
        if (text.Length > 100_000)
            return (null, text);

        if (text.AsSpan().IndexOfAny("#>*_~\u0060[+|-<.\\=)") >= 0)
        {
            var document = Markdown.Parse(text, Pipeline);
            if (document.Descendants().Skip(4000).Any())
                return (null, text);

            if (document.Descendants().Any(node => IsFormatting(node, text, conservative)))
                return (document, text);
        }

        return (null, conservative ? text : ProseLayout.AddParagraphBreaks(text));
    }

    private static bool IsFormatting(MarkdownObject node, string text, bool conservative) => node switch
    {
        HeadingBlock or FencedCodeBlock or QuoteBlock or ThematicBreakBlock or Table => true,
        ListBlock list => !conservative || list.Count > 1,
        CodeBlock => !conservative,
        LinkInline or AutolinkInline or CodeInline => true,
        LineBreakInline line => !conservative && line.IsHard,
        EmphasisInline emphasis => !conservative || emphasis.DelimiterCount > 1 ||
            text.AsSpan(emphasis.Span.Start, emphasis.Span.Length).Contains(' '),
        _ => false
    };
}
