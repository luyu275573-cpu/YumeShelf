using System.IO;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public sealed record OfflineGameMetadata(string Title, string? CoverPath);

public static class OfflineGameMetadataService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];
    private static readonly string[] CoverHints = ["cover", "封面", "title", "key", "visual", "logo", "cg"];

    public static OfflineGameMetadata Read(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var title = SelectTitle(directory, executableName);
        return new(title, FindCover(directory, title));
    }

    private static string SelectTitle(string directory, string executableName)
    {
        var directoryName = Path.GetFileName(directory);
        if (!string.IsNullOrWhiteSpace(directoryName) && !IsGenericName(directoryName)) return directoryName;
        var cleaned = executableName.Replace("_chs", string.Empty, StringComparison.OrdinalIgnoreCase);
        return IsGenericName(cleaned) ? executableName : cleaned;
    }

    private static string? FindCover(string directory, string title)
    {
        try
        {
            var deadline = Stopwatch.StartNew();
            var candidates = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Take(128)
                .OrderByDescending(path => CoverHints.Any(hint => Path.GetFileName(path).Contains(hint, StringComparison.OrdinalIgnoreCase)))
                .TakeWhile(_ => deadline.Elapsed < TimeSpan.FromSeconds(2))
                .Select(path => (Path: path, Score: CoverScore(path, title)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .ToArray();
            return candidates.Length == 0 ? GetDefaultCoverPath() : candidates[0].Path;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { AppLog.Write("cover.discovery-failed", ex); return GetDefaultCoverPath(); }
    }

    private static int CoverScore(string path, string title)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var score = CoverHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)) ? 8 : 0;
        if (name.Contains(title, StringComparison.OrdinalIgnoreCase)) score += 6;
        try
        {
            var image = GameImageLoader.Load(path, 320);
            var ratio = (double)image.PixelWidth / image.PixelHeight;
            if ((ratio >= 0.55 && ratio <= 0.85) || (ratio >= 1.5 && ratio <= 2)) score += 4;
            if (image.PixelWidth >= 250 && image.PixelHeight >= 140) score += 2;
        }
        catch (Exception ex) when (GameImageLoader.IsImageError(ex)) { AppLog.Write("cover.invalid-candidate", ex); return 0; }
        return score;
    }

    private static bool IsGenericName(string value)
        => value.Length == 0 || value.Equals("game", StringComparison.OrdinalIgnoreCase)
            || value.Equals("data", StringComparison.OrdinalIgnoreCase) || value.Equals("pc", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bin", StringComparison.OrdinalIgnoreCase) || value.Equals("extracted", StringComparison.OrdinalIgnoreCase);

    private static string? GetDefaultCoverPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "DefaultCover.png");
        return File.Exists(path) ? path : null;
    }
}
