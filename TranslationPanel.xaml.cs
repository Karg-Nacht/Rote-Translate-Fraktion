using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rtf;

public partial class TranslationPanel : OverlayWindow
{
    private const double HeaderHeight = 46;
    private const double MinimumPanelHeight = 232;
    private readonly ScaleTransform settingsPulse = new();
    private readonly RectangleGeometry silhouette = new() { RadiusX = 14, RadiusY = 14 };
    private OverlayStoryboard? transition;
    private Point dockCenter;
    private bool moved;
    private Native.Point? dragOrigin;
    private Native.Rect dragWindow;
    private nint focusBeforeCopy;
    private (int CursorY, double Height, double Limit, double Scale)? resize;
    private double preferredHeight;

    public TranslationPanel() : this(new TranslationService(new TranslationSettings()), false, 242) { }

    internal TranslationPanel(TranslationService translator, bool translateOnOpen, double panelHeight)
    {
        this.translator = translator;
        this.translateOnOpen = translateOnOpen;
        preferredHeight = double.IsFinite(panelHeight) ? Math.Clamp(panelHeight, MinimumPanelHeight, 5000) : 242;
        InitializeComponent();
        SettingsGlyph.RenderTransform = settingsPulse;
        Outline.Data = silhouette;
        Regions.Clip = silhouette;
        InitializeEditor();
    }

    internal event Action? CloseRequested;
    internal event Action<FrameworkElement>? SettingsRequested;
    internal event Action<double>? HeightChanged;
    internal Action<Action<Native.Point, int, bool>>? TrackLeftPointer { get; set; }
    internal bool IsClosing { get; private set; }

    internal void ShowSelection(string text, SelectionIcon icon)
    {
        LoadSource(text);
        var center = icon.ScreenCenter;
        var (work, scale) = PlacementArea(new Native.Point((int)center.X, (int)center.Y));
        Height = Math.Min(preferredHeight, MaxPanelHeight(work.Height, scale));
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Height * scale);
        int inset = (int)Math.Round(HeaderHeight / 2 * scale);
        MoveWithoutActivation(
            Math.Clamp((int)center.X - inset, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp((int)center.Y - inset, work.Top, Math.Max(work.Top, work.Bottom - height)));
        Outline.Opacity = Header.Opacity = Body.Opacity = 0;
        Show();
        dockCenter = PointFromScreen(center);
        moved = false;

        var seed = BadgeBounds(dockCenter, icon.BadgeWidth);
        var header = PanelBounds(HeaderHeight);
        var full = PanelBounds(Height);
        silhouette.Rect = seed;

        var opening = new OverlayStoryboard();
        icon.AnimateDeparture(opening);
        opening.Reveal(Regions, (0, seed), (115, seed), (215, header), (360, full));
        opening.Fade(Outline, (0, 0), (132, 0), (208, 1));
        opening.Fade(Header, (0, 0), (160, 0), (215, 1));
        opening.Fade(Body, (0, 0), (360, 0), (450, 1));
        Run(opening, () =>
        {
            silhouette.Rect = full;
            Outline.Opacity = Header.Opacity = Body.Opacity = 1;
            icon.Hide();
        });
        bool useTranslatorOnOpen = translationRoute == TranslationRoute.Google ||
            (translationRoute == TranslationRoute.Configured && translator.StartsWithTranslator);
        if ((translateOnOpen || useTranslatorOnOpen) && !string.IsNullOrWhiteSpace(text))
            StartTranslation();
    }

    internal void CollapseTo(SelectionIcon icon, Action complete)
    {
        if (IsClosing)
            return;
        IsClosing = true;
        TargetLanguage.Close();
        CancelTranslation();

        if (moved)
            icon.MoveToCenter(PointToScreen(dockCenter));

        var seed = BadgeBounds(PointFromScreen(icon.ScreenCenter), icon.BadgeWidth);
        var start = silhouette.Rect;
        var strip = new Rect(start.X, start.Y, start.Width, Math.Min(start.Height, HeaderHeight - 1));
        int fadeEnd = Body.Opacity > 0 ? 80 : 0;
        int foldEnd = fadeEnd + (start.Height > strip.Height ? 145 : 0);
        int stripEnd = foldEnd + 22;
        int end = stripEnd + 105;

        var closing = new OverlayStoryboard();
        icon.AnimateReturn(closing, stripEnd + 23, end);
        closing.Fade(Body, (0, Body.Opacity), (fadeEnd, 0));
        closing.Reveal(Regions, (0, start), (fadeEnd, start), (foldEnd, strip),
            (stripEnd, strip), (end, seed));
        closing.Fade(Header, (0, Header.Opacity), (stripEnd, Header.Opacity), (stripEnd + 73, 0));
        closing.Fade(Outline, (0, Outline.Opacity), (stripEnd + 28, Outline.Opacity), (end, 0));
        Run(closing, () =>
        {
            RestoreCopyFocus();
            Hide();
            LoadSource(string.Empty);
            Translation.SetContent(string.Empty);
            icon.CompleteTurn(() =>
            {
                IsClosing = false;
                complete();
            });
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelTranslation();
        copyFeedback.Stop();
        LoadSource(string.Empty);
        Translation.SetContent(string.Empty);
        base.OnClosed(e);
    }

    private Rect PanelBounds(double height) => new(.5, .5, Width - 1, height - 1);

    private static Rect BadgeBounds(Point center, double size) =>
        new(center.X - size / 2, center.Y - size / 2, size, size);

    private void Run(OverlayStoryboard storyboard, Action complete)
    {
        transition?.Remove(this);
        transition = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (transition != storyboard)
                return;

            transition = null;
            storyboard.Remove(this);
            complete();
        };
        storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private void FocusContent(object sender, MouseButtonEventArgs args)
    {
        if (IsClosing || sender is not UIElement view)
            return;

        ActivateContent();
        view.Focus();
    }

    private void ActivateContent()
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground != Handle && !Native.IsOwnWindow(foreground) && Activate())
            focusBeforeCopy = foreground;
    }

