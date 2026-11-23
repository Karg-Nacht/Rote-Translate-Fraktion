using System.Windows;
using System.Windows.Media;

namespace Rtf;

internal static class MarkdownTheme
{
    internal static readonly Brush Ink = Brush("Ink");
    internal static readonly Brush Heading = Ink;
    internal static readonly Brush Muted = Brush("SecondaryInk");
    internal static readonly Brush Accent = Brush("Bone");
    internal static readonly Brush Edge = Brush("Edge");
    internal static readonly Brush Code = Brush("Graphite");
    internal static readonly Brush CodeBlock = RoundedCodeSurface();
    internal static readonly Brush TableHeader = Brush("Mineral");
    internal static readonly FontFamily Monospace = (FontFamily)Application.Current.FindResource("CodeFont");

    private static Brush RoundedCodeSurface()
    {
        var outline = new RectangleGeometry(new Rect(0, 0, 100, 100), 2, 2);
        var brush = new DrawingBrush(new GeometryDrawing(Code, null, outline));
        brush.Freeze();
        return brush;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
