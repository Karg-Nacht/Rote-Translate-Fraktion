using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rtf;

internal enum TranslationMode
{
    AiFirst,
    TranslatorsFirst,
    AiOnly,
    TranslatorsOnly,
    LocalAiOnly
}

internal sealed record AiConnection(bool Enabled, string Model, string Address, string Key);

internal sealed record TranslationSettings
{
    public TranslationMode Mode { get; init; }
    public string Target { get; init; } = "";
    public string Instructions { get; init; } = "";
    public bool TranslateOnOpen { get; init; }
    public bool GoogleLive { get; init; }
    public bool GoogleWeb { get; init; } = true;
    public bool MyMemory { get; init; } = true;
    public Dictionary<string, AiConnection> Ai { get; init; } = new();
    public Dictionary<string, string> TranslatorApi { get; init; } = new();

    internal string? GetSetting(string name)
    {
        if (name == "RTF_TARGET_LANGUAGE" && !string.IsNullOrWhiteSpace(Target))
            return Target;

        return TranslatorApi.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : Environment.GetEnvironmentVariable(name);
    }
}

internal sealed class TranslationSettingsStore
{
    private readonly string path;

    internal TranslationSettingsStore(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rote Translate Fraktion", "translation-settings.dat");
        try
        {
            if (File.Exists(this.path))
            {
                byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(this.path), null, DataProtectionScope.CurrentUser);
                try { Current = JsonSerializer.Deserialize<TranslationSettings>(data) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(data); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or CryptographicException or JsonException)
        {
            LoadFailed = true;
        }
    }

    internal TranslationSettings Current { get; private set; } = new();
    internal bool LoadFailed { get; private set; }

    internal void Save(TranslationSettings settings)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            byte[] encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            string draft = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllBytes(draft, encrypted);
                File.Move(draft, path, overwrite: true);
            }
            finally
            {
                File.Delete(draft);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        Current = settings;
        LoadFailed = false;
    }
}