    private void RestoreCopyFocus()
    {
        if (focusBeforeCopy != 0 && Native.GetForegroundWindow() == Handle)
            Native.SetForegroundWindow(focusBeforeCopy);

        focusBeforeCopy = 0;
    }

    private void SettingsClicked(object sender, RoutedEventArgs args)
    {
        if (IsClosing || Header.Opacity < .9)
            return;

        if (SystemParameters.ClientAreaAnimation)
        {
            var press = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            press.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.Zero));
            press.KeyFrames.Add(new EasingDoubleKeyFrame(.89, TimeSpan.FromMilliseconds(34)));
            press.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(112)));
            settingsPulse.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            settingsPulse.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        }

        SettingsRequested?.Invoke(SettingsButton);
    }

    private void CloseClicked(object sender, RoutedEventArgs args)
    {
        if (Header.Opacity >= .9)
            CloseRequested?.Invoke();
    }

    private void StartDrag(object sender, MouseButtonEventArgs args)
    {
        if (transition is null && sender is UIElement grip && Native.GetCursorPos(out var point) &&
            Native.GetWindowRect(Handle, out dragWindow) && (TrackLeftPointer is not null || grip.CaptureMouse()))
        {
            dragOrigin = point;
            TrackLeftPointer?.Invoke((cursor, _, released) =>
            {
                MovePanelTo(cursor);
                if (released)
                    dragOrigin = null;
            });
            args.Handled = true;
        }
    }

    private void MoveDrag(object sender, MouseEventArgs args)
    {
        if (TrackLeftPointer is null && Native.GetCursorPos(out var cursor))
            MovePanelTo(cursor);
    }

    private void MovePanelTo(Native.Point cursor)
    {
        if (!IsClosing && dragOrigin is { } start && (cursor.X != start.X || cursor.Y != start.Y || moved))
        {
            var (work, scale) = PlacementArea(cursor);
            Height = Math.Min(preferredHeight, MaxPanelHeight(work.Height, scale));
            int left = dragWindow.Left + cursor.X - start.X;
            int top = dragWindow.Top + cursor.Y - start.Y;
            MoveWithoutActivation(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - (int)Math.Ceiling(Width * scale))),
                Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - (int)Math.Ceiling(Height * scale))));
            silhouette.Rect = PanelBounds(Height);
            moved = true;
        }
    }

    private void EndDrag(object sender, MouseButtonEventArgs args) =>
        ((UIElement)sender).ReleaseMouseCapture();

    private void DragCaptureLost(object sender, MouseEventArgs args) => dragOrigin = null;

    private static double MaxPanelHeight(int workHeight, double scale) => Math.Max(1, workHeight / scale * .84);

    private void StartResize(object sender, DragStartedEventArgs args)
    {
        if (TrackLeftPointer is null && Native.GetCursorPos(out var point))
            CaptureResize(point);
    }

    internal void TryStartResize(Native.Point point)
    {
        if (TrackLeftPointer is null || !IsVisible || Native.WindowFromPoint(point) != Handle ||
            !Native.GetWindowRect(Handle, out var bounds))
            return;
        var (_, scale) = PlacementArea(point);
        if (point.Y < bounds.Bottom - HeightGrip.Height * scale || !CaptureResize(point))
            return;
        TrackLeftPointer((cursor, _, released) =>
        {
            ResizeAt(cursor);
            if (released)
                FinishResize(cancelled: false);
        });
    }

    private bool CaptureResize(Native.Point point)
    {
        if (transition is not null || IsClosing || !Native.GetWindowRect(Handle, out var bounds))
            return false;
        var (work, scale) = PlacementArea(new Native.Point(bounds.Left + (bounds.Right - bounds.Left) / 2, bounds.Top));
        double limit = Math.Min(MaxPanelHeight(work.Height, scale), Math.Max(1, (work.Bottom - bounds.Top) / scale));
        resize = (point.Y, Height, limit, scale);
        return true;
    }

    private void MoveResize(object sender, DragDeltaEventArgs args)
    {
        if (TrackLeftPointer is null && Native.GetCursorPos(out var point))
            ResizeAt(point);
    }

    private void ResizeAt(Native.Point point)
    {
        if (resize is not { } start || IsClosing)
            return;

        Height = Math.Clamp(start.Height + (point.Y - start.CursorY) / start.Scale,
            Math.Min(MinimumPanelHeight, start.Limit), start.Limit);
        silhouette.Rect = PanelBounds(Height);
    }

    private void EndResize(object sender, DragCompletedEventArgs args)
    {
        if (TrackLeftPointer is null)
            FinishResize(args.Canceled);
    }

    private void FinishResize(bool cancelled)
    {
        if (resize is { } start)
        {
            if (cancelled)
                Height = start.Height;
            else if (Height != start.Height)
            {
                preferredHeight = Math.Max(MinimumPanelHeight, Height);
                HeightChanged?.Invoke(preferredHeight);
            }
            silhouette.Rect = PanelBounds(Height);
        }
        resize = null;
    }
}
