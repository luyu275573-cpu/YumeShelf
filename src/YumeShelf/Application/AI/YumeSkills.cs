using System.IO;

namespace YumeShelf.Application.AI;

public static class YumeSkills
{
    // Deliberately compiled allowlist. No directory, environment or user-configured overrides.
    public static string Role { get; } = Read("role");
    public static string Vision { get; } = Read("vision");
    public static string Library { get; } = Read("library");
    public static string Research { get; } = Read("research");
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(new[] { "role@2", "vision@1", "library@6", "research@3" });
    private static string Read(string id)
    {
        using var stream = typeof(YumeSkills).Assembly.GetManifestResourceStream($"YumeShelf.Application.AI.Skills.{id}.md")
            ?? throw new InvalidOperationException("Yume 内置技能资源缺失，请重新安装应用。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
