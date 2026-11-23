using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Rtf;

public class OverlayWindow : Window
{
    private int animationVersion;
    protected nint Handle => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = Handle;
        const int extendedStyle = -20;
        const int noActivate = 0x08000000;
        const int toolWindow = 0x00000080;
        Native.SetWindowLong(hwnd, extendedStyle, Native.GetWindowLong(hwnd, extendedStyle) | noActivate | toolWindow);
        HwndSource.FromHwnd(hwnd).AddHook(PreventMouseActivation);
    }

    private static nint PreventMouseActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) // WM_MOUSEACTIVATE
            return 0;

        handled = true;
        return 3; // MA_NOACTIVATE, deliver the click
    }

    internal void Place(Rect? selection, Native.Point fallback)
    {
        var point = selection is { } rect
            ? new Native.Point((int)rect.Right, (int)rect.Bottom)
            : fallback;

        var (work, scale) = PlacementArea(point);
        int gap = (int)Math.Ceiling(8 * scale);
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Height * scale);

        int x = Math.Clamp(point.X + gap, work.Left, Math.Max(work.Left, work.Right - width));
        int y = point.Y + gap;
        if (y + height > work.Bottom)
            y = (int)(selection?.Top ?? point.Y) - height - gap;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        MoveWithoutActivation(x, y);
    }

    private protected void MoveWithoutActivation(int x, int y) =>
        Native.SetWindowPos(Handle, 0, x, y, 0, 0, Native.MoveWithoutActivation);

    internal static (System.Drawing.Rectangle Work, double Scale) PlacementArea(Native.Point point)
    {
        var work = Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        nint monitor = Native.MonitorFromPoint(point, 2);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1d;
        return (work, scale);
    }

    internal void Reveal()
    {
        animationVersion++;
        var content = (FrameworkElement)Content;
        double from = IsVisible ? content.Opacity : 0;
        if (!IsVisible)
        {
            content.Opacity = 0;
            Show();
        }

        Animate(content, from, 1, 170);
    }

    internal void Dismiss()
    {
        if (!IsVisible)
            return;

        int version = ++animationVersion;
        var content = (FrameworkElement)Content;
        Animate(content, content.Opacity, 0, 130, () =>
        {
            if (version == animationVersion)
                Hide();
        });
    }

    private static void Animate(FrameworkElement element, double from, double to, int milliseconds,
        Action? completed = null)
    {
        var animation = Transition(from, to, milliseconds);
        if (completed is not null)
            animation.Completed += (_, _) => completed();
        element.Opacity = to;
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static DoubleAnimation Transition(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
}
