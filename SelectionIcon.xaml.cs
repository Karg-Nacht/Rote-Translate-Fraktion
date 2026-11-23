using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rtf;

public partial class SelectionIcon : OverlayWindow
{
    private const double HalfTurn = 180;
    private Native.Point? grabbedAt;
    private Native.Point previousPoint;
    private Point grabOffset;
    private long previousTime;
    private bool dragged;

    public SelectionIcon() => InitializeComponent();

    internal event Action? OpenRequested;
    internal event Action? HideRequested;
    internal event Action<bool>? GrabChanged;
    internal event Action<Point>? PositionChanged;
    internal Action<Action<Native.Point, int, bool>>? TrackLeftPointer { get; set; }

    internal Point ScreenCenter
    {
        get
        {
            Native.GetWindowRect(Handle, out var bounds);
            return new Point((bounds.Left + bounds.Right) / 2d, (bounds.Top + bounds.Bottom) / 2d);
        }
    }
    internal double BadgeWidth => OpenButton.Width;

    internal void MoveToCenter(Point center)
    {
        var point = new Native.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
        var (work, scale) = PlacementArea(point);
        int size = (int)Math.Ceiling(Width * scale);
        MoveWithoutActivation(Math.Clamp(point.X - size / 2, work.Left, Math.Max(work.Left, work.Right - size)),
            Math.Clamp(point.Y - size / 2, work.Top, Math.Max(work.Top, work.Bottom - size)));
    }

    internal void AnimateDeparture(OverlayStoryboard motion)
    {
        SetTransitionInput(active: true);
        RaisePlate();
        motion.Animate(CrystalArtwork, TransformProperty(1, RotateTransform.AngleProperty), (0, 0), (80, 0), (260, HalfTurn));
        motion.Animate(CrystalArtwork, TransformProperty(0, ScaleTransform.ScaleXProperty), (0, 1), (80, .91), (155, 1));
        motion.Animate(CrystalArtwork, TransformProperty(0, ScaleTransform.ScaleYProperty), (0, 1), (80, .91), (155, 1));
        motion.Fade(CrystalArtwork, (0, 1), (260, 1), (335, 0));
        motion.Fade(OpenButton, (0, 1), (278, 1), (370, 0));
    }

    internal void AnimateReturn(OverlayStoryboard motion, int fadeStart, int end)
    {
        double opacity = IsVisible ? OpenButton.Opacity : 0;
        double artworkOpacity = IsVisible ? CrystalArtwork.Opacity : 0;
        double angle = IsVisible ? Rotation.Angle : HalfTurn;
        OpenButton.Opacity = opacity;
        CrystalArtwork.Opacity = artworkOpacity;
        if (!IsVisible)
            Show();

        RaisePlate();


        motion.Animate(CrystalArtwork, TransformProperty(1, RotateTransform.AngleProperty),
            (0, angle), (fadeStart, HalfTurn));
        motion.Fade(OpenButton, (0, opacity), (fadeStart, opacity), (end - 12, 1));
        motion.Fade(CrystalArtwork, (0, artworkOpacity), (fadeStart + 12, artworkOpacity), (end, 1));
    }

