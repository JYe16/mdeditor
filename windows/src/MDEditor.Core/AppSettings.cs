using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDEditor.Core;

/// <summary>Contents of %LOCALAPPDATA%\MDEditor\settings.json. A missing or corrupt file yields defaults.</summary>
public sealed class AppSettings
{
    public const double MinZoom = 0.6;
    public const double MaxZoom = 3.0;

    public string ViewMode { get; set; } = "split";
    public double Zoom { get; set; } = 1.0;
    public bool AutoSave { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static double ClampZoom(double value) => Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 1);

    public static AppSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
            settings.Zoom = double.IsFinite(settings.Zoom) ? ClampZoom(settings.Zoom) : 1.0;
            if (settings.ViewMode is not ("editor" or "split" or "preview")) settings.ViewMode = "split";
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafeFile.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a window size is not worth bothering the user about.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
