using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Rtf;

public sealed class MarkdownTextView : RichTextBox
{
    private static readonly DependencyPropertyKey RawTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(RawText), typeof(string), typeof(MarkdownTextView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RawTextProperty = RawTextPropertyKey.DependencyProperty;
    public string RawText => (string)GetValue(RawTextProperty);

    private CancellationTokenSource? pending;
    private bool conservative;

    internal void SetContent(string text, bool conservative = false)
    {
        if (RawText == text && this.conservative == conservative)
            return;

        pending?.Cancel();
        pending = null;
        this.conservative = conservative;
        SetValue(RawTextPropertyKey, text);
        Document = MarkdownDocumentRenderer.Plain(string.Empty);
        ResetScroll();
        if (text.Length > 0)
        {
            var request = new CancellationTokenSource();
            pending = request;
            _ = ShowContentAsync(text, conservative, request);
        }
    }

    private async Task ShowContentAsync(string text, bool conservative, CancellationTokenSource request)
    {
        try
        {
            var (markdown, plainText) = await Task.Run(() => MarkdownService.Prepare(text, conservative), request.Token);
            request.Token.ThrowIfCancellationRequested();
            var rendered = markdown is null ? MarkdownDocumentRenderer.Plain(plainText)
                : await new MarkdownDocumentRenderer(text).RenderAsync(markdown, !conservative, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Document = rendered;
            ResetScroll();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            if (!request.IsCancellationRequested)
                Document = MarkdownDocumentRenderer.Plain(text);
        }
        finally
        {
            if (pending == request)
                pending = null;
            request.Dispose();
        }
    }

    private void ResetScroll()
    {
        if (Template?.FindName("PART_ContentHost", this) is PanelTextScrollViewer scroll)
            scroll.ResetScroll();
    }
}
