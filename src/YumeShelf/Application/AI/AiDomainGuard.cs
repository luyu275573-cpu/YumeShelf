namespace YumeShelf.Application.AI;

public static class AiDomainGuard
{
    private static readonly string[] Keywords = ["galgame", "视觉小说", "visual novel", "恋爱冒险", "美少女游戏", "汉化", "声优", "攻略", "引擎"];
    public static bool IsRelevant(string text) => Keywords.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
}
