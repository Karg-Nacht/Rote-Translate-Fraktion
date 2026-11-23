using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rtf;

public partial class SettingsWindow : Window
{
    private sealed record Choice<T>(T Value, string Label);
    private sealed record ClassicApi(string Name, string Key, string? Extra = null, string? ExtraLabel = null);

    private static readonly Choice<TranslationMode>[] Modes =
    {
        new(TranslationMode.AiFirst, "AI first, translation services second"),
        new(TranslationMode.TranslatorsFirst, "Translation services first, AI second"),
        new(TranslationMode.AiOnly, "AI only"),
        new(TranslationMode.TranslatorsOnly, "Translation services only"),
        new(TranslationMode.LocalAiOnly, "Local AI only")
    };

    private static readonly ClassicApi[] Apis =
    {
        new("Yandex Translate", "RTF_YANDEX_TRANSLATOR_KEY"),
        new("DeepL", "RTF_DEEPL_KEY"),
        new("Microsoft Translator", "RTF_AZURE_TRANSLATOR_KEY", "RTF_AZURE_TRANSLATOR_REGION", "Azure region"),
        new("Google Cloud", "RTF_GOOGLE_CLOUD_KEY"),
        new("LibreTranslate", "RTF_LIBRETRANSLATE_KEY", "RTF_LIBRETRANSLATE_URL", "LibreTranslate base URL")
    };

    private readonly TranslationSettingsStore store;
    private readonly Dictionary<string, AiConnection> connections;
    private readonly Dictionary<string, string> translatorApi;
    private AiProfile? currentAi;
    private ClassicApi? currentApi;
    private CancellationTokenSource? modelRequest;
    private IInputElement? focusBeforeDiscardPrompt;
    private bool closeInProgress;
    private bool closeApproved;
    private bool savedSuccessfully;
    private readonly FrameworkElement? settingsAnchor;
    private readonly bool animateOpening = SystemParameters.ClientAreaAnimation;
    private readonly RectangleGeometry silhouette = new() { RadiusX = 15, RadiusY = 15 };
    private OverlayStoryboard? windowMotion;

    internal SettingsWindow(TranslationSettingsStore store, FrameworkElement? settingsAnchor)
    {
        this.store = store;
        this.settingsAnchor = settingsAnchor;
        var saved = store.Current;
        connections = new(saved.Ai);
        translatorApi = new(saved.TranslatorApi);
        InitializeComponent();
        Outline.Data = silhouette;
        Regions.Clip = silhouette;
        Chrome.SizeChanged += (_, _) =>
        {
            if (windowMotion is null && !silhouette.Rect.IsEmpty)
                silhouette.Rect = WindowBounds();
        };
        EdgeHighlight.RenderTransform = new TranslateTransform();
        ModelPicker.SelectionChanged += ModelChanged;
        if (animateOpening)
            Chrome.Opacity = ShellShadow.Opacity = 0;

        var targets = new List<PickerOption>
        {
            new("", "Russian (default)"),
            new("ru", "Russian"), new("en", "English"), new("de", "German"),
            new("fr", "French"), new("es", "Spanish"), new("it", "Italian"),
            new("pt", "Portuguese"), new("uk", "Ukrainian"), new("pl", "Polish"),
            new("tr", "Turkish"), new("zh", "Chinese"), new("ja", "Japanese"),
            new("ko", "Korean"), new("ar", "Arabic"), new("hi", "Hindi")
        };
        if (!targets.Any(target => target.Value == saved.Target))
            targets.Add(new(saved.Target, saved.Target.ToUpperInvariant()));

        ModePicker.ItemsSource = Modes;
        ModePicker.SelectedValue = saved.Mode;
        if (ModePicker.SelectedIndex < 0)
            ModePicker.SelectedIndex = 0;
        TargetPicker.SetItems(targets);
        TargetPicker.SelectValue(saved.Target);
        StyleInstructions.Text = saved.Instructions;
        RequestPicker.ItemsSource = new Choice<bool>[]
        {
            new(false, "Manually"),
            new(true, "When the panel opens")
        };
        RequestPicker.SelectedValue = saved.TranslateOnOpen;
        UseGoogle.IsChecked = saved.GoogleWeb;
        GoogleLive.IsChecked = saved.GoogleLive;
        UseMemory.IsChecked = saved.MyMemory;
        AiPicker.ItemsSource = AiProfile.All;
        AiPicker.SelectedIndex = 0;
        ApiPicker.ItemsSource = Apis;
        ApiPicker.SelectedIndex = 0;
        if (store.LoadFailed)
            ShowFeedback("Couldn't load settings. Showing defaults.", error: true);
    }

