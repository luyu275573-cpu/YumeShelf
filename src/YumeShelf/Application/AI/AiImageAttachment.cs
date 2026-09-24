using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YumeShelf.Common;

namespace YumeShelf.Application.AI;

// Only the pending turn owns the payload. Conversation bubbles retain a small preview instead.
public sealed class AiImageAttachment
{
    private AiImageAttachment(byte[] png, string name, int width, int height)
    {
        Name = name;
        PngBytes = png;
        Width = width; Height = height; ByteCount = png.Length;
        DataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        using var stream = new MemoryStream(png, writable: false);
        var preview = new BitmapImage();
        preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) preview.DecodePixelWidth = Math.Min(256, width);
        else preview.DecodePixelHeight = Math.Min(256, height);
        preview.StreamSource = stream; preview.EndInit(); preview.Freeze();
        Preview = preview;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public int ByteCount { get; }
    public BitmapSource Preview { get; }
    public string Details => $"{Width} × {Height} · {ByteCount / 1024d:0.#} KB · 已优化";
    internal string DataUrl { get; }
    internal byte[] PngBytes { get; }

    public static AiImageAttachment FromFile(string path)
        => FromBitmap(GameImageLoader.Load(path, AiLimits.ImageEdge), Path.GetFileName(path));

    public static AiImageAttachment FromBitmap(BitmapSource source, string name = "剪贴板截图")
    {
        ValidateDimensions(source);
        for (var edge = AiLimits.ImageEdge; edge >= 900; edge = edge * 3 / 4)
        {
            var scale = Math.Min(1d, (double)edge / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            // Copy pixels into a fresh bitmap: no source metadata, file handles or full-sized image references survive.
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var visible = false;
            for (var i = 3; i < pixels.Length; i += 4) visible |= pixels[i] != 0;
            if (!visible) throw new InvalidDataException("图片完全透明，无法识别。请重新复制，或保存图片后通过“选择图片”导入。");
            var clean = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(clean));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length <= AiLimits.ImageBytes)
                return new AiImageAttachment(output.ToArray(), name, clean.PixelWidth, clean.PixelHeight);
        }
        throw new InvalidDataException("优化后的图片仍超过 4 MB，请裁剪后重试。");
    }

    internal static void ValidateDimensions(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || (long)source.PixelWidth * source.PixelHeight > 40_000_000)
            throw new InvalidDataException("图片尺寸无效或超过 4000 万像素，请裁剪后重试。");
    }
}
