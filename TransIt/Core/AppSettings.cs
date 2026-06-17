using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransIt.Core;

public enum TranslationProvider { OpenAI, Google }

public class AppSettings
{
    public string OpenAiApiKey     { get; set; } = string.Empty;
    public string GoogleApiKey     { get; set; } = string.Empty;
    public TranslationProvider Provider { get; set; } = TranslationProvider.OpenAI;
    public string OpenAiModel      { get; set; } = "gpt-4o-mini";
    public string SourceLanguage   { get; set; } = "en";
    public string TargetLanguage   { get; set; } = "vi";
    public int RealtimeIntervalMs  { get; set; } = 2000;
    public double OverlayOpacity   { get; set; } = 1.0;
    public bool   RegionOverlayMode { get; set; } = true;
    public bool   ShowDebugRects    { get; set; } = false;

    [JsonIgnore]
    private static readonly string _settingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "TransIt", "settings.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts) ?? new AppSettings();
            }
        }
        catch { /* return defaults on any error */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, _jsonOpts));
        }
        catch { /* best-effort */ }
    }

    [JsonIgnore]
    public bool HasValidApiKey =>
        Provider == TranslationProvider.OpenAI
            ? !string.IsNullOrWhiteSpace(OpenAiApiKey)
            : !string.IsNullOrWhiteSpace(GoogleApiKey);
}
