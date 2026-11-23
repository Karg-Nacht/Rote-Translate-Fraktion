using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Rtf;

internal sealed class OverlayPreferences
{
    public sealed record Layout(double PanelHeight = 242, int? CrystalX = null, int? CrystalY = null);

    private readonly string path;
    private Task pendingSave = Task.CompletedTask;

    internal OverlayPreferences(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rote Translate Fraktion", "overlay.json");
        try
        {
            if (File.Exists(this.path))
                Current = JsonSerializer.Deserialize<Layout>(File.ReadAllText(this.path)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal Layout Current { get; private set; } = new();

    internal void Save(Layout layout)
    {
        Current = layout;
        pendingSave = pendingSave.ContinueWith(_ => Write(layout), TaskScheduler.Default);
    }

    internal void Flush() => pendingSave.Wait(TimeSpan.FromMilliseconds(250));

    private void Write(Layout layout)
    {
        string draft = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(draft, JsonSerializer.Serialize(layout));
            File.Move(draft, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(draft); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
