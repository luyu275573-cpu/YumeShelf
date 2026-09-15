using System.IO;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public sealed record GameImportResult(int Added, int Duplicates, int Failed);

public static class GameIdentity
{
    public static bool AreSame(string first, string second)
    {
        first = Path.GetFullPath(first); second = Path.GetFullPath(second);
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        var directory = Path.GetDirectoryName(first)!;
        if (!string.Equals(directory, Path.GetDirectoryName(second), StringComparison.OrdinalIgnoreCase)) return false;
        // Only known alternative BGI entry points share identity; an editable engine label is not evidence.
        try
        {
            var engine = GameScanService.DetectEngine(directory);
            if (IsBgiEntry(first) && IsBgiEntry(second) && engine == "BGI") return true;
            return engine == "自定义视觉小说引擎" && EntryStem(first).Equals(EntryStem(second), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.ChangeExtension(first, ".bin")) && File.Exists(Path.ChangeExtension(second, ".bin"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLog.Write("game.identity-unavailable", ex); return false; }
    }

    private static bool IsBgiEntry(string path) => Path.GetFileNameWithoutExtension(path).StartsWith("bgi", StringComparison.OrdinalIgnoreCase);
    private static string EntryStem(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("_chs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".chs", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
