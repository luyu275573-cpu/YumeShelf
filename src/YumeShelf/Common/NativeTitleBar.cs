using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace YumeShelf.Common;

public static class NativeTitleBar
{
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;

    public static void Sync(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var background = (window.FindResource("WindowBackground") as SolidColorBrush)?.Color ?? Colors.White;
        var caption = ColorRef(background);
        var text = ColorRef(background.R + background.G + background.B > 390 ? Colors.Black : Colors.White);
        DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, CaptionTextColor, ref text, sizeof(int));
    }

    private static int ColorRef(System.Windows.Media.Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
