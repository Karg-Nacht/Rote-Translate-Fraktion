using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rtf;

public sealed class SettingsTabControl : TabControl
{
    private const double IndicatorInset = 12;
    private Rect previousIndicator = Rect.Empty;
    private Border? indicator;
    private FrameworkElement? track;

    public SettingsTabControl() => LayoutUpdated += (_, _) => MoveIndicator();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        indicator = GetTemplateChild("TabIndicator") as Border;
        if (indicator is not null)
            indicator.RenderTransform = new TranslateTransform();
        track = GetTemplateChild("TabTrack") as FrameworkElement;
        previousIndicator = Rect.Empty;
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (e.Source != this)
            return;

        (GetTemplateChild("TabScroll") as ScrollViewer)?.ScrollToTop();
        MoveIndicator();
        if (!IsLoaded || e.RemovedItems.Count == 0 || !SystemParameters.ClientAreaAnimation ||
            GetTemplateChild("PART_SelectedContentHost") is not ContentPresenter content)
            return;

        var easing = (IEasingFunction)FindResource("EaseOut");
        var offset = new TranslateTransform();
        content.RenderTransform = offset;
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });
    }

    private void MoveIndicator()
    {
        if (indicator?.RenderTransform is not TranslateTransform offset || track is null || SelectedIndex < 0 ||
            ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not TabItem { ActualWidth: > 0 } tab)
            return;

        var origin = tab.TranslatePoint(new Point(IndicatorInset, tab.ActualHeight - indicator.Height), track);
        double width = Math.Max(0, tab.ActualWidth - 2 * IndicatorInset);
        var destination = new Rect(origin, new Size(width, indicator.Height));
        if (destination == previousIndicator)
            return;

        bool animate = !previousIndicator.IsEmpty && SystemParameters.ClientAreaAnimation;
        previousIndicator = destination;
        SetPosition(offset, TranslateTransform.XProperty, origin.X, animate);
        SetPosition(offset, TranslateTransform.YProperty, origin.Y, animate);
        SetPosition(indicator, WidthProperty, width, animate);
        indicator.Opacity = 1;
    }

    private void SetPosition(IAnimatable target, DependencyProperty property, double value, bool animate)
    {
        var element = (DependencyObject)target;
        double from = (double)element.GetValue(property);
        element.SetValue(property, value);
        target.BeginAnimation(property, animate ? new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
            FillBehavior = FillBehavior.Stop
        } : null);
    }
}
