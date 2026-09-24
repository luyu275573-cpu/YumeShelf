using System.IO;
using System.Text.RegularExpressions;

namespace YumeShelf.Application.AI;

public sealed record AiGameResearch(AiGameSummary Original, IReadOnlyList<string> Fields, IReadOnlyList<AiSource> Sources, IReadOnlyList<AiCoverCandidate> Covers)
{
    public AiMetadataDraft Select(AiSource source)
    {
        if (!Sources.Contains(source)) throw new InvalidOperationException("请从本轮检索结果中选择资料。");
        var proposals = new List<AiFieldSuggestion>();
        foreach (var field in Fields.Where(f => f != "Cover"))
        {
            var value = field switch
            {
                "Title" => source.Title.Split(" / ")[0],
                "Description" => source.Description,
                "ReleaseDate" => source.Released ?? "",
                "ReleaseYear" => source.Released is { Length: >= 4 } ? source.Released[..4] : "",
                _ => "" // VNDB does not establish a game's engine or gameplay genre.
            };
            if (string.IsNullOrWhiteSpace(value) || value == Original.Field(field)) continue;
            try { AiMetadataDraft.ValidateField(field, value); }
            catch (InvalidOperationException) { continue; }
            proposals.Add(new(field, Original.Field(field), value) { Accepted = true });
        }
        return new(Original, proposals, $"来源：{source.Title}（{source.Url}）。简介保留来源原文；未取得可靠资料的字段保持原值。请选取要保存的内容。");
    }
}

public sealed record AiScanDraft(string Root, GameScanResult Result);

public static class AiLibraryTasks
{
    public static IReadOnlyList<AiGameSummary> Search(IReadOnlyList<AiGameSummary> library, string query)
        => AiLibraryQuery.Match(library, query).Take(5).ToArray();

    public static string ValidateScanPath(string path, string userQuestion)
    {
        path = path.Trim();
        if (path.Length is < 3 or > 500 || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
            throw new InvalidOperationException("请在消息中提供本机磁盘的完整文件夹或 EXE 路径。");
        var full = Path.GetFullPath(path);
        if (!ExtractPaths(userQuestion).Any(p => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)),
                Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("只能扫描你在当前消息中明确提供的完整路径，请用引号包住路径后重试。");
        if (!Directory.Exists(full) && !(File.Exists(full) && Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("提供的本地文件夹或 EXE 不存在。");
        return full;
    }

    private static IEnumerable<string> ExtractPaths(string text)
    {
        // Consume whole quoted spans before considering bare paths. Spaces are part of a path,
        // never evidence that a shorter prefix was authorized. Ambiguous prose needs quotes.
        var pattern = "\"(?<quoted>[^\"\\r\\n]*)\"|'(?<quoted>[^'\\r\\n]*)'|`(?<quoted>[^`\\r\\n]*)`|“(?<quoted>[^”\\r\\n]*)”|‘(?<quoted>[^’\\r\\n]*)’|(?<![A-Za-z0-9_])(?<bare>[A-Za-z]:[\\\\/][^\"'`“”‘’\\r\\n，。；！？<>|]*)";
        foreach (Match match in Regex.Matches(text, pattern))
        {
            var candidate = (match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value).Trim();
            if (Regex.IsMatch(candidate, @"\A[A-Za-z]:[\\/]") && candidate.IndexOfAny(['*', '?', '\0']) < 0 && !candidate[2..].Contains(':'))
                yield return candidate;
        }
    }
}
