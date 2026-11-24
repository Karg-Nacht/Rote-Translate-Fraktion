using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rtf;

internal sealed record TranslationResult(string? Text, string? Provider, string? Target,
    bool ConnectionFailed = false, bool NeedsConfiguration = false, string? Source = null,
    bool RateLimited = false);

internal enum TranslationRoute
{
    Configured,
    Google,
    Ai
}

internal sealed class TranslationService
{
    private const string DefaultTarget = "ru";
    private static readonly HttpClient SharedClient = CreateClient(useProxy: true);
    private static readonly HttpClient LoopbackClient = CreateClient(useProxy: false);
    private static readonly HttpClient CatalogClient = CreateClient(useProxy: true, bufferSize: 12_000_000);

    private readonly HttpClient client;
    private readonly HttpClient localClient;
    private readonly Provider[] providers;
    private readonly Provider[] aiProviders;
    private readonly Provider[] googleProviders;
    private readonly bool googleLiveEnabled;
    private readonly string? preferredTarget;
    private sealed record Reply(string Text, string? Source = null, string? Target = null);
    private delegate Task<Reply?> TranslateRequest(string text, string target, CancellationToken cancellation);
    private sealed record Provider(string Name, TranslateRequest Translate, int TimeoutSeconds = 8, bool Google = false);
    internal string FallbackTarget => preferredTarget ?? DefaultTarget;
    internal bool StartsWithTranslator => providers.Length > 0 && !aiProviders.Contains(providers[0]);
    internal bool CanTranslateLive(TranslationRoute route) => googleLiveEnabled && route switch
    {
        TranslationRoute.Configured => providers.Any(provider => provider.Google),
        TranslationRoute.Google => googleProviders.Length != 0,
        _ => false
    };

    internal TranslationService(TranslationSettings settings)
        : this(SharedClient, settings.GetSetting, settings, LoopbackClient) { }

    internal TranslationService(HttpClient client, Func<string, string?> setting,
        TranslationSettings? settings = null, HttpClient? localClient = null)
    {
        this.client = client;
        this.localClient = localClient ?? client;
        preferredTarget = NormalizeTarget(setting("RTF_TARGET_LANGUAGE"));
        settings ??= new TranslationSettings();

        aiProviders = ConfigureAi(settings).ToArray();
        var classic = ConfigureClassic(setting, settings.GoogleWeb, settings.MyMemory).ToArray();
        providers = settings.Mode switch
        {
            TranslationMode.AiOnly or TranslationMode.LocalAiOnly => aiProviders,
            TranslationMode.TranslatorsOnly => classic,
            TranslationMode.TranslatorsFirst => classic.Concat(aiProviders).ToArray(),
            _ => aiProviders.Concat(classic).ToArray()
        };
        googleProviders = settings.Mode == TranslationMode.LocalAiOnly
            ? [] : classic.Where(provider => provider.Google).ToArray();
        googleLiveEnabled = settings.GoogleLive;
    }

    internal static HttpClient CatalogClientFor(Uri endpoint) => endpoint.IsLoopback ? LoopbackClient : CatalogClient;

