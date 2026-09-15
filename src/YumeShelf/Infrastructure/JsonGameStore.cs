using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YumeShelf.Domain;

namespace YumeShelf.Infrastructure;

public sealed class JsonGameStore
{
    private const long MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _databasePath;
    private string? _version;
    private bool _loaded;
    public string? ReadError { get; private set; }
    public string DataDirectory => Path.GetDirectoryName(_databasePath)!;
    public bool IsReadOnly => ReadError is not null;

    public JsonGameStore(string? databasePath = null)
        => _databasePath = Path.GetFullPath(databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YumeShelf", "library.json"));

    public IReadOnlyList<Game> Load(bool throwOnError = false)
    {
        _loaded = true;
        ReadError = null;
        try
        {
            var bytes = ReadBytes();
            _version = Fingerprint(bytes);
            if (bytes is null) return [];
            var games = Parse(bytes, out var invalid);
            if (invalid > 0)
            {
                ReadError = $"游戏库有 {invalid} 条无效或重复记录，已保留可读取项目并暂停写入。";
                AppLog.Write("library.invalid-records");
                if (throwOnError) throw new InvalidDataException(ReadError);
            }
            return games;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ReadError ??= "游戏库无法完整读取，原文件已保留并暂停写入。请检查数据文件或从备份恢复。";
            AppLog.Write("library.load-failed", ex);
            if (throwOnError) throw;
            return [];
        }
    }

    public void Save(IEnumerable<Game> games)
    {
        if (!_loaded) Load();
        if (IsReadOnly) throw new InvalidDataException(ReadError);
        Write(games, recover: false);
    }

    // Only after explicit confirmation; preserve the original file for recovery.
    public void Recover(IEnumerable<Game> games) => Write(games, recover: true);

    private void Write(IEnumerable<Game> games, bool recover)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(games.ToArray(), JsonOptions);
        if (json.Length > MaxBytes) throw new InvalidDataException("游戏库超过允许的文件大小，请精简过长资料。");
        _ = Parse(json, out var invalid);
        if (invalid > 0) throw new InvalidDataException("游戏条目包含无效字段或重复 ID，无法保存。");
        Directory.CreateDirectory(DataDirectory);
        using var writeLock = new FileStream(_databasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadBytes();
        if (!recover && Fingerprint(current) != _version) throw new LibraryConflictException();
        if (recover && current is not null)
            File.Copy(_databasePath, _databasePath + $".recovery-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var temporary = _databasePath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(json);
                file.Flush(flushToDisk: true);
            }
            if (current is not null && !recover) File.Copy(_databasePath, _databasePath + ".bak", true);
            File.Move(temporary, _databasePath, true);
            _version = Fingerprint(json);
            _loaded = true;
            ReadError = null;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("library.temp-cleanup", ex); }
        }
    }

    private byte[]? ReadBytes()
    {
        try
        {
            using var file = new FileStream(_databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxBytes) throw new InvalidDataException("游戏库文件过大，已停止读取。");
            using var buffer = new MemoryStream();
            file.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private static List<Game> Parse(byte[] bytes, out int invalid)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a game array.");
        var games = new List<Game>();
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        invalid = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            try
            {
                if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Any(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && Guid.TryParse(p.Value.GetString(), out var id) && id != Guid.Empty))
                    throw new JsonException("Missing stable game ID.");
                var game = element.Deserialize<Game>(JsonOptions);
                if (game is null || game.Id == Guid.Empty || !Path.IsPathFullyQualified(game.ExecutablePath)
                    || !string.Equals(Path.GetExtension(game.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("Invalid game identity.");
                game.ExecutablePath = Path.GetFullPath(game.ExecutablePath);
                game.RootPath = string.IsNullOrWhiteSpace(game.RootPath) ? Path.GetDirectoryName(game.ExecutablePath)! : Path.GetFullPath(game.RootPath);
                game.WorkingDirectory = string.IsNullOrWhiteSpace(game.WorkingDirectory) ? game.RootPath : Path.GetFullPath(game.WorkingDirectory);
                game.Title = string.IsNullOrWhiteSpace(game.Title) ? Path.GetFileNameWithoutExtension(game.ExecutablePath) : game.Title;
                game.Engine ??= "未知引擎";
                game.Description ??= string.Empty;
                game.LaunchArguments ??= string.Empty;
                game.Tags = game.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
                game.TotalPlaySeconds = Math.Max(0, game.TotalPlaySeconds);
                if (ids.Contains(game.Id) || paths.Contains(game.ExecutablePath)) throw new JsonException("Duplicate game identity.");
                ids.Add(game.Id); paths.Add(game.ExecutablePath); games.Add(game);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { invalid++; }
        }
        return games;
    }
}

public sealed class LibraryConflictException : IOException
{
    public LibraryConflictException() : base("游戏库已被其他实例修改。当前修改已保留，请先处理数据冲突。") { }
}
