using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YumeShelf.Common;
using YumeShelf.Domain;

namespace YumeShelf.Application.AI;

// The only shape sent to the model. Paths, executable arguments and play history never enter it.
public sealed record AiGameSummary(Guid Id, string Title, string Engine, int? ReleaseYear, string Description, string Tags)
{
    [JsonIgnore] public string Revision { get; init; } = "";
    [JsonIgnore] public string? CoverPath { get; init; }
    [JsonIgnore] public bool IsFavorite { get; init; }
    [JsonIgnore] public bool HasPlayed { get; init; }
    [JsonIgnore] public string LocalState { get; init; } = "unknown";
    public string ReleaseDate { get; init; } = "";
    public string GameType { get; init; } = "";
    public static AiGameSummary From(Game game) => new(game.Id, Limit(game.Title, 300), Limit(game.Engine, 100),
        game.ReleaseYear, Limit(game.Description, 3000), Limit(string.Join(", ", game.Tags), 500))
        { ReleaseDate = game.ReleaseDate, GameType = game.GameType, CoverPath = game.CoverPath,
            IsFavorite = game.IsFavorite, HasPlayed = game.LastPlayedAt is not null || game.TotalPlaySeconds > 0,
            LocalState = !System.IO.File.Exists(game.ExecutablePath) ? "missing" : game.LastLaunchStatus == "Running" ? "running" : "available",
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { game.Id, game.Title, game.Engine, game.ReleaseYear, game.ReleaseDate, game.GameType, game.Description, game.Tags, game.CoverPath })))) };
    private static string Limit(string value, int count) => value.Length <= count ? value : value[..count];
    public string Field(string key) => key switch
    {
        "Title" => Title, "Engine" => Engine, "ReleaseYear" => ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? "",
        "Description" => Description, "Tags" => Tags, "ReleaseDate" => ReleaseDate, "GameType" => GameType, _ => throw new InvalidOperationException("不允许修改该字段。")
    };
}

public sealed class AiFieldSuggestion(string field, string original, string proposed) : ObservableObject
{
    private bool _accepted;
    public string Field { get; } = field;
    public string Label => Field switch { "Title" => "标题", "Engine" => "引擎", "ReleaseYear" => "发行年份", "ReleaseDate" => "发行日期", "GameType" => "游戏类型", "Description" => "简介", "Tags" => "标签", _ => Field };
    public string Original { get; } = original;
    public string Proposed { get; } = proposed;
    public string ProposedDisplay => Proposed.Length == 0 ? "（清空此项）" : Proposed;
    public bool Accepted { get => _accepted; set => SetProperty(ref _accepted, value); }
}

public sealed record AiMetadataDraft(AiGameSummary Original, IReadOnlyList<AiFieldSuggestion> Fields, string Reason)
{
    public IReadOnlyList<AiCoverCandidate> Covers { get; init; } = [];
    public string Title => $"审核资料建议 · {Original.Title}";
    public static void ValidateField(string field, string value)
    {
        var max = field switch { "Title" => 300, "Engine" => 100, "ReleaseYear" => 4, "ReleaseDate" => 10, "GameType" => 60, "Description" => 3000, "Tags" => 500, _ => 0 };
        var canBeCleared = field is "Engine" or "ReleaseYear" or "ReleaseDate" or "GameType" or "Description" or "Tags";
        if (max == 0 || (!canBeCleared && string.IsNullOrWhiteSpace(value)) || (value.Length > 0 && string.IsNullOrWhiteSpace(value)) || value.Length > max)
            throw new InvalidOperationException("资料建议包含无效字段或过长内容。");
        if (field == "ReleaseYear" && value.Length > 0 && (!int.TryParse(value, out var year) || year < 1900 || year > 2100))
            throw new InvalidOperationException("资料建议中的发行年份无效。");
        if (field == "Tags" && value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 20)
            throw new InvalidOperationException("资料建议中的标签过多。");
        if (field == "ReleaseDate" && value.Length > 0 && (!DateTime.TryParseExact(value, ["yyyy", "yyyy-MM", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 1900 || date.Year > 2100))
            throw new InvalidOperationException("发行日期须为有效的 YYYY、YYYY-MM 或 YYYY-MM-DD。");
    }
    public static void SetField(Game game, string field, string value, bool restoring = false)
    {
        if (!restoring) ValidateField(field, value);
        switch (field)
        {
            case "Title": game.Title = value; break;
            case "Engine": game.Engine = value; break;
            case "ReleaseYear": game.ReleaseYear = value.Length == 0 ? null : int.Parse(value, CultureInfo.InvariantCulture); break;
            case "ReleaseDate": game.ReleaseDate = value; break;
            case "GameType": game.GameType = value; break;
            case "Description": game.Description = value; break;
            case "Tags": game.Tags = restoring ? JsonSerializer.Deserialize<List<string>>(value)! : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList(); break;
        }
    }
}
