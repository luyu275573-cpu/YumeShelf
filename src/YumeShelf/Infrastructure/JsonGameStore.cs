using System.Text.Json;
using System.IO;
using YumeShelf.Domain;

namespace YumeShelf.Infrastructure;

public sealed class JsonGameStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _databasePath;

    public JsonGameStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YumeShelf", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
    }

    public IReadOnlyList<Game> Load(bool throwOnError = false)
    {
        if (!File.Exists(_databasePath)) return [];
        try
        {
            var json = File.ReadAllText(_databasePath);
            return JsonSerializer.Deserialize<List<Game>>(json, JsonOptions) ?? [];
        }
        catch (JsonException) when (!throwOnError) { return []; }
        catch (IOException) when (!throwOnError) { return []; }
    }

    public void Save(IEnumerable<Game> games)
    {
        var json = JsonSerializer.Serialize(games, JsonOptions);
        var temporaryPath = _databasePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _databasePath, overwrite: true);
    }
}
