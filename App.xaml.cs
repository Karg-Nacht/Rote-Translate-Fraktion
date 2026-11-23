using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Rtf;

public partial class App : Application
{
    private readonly DispatcherTimer expiration = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly TranslationSettingsStore translationSettings = new();
    private readonly OverlayPreferences overlayPreferences = new();
    private SettingsWindow? settingsWindow;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private SelectionMonitor? monitor;
    private DesktopInput? input;
    private SelectionIcon? icon;
    private TranslationPanel? panel;
    private Selection? lastSelection;
    private Selection? offeredSelection;
    private bool pinned;
    private bool emptyOffer;
    private bool crystalGrabbed;

    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);

        trayMenu = new TrayExitMenu(() => Dispatcher.BeginInvoke(() => ShowSettings(null)), () => Shutdown());
        using var trayArtwork = GetResourceStream(new Uri("/RTF;component/TrayCrystal.ico", UriKind.Relative)).Stream;
        trayIcon = new System.Drawing.Icon(trayArtwork, Forms.SystemInformation.SmallIconSize);
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Rote Translate Fraktion",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        expiration.Tick += (_, _) => DismissIcon();

        try
        {
            monitor = new SelectionMonitor((selection, version) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (monitor?.IsCurrent(version) == true)
                        UpdateSelection(selection);
                }, DispatcherPriority.Background);
            });
            input = new DesktopInput(OnForegroundChanged, monitor.InteractionEnded, OfferEmpty, point =>
            {
                if (pinned)
                    panel?.TryStartResize(point);
            });
        }
        catch (Win32Exception error)
        {
            new StartupErrorWindow(error.Message).ShowDialog();
            Shutdown(1);
        }
    }

    private void OnForegroundChanged()
    {
        monitor?.ForegroundChanged();
        nint foreground = Native.GetForegroundWindow();
        if (Native.IsOwnWindow(foreground))
            return;

        if (lastSelection?.Window != foreground)
        {
            lastSelection = null;
            if (!pinned && !emptyOffer && !crystalGrabbed)
                DismissIcon();
        }
    }

    private void UpdateSelection(Selection? selection)
    {
        nint foreground = Native.GetForegroundWindow();
        if (emptyOffer || crystalGrabbed || Native.IsOwnWindow(foreground) ||
            (selection is not null && selection.Window != foreground))
            return;

        bool sameSelection = selection?.Window == lastSelection?.Window &&
            selection?.Text == lastSelection?.Text && selection?.Bounds == lastSelection?.Bounds;
        lastSelection = selection;
        if (pinned || sameSelection)
            return;

        if (selection is null)
        {
            DismissIcon();
            return;
        }

        EnsureIcon();
        OfferSelection(selection, reposition: true);
        icon!.Reveal();
    }

    private void OpenPanel()
    {
        if (icon is null || pinned ||
            (!emptyOffer && (offeredSelection is not { } selection || selection.Window != Native.GetForegroundWindow())))
            return;

        expiration.Stop();
        pinned = true;
        if (panel is null)
        {
            panel = new TranslationPanel(new TranslationService(translationSettings.Current),
                translationSettings.Current.TranslateOnOpen, overlayPreferences.Current.PanelHeight);
            if (input is { } desktop)
                panel.TrackLeftPointer = desktop.TrackLeftPointer;
            panel.SettingsRequested += ShowSettings;
            panel.CloseRequested += ClosePanel;
            panel.HeightChanged += height => overlayPreferences.Save(overlayPreferences.Current with { PanelHeight = height });
        }
        panel.ShowSelection(emptyOffer ? string.Empty : offeredSelection!.Text, icon);
    }

    private void ShowSettings(FrameworkElement? anchor)
    {
        if (settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        if (!pinned)
            DismissIcon();

        var window = new SettingsWindow(translationSettings, anchor);
        settingsWindow = window;
        window.Closed += (_, _) => settingsWindow = null;
        window.SettingsSaved += () => panel?.UseTranslator(new TranslationService(translationSettings.Current),
            translationSettings.Current.TranslateOnOpen);
        window.Show();
        window.Activate();
    }

    private void ClosePanel()
    {
        if (pinned && panel is not null && icon is not null)
            panel.CollapseTo(icon, FinishClosing);
    }

    private void FinishClosing()
    {
        pinned = false;
        if (emptyOffer)
        {
            RestartExpiration();
            return;
        }
        if (lastSelection is not { } selection || selection.Window != Native.GetForegroundWindow())
        {
            DismissIcon();
            return;
        }

        // Keep the return position even if the provider refreshed the bounds
        // of the same text while the panel was open.
        bool changed = offeredSelection?.Window != selection.Window ||
            offeredSelection?.Text != selection.Text;
        OfferSelection(selection, reposition: changed);
    }

    private void OfferSelection(Selection selection, bool reposition)
    {
        offeredSelection = selection;
        if (reposition && icon is { } crystal)
        {
            var anchor = selection.Bounds is { } bounds
                ? new Native.Point((int)bounds.Right, (int)bounds.Bottom) : selection.Fallback;
            if (overlayPreferences.Current is { CrystalX: int x, CrystalY: int y } &&
                Native.MonitorFromPoint(new Native.Point(x, y), 0) is var savedMonitor && savedMonitor != 0 &&
                savedMonitor == Native.MonitorFromPoint(anchor, 2))
                crystal.MoveToCenter(new Point(x, y));
            else
                crystal.Place(selection.Bounds, selection.Fallback);
        }
        RestartExpiration();
    }

    private void EnsureIcon()
    {
        if (icon is not null)
            return;

        icon = new SelectionIcon();
        if (input is { } desktop)
            icon.TrackLeftPointer = desktop.TrackLeftPointer;
        icon.OpenRequested += OpenPanel;
        icon.HideRequested += () => DismissIcon(immediately: true);
        icon.GrabChanged += active =>
        {
            crystalGrabbed = active;
            if (active)
                expiration.Stop();
            else if (!pinned && (emptyOffer || offeredSelection is not null))
                RestartExpiration();
        };
        icon.PositionChanged += position => overlayPreferences.Save(overlayPreferences.Current with
        {
            CrystalX = (int)Math.Round(position.X),
            CrystalY = (int)Math.Round(position.Y)
        });
    }

    private void OfferEmpty(Native.Point cursor)
    {
        if (pinned || crystalGrabbed || settingsWindow?.IsActive == true)
            return;

        EnsureIcon();
        offeredSelection = null;
        emptyOffer = true;
        icon!.Place(null, cursor);
        icon.Reveal();
        RestartExpiration();
    }

    private void RestartExpiration()
    {
        expiration.Stop();
        expiration.Start();
    }

    private void DismissIcon(bool immediately = false)
    {
        expiration.Stop();
        offeredSelection = null;
        emptyOffer = false;
        if (immediately)
            icon?.Hide();
        else
            icon?.Dismiss();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        expiration.Stop();
        input?.Dispose();
        overlayPreferences.Flush();
        monitor?.Dispose();
        monitor = null;
        tray?.Dispose();
        trayIcon?.Dispose();
        trayMenu?.Dispose();
        base.OnExit(e);
    }
}
