using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rtf;

public partial class TranslationPanel
{
    private readonly DispatcherTimer editDebounce = new() { Interval = TimeSpan.FromMilliseconds(480) };
    private readonly DispatcherTimer copyFeedback = new() { Interval = TimeSpan.FromMilliseconds(1250) };
    private readonly SemaphoreSlim requestGate = new(1);
    private TranslationService translator;
    private CancellationTokenSource? translationRequest;
    private bool translateOnOpen;
    private TranslationRoute translationRoute;
    private bool loadingSource = true;
    private string? targetOverride;
    private string? sourceLanguage;

    private void InitializeEditor()
    {
        TargetLanguage.Opening += (_, _) => ActivateContent();
        TargetLanguage.SelectionChanged += (_, _) => ChooseLanguage();
        SetLanguageOptions();
        editDebounce.Tick += (_, _) =>
        {
            editDebounce.Stop();
            sourceLanguage = TranslationLanguages.PredictSource(Original.Text) ?? sourceLanguage;
            UpdateTargetLabel();
            if (translator.CanTranslateLive(translationRoute))
                StartTranslation(TranslationRoute.Google);
        };
        copyFeedback.Tick += (_, _) =>
        {
            copyFeedback.Stop();
            CopyOriginal.Tag = CopyTranslation.Tag = false;
        };
        LoadSource(string.Empty);
    }

    internal void UseTranslator(TranslationService updated, bool automatic)
    {
        translator = updated;
        translateOnOpen = automatic;
        SetLanguageOptions();
        CancelTranslation();
        ResetResult();
        UpdateTargetLabel();
    }

    private void LoadSource(string text)
    {
        loadingSource = true;
        SetOriginalPreview(false);
        Original.IsUndoEnabled = false;
        Original.Text = text;
        Original.IsUndoEnabled = true;
        Original.CaretIndex = 0;
        Original.ScrollToHome();
        sourceLanguage = TranslationLanguages.PredictSource(text);
        loadingSource = false;
        ResetResult();
        UpdateTargetLabel();
    }

    private void OriginalChanged(object sender, TextChangedEventArgs args)
    {
        if (loadingSource || IsClosing)
            return;

        CancelTranslation();
        ResetResult();
        if (string.IsNullOrWhiteSpace(Original.Text))
        {
            sourceLanguage = null;
            UpdateTargetLabel();
        }
        else
            editDebounce.Start();
    }

    private void ResetResult()
    {
        Translation.SetContent(string.Empty);
        TranslationProvider.Text = string.Empty;
        TranslationStatus.Text = string.IsNullOrWhiteSpace(Original.Text) ? "The translation will appear here." : "";
        TranslationStatus.Foreground = (Brush)FindResource("SecondaryInk");
        TranslationProgress.Visibility = Visibility.Collapsed;
        TranslationRetry.Visibility = Visibility.Collapsed;
        CopyTranslation.IsEnabled = false;
        UpdateEditorActions();
    }

    private void UpdateEditorActions()
    {
        TranslateButton.IsEnabled = !IsClosing && translationRequest is null && !string.IsNullOrWhiteSpace(Original.Text);
        CopyOriginal.IsEnabled = PreviewOriginal.IsEnabled = ClearOriginal.IsEnabled = Original.Text.Length != 0;
    }

    private void StartTranslation(TranslationRoute? routeOverride = null)
    {
        string text = Original.Text;
        if (IsClosing || !IsVisible || translationRequest is not null || string.IsNullOrWhiteSpace(text))
            return;

        editDebounce.Stop();
        var request = new CancellationTokenSource();
        translationRequest = request;
        ResetResult();
        TranslationStatus.Text = "Preparing translation…";
        TranslationProgress.Visibility = Visibility.Visible;
        _ = TranslateCurrentAsync(text, targetOverride, routeOverride ?? translationRoute, request);
    }