    internal event Action? SettingsSaved;

    private void AiChanged(object sender, SelectionChangedEventArgs args)
    {
        ReadCurrentAi();
        CancelModelList();
        ModelPicker.Close();
        ModelPicker.Visibility = Visibility.Collapsed;
        currentAi = AiPicker.SelectedItem as AiProfile;
        if (currentAi is not { } profile)
            return;

        var connection = connections.GetValueOrDefault(profile.Id) ?? profile.DefaultConnection;
        AiEnabled.IsChecked = connection.Enabled;
        AiKey.Password = connection.Key;
        AiModel.Text = connection.Model;
        AiAddress.Text = profile.EditableAddress ? connection.Address : profile.DefaultAddress;
        AiAddress.IsReadOnly = !profile.EditableAddress;
        LoadModels.IsEnabled = true;
        AiHint.Text = profile.Id switch
        {
            "openrouter" => "openrouter/free picks an available free model. An OpenRouter API key is required.",
            "polza" => "Choose a text model from the Polza.ai catalog. Requests may use account credits.",
            "ollama" => "Start Ollama and download a model. An API key is usually optional.",
            "lm-studio" => "Start the local server in LM Studio. Enter an API key if authentication is enabled.",
            _ => "Enter the base URL of a compatible API, such as https://api.openai.com/v1."
        };
    }

    private AiConnection ReadCurrentAi()
    {
        var connection = new AiConnection(AiEnabled.IsChecked == true, AiModel.Text.Trim(),
            AiAddress.Text.Trim(), AiKey.Password.Trim());
        if (currentAi is { } profile)
            connections[profile.Id] = connection;

        return connection;
    }

    private void ApiChanged(object sender, SelectionChangedEventArgs args)
    {
        ReadCurrentApi();
        currentApi = ApiPicker.SelectedItem as ClassicApi;
        if (currentApi is not { } api)
            return;

        TranslatorKey.Password = translatorApi.GetValueOrDefault(api.Key, "");
        ApiExtraFields.Visibility = api.Extra is null ? Visibility.Collapsed : Visibility.Visible;
        ApiExtraLabel.Content = api.ExtraLabel;
        ApiExtra.Text = api.Extra is { } extra ? translatorApi.GetValueOrDefault(extra, "") : "";
    }

    private void ReadCurrentApi()
    {
        if (currentApi is not { } api)
            return;

        translatorApi[api.Key] = TranslatorKey.Password.Trim();
        if (api.Extra is { } extra)
            translatorApi[extra] = ApiExtra.Text.Trim();
    }

    private async void LoadModelsClicked(object sender, RoutedEventArgs args)
    {
        if (currentAi is not { } profile)
            return;

        if (ModelPicker.Visibility == Visibility.Visible)
        {
            ModelPicker.Close();
            ModelPicker.Visibility = Visibility.Collapsed;
            return;
        }

        var connection = ReadCurrentAi();
        if (!profile.TryGetBase(connection, out _))
        {
            AiHint.Text = "Check the API URL. A local model requires a loopback address on this PC.";
            return;
        }
        if (profile.RequiresKey && connection.Key.Length == 0)
        {
            AiHint.Text = "Enter an API key to load models.";
            return;
        }

        CancelModelList();
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        modelRequest = request;
        LoadModels.IsEnabled = false;
        ModelPicker.Visibility = Visibility.Collapsed;
        ModelsProgress.Visibility = Visibility.Visible;
        AiHint.Text = "Loading models…";
        try
        {
            var models = await AiTranslationClient.ListModelsAsync(profile, connection, request.Token);
            if (modelRequest != request)
                return;

            ModelPicker.SetItems(models.Select(model => new PickerOption(model, model)).ToArray());
            ModelPicker.SelectedItem = null;
            ModelPicker.Visibility = models.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (models.Length > 0)
                ModelPicker.Open();
            AiHint.Text = models.Length > 0 ? "Select a text model from the list." : "The server returned no models.";
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException
            or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (modelRequest == request)
                AiHint.Text = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                    ? "The provider rejected the API key or denied access."
                    : "Couldn't load models. Check the API URL, key, or local server.";
        }
        finally
        {
            if (modelRequest == request)
            {
                modelRequest = null;
                ModelsProgress.Visibility = Visibility.Collapsed;
                LoadModels.IsEnabled = true;
            }
            request.Dispose();
        }
    }

