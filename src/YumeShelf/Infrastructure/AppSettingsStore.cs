using System.Text.Json;
using System.IO;

namespace YumeShelf.Infrastructure;

public sealed class AppSettingsStore
{
    private readonly string _settingsPath;

    public AppSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YumeShelf", "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
            return (settings ?? new AppSettings()).Normalize();
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _settingsPath, true);
    }

}

public sealed record AppSettings
{
    public string? BackgroundImagePath { get; init; }
    public double BackgroundOpacity { get; init; } = 0.6;
    public double BackgroundBlur { get; init; }
    public double PageTransparency { get; init; } = 0.18;
    public bool SimpleLayout { get; init; }
    public string ColorPalette { get; init; } = "樱粉色";
    public bool NightMode { get; init; }
    public string AiApiBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string AiModel { get; init; } = "gpt-4o-mini";
    public string AiApiKeyProtected { get; init; } = string.Empty;
    public int AiTimeoutSeconds { get; init; } = 30;

    public AppSettings Normalize() => this with
    {
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 0.6,
        BackgroundBlur = double.IsFinite(BackgroundBlur) ? Math.Clamp(BackgroundBlur, 0, 30) : 0,
        PageTransparency = double.IsFinite(PageTransparency) ? Math.Clamp(PageTransparency, 0, 1) : 0.18
        ,ColorPalette = ColorPalette is "樱粉色" or "浅蓝色" or "淡黄色" or "跟随壁纸配色" ? ColorPalette : "樱粉色"
        ,AiApiBaseUrl = string.IsNullOrWhiteSpace(AiApiBaseUrl) ? "https://api.openai.com/v1" : AiApiBaseUrl.Trim()
        ,AiModel = string.IsNullOrWhiteSpace(AiModel) ? "gpt-4o-mini" : AiModel.Trim()
        ,AiTimeoutSeconds = Math.Clamp(AiTimeoutSeconds, 5, 120)
    };
}
