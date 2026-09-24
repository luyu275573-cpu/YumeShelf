using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YumeShelf.Application;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void StartupChecks()
    {
        var file = Path.Combine(Folder("startup"), "library.json");
        var game = GameAt(FakeExe(Folder("startup-game"))); game.Title = "启动检查游戏";
        new JsonGameStore(file).Save([game]);
        var original = File.ReadAllBytes(file);
        var vm = Vm(file);
        var window = new MainWindow(vm, showIntroduction: true);
        var root = (FrameworkElement)window.Content;
        var shell = (FrameworkElement)window.FindName("ShellContent");
        var intro = (StartupIntro)window.FindName("StartupIntroduction");
        var name = (TextBlock)intro.FindName("AppNameText");
        var tagline = (TextBlock)intro.FindName("TaglineText");
        var enter = (Button)intro.FindName("EnterLibraryButton");
        Check(window.Title == "YumeShelf" && !shell.IsEnabled && intro.Visibility == Visibility.Visible && name.Opacity == 0 && tagline.Opacity == 0,
            "startup begins with a blocking brand surface and no early tagline flash");
        var windows = System.Windows.Application.Current.Windows.Count;
        var finishes = 0; intro.Finished += (_, _) => finishes++;
        Render(root, "startup-initial.png", 900, 580);
        intro.Start(true);
        PumpUntil(() => name.Opacity > 0.1);
        Check(tagline.Opacity == 0 && !shell.IsEnabled, "app name appears before tagline and underlying navigation stays disabled");
        Render(root, "startup-name.png", 900, 580);
        PumpUntil(() => tagline.Opacity > 0.99);
        Check(tagline.Text == "一个更懂你的游戏盒子..." && name.Opacity > 0.99 && ((Image)intro.FindName("IntroIcon")).Source is not null,
            "startup presents the new YS icon, final brand name and exact introduction sentence");
        Render(root, "startup-tagline-small.png", 900, 580);
        var buttonBounds = Bounds(enter, root);
        Check(buttonBounds.Left >= 0 && buttonBounds.Right <= 900 && buttonBounds.Bottom <= 580 && enter.IsEnabled,
            "skip control remains visible and reachable at minimum content size");
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
        Render(root, "startup-tagline-night.png", 1200, 720);
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings());
        PumpUntil(() => intro.Visibility == Visibility.Collapsed, 5000);
        Check(shell.IsEnabled && finishes == 1 && System.Windows.Application.Current.Windows.Count == windows && vm.Games.Single().Id == game.Id,
            "startup automatically reveals the existing library exactly once without a second window");
        intro.Start(true);
        vm.OpenAiAssistantCommand.Execute(null); vm.NavigateCommand.Execute("All");
        Check(intro.Visibility == Visibility.Collapsed && finishes == 1 && File.ReadAllBytes(file).SequenceEqual(original),
            "navigation cannot replay startup or modify the persisted game library");
        Render(root, "startup-finished-library.png", 900, 580);
        window.Close();

        foreach (var action in new[] { "button", "enter", "escape", "reduced-motion", "close" })
        {
            var host = new MainWindow(Vm(file), showIntroduction: true);
            var view = (StartupIntro)host.FindName("StartupIntroduction");
            var count = 0; view.Finished += (_, _) => count++;
            var visual = (FrameworkElement)host.Content;
            visual.Measure(new Size(900, 580)); visual.Arrange(new Rect(0, 0, 900, 580)); visual.UpdateLayout();
            if (action == "reduced-motion") view.Start(false);
            else
            {
                view.Start(true);
                if (action == "button") ((Button)view.FindName("EnterLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (action == "close") host.Close();
                else
                {
                    // A disconnected test visual still needs a presentation source for routed key input.
                    using var source = new HwndSource(new HwndSourceParameters("startup-key-fixture") { Width = 1, Height = 1, WindowStyle = 0 });
                    view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, action == "enter" ? Key.Enter : Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
            }
            view.Skip(); view.Start(true);
            Check(action == "close" ? count == 0 : count == 1 && view.Visibility == Visibility.Collapsed && ((FrameworkElement)host.FindName("ShellContent")).IsEnabled,
                "startup lifecycle handles " + action + " without duplicate completion or delayed reopening");
            if (action != "close") host.Close();
        }

        var bitmap = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "YumeShelfIcon.png")));
        var pixels = new byte[512 * 512 * 4]; new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 512 * 4, 0);
        var darkPixels = Enumerable.Range(0, 512 * 512).Count(i => pixels[i * 4 + 3] > 200 && pixels[i * 4] < 70 && pixels[i * 4 + 1] < 70 && pixels[i * 4 + 2] < 70);
        Check(bitmap.PixelWidth == 512 && bitmap.PixelHeight == 512 && pixels[3] == 0 && darkPixels > 10000,
            "shipping icon contains the dark YS mark and transparent rounded corners at full resolution");
    }
}