    private void ModelChanged(object? sender, EventArgs args)
    {
        if (ModelPicker.SelectedItem?.Value is { } model)
        {
            bool moveFocus = ModelPicker.IsKeyboardFocusWithin;
            AiModel.Text = model;
            ModelPicker.Close();
            ModelPicker.Visibility = Visibility.Collapsed;
            if (moveFocus)
                AiModel.Focus();
        }
    }

    private void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (closeInProgress)
            return;

        HideDiscardPrompt();
        ReadCurrentAi();
        ReadCurrentApi();
        foreach (var profile in AiProfile.All)
        {
            if (!connections.TryGetValue(profile.Id, out var connection) || !connection.Enabled)
                continue;

            string? problem = !profile.TryGetBase(connection, out _) ? "check the API URL."
                : profile.RequiresKey && connection.Key.Length == 0 ? "enter an API key."
                : connection.Model.Length == 0 ? "enter a model ID."
                : null;
            if (problem is not null)
            {
                Tabs.SelectedIndex = 1;
                AiPicker.SelectedItem = profile;
                AiHint.Text = $"{profile.Name}: {problem}";
                return;
            }
        }

        if (translatorApi.TryGetValue("RTF_LIBRETRANSLATE_URL", out string? libre) &&
            libre.Length > 0 && TranslationService.LibreTranslateEndpoint(libre) is null)
        {
            Tabs.SelectedIndex = 2;
            ApiPicker.SelectedItem = Apis[^1];
            ShowFeedback("LibreTranslate requires HTTPS or a local HTTP URL.", error: true);
            return;
        }

        try { store.Save(ReadDraft()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowFeedback("Couldn't save settings. Check access to your Windows profile.", error: true);
            return;
        }

        savedSuccessfully = true;
        ShowFeedback("✓  Settings saved", error: false);
        BeginClose(550);
    }

    private TranslationSettings ReadDraft() => new()
    {
        Mode = (TranslationMode)ModePicker.SelectedValue,
        Target = TargetPicker.SelectedItem?.Value ?? "",
        Instructions = StyleInstructions.Text.Trim(),
        TranslateOnOpen = RequestPicker.SelectedValue is true,
        GoogleLive = GoogleLive.IsChecked == true,
        GoogleWeb = UseGoogle.IsChecked == true,
        MyMemory = UseMemory.IsChecked == true,
        Ai = new(connections),
        TranslatorApi = new(translatorApi)
    };

    private bool HasUnsavedChanges()
    {
        ReadCurrentAi();
        ReadCurrentApi();
        var draft = ReadDraft();
        var saved = store.Current;
        if (draft.Mode != (Enum.IsDefined(saved.Mode) ? saved.Mode : Modes[0].Value) || draft.Target != saved.Target ||
            draft.Instructions != saved.Instructions.Trim() || draft.GoogleWeb != saved.GoogleWeb || draft.MyMemory != saved.MyMemory ||
            draft.TranslateOnOpen != saved.TranslateOnOpen || draft.GoogleLive != saved.GoogleLive)
            return true;

        // Visiting a provider can add an entry with the same effective defaults.
        foreach (var profile in AiProfile.All)
        {
            var original = saved.Ai.GetValueOrDefault(profile.Id) ?? profile.DefaultConnection;
            var updated = draft.Ai.GetValueOrDefault(profile.Id) ?? profile.DefaultConnection;
            if (original.Enabled != updated.Enabled || original.Model.Trim() != updated.Model.Trim() ||
                original.Key.Trim() != updated.Key.Trim() ||
                (profile.EditableAddress && original.Address.Trim() != updated.Address.Trim()))
                return true;
        }

        return draft.TranslatorApi.Any(entry => entry.Value.Trim() != saved.TranslatorApi.GetValueOrDefault(entry.Key, "").Trim());
    }

