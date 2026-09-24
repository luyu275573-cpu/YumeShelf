using System.Text;
using System.Text.RegularExpressions;

namespace YumeShelf.Application.AI;

// Local read results are rendered from the snapshot, never rewritten by a model.
public sealed record AiLibraryQuery(string Operation, string Query = "", string Field = "title", int Offset = 0,
    bool? Favorite = null, bool? Played = null, string State = "any")
{
    public const int PageSize = 20;
    public const string Unavailable = "当前游戏库无法可靠读取或存在未保存修改，暂时不能确认数量和记录。请先在游戏库页面处理提示后重试。";

    public static AiLibraryQuery? TryDirect(string question)
    {
        // Only complete, unambiguous commands take this offline shortcut. Other wording,
        // filters, mixed requests and domain classification still go to the model.
        var text = Regex.Replace(question.Normalize(NormalizationForm.FormKC), @"\s+", "").TrimEnd('?', '？', '。');
        const string prefix = @"(?:请|麻烦)?(?:告诉我|帮我看看|帮我查一下|帮我看一下)?";
        const string library = @"(?:(?:我(?:(?:现在|目前))?的?|当前|现在|目前)?(?:本地)?(?:游戏)?库(?:里|中|内)?|我)";
        if (Regex.IsMatch(text, "\\A" + prefix + library + @"(?:现在|目前)?(?:一共|总共|总计)?(?:有|拥有|收录了?|添加了?)?(?:几个|多少(?:个|部|款)?|几部|几款)(?:游戏|作品)(?:了|呢|啊|呀|吗)?\z"))
            return new("count");
        if (Regex.IsMatch(text, "\\A" + prefix + library + @"(?:都)?(?:有|收录了?|添加了?)?哪些游戏(?:呢|啊|呀|吗)?\z") ||
            Regex.IsMatch(text, "\\A" + prefix + library + @"(?:都)?(?:有|收录了?|添加了?)?哪些(?:游戏|作品)(?:呢|啊|呀|吗)?\z") ||
            Regex.IsMatch(text, "\\A" + prefix + @"(?:列出|列一下|查看|显示)" + library + @"(?:的)?(?:全部|所有)?(?:游戏|作品|游戏列表|列表)\z"))
            return new("list");
        return null;
    }

    public string Execute(IReadOnlyList<AiGameSummary> library, bool available)
    {
        if (!available) return Unavailable;
        if (Operation is not ("count" or "list") || Field is not ("title" or "engine" or "type" or "tag" or "year") ||
            Query.Length > 200 || Offset < 0 || (Operation == "count" && Offset != 0) || (Field != "title" && string.IsNullOrWhiteSpace(Query)) ||
            State is not ("any" or "running" or "missing" or "available"))
            throw new InvalidOperationException("本地查询参数无效，请明确要统计或列出的游戏范围。");
        var matches = Match(library, Query, Field);
        var filters = new List<string>();
        if (Favorite is { } favorite) { matches = matches.Where(g => g.IsFavorite == favorite).ToArray(); filters.Add(favorite ? "已收藏" : "未收藏"); }
        if (Played is { } played) { matches = matches.Where(g => g.HasPlayed == played).ToArray(); filters.Add(played ? "有游玩记录" : "无游玩记录"); }
        if (State != "any")
        {
            matches = matches.Where(g => g.LocalState == State).ToArray();
            filters.Add(State switch { "running" => "运行中", "missing" => "启动文件缺失或不可访问", _ => "启动文件存在且未运行（未验证可成功启动）" });
        }
        if (filters.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(Query)) filters.Insert(0, $"{FieldLabel(Field)}匹配“{Query}”");
            var filteredSummary = $"你的游戏库共 {library.Count} 个游戏，满足条件【{string.Join("、", filters)}】的有 {matches.Count} 个。";
            if (Played is not null) filteredSummary += " 游玩情况仅依据本应用记录。";
            if (State == "missing") filteredSummary += " 请确认磁盘已连接及访问权限；此结果不会删除记录。";
            return ListResult(matches, filteredSummary);
        }
        var summary = string.IsNullOrWhiteSpace(Query) ? $"你的游戏库目前共有 {library.Count} 个游戏。" :
            $"你的游戏库共 {library.Count} 个游戏，其中{FieldLabel(Field)}匹配“{Query}”的有 {matches.Count} 个。";
        return ListResult(matches, summary);
    }

    private string ListResult(IReadOnlyList<AiGameSummary> matches, string summary)
    {
        if (Operation == "count" || matches.Count == 0) return summary;
        if (Offset >= matches.Count) return summary + " 已超出列表范围，请从第1项重新查看。";
        var items = matches.Skip(Offset).Take(PageSize).Select((g, i) => $"{Offset + i + 1}. {g.Title}");
        return summary + "\n\n" + string.Join("\n", items) + (matches.Count > PageSize ?
            $"\n\n当前显示第 {Offset + 1}–{Math.Min(Offset + PageSize, matches.Count)} 项，共 {matches.Count} 项。" : "");
    }

    public static IReadOnlyList<AiGameSummary> Match(IReadOnlyList<AiGameSummary> library, string query, string field = "title")
    {
        if (string.IsNullOrWhiteSpace(query)) return library;
        var key = Normalize(query);
        if (key.Length == 0) return [];
        return library.Where(g => field switch
        {
            "title" => Normalize(g.Title).Contains(key, StringComparison.Ordinal) ||
                (Normalize(g.Title).Length >= 2 && key.Contains(Normalize(g.Title), StringComparison.Ordinal)) || Normalize(g.Tags).Contains(key, StringComparison.Ordinal),
            "engine" => Normalize(g.Engine) == key,
            "type" => Normalize(g.GameType) == key,
            "tag" => g.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(t => Normalize(t) == key),
            "year" => g.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) == query,
            _ => false
        }).ToArray();
    }
    private static string Normalize(string value) => Regex.Replace(Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s*[vV]\d+(?:\.\d+)*$", ""), @"[^\p{L}\p{N}]", "").ToUpperInvariant();
    private static string FieldLabel(string field) => field switch { "engine" => "引擎", "type" => "游戏类型", "tag" => "标签", "year" => "发行年份", _ => "标题或标签" };
}
