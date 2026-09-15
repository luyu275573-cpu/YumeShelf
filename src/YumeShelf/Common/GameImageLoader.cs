using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using YumeShelf.Infrastructure;

namespace YumeShelf.Common;

public static class GameImageLoader
{
    public static string DefaultCover => Path.Combine(AppContext.BaseDirectory, "Assets", "DefaultCover.png");
    public static BitmapImage Load(string path, int maxWidth = 640)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("图片超过 32 MB，请选择较小的图片。");
        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000 || frame.PixelWidth == 0 || frame.PixelHeight == 0)
            throw new InvalidDataException("图片尺寸过大或无效。");
        file.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelHeight > frame.PixelWidth * 2) image.DecodePixelHeight = Math.Min(maxWidth * 2, frame.PixelHeight);
        else image.DecodePixelWidth = Math.Min(maxWidth, frame.PixelWidth);
        image.StreamSource = file;
        image.EndInit();
        image.Freeze();
        return image;
    }
    public static bool IsImageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
        or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException;
}

public sealed class CoverImageConverter : IValueConverter
{
    private readonly Dictionary<string, (DateTime Stamp, WeakReference<BitmapImage> Image)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var path in new[] { value as string, GameImageLoader.DefaultCover })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.Image.TryGetTarget(out var image)) return image;
                image = GameImageLoader.Load(path);
                if (_cache.Count >= 64) _cache.Clear();
                _cache[path] = (stamp, new WeakReference<BitmapImage>(image));
                return image;
            }
            catch (Exception ex) when (GameImageLoader.IsImageError(ex)) { AppLog.Write("cover.load-failed", ex); }
        }
        return null;
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
