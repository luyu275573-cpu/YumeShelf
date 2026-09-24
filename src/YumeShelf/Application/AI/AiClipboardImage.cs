using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YumeShelf.Application.AI;

public static class AiClipboardImage
{
    private static readonly string[] PngFormats = ["PNG", "image/png"];
    public static bool ContainsImage(System.Windows.IDataObject? data) => data is not null &&
        (PngFormats.Any(f => data.GetDataPresent(f, false)) || data.GetDataPresent(System.Windows.DataFormats.Bitmap));

    public static BitmapSource? Read(System.Windows.IDataObject? data)
    {
        if (data is null) return null;
        foreach (var format in PngFormats)
        {
            if (!data.GetDataPresent(format, false)) continue;
            var value = data.GetData(format, false);
            using var owned = new MemoryStream();
            if (value is byte[] bytes)
            {
                if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("剪贴板图片超过 32 MB。");
                owned.Write(bytes);
            }
            else if (value is Stream source)
            {
                var position = source.CanSeek ? source.Position : 0;
                try
                {
                    if (source.CanSeek) source.Position = 0;
                    var buffer = new byte[8192];
                    int count;
                    while ((count = source.Read(buffer)) > 0)
                    {
                        if (owned.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("剪贴板图片超过 32 MB。");
                        owned.Write(buffer, 0, count);
                    }
                }
                finally { if (source.CanSeek) source.Position = position; }
            }
            else continue;
            owned.Position = 0;
            var decoder = BitmapDecoder.Create(owned, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            AiImageAttachment.ValidateDimensions(decoder.Frames[0]);
            var snapshot = decoder.Frames[0].CloneCurrentValue();
            // Own the pixels before disposing the clipboard stream.
            var stride = checked((snapshot.PixelWidth * snapshot.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * snapshot.PixelHeight)];
            snapshot.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(snapshot.PixelWidth, snapshot.PixelHeight, 96, 96, snapshot.Format, snapshot.Palette, pixels, stride);
            result.Freeze();
            return result;
        }
        if (data.GetData(System.Windows.DataFormats.Bitmap) is not BitmapSource bitmap) return null;
        AiImageAttachment.ValidateDimensions(bitmap);
        // Native DIB alpha is undefined. Repair only this fallback, never encoded PNG transparency.
        if (data.GetDataPresent(System.Windows.DataFormats.Dib, false) && bitmap.Format == PixelFormats.Bgra32)
        {
            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4) hasAlpha |= pixels[i] != 0;
            if (!hasAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                bitmap = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            }
        }
        var frozen = bitmap.CloneCurrentValue(); frozen.Freeze(); return frozen;
    }
}