    private async Task TranslateCurrentAsync(string text, string? target, TranslationRoute route, CancellationTokenSource request)
    {
        var progress = new Progress<string>(provider =>
        {
            if (translationRequest == request)
                TranslationStatus.Text = $"Translating with {provider}…";
        });
        try
        {
            await requestGate.WaitAsync(request.Token);
            TranslationResult result;
            try
            {
                result = await translator.TranslateAsync(text, progress, request.Token, target, route);
            }
            finally { requestGate.Release(); }

            if (translationRequest != request)
                return;
            if (result.Text is { } translated)
            {
                Translation.SetContent(translated);
                CopyTranslation.IsEnabled = true;
                TranslationStatus.Text = string.Empty;
                sourceLanguage = result.Source ?? sourceLanguage;
                if (result.Source is null && targetOverride is null)
                    SetTargetLabel(result.Target!);
                else
                    UpdateTargetLabel();
                TranslationProvider.Text = result.Provider;
                return;
            }
            TranslationStatus.Text = result switch
            {
                { NeedsConfiguration: true } when route == TranslationRoute.Google =>
                    "Google Translate is unavailable. Check your translation providers and priority.",
                { NeedsConfiguration: true } when route == TranslationRoute.Ai =>
                    "Enable and configure an AI provider for translation.",
                { NeedsConfiguration: true } => "Enable a provider in Settings.",
                { ConnectionFailed: true } => "Couldn't connect to the translation provider.",
                _ => "The provider returned no translation."
            };
            TranslationStatus.Foreground = (Brush)FindResource("WarningInk");
            TranslationRetry.Visibility = result.NeedsConfiguration ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (translationRequest == request)
            {
                translationRequest = null;
                TranslationProgress.Visibility = Visibility.Collapsed;
                UpdateEditorActions();
            }
            request.Dispose();
        }
    }

    private void CancelTranslation()
    {
        editDebounce.Stop();
        var request = translationRequest;
        translationRequest = null;
        request?.Cancel();
    }

    private void SwitchProviderClicked(object sender, RoutedEventArgs args)
    {
        if (IsClosing || Header.Opacity < .9)
            return;

        translationRoute = translationRoute == TranslationRoute.Google ? TranslationRoute.Ai : TranslationRoute.Google;
        ProviderSwitch.Content = translationRoute == TranslationRoute.Google ? "G" : "AI";
        ProviderSwitch.ToolTip = translationRoute == TranslationRoute.Google
            ? "Google Translate is active. Switch to AI."
            : "AI is active. Switch to Google Translate.";
        CancelTranslation();
        StartTranslation();
    }

    private void TranslateClicked(object sender, RoutedEventArgs args) => StartTranslation();
    private void RetryTranslationClicked(object sender, RoutedEventArgs args) => StartTranslation();

    private void UpdateTargetLabel() =>
        SetTargetLabel(targetOverride ?? TranslationLanguages.TargetFor(sourceLanguage, translator.FallbackTarget));

    private void SetTargetLabel(string code)
    {
        string name = TranslationLanguages.Label(code);
        TargetLanguage.DisplayText = name;
        TargetLanguage.ToolTip = targetOverride is null ? $"Automatic · {name}" : name;
    }

    private void SetLanguageOptions()
    {
        var choices = TranslationLanguages.All.AsEnumerable();
        if (!choices.Any(choice => choice.Code == translator.FallbackTarget))
            choices = choices.Append(new TranslationLanguages.Choice(translator.FallbackTarget, TranslationLanguages.Label(translator.FallbackTarget)));
        TargetLanguage.SetItems(choices.Select(choice => new PickerOption(choice.Code, choice.Label)).ToArray());
        TargetLanguage.SelectValue(targetOverride ?? "");
    }

    private void ChooseLanguage()
    {
        string? code = TargetLanguage.SelectedItem?.Value;
        if (code is null)
            return;

        string? target = code.Length == 0 ? null : code;
        if (target == targetOverride)
            return;

        targetOverride = target;
        CancelTranslation();
        ResetResult();
        UpdateTargetLabel();
        if (translator.CanTranslateLive(translationRoute) && !string.IsNullOrWhiteSpace(Original.Text))
            editDebounce.Start();
    }

    private void PreviewOriginalClicked(object sender, RoutedEventArgs args)
    {
        bool preview = Original.Visibility == Visibility.Visible;
        SetOriginalPreview(preview);
        ActivateContent();
        if (preview)
            OriginalPreview.Focus();
        else
            Original.Focus();
    }

    private void SetOriginalPreview(bool preview)
    {
        OriginalPreview.SetContent(preview ? Original.Text : string.Empty, conservative: true);
        OriginalPreview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        Original.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewOriginal.Content = preview ? "✎" : "MD";
        PreviewOriginal.ToolTip = preview ? "Edit original" : "Preview original as Markdown";
    }

    private void ClearOriginalClicked(object sender, RoutedEventArgs args)
    {
        if (IsClosing)
            return;

        SetOriginalPreview(false);
        Original.Clear();
        ActivateContent();
        Original.Focus();
    }


    private void CopyOriginalClicked(object sender, RoutedEventArgs args) => CopyText(Original.Text, CopyOriginal);
    private void CopyTranslationClicked(object sender, RoutedEventArgs args) => CopyText(Translation.RawText, CopyTranslation);

    private void CopyText(string text, Button button)
    {
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
            button.Tag = true;
            copyFeedback.Stop();
            copyFeedback.Start();
        }
        catch (ExternalException)
        {
            TranslationProvider.Text = "The clipboard is busy";
        }
    }
}
