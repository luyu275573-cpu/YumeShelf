using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using YumeShelf.Application.AI;

namespace YumeShelf.Infrastructure;

public static class PackageVerification
{
    // Explicit diagnostics mode: validate shipped assets without opening a library, settings,
    // network connection or single-instance mutex. Suitable for an isolated release smoke check.
    public static void Run(string reportPath)
    {
        var root = AppContext.BaseDirectory;
        string[] assets = ["Assets/DefaultCover.png", "Assets/YumeShelfIcon.png", "Assets/YumeShelf.ico",
            "Assets/Backgrounds/pink.png", "Assets/Backgrounds/blue.png", "Assets/Backgrounds/yellow.png"];
        foreach (var asset in assets)
        {
            using var file = File.OpenRead(Path.Combine(root, asset));
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("Missing image frame.");
        }
        _ = new ResourceDictionary { Source = new Uri("/YumeShelf;component/Resources/Styles.xaml", UriKind.Relative) };
        if (YumeSkills.Ids.Count != 4 || YumeAgent.ToolNames.Count != 9) throw new InvalidDataException("Missing agent resources.");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = true,
            version = typeof(App).Assembly.GetName().Version?.ToString(), assets, skills = YumeSkills.Ids,
            selfContainedRuntime = File.Exists(Path.Combine(root, "coreclr.dll")), userDataAccessed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
