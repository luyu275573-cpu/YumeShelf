using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    [STAThread]
    private static int Main()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        AppLog.DirectoryPath = Path.Combine(output, "logs");
        // Load production resources without running its StartupUri against the user's library.
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YumeShelf;component/Resources/Colors.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YumeShelf;component/Resources/Styles.xaml", UriKind.Relative) });
        app.Resources["BooleanToVisibilityConverter"] = new YumeShelf.Common.BooleanToVisibilityConverter();
        app.Resources["InverseBooleanToVisibilityConverter"] = new YumeShelf.Common.InverseBooleanToVisibilityConverter();
        app.Resources["InverseBooleanConverter"] = new YumeShelf.Common.InverseBooleanConverter();
        var trace = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var imagePath = Path.Combine(output, "背景 sample.png");
            var landscape = new DrawingVisual();
            using (var drawing = landscape.RenderOpen())
            {
                drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(26, 41, 78), Color.FromRgb(220, 145, 137), 90), null, new Rect(0, 0, 960, 540));
                drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 216, 162)), null, new Point(700, 190), 75, 75);
                drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(40, 66, 94)), null, new Point(250, 640), 620, 350);
                drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(24, 43, 67)), null, new Point(850, 690), 560, 330);
            }
            WriteImage(landscape, imagePath, 960, 540);

            var settingsPath = Path.Combine(output, "settings.json");
            // Reproduce the existing path-only JSON, then migrate by saving new settings.
            File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(new { BackgroundImagePath = imagePath }));
            var store = new AppSettingsStore(settingsPath);
            var original = store.Load();
            Check(original.BackgroundImagePath == imagePath && !original.SimpleLayout, "legacy settings load");
            var writes = 0;
            var vm = new SettingsViewModel(original, settings => { store.Save(settings); writes++; });
            bool? closed = null;
            vm.CloseRequested += result => closed = result;
            var before = File.ReadAllText(settingsPath);
            vm.SelectBackground(imagePath);
            vm.BackgroundOpacity = 0.85;
            vm.BackgroundBlur = 12;
            vm.PageTransparency = 0.4;
            vm.SimpleLayout = true;
            Check(vm.PreviewImage is not null, "selected image decodes for preview");
            using (File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check(writes == 0 && File.ReadAllText(settingsPath) == before, "preview never writes settings");
            vm.CancelCommand.Execute(null);
            Check(closed == false && writes == 0 && File.ReadAllText(settingsPath) == before, "cancel discards all changes");

            vm.ConfirmCommand.Execute(null);
            var saved = new AppSettingsStore(settingsPath).Load();
            Check(closed == true && writes == 1 && saved.SimpleLayout && saved.BackgroundBlur == 12 && saved.PageTransparency == 0.4 &&
                  saved.BackgroundOpacity == 0.85 && saved.BackgroundImagePath == imagePath, "confirm persists appearance and layout");
            var reopened = new SettingsViewModel(saved, store.Save);
            Check(reopened.HasBackground && reopened.SimpleLayout, "reopen restores preview and layout");
            var corruptPath = Path.Combine(output, "corrupt.png");
            File.WriteAllText(corruptPath, "invalid image");
            reopened.SelectBackground(corruptPath);
            Check(reopened.HasBackground && reopened.ErrorMessage.Length > 0, "invalid image preserves previous preview");
            reopened.ClearBackgroundCommand.Execute(null);
            Check(!reopened.HasCustomBackground && reopened.HasBackground && store.Load().BackgroundImagePath == imagePath, "clear previews the default and waits for confirmation");
            reopened.ConfirmCommand.Execute(null);
            Check(store.Load().BackgroundImagePath is null, "confirmed clear persists");

            bool failedClosed = false;
            var failing = new SettingsViewModel(original, _ => throw new IOException("simulated write failure"));
            failing.CloseRequested += _ => failedClosed = true;
            failing.ConfirmCommand.Execute(null);
            Check(!failedClosed && failing.ErrorMessage.Length > 0, "save failure stays open with message");
            var missing = new SettingsViewModel(original with { BackgroundImagePath = Path.Combine(output, "missing.png") }, store.Save);
            Check(!missing.HasBackground && missing.ErrorMessage.Length > 0, "missing image is recoverable");

            var window = new SettingsPage { DataContext = new SettingsViewModel(saved, store.Save) };
            var windowVm = (SettingsViewModel)window.DataContext;
            var content = (FrameworkElement)window.Content;
            Render(content, Path.Combine(output, "appearance.png"), 920, 700);
            var preview = Descendants(content).OfType<Image>().Single();
            Check(preview.Source is not null && preview.Opacity == saved.BackgroundOpacity &&
                  preview.Effect is System.Windows.Media.Effects.BlurEffect blur && blur.Radius == saved.BackgroundBlur, "preview effects bind");
            foreach (var section in windowVm.Sections)
            {
                windowVm.SelectedSection = section;
                Render(content, Path.Combine(output, section + ".png"), 920, 700);
                var expectedText = section switch { "应用美化" => "背景效果预览", "页面分栏" => "简化页面", _ => "云端 AI 服务" };
                Check(Descendants(content).OfType<TextBlock>().Any(t => t.Text == expectedText && IsVisibleWithin(t, content)), section + " content switches");
                Check(windowVm.SimpleLayout == saved.SimpleLayout, "navigation preserves selected layout");
            }
            windowVm.SelectedSection = "应用美化";
            Render(content, Path.Combine(output, "appearance-small.png"), 780, 540);

            foreach (var simple in new[] { false, true })
            {
                store.Save(saved with { SimpleLayout = simple });
                var mainVm = new MainWindowViewModel(
                    new YumeShelf.Application.GameLibraryService(new JsonGameStore(Path.Combine(output, "library.json"))),
                    new YumeShelf.Application.GameLaunchService(), store);
                var main = new MainWindow(mainVm);
                Render((FrameworkElement)main.Content, Path.Combine(output, simple ? "main-simple.png" : "main-full.png"), simple ? 900 : 1200, 720);
                Check(mainVm.DetailColumnWidth.Value == (simple ? 0 : 316), "layout width applied");
                Check(mainVm.PageTransparency == saved.PageTransparency, "page transparency applied");
                main.Close();
            }
            CheckEmbeddedNavigation(output, store);
            CheckOperationFeedback(output);
            Check(trace.Output.Length == 0, "no WPF binding errors: " + trace.Output);
            Console.WriteLine("PASS: settings regression checks, rendered artifacts: " + output);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine(trace.Output);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void CheckEmbeddedNavigation(string output, AppSettingsStore store)
    {
        var games = new JsonGameStore(Path.Combine(output, "navigation-library.json"));
        var game = new YumeShelf.Domain.Game
        {
            Title = "页面切换测试 · テストゲーム", RootPath = output,
            ExecutablePath = Path.Combine(output, "navigation-test.exe"), Engine = "测试引擎",
            IsFavorite = true, LastPlayedAt = DateTimeOffset.Now
        };
        games.Save([game]);
        var vm = new MainWindowViewModel(new YumeShelf.Application.GameLibraryService(games),
            new YumeShelf.Application.GameLaunchService(), store);
        var main = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var root = (FrameworkElement)main.Content;
        var library = (FrameworkElement)main.FindName("LibraryPage");
        var assistant = (AiAssistantPage)main.FindName("AssistantPage");
        var settings = (SettingsPage)main.FindName("SettingsContent");
        var pages = new FrameworkElement[] { library, assistant, settings };
        var windowCount = System.Windows.Application.Current.Windows.Count;
        vm.SelectedGame = vm.Games.Single();
        vm.SearchText = "テスト";
        vm.IsListView = true;
        vm.NavigateCommand.Execute("Favorites");
        vm.OpenSettingsCommand.Execute(null);
        var draft = vm.SettingsEditor;
        var initial = store.Load();
        draft.PageTransparency = 0.73;
        draft.NightMode = !initial.NightMode;
        vm.OpenAiAssistantCommand.Execute(null);
        var chat = vm.AiAssistant;
        chat.Query = "请介绍这个游戏。";
        chat.Conversation.Add(new AiAssistantViewModel.AiConversationItem(false, "这是一条用于检查切页后保留的测试消息。"));
        vm.OpenSettingsCommand.Execute(null);
        Check(ReferenceEquals(draft, vm.SettingsEditor) && draft.PageTransparency == 0.73 &&
              store.Load().PageTransparency == initial.PageTransparency, "navigation preserves unsaved settings without writing");
        vm.SettingsEditor.CancelCommand.Execute(null);
        Check(vm.ActiveNavigation == "Settings" && vm.SettingsEditor.PageTransparency == initial.PageTransparency &&
              vm.SettingsEditor.NightMode == initial.NightMode, "cancel restores saved settings and stays on page");
        vm.SettingsEditor.PageTransparency = 0.47;
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        Check(vm.ActiveNavigation == "Settings" && store.Load().PageTransparency == 0.47 &&
              vm.PageTransparency == 0.47 && vm.SettingsEditor.SaveNotice == "设置已保存", "save applies settings without closing shell");
        vm.OpenAiAssistantCommand.Execute(null);
        Check(ReferenceEquals(chat, vm.AiAssistant) && chat.Query == "请介绍这个游戏。" && chat.Conversation.Count == 2,
              "AI draft and conversation survive navigation and settings save");
        vm.NavigateCommand.Execute("Favorites");
        Check(vm.ActiveNavigation == "Favorites" && vm.IsListView && vm.SearchText == "テスト" &&
              vm.SelectedGame?.Id == game.Id && vm.SelectedFilter == "已收藏", "library selection, query and view survive navigation");
        vm.NavigateCommand.Execute("All");
        vm.SelectedFilter = "已收藏";
        vm.OpenAiAssistantCommand.Execute(null);
        vm.NavigateCommand.Execute("All");
        Check(vm.SelectedFilter == "已收藏", "returning to library preserves manual dropdown filter");

        foreach (var size in new[] { (Width: 1200, Height: 720), (Width: 900, Height: 580) })
        {
            vm.OpenSettingsCommand.Execute(null);
            foreach (var section in vm.SettingsEditor.Sections)
            {
                vm.SettingsEditor.SelectedSection = section;
                Render(root, Path.Combine(output, $"embedded-settings-{section}-{size.Width}.png"), size.Width, size.Height);
                Check(pages.Count(p => p.Visibility == Visibility.Visible) == 1 && settings.Visibility == Visibility.Visible,
                    $"settings owns right pane at {size.Width}: {section}");
                foreach (var label in new[] { "取消", "确认并保存" })
                    CheckInside(Descendants(settings).OfType<Button>().Single(b => Equals(b.Content, label)), settings, $"{label} stays inside settings");
            }
            vm.OpenAiAssistantCommand.Execute(null);
            chat.Query = string.Join('\n', Enumerable.Repeat("中日文输入测试 / 入力テスト", 30));
            Render(root, Path.Combine(output, $"embedded-ai-{size.Width}.png"), size.Width, size.Height);
            Check(pages.Count(p => p.Visibility == Visibility.Visible) == 1 && assistant.Visibility == Visibility.Visible,
                $"AI owns right pane at {size.Width}");
            foreach (var label in new[] { "停止", "发送" })
                CheckInside(Descendants(assistant).OfType<Button>().Single(b => Equals(b.Content, label)), assistant, $"{label} stays inside AI page");
            Check(((FrameworkElement)assistant.FindName("QueryEditor")).ActualHeight <= 140, "long input does not displace conversation or actions");
            var selectedNav = Descendants(root).OfType<Button>().Single(b => Equals(b.Tag, "AI"));
            Check(selectedNav.FontWeight == FontWeights.SemiBold, "active navigation is highlighted");
        }
        chat.Query = "请介绍这个游戏。";
        vm.OpenSettingsCommand.Execute(null);
        vm.SettingsEditor.SelectedSection = "应用美化";
        vm.SettingsEditor.NightMode = true;
        Render(root, Path.Combine(output, "embedded-settings-night-preview.png"), 1200, 720);
        Check(((SolidColorBrush)System.Windows.Application.Current.Resources["WindowBackground"]).Color.R > 200,
            "night preview remains local until save");
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        vm.OpenAiAssistantCommand.Execute(null);
        Render(root, Path.Combine(output, "embedded-ai-night.png"), 1200, 720);
        Check(((SolidColorBrush)assistant.FindResource("WindowBackground")).Color.R < 40, "saved theme reaches embedded AI page");
        vm.OpenSettingsCommand.Execute(null);
        vm.SettingsEditor.NightMode = false;
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        vm.NavigateCommand.Execute("All");
        main.Show();
        Pump(TimeSpan.FromMilliseconds(50));
        var nav = Descendants(root).OfType<Button>().Single(b => Equals(b.Tag, "AI"));
        var origin = nav.TranslatePoint(new Point(), root);
        vm.OpenAiAssistantCommand.Execute(null);
        Pump(TimeSpan.FromMilliseconds(40));
        if (SystemParameters.ClientAreaAnimation)
            Check(((TranslateTransform)assistant.RenderTransform).Y > 0 && !library.IsEnabled, "lower navigation enters from below and disables outgoing page");
        Pump(TimeSpan.FromMilliseconds(260));
        vm.NavigateCommand.Execute("All");
        Pump(TimeSpan.FromMilliseconds(40));
        if (SystemParameters.ClientAreaAnimation)
            Check(((TranslateTransform)library.RenderTransform).Y < 0, "upper navigation enters from above");
        for (var i = 0; i < 20; i++)
        {
            vm.OpenSettingsCommand.Execute(null);
            vm.OpenAiAssistantCommand.Execute(null);
            vm.NavigateCommand.Execute("Recent");
        }
        vm.OpenSettingsCommand.Execute(null);
        Pump(TimeSpan.FromMilliseconds(300));
        Check(settings.Visibility == Visibility.Visible && settings.IsEnabled &&
              pages.Count(p => p.Visibility == Visibility.Visible) == 1 && settings.Opacity == 1 &&
              ((TranslateTransform)settings.RenderTransform).Y == 0, "rapid transitions settle on exactly one interactive page");
        Check(nav.TranslatePoint(new Point(), root) == origin, "sidebar stays stationary throughout transitions");
        Check(System.Windows.Application.Current.Windows.Count == windowCount, "settings and AI navigation create no child windows");
        main.Close();
    }

    private static void CheckInside(FrameworkElement element, FrameworkElement page, string message)
    {
        var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
              bounds.Right <= page.ActualWidth + 1 && bounds.Bottom <= page.ActualHeight + 1, message);
    }

    private static void Pump(TimeSpan interval)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static bool IsVisibleWithin(DependencyObject target, DependencyObject root)
    {
        for (var current = target; current is not null && current != root; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement content, string path, int width, int height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        WriteImage(content, path, width, height);
    }

    private static void WriteImage(Visual visual, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class BindingErrors : TraceListener
    {
        public StringBuilder Output { get; } = new();
        public override void Write(string? message) => Output.Append(message);
        public override void WriteLine(string? message) => Output.AppendLine(message);
    }
}
