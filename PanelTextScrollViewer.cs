using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Rtf;

public sealed class PanelTextScrollViewer : ScrollViewer
{
    private const double WheelDetent = 120;
    private const double TextLineHeight = 22;

    private static readonly DependencyPropertyKey IsWheelScrollingPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsWheelScrolling), typeof(bool), typeof(PanelTextScrollViewer),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsWheelScrollingProperty = IsWheelScrollingPropertyKey.DependencyProperty;

    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.Register("AnimatedOffset", typeof(double), typeof(PanelTextScrollViewer),
            new PropertyMetadata(0d, OnAnimatedOffsetChanged));

    private double wheelTarget;
    private int wheelVersion;

    public bool IsWheelScrolling => (bool)GetValue(IsWheelScrollingProperty);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled)
            return;

        e.Handled = true;
        int lines = SystemParameters.WheelScrollLines;
        if (e.Delta == 0 || ScrollableHeight <= 0 || lines == 0)
            return;

        double step = lines < 0 ? ViewportHeight : lines * TextLineHeight;
        double distance = -e.Delta / WheelDetent * step;
        double current = VerticalOffset;
        double pending = IsWheelScrolling && Math.Sign(wheelTarget - current) == Math.Sign(distance)
            ? wheelTarget : current;
        double target = Math.Clamp(pending + distance, 0, ScrollableHeight);
        if (target == pending)
            return;

        wheelTarget = target;
        int version = ++wheelVersion;
        SetValue(IsWheelScrollingPropertyKey, true);

        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(125))
        {
            DecelerationRatio = .75
        };
        animation.Completed += (_, _) =>
        {
            if (wheelVersion != version)
                return;

            StopWheelScroll();
            ScrollToVerticalOffset(target);
        };
        BeginAnimation(AnimatedOffsetProperty, animation);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        StopWheelScroll();
        base.OnPreviewMouseLeftButtonDown(e);
    }

    internal void ResetScroll()
    {
        StopWheelScroll();
        ScrollToTop();
    }

    private void StopWheelScroll()
    {
        if (!IsWheelScrolling)
            return;

        wheelVersion++;
        SetValue(IsWheelScrollingPropertyKey, false);
        BeginAnimation(AnimatedOffsetProperty, null);
    }

    private static void OnAnimatedOffsetChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (PanelTextScrollViewer)source;
        if (viewer.IsWheelScrolling)
            viewer.ScrollToVerticalOffset((double)e.NewValue);
    }
}
