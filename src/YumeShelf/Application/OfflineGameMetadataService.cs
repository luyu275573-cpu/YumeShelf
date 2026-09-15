using System.IO;
using System.Windows.Media.Imaging;

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
            var candidates = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(path => (Path: path, Score: CoverScore(path, title)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .ToArray();
            return candidates.Length == 0 ? GetDefaultCoverPath() : candidates[0].Path;
        }
        catch (UnauthorizedAccessException) { return GetDefaultCoverPath(); }
        catch (IOException) { return GetDefaultCoverPath(); }
    }

    private static int CoverScore(string path, string title)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var score = CoverHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)) ? 8 : 0;
        if (name.Contains(title, StringComparison.OrdinalIgnoreCase)) score += 6;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            var ratio = (double)decoder.Frames[0].PixelWidth / decoder.Frames[0].PixelHeight;
            if (ratio >= 0.55 && ratio <= 0.85) score += 4;
            if (decoder.Frames[0].PixelWidth >= 500 && decoder.Frames[0].PixelHeight >= 500) score += 2;
        }
        catch (Exception) { return score; }
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