    private Point? AnchorOnScreen() => settingsAnchor is { IsVisible: true } element &&
        PresentationSource.FromVisual(element) is not null
            ? element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)) : null;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var location = AnchorOnScreen();
        var point = location is { } anchor ? new Native.Point((int)anchor.X, (int)anchor.Y)
            : Native.GetCursorPos(out var cursor) ? cursor : default;
        var (work, scale) = OverlayWindow.PlacementArea(point);
        Width = Math.Min(Width, Math.Floor(work.Width / scale));
        Height = Math.Min(Height, Math.Floor(work.Height / scale));
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Height * scale);
        int left = work.Left + (work.Width - width) / 2;
        int top = work.Top + (work.Height - height) / 2;
        if (location is { } gear)
        {
            // Place the final header around the gear whenever the monitor has room below it.
            left = Math.Clamp((int)Math.Round(gear.X - width + 54 * scale), work.Left, Math.Max(work.Left, work.Right - width));
            top = Math.Clamp((int)Math.Round(gear.Y - 51 * scale), work.Top, Math.Max(work.Top, work.Bottom - height));
        }
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, 0, left, top, 0, 0, Native.MoveWithoutActivation);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (closeInProgress || !silhouette.Rect.IsEmpty)
            return;

        silhouette.Rect = animateOpening ? SeedBounds() : WindowBounds();
        if (animateOpening)
            AnimateWindow(opening: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || closeApproved || !IsLoaded || Dispatcher.HasShutdownStarted)
            return;

        e.Cancel = true;
        if (closeInProgress)
            return;

        if (HasUnsavedChanges())
        {
            if (DiscardActions.Visibility != Visibility.Visible)
            {
                focusBeforeDiscardPrompt = Keyboard.FocusedElement;
                Feedback.Visibility = Visibility.Collapsed;
                DiscardPrompt.Visibility = DiscardActions.Visibility = Visibility.Visible;
                KeepEditingButton.Focus();
            }
            return;
        }

        if (SystemParameters.ClientAreaAnimation)
            BeginClose();
        else
            e.Cancel = false;
    }

    private void HideDiscardPrompt(bool restoreFocus = false)
    {
        DiscardPrompt.Visibility = DiscardActions.Visibility = Visibility.Collapsed;
        Feedback.Visibility = Visibility.Visible;
        if (restoreFocus && focusBeforeDiscardPrompt is { } focused)
            Keyboard.Focus(focused);
        focusBeforeDiscardPrompt = null;
    }

    private void ShowFeedback(string message, bool error)
    {
        Feedback.Foreground = (Brush)FindResource(error ? "WarningInk" : "Bone");
        Feedback.Text = message;
    }

    private void BeginClose(int delay = 0)
    {
        closeInProgress = true;
        Chrome.IsHitTestVisible = false;
        TargetPicker.Close();
        ModelPicker.Close();
        CancelModelList();
        AnimateWindow(opening: false, delay);
    }

    private Rect WindowBounds() => new(.5, .5, Math.Max(1, Chrome.ActualWidth - 1), Math.Max(1, Chrome.ActualHeight - 1));

    private Rect SeedBounds()
    {
        const double radius = 11;
        Point center = AnchorOnScreen() is { } screen
            ? PointFromScreen(screen) - new Vector(Chrome.Margin.Left, Chrome.Margin.Top)
            : new Point(Chrome.ActualWidth - 37, SettingsHeader.ActualHeight / 2);
        // The panel can move while settings are open. Keep the return inside this window's surface.
        double x = Math.Clamp(center.X, radius, Math.Max(radius, Chrome.ActualWidth - radius));
        double y = Math.Clamp(center.Y, radius, Math.Max(radius, Chrome.ActualHeight - radius));
        return new Rect(x - radius, y - radius, radius * 2, radius * 2);
    }

    private void AnimateWindow(bool opening, int delay = 0)
    {
        var navigation = (FrameworkElement)Tabs.Template.FindName("TabTrack", Tabs);
        var content = (FrameworkElement)Tabs.Template.FindName("TabScroll", Tabs);
        UIElement[] layers = [Chrome, ShellShadow, SettingsHeader, navigation, content, SettingsFooter];
        var initialOpacity = layers.Select(layer => (Layer: layer, layer.Opacity)).ToArray();
        Rect full = WindowBounds();
        Rect seed = SeedBounds();
        Rect start = silhouette.Rect.IsEmpty ? seed : silhouette.Rect;
        double headerHeight = Math.Min(full.Height, SettingsHeader.ActualHeight);
        var header = new Rect(full.X, full.Y, full.Width, headerHeight);

        windowMotion?.Remove(this);
        silhouette.Rect = start; // A close can interrupt any stage of the reveal.
        foreach (var (layer, opacity) in initialOpacity)
            layer.Opacity = opacity;

        var sequence = new OverlayStoryboard { BeginTime = TimeSpan.FromMilliseconds(delay) };
        windowMotion = sequence;
        if (opening)
        {
            sequence.Reveal(Regions, (0, seed), (38, seed), (133, header), (263, full));
            sequence.Fade(Chrome, (0, 0), (19, .55), (77, 1));
            sequence.Fade(SettingsHeader, (0, 0), (81, 0), (157, 1));
            sequence.Fade(navigation, (0, 0), (145, 0), (218, 1));
            sequence.Fade(content, (0, 0), (177, 0), (303, 1));
            sequence.Fade(SettingsFooter, (0, 0), (229, 0), (320, 1));
            sequence.Fade(ShellShadow, (0, 0), (263, 0), (320, 1));
            sequence.Fade(EdgeHighlight, (0, 0), (108, 0), (163, .55), (260, 0));
            sequence.Animate(EdgeHighlight, new PropertyPath("(0).(1)", RenderTransformProperty, TranslateTransform.XProperty),
                (0, -68), (107, -68), (260, 197));
        }
        else if (SystemParameters.ClientAreaAnimation)
        {
            var strip = new Rect(start.X, start.Y, start.Width, Math.Min(start.Height, headerHeight));
            int foldEnd = start.Height > strip.Height ? 134 : 59;
            int end = foldEnd + 87;
            sequence.Reveal(Regions, (0, start), (42, start), (foldEnd, strip), (end, seed));
            sequence.Fade(content, (0, content.Opacity), (63, 0));
            sequence.Fade(navigation, (0, navigation.Opacity), (72, 0));
            sequence.Fade(SettingsFooter, (0, SettingsFooter.Opacity), (52, 0));
            sequence.Fade(ShellShadow, (0, ShellShadow.Opacity), (41, 0));
            sequence.Fade(SettingsHeader, (0, SettingsHeader.Opacity), (foldEnd, SettingsHeader.Opacity), (end - 20, 0));
            sequence.Fade(Chrome, (0, Chrome.Opacity), (foldEnd, Chrome.Opacity), (end, 0));
        }
        else
            sequence.Fade(Chrome, (0, Chrome.Opacity), (1, Chrome.Opacity));

        sequence.Completed += (_, _) =>
        {
            if (windowMotion != sequence)
                return;

            windowMotion = null;
            if (opening)
            {
                silhouette.Rect = WindowBounds();
                foreach (var layer in layers)
                    layer.Opacity = 1;
            }
            else
                Chrome.Opacity = 0;

            sequence.Remove(this);
            if (!opening)
            {
                closeApproved = true;
                Close();
            }
        };
        sequence.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private void CancelModelList()
    {
        var request = modelRequest;
        modelRequest = null;
        request?.Cancel();
        ModelsProgress.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelModelList();
        windowMotion?.Remove(this);
        base.OnClosed(e);
        if (savedSuccessfully)
            SettingsSaved?.Invoke();
    }

    private void DragHeader(object sender, MouseButtonEventArgs e) => DragMove();
    private void CancelClicked(object sender, RoutedEventArgs e) => Close();

    private void KeepEditingClicked(object sender, RoutedEventArgs e) => HideDiscardPrompt(restoreFocus: true);

    private void DiscardClicked(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            BeginClose();
        }
        else
        {
            closeApproved = true;
            Close();
        }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (closeInProgress)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape &&
            !TargetPicker.IsDropDownOpen && !ModelPicker.IsDropDownOpen &&
            !new[] { ModePicker, AiPicker, ApiPicker, RequestPicker }.Any(picker => picker.IsDropDownOpen))
        {
            e.Handled = true;
            if (DiscardActions.Visibility == Visibility.Visible)
                HideDiscardPrompt(restoreFocus: true);
            else
                Close();
        }
    }
}