    private static PropertyPath TransformProperty(int index, DependencyProperty property) =>
        new($"(0).(1)[{index}].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, property);

    internal void CompleteTurn(Action complete)
    {
        OpenButton.Opacity = 1;
        CrystalArtwork.Opacity = 1;
        var spin = new OverlayStoryboard();
        spin.Animate(CrystalArtwork, TransformProperty(1, RotateTransform.AngleProperty), (0, HalfTurn), (180, HalfTurn * 2));
        spin.Completed += (_, _) =>
        {
            spin.Remove(this);
            SetTransitionInput(active: false);
            complete();
        };
        spin.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private void RaisePlate()
    {
        const int topmost = -1;
        const uint keepLocationAndSize = 0x0001 | 0x0002;
        const uint noActivate = 0x0010;
        Native.SetWindowPos(Handle, topmost, 0, 0, 0, 0, keepLocationAndSize | noActivate);
    }

    private void SetTransitionInput(bool active)
    {
        const int extendedStyle = -20;
        const int transparent = 0x00000020;
        nint hwnd = Handle;
        int style = Native.GetWindowLong(hwnd, extendedStyle);
        Native.SetWindowLong(hwnd, extendedStyle, active ? style | transparent : style & ~transparent);
        OpenButton.IsHitTestVisible = !active;
    }

    private void OpenClicked(object sender, RoutedEventArgs args) => OpenRequested?.Invoke();

    private void CrystalPressed(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Middle)
        {
            args.Handled = true;
            HideRequested?.Invoke();
            return;
        }
        if (args.ChangedButton != MouseButton.Left || grabbedAt is not null || !Native.GetCursorPos(out var cursor) ||
            (TrackLeftPointer is null && !OpenButton.CaptureMouse()))
            return;

        var (_, scale) = PlacementArea(cursor);
        var center = ScreenCenter;
        grabOffset = new Point((cursor.X - center.X) / scale, (cursor.Y - center.Y) / scale);
        grabbedAt = previousPoint = cursor;
        previousTime = Environment.TickCount;
        dragged = false;
        GrabChanged?.Invoke(true);
        AnimateArtwork(.94, .93, 0, 0, 0, 100);
        TrackLeftPointer?.Invoke(TrackGrab);
        args.Handled = true;
    }

    private void CrystalMoved(object sender, MouseEventArgs args)
    {
        if (TrackLeftPointer is not null || grabbedAt is null || args.LeftButton != MouseButtonState.Pressed ||
            !Native.GetCursorPos(out var cursor))
            return;
        TrackGrab(cursor, Environment.TickCount, false);
        args.Handled = true;
    }

    private void TrackGrab(Native.Point cursor, int timestamp, bool release)
    {
        if (grabbedAt is not { } start)
            return;
        var (_, scale) = PlacementArea(cursor);
        if (dragged || Math.Max(Math.Abs(cursor.X - start.X), Math.Abs(cursor.Y - start.Y)) >= 6 * scale)
        {
            dragged = true;
            MoveToCenter(new Point(cursor.X - grabOffset.X * scale, cursor.Y - grabOffset.Y * scale));
            if (!release)
            {
                double elapsed = Math.Max(12, timestamp - previousTime);
                double x = Math.Clamp((cursor.X - previousPoint.X) / (elapsed * scale), -1.5, 1.5);
                double y = Math.Clamp((cursor.Y - previousPoint.Y) / (elapsed * scale), -1.5, 1.5);
                AnimateArtwork(1 + Math.Abs(x) * .07 - Math.Abs(y) * .025,
                    1 + Math.Abs(y) * .07 - Math.Abs(x) * .025, x * 8, -x * 1.4, -y * 1.4, 100);
                previousPoint = cursor;
                previousTime = timestamp;
            }
        }
        if (!release)
            return;
        bool open = !dragged && IsVisible && new Rect(OpenButton.RenderSize).Contains(
            OpenButton.PointFromScreen(new Point(cursor.X, cursor.Y)));
        FinishGrab();
        if (open)
        {
            ResetArtwork();
            OpenRequested?.Invoke();
        }
    }

    private void CrystalReleased(object sender, MouseButtonEventArgs args)
    {
        if (TrackLeftPointer is not null || args.ChangedButton != MouseButton.Left || grabbedAt is null ||
            !Native.GetCursorPos(out var cursor))
            return;
        TrackGrab(cursor, Environment.TickCount, true);
        OpenButton.ReleaseMouseCapture();
        args.Handled = true;
    }

    private void CrystalCaptureLost(object sender, MouseEventArgs args)
    {
        if (grabbedAt is not null)
            FinishGrab();
    }

    private void FinishGrab()
    {
        grabbedAt = null;
        if (dragged)
            PositionChanged?.Invoke(ScreenCenter);
        dragged = false;
        AnimateArtwork(1, 1, 0, 0, 0, 260);
        GrabChanged?.Invoke(false);
    }

    private void AnimateArtwork(double x, double y, double angle, double offsetX, double offsetY, int duration)
    {
        AnimateTransform(Compression, ScaleTransform.ScaleXProperty, x, duration);
        AnimateTransform(Compression, ScaleTransform.ScaleYProperty, y, duration);
        AnimateTransform(Rotation, RotateTransform.AngleProperty, angle, duration);
        AnimateTransform(Inertia, TranslateTransform.XProperty, offsetX, duration);
        AnimateTransform(Inertia, TranslateTransform.YProperty, offsetY, duration);
    }

    private static void AnimateTransform(Animatable transform, DependencyProperty property, double target, int duration)
    {
        double from = (double)transform.GetValue(property);
        transform.SetValue(property, target);
        transform.BeginAnimation(property, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(duration))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ResetArtwork()
    {
        Compression.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Compression.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        Inertia.BeginAnimation(TranslateTransform.XProperty, null);
        Inertia.BeginAnimation(TranslateTransform.YProperty, null);
    }
}
