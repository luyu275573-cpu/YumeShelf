using System.IO;
using System.Windows.Media.Imaging;

namespace YumeShelf.Common;

public static class BackgroundImageLoader
{
    public static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 1920;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
