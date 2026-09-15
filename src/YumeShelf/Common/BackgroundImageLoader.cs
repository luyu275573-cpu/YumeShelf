using System.IO;
using System.Windows.Media.Imaging;

namespace YumeShelf.Common;

public static class BackgroundImageLoader
{
    public static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return GameImageLoader.Load(path, 1920);
    }
}
