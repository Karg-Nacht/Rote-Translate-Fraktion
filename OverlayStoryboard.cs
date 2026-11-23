using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rtf;

internal sealed class OverlayStoryboard : Storyboard
{
    private static readonly SineEase EaseInOut = CreateEasing();
    private static readonly SineEase EaseOut = CreateEasing(EasingMode.EaseOut);

    public void Fade(UIElement target, params (int Milliseconds, double Value)[] frames) =>
        Animate(target, new PropertyPath(UIElement.OpacityProperty), frames);

    public void Animate(UIElement target, PropertyPath property,
        params (int Milliseconds, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (milliseconds, value) in frames)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, TimeSpan.FromMilliseconds(milliseconds), EaseInOut));
        Add(animation, target, property);
    }

    public void Reveal(UIElement target, params (int Milliseconds, Rect Bounds)[] frames)
    {
        var animation = new RectAnimationUsingKeyFrames();
        foreach (var (milliseconds, bounds) in frames)
            animation.KeyFrames.Add(new EasingRectKeyFrame(bounds, TimeSpan.FromMilliseconds(milliseconds), EaseOut));
        Add(animation, target, new PropertyPath("(0).(1)", UIElement.ClipProperty, RectangleGeometry.RectProperty));
    }

    private void Add(Timeline animation, UIElement target, PropertyPath property)
    {
        SetTarget(animation, target);
        SetTargetProperty(animation, property);
        Children.Add(animation);
    }

    private static SineEase CreateEasing(EasingMode mode = EasingMode.EaseInOut)
    {
        var easing = new SineEase { EasingMode = mode };
        easing.Freeze();
        return easing;
    }
}