    private static HttpClient CreateClient(bool useProxy, int bufferSize = 1_000_000) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = useProxy,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = bufferSize
    };


    internal async Task<TranslationResult> TranslateAsync(string text, IProgress<string>? progress,
        CancellationToken cancellation, string? targetOverride = null, TranslationRoute route = TranslationRoute.Configured)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(null, null, null);

        var activeProviders = route switch
        {
            TranslationRoute.Google => googleProviders,
            TranslationRoute.Ai => aiProviders,
            _ => providers
        };
        if (activeProviders.Length == 0)
            return new(null, null, null, NeedsConfiguration: true);

        bool connectionFailed = false;
        bool serverResponded = false;
        bool rateLimited = false;
        foreach (var provider in activeProviders)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(provider.Name);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds + Math.Min(17, text.Length / 400)));

            try
            {
                string target = targetOverride ?? "auto";
                var translated = await provider.Translate(text, target, deadline.Token).ConfigureAwait(false);
                if (translated is null)
                    continue;

                serverResponded = true;
                if (translated.Target is { } aiTarget)
                    target = aiTarget;

                cancellation.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(translated?.Text))
                    return new(translated.Text, provider.Name, target, Source: translated.Source);
            }
            catch (HttpRequestException error) when (error.StatusCode is null)
            {
                connectionFailed = true;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                connectionFailed = true;
            }
            catch (HttpRequestException error) when (
                route == TranslationRoute.Google && error.StatusCode == HttpStatusCode.TooManyRequests)
            {
                serverResponded = true;
                rateLimited = true;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException
                or FormatException or IOException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                serverResponded = true;
            }
        }

        cancellation.ThrowIfCancellationRequested();
        return new(null, null, null, connectionFailed && !serverResponded, RateLimited: rateLimited);
    }

    private IEnumerable<Provider> ConfigureAi(TranslationSettings settings)
    {
        foreach (var profile in AiProfile.All)
        {
            if (!settings.Ai.TryGetValue(profile.Id, out var connection) || !connection.Enabled ||
                string.IsNullOrWhiteSpace(connection.Model) ||
                (profile.RequiresKey && string.IsNullOrWhiteSpace(connection.Key)) ||
                !profile.TryGetBase(connection, out var endpoint) || endpoint is null ||
                (settings.Mode == TranslationMode.LocalAiOnly && (!profile.Local || !endpoint.IsLoopback)))
                continue;

            yield return new(profile.Name,
                (text, target, ct) => TranslateAi(endpoint, connection, settings.Instructions, text, target, ct),
                endpoint.IsLoopback ? 85 : 40);
        }
    }

    private Task<Reply?> TranslateAi(Uri endpoint, AiConnection connection, string instructions, string text,
        string target, CancellationToken cancellation)
    {
        string requestedTarget = target;
        return TranslateChunks(text, target, 6500, async (part, _, ct) =>
        {
            var answer = await AiTranslationClient.TranslateAsync(endpoint.IsLoopback ? localClient : client,
                endpoint, connection, part, requestedTarget, instructions, ct, FallbackTarget).ConfigureAwait(false);
            requestedTarget = answer.Target;
            return new(answer.Text, answer.Source, answer.Target);
        }, cancellation);
    }

    private IEnumerable<Provider> ConfigureClassic(Func<string, string?> setting, bool googleWeb, bool myMemory)
    {
        // An explicitly configured local instance gets first chance to keep text on-device.
        var libreKey = ReadSetting(setting, "RTF_LIBRETRANSLATE_KEY");
        if (LibreTranslateEndpoint(ReadSetting(setting, "RTF_LIBRETRANSLATE_URL")) is { } libre)
            yield return Classic("LibreTranslate", (text, target, ct) => TranslateLibre(libre, libreKey, text, target, ct));

        var azureKey = ReadSetting(setting, "RTF_AZURE_TRANSLATOR_KEY");
        if (azureKey is not null)
        {
            var region = ReadSetting(setting, "RTF_AZURE_TRANSLATOR_REGION");
            yield return Classic("Microsoft Translator", (text, target, ct) => TranslateAzure(azureKey, region, text, target, ct));
        }

        var deepLKey = ReadSetting(setting, "RTF_DEEPL_KEY");
        if (deepLKey is not null)
            yield return Classic("DeepL", (text, target, ct) => TranslateDeepL(deepLKey, text, target, ct));

        var googleKey = ReadSetting(setting, "RTF_GOOGLE_CLOUD_KEY");
        if (googleKey is not null)
            yield return Classic("Google Cloud", (text, target, ct) => TranslateGoogleCloud(googleKey, text, target, ct));

        var yandexKey = ReadSetting(setting, "RTF_YANDEX_TRANSLATOR_KEY");
        if (yandexKey is not null)
            yield return Classic("Yandex Translate", (text, target, ct) =>
                TranslateChunks(text, target, 9500, (part, language, token) =>
                    TranslateYandex(yandexKey, part, language, token), ct));

        if (googleWeb)
            yield return Classic("Google Translate", (text, target, ct) => TranslateChunks(text, target, 1200, TranslateGoogleWeb, ct), google: true);
        if (myMemory)
            yield return Classic("MyMemory", (text, target, ct) => text.Length <= 5000
                ? TranslateChunks(text, target, 500, TranslateMemory, ct)
                : Task.FromResult<Reply?>(null));
    }

    private Provider Classic(string name, TranslateRequest translate, bool google = false) =>
        new(name, async (text, target, cancellation) =>
        {
            bool automatic = target == "auto";
            string initial = automatic ? TranslationLanguages.TargetFor(TranslationLanguages.PredictSource(text), FallbackTarget) : target;
            var result = await translate(text, initial, cancellation).ConfigureAwait(false);
            if (result is null)
                return null;

            string detectedTarget = automatic && !string.IsNullOrWhiteSpace(result.Source)
                ? TranslationLanguages.TargetFor(result.Source, FallbackTarget) : initial;
            if (detectedTarget != initial)
                result = await translate(text, detectedTarget, cancellation).ConfigureAwait(false);
            return result is null ? null : result with { Target = detectedTarget };
        }, Google: google);

    internal static Uri? LibreTranslateEndpoint(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return null;

        string value = endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri(value.EndsWith("/translate", StringComparison.Ordinal) ? value : value + "/translate");
    }

    private async Task<Reply?> TranslateGoogleWeb(string text, string target, CancellationToken cancellation)
    {
        // Public web endpoint. Its format and availability can change independently of Cloud Translation.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://translate.googleapis.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={Escape(target)}&q={Escape(text)}");
        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var first = ElementAt(document.RootElement, 0);
        var translated = ReadString(ElementAt(first, 0));
        return translated is null ? throw InvalidResponse() : new(translated, ReadString(ElementAt(first, 1)));
    }

    private async Task<Reply?> TranslateMemory(string text, string target, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.mymemory.translated.net/get?q={Escape(text)}&langpair={Escape("autodetect|" + target)}");
        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var response = document.RootElement;
        var data = Member(response, "responseData");
        var status = Member(response, "responseStatus");

        if (Member(response, "quotaFinished").ValueKind == JsonValueKind.True ||
            Member(response, "mtLangSupported").ValueKind == JsonValueKind.False)
            throw InvalidResponse();

        if (status.ToString() == "403" &&
            string.Equals(ReadString(Member(response, "responseDetails")), "PLEASE SELECT TWO DISTINCT LANGUAGES",
                StringComparison.OrdinalIgnoreCase))
            return new(text, target);

        if (status.ToString() != "200")
            throw InvalidResponse();

        string? translated = ReadString(Member(data, "translatedText"));
        return translated is null ? throw InvalidResponse() :
            new(WebUtility.HtmlDecode(translated), ReadString(Member(data, "detectedLanguage")));
    }

    private async Task<Reply?> TranslateLibre(Uri endpoint, string? key, string text, string target,
        CancellationToken cancellation)
    {
        var fields = new Dictionary<string, string>
        {
            ["q"] = text, ["source"] = "auto", ["target"] = target, ["format"] = "text"
        };
        if (key is not null)
            fields["api_key"] = key;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var root = document.RootElement;
        var translated = ReadString(Member(root, "translatedText"));
        return translated is null ? throw InvalidResponse() :
            new(translated, ReadString(Member(Member(root, "detectedLanguage"), "language")));
    }

    private async Task<Reply?> TranslateAzure(string key, string? region, string text, string target,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Escape(target)}")
        {
            Content = JsonContent.Create(new[] { new { Text = text } })
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        if (region is not null)
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);

        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var root = ElementAt(document.RootElement, 0);
        var translated = ReadString(Member(ElementAt(Member(root, "translations"), 0), "text"));
        return translated is null ? throw InvalidResponse() :
            new(translated, ReadString(Member(Member(root, "detectedLanguage"), "language")));
    }

    private async Task<Reply?> TranslateDeepL(string key, string text, string target, CancellationToken cancellation)
    {
        string host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
        string language = target.Equals("en", StringComparison.OrdinalIgnoreCase) ? "EN-US" : target.ToUpperInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate")
        {
            Content = JsonContent.Create(new { text = new[] { text }, target_lang = language })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);

        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var first = ElementAt(Member(document.RootElement, "translations"), 0);
        var translated = ReadString(Member(first, "text"));
        return translated is null ? throw InvalidResponse() :
            new(translated, ReadString(Member(first, "detected_source_language")));
    }

    private Task<Reply?> TranslateGoogleCloud(string key, string text, string target, CancellationToken cancellation) =>
        TranslateChunks(text, target, 12_000,
            (part, language, ct) => TranslateGoogleCloudPart(key, part, language, ct), cancellation);

    private async Task<Reply?> TranslateGoogleCloudPart(string key, string text, string target,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://translation.googleapis.com/language/translate/v2")
        {
            Content = JsonContent.Create(new { q = text, target, format = "text" })
        };
        request.Headers.Add("X-Goog-Api-Key", key);

        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var first = ElementAt(Member(Member(document.RootElement, "data"), "translations"), 0);
        var translated = ReadString(Member(first, "translatedText"));
        return translated is null ? throw InvalidResponse() :
            new(WebUtility.HtmlDecode(translated), ReadString(Member(first, "detectedSourceLanguage")));
    }

    private async Task<Reply?> TranslateYandex(string key, string text, string target, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://translate.api.cloud.yandex.net/translate/v2/translate")
        {
            Content = JsonContent.Create(new { texts = new[] { text }, targetLanguageCode = target, format = "PLAIN_TEXT" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", key);

        using var document = await SendJson(request, cancellation).ConfigureAwait(false);
        var first = ElementAt(Member(document.RootElement, "translations"), 0);
        var translated = ReadString(Member(first, "text"));
        return translated is null ? throw InvalidResponse() :
            new(translated, ReadString(Member(first, "detectedLanguageCode")));
    }

    private async Task<JsonDocument> SendJson(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await (request.RequestUri!.IsLoopback ? localClient : client)
            .SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellation).ConfigureAwait(false);
    }

    private static async Task<Reply?> TranslateChunks(string text, string target, int byteLimit,
        TranslateRequest translate, CancellationToken cancellation)
    {
        if (Encoding.UTF8.GetByteCount(text) <= byteLimit)
            return await translate(text, target, cancellation).ConfigureAwait(false);

        var output = new StringBuilder(text.Length);
        string? source = null;
        string? translatedTarget = null;
        int longestDetected = 0;
        for (int offset = 0; offset < text.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            int length = ChunkLength(text, offset, byteLimit);
            string part = text.Substring(offset, length);
            offset += length;

            string content = part.Trim();
            if (content.Length == 0)
            {
                output.Append(part);
                continue;
            }

            var reply = await translate(content, target, cancellation).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply?.Text))
                throw InvalidResponse();

            translatedTarget ??= reply.Target;
            if (!string.IsNullOrWhiteSpace(reply.Source) && content.Length > longestDetected)
            {
                source = reply.Source;
                longestDetected = content.Length;
            }

            int first = part.Length - part.TrimStart().Length;
            output.Append(part.AsSpan(0, first)).Append(reply.Text.Trim()).Append(part.AsSpan(first + content.Length));
        }

        return new(output.ToString(), source, translatedTarget);
    }

    private static int ChunkLength(string text, int offset, int byteLimit)
    {
        int bytes = 0;
        int length = 0;
        int boundary = 0;
        foreach (Rune rune in text.AsSpan(offset).EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > byteLimit)
                break;

            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
            if (Rune.IsWhiteSpace(rune))
                boundary = length;
        }

        // Prefer a whitespace boundary when possible, without producing tiny requests.
        return offset + length < text.Length && boundary > length / 2 ? boundary : length;
    }

    private static string? NormalizeTarget(string? value)
    {
        string? candidate = value?.Trim();
        if (candidate is null || candidate.Length is < 2 or > 16 ||
            !candidate.Split('-').All(part => part.Length is >= 2 and <= 8 && part.All(char.IsAsciiLetterOrDigit)))
            return null;

        return candidate.ToLowerInvariant();
    }

    private static string? ReadSetting(Func<string, string?> setting, string key)
    {
        var value = setting(key)?.Trim();
        return !string.IsNullOrEmpty(value) && !value.Contains('\r') && !value.Contains('\n') ? value : null;
    }

    private static JsonElement Member(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;

    private static JsonElement ElementAt(JsonElement value, int index) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index ? value[index] : default;

    private static string? ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static InvalidDataException InvalidResponse() => new("Translation service returned an unusable response.");
}

