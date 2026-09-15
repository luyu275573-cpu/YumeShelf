using System.IO;

namespace YumeShelf.Application;

public static class BuiltInBackgrounds
{
    public static string? Resolve(string palette, bool nightMode = false)
    {
        if (nightMode) return null;
        var file = palette switch { "樱粉色" => "pink.png", "浅蓝色" => "blue.png", "淡黄色" => "yellow.png", _ => null };
        if (file is null) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", file);
        return File.Exists(path) ? path : null;
    }
}
