using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace Rtf;

internal sealed class MarkdownDocumentRenderer(string raw)
{
    internal static FlowDocument Plain(string text)
    {
        var document = CreateDocument();
        if (text.Length != 0)
            document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0), LineHeight = 22 });
        return document;
    }

    private static FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        Foreground = MarkdownTheme.Ink,
        IsHyphenationEnabled = false
    };

    internal async Task<FlowDocument> RenderAsync(MarkdownDocument markdown, bool formatProse, CancellationToken cancellation)
    {
        var document = CreateDocument();
        var batch = Stopwatch.StartNew();
        foreach (var block in markdown)
        {
            cancellation.ThrowIfCancellationRequested();
            // Only split top-level prose with no inline structure to preserve.
            var rendered = RenderBlock(block);
            if (formatProse && block is ParagraphBlock && rendered is Paragraph paragraph &&
                paragraph.Inlines.FirstInline is Run run && run.NextInline is null)
                run.Text = ProseLayout.AddParagraphBreaks(run.Text);
            document.Blocks.Add(rendered);
            if (batch.ElapsedMilliseconds >= 8)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                batch.Restart();
            }
        }
        return document;
    }

    private Block RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return Paragraph(heading.Inline, 5, 7, heading.Level switch { 1 => 18, 2 => 16, 3 => 15, _ => 14 },
                    emphasized: true);
            case ParagraphBlock paragraph:
                return Paragraph(paragraph.Inline);
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = MarkdownTheme.Monospace, FontSize = 12,
                    Background = MarkdownTheme.CodeBlock, Padding = new Thickness(7, 5, 7, 5),
                    Margin = new Thickness(0, 1, 0, 8), LineHeight = 18
                };
            case QuoteBlock quote:
                var section = new Section
                {
                    Foreground = MarkdownTheme.Muted, BorderBrush = MarkdownTheme.Edge,
                    BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0),
                    Margin = new Thickness(0, 2, 0, 6)
                };
                AppendBlocks(section.Blocks, quote);
                return section;
            case ListBlock list:
                return RenderList(list);
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = MarkdownTheme.Edge, BorderThickness = new Thickness(0, 1, 0, 0),
                    FontSize = 1, LineHeight = 1, Margin = new Thickness(0, 6, 0, 8)
                };
            case MdTable table:
                return RenderTable(table);
            default:
                return new Paragraph(new Run(Source(block))) { Margin = new Thickness(0, 0, 0, 8) };
        }
    }

    private List RenderList(ListBlock source)
    {
        var list = new List
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            MarkerOffset = 3, Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(17, 0, 0, 0)
        };
        if (source.IsOrdered && int.TryParse(source.OrderedStart, out int start) && start > 0)
            list.StartIndex = start;
        foreach (var item in source.OfType<ListItemBlock>())
        {
            var entry = new ListItem { Margin = new Thickness(0) };
            AppendBlocks(entry.Blocks, item, compact: !source.IsLoose);
            list.ListItems.Add(entry);
        }
        return list;
    }

    private void AppendBlocks(BlockCollection target, ContainerBlock blocks, bool compact = false)
    {
        foreach (var block in blocks)
        {
            var rendered = RenderBlock(block);
            if (compact && rendered is Paragraph && block is ParagraphBlock)
                rendered.Margin = new Thickness(0, 0, 0, 2);
            target.Add(rendered);
        }
    }

    private Table RenderTable(MdTable source)
    {
        var rows = source.OfType<MdRow>().ToArray();
        int columns = rows.Max(row => row.Count);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 3, 0, 9), FontSize = 12 };
        // The panel columns are deliberately narrow. Wide tables read as labeled rows.
        bool stacked = columns > 4 && rows.Length > 1 && rows[0].IsHeader;
        for (int i = 0; i < (stacked ? 2 : columns); i++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        if (stacked)
        {
            foreach (var sourceRow in rows.Skip(1))
            {
                var group = new TableRowGroup();
                var headers = rows[0].OfType<MdCell>().ToArray();
                var values = sourceRow.OfType<MdCell>().ToArray();
                for (int i = 0; i < columns; i++)
                {
                    var row = new TableRow();
                    row.Cells.Add(Cell(i < headers.Length ? headers[i] : null, header: true));
                    row.Cells.Add(Cell(i < values.Length ? values[i] : null, header: false));
                    group.Rows.Add(row);
                }
                group.Rows[^1].Cells[0].BorderThickness = new Thickness(0, 0, 1, 2);
                group.Rows[^1].Cells[1].BorderThickness = new Thickness(0, 0, 1, 2);
                table.RowGroups.Add(group);
            }
        }
        else
        {
            var group = new TableRowGroup();
            foreach (var sourceRow in rows)
            {
                var row = new TableRow();
                foreach (var cell in sourceRow.OfType<MdCell>())
                    row.Cells.Add(Cell(cell, sourceRow.IsHeader));
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
        }
        return table;
    }

    private TableCell Cell(MdCell? content, bool header)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(5, 3, 5, 3), BorderBrush = MarkdownTheme.Edge,
            BorderThickness = new Thickness(0, 0, 1, 1)
        };
        if (header)
        {
            cell.Background = MarkdownTheme.TableHeader;
            cell.FontWeight = FontWeights.SemiBold;
        }
        if (content is not null)
        {
            AppendBlocks(cell.Blocks, content, compact: true);
            foreach (Block block in cell.Blocks)
                if (block is Paragraph paragraph)
                    paragraph.LineHeight = 16;
        }
        return cell;
    }

    private Paragraph Paragraph(ContainerInline? inlines, int top = 0, int bottom = 8,
        int? fontSize = null, bool emphasized = false)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, top, 0, bottom), LineHeight = 22 };
        if (fontSize is int size)
            paragraph.FontSize = size;
        if (emphasized)
        {
            paragraph.Foreground = MarkdownTheme.Heading;
            paragraph.FontWeight = FontWeights.SemiBold;
        }
        AppendInlines(paragraph.Inlines, inlines);
        return paragraph;
    }

    private void AppendInlines(InlineCollection target, ContainerInline? inlines)
    {
        for (var node = inlines?.FirstChild; node is not null; node = node.NextSibling)
        {
            System.Windows.Documents.Inline inline;
            switch (node)
            {
                case LiteralInline literal:
                    inline = new Run(literal.Content.ToString());
                    break;
                case HtmlEntityInline entity:
                    inline = new Run(entity.Transcoded.ToString());
                    break;
                case LineBreakInline:
                    inline = new LineBreak();
                    break;
                case CodeInline code:
                    inline = new Run(code.Content) { FontFamily = MarkdownTheme.Monospace, Background = MarkdownTheme.Code };
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~')
                        span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount == 2)
                        span.FontWeight = FontWeights.SemiBold;
                    else
                        span.FontStyle = FontStyles.Italic;
                    AppendInlines(span.Inlines, emphasis);
                    inline = span;
                    break;
                case LinkInline link:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
                    var label = link.IsImage ? new Span() : LinkLabel(url);
                    AppendInlines(label.Inlines, link);
                    if (label.Inlines.Count == 0 && !link.IsImage)
                        label.Inlines.Add(new Run(url ?? ""));
                    inline = label;
                    break;
                case AutolinkInline auto:
                    var autoLabel = LinkLabel(auto.IsEmail ? null : auto.Url);
                    autoLabel.Inlines.Add(new Run(auto.Url));
                    inline = autoLabel;
                    break;
                default:
                    inline = new Run(Source(node));
                    break;
            }
            target.Add(inline);
        }
    }

    private static Span LinkLabel(string? url)
    {
        if (url is null || url.Any(char.IsControl) || url.Contains('\\') ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            return new Span();

        var link = new Hyperlink
        {
            NavigateUri = uri, Foreground = MarkdownTheme.Accent, ToolTip = uri.AbsoluteUri
        };
        link.RequestNavigate += OpenLink;
        return link;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    private string Source(MarkdownObject item) =>
        item.Span.Start >= 0 && item.Span.End >= item.Span.Start && item.Span.End < raw.Length
            ? raw.Substring(item.Span.Start, item.Span.Length) : "";
}
