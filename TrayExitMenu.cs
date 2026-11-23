using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rtf;

internal sealed class TrayExitMenu : ContextMenuStrip
{
    public TrayExitMenu(Action settings, Action exit)
    {
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Padding = new Padding(5);
        Renderer = new ExitRenderer();

        var configuration = new ToolStripMenuItem("RTF Settings") { AutoSize = false, Name = "Settings" };
        configuration.Click += (_, _) => settings();
        var quit = new ToolStripMenuItem("Exit RTF") { AutoSize = false };
        quit.Click += (_, _) => exit();
        Items.AddRange(new ToolStripItem[] { configuration, quit });
        Opening += (_, _) =>
        {
            Padding = new Padding(LogicalToDeviceUnits(5));
            configuration.Size = quit.Size = LogicalToDeviceUnits(new Size(148, 34));
        };
    }

    private sealed class ExitRenderer : ToolStripRenderer
    {
        private static readonly Color Surface = MineralColor("Graphite");
        private static readonly Color Edge = MineralColor("Edge");
        private static readonly Color Highlight = MineralColor("HoverFill");
        private static readonly Color Text = MineralColor("Ink");
        private static readonly Color Accent = MineralColor("Bone");

        private static Color MineralColor(string key)
        {
            var brush = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource(key);
            var color = brush.Color;
            return Color.FromArgb(color.R, color.G, color.B);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Edge);
            var bounds = e.ToolStrip.ClientRectangle;
            e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var graphics = e.Graphics;
            using var shape = new GraphicsPath();
            using var brush = new SolidBrush(e.Item.Selected ? Highlight : Surface);
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            int corner = Math.Max(1, bounds.Height / 3);
            shape.AddRoundedRectangle(bounds, new Size(corner, corner));
            var oldSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(brush, shape);
            graphics.SmoothingMode = oldSmoothing;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            float unit = bounds.Height / 34f;
            float x = 17 * unit;
            float y = bounds.Height / 2f;
            float diameter = 11 * unit;
            using var pen = new Pen(Accent, 1.35f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var graphics = e.Graphics;
            var oldSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Name == "Settings")
            {
                graphics.DrawEllipse(pen, x - 3.5f * unit, y - 3.5f * unit, 7 * unit, 7 * unit);
                for (int tooth = 0; tooth < 8; tooth++)
                {
                    double angle = tooth * Math.PI / 4;
                    float dx = (float)Math.Cos(angle) * unit;
                    float dy = (float)Math.Sin(angle) * unit;
                    graphics.DrawLine(pen, x + 3.8f * dx, y + 3.8f * dy, x + 5.7f * dx, y + 5.7f * dy);
                }
            }
            else
            {
                graphics.DrawArc(pen, x - diameter / 2, y - diameter / 2, diameter, diameter, -53, 286);
                graphics.DrawLine(pen, x, y - 6.5f * unit, x, y - .75f * unit);
            }
            graphics.SmoothingMode = oldSmoothing;

            int inset = (int)(34 * unit);
            bounds.X += inset;
            bounds.Width -= inset;
            TextRenderer.DrawText(graphics, e.Text, e.TextFont, bounds, Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }
}
