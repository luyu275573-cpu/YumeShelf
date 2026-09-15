using System.Windows;
using System.IO;
using System.ComponentModel;
using Panel = System.Windows.Controls.Panel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YumeShelf.Common;
using System.Windows.Media.Imaging;

namespace YumeShelf.Presentation;

public partial class MainWindow : Window
{
    private FrameworkElement? _currentPage;
    private int _navigationIndex;
    private int _transitionVersion;
    private bool _hasNativeWindow;

    public MainWindow() : this(new MainWindowViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel, Func<MessageBoxResult>? choosePendingClose = null)
    {
        InitializeComponent();
        Icon = LoadIcon();
        BrandIconImage.Source = Icon;
        SourceInitialized += (_, _) => { _hasNativeWindow = true; NativeTitleBar.Sync(this); };
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is MainWindowViewModel old) old.PropertyChanged -= ViewModelChanged;
            if (args.NewValue is MainWindowViewModel current)
            {
                current.PropertyChanged += ViewModelChanged;
                // Let inherited DataContext reach the whole tree before hiding/showing pages.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(DataContext, current)) ShowPage(current.ActiveNavigation, false);
                }), System.Windows.Threading.DispatcherPriority.DataBind);
            }
        };
        Closing += (_, args) =>
        {
            if (DataContext is not MainWindowViewModel current || !current.HasUnsavedLibraryChanges) return;
            var choice = choosePendingClose?.Invoke() ?? System.Windows.MessageBox.Show(this, "游戏库仍有保存失败的修改。\n选择“是”重新保存并退出；“否”放弃这些修改退出；“取消”返回应用。", "有未保存的游戏库修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            args.Cancel = choice == MessageBoxResult.Cancel || (choice == MessageBoxResult.Yes && !current.TrySavePendingChanges());
        };
        Closed += (_, _) =>
        {
            _transitionVersion++;
            if (DataContext is MainWindowViewModel current)
            {
                current.PropertyChanged -= ViewModelChanged;
                current.CancelPendingRequests();
            }
        };
        DataContext = viewModel;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not MainWindowViewModel vm) return;
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveNavigation)) ShowPage(vm.ActiveNavigation, IsLoaded);
        if (args.PropertyName == nameof(MainWindowViewModel.ShellPanelBrush) && _hasNativeWindow) NativeTitleBar.Sync(this);
    }

    private void ShowPage(string navigation, bool animate)
    {
        FrameworkElement target = navigation switch { "AI" => AssistantPage, "Settings" => SettingsContent, _ => LibraryPage };
        var index = navigation switch { "All" => 0, "Recent" => 1, "Favorites" => 2, "AI" => 3, _ => 4 };
        var offset = index >= _navigationIndex ? 36d : -36d;
        var previous = _currentPage;
        var version = ++_transitionVersion;
        _navigationIndex = index;
        _currentPage = target;

        // Replace any unfinished transition so rapid navigation cannot leave a stale page on top.
        foreach (var page in new FrameworkElement[] { LibraryPage, AssistantPage, SettingsContent })
        {
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = 1;
            page.RenderTransform = new TranslateTransform();
            page.Visibility = Visibility.Collapsed;
            page.IsEnabled = false;
            Panel.SetZIndex(page, 0);
        }
        target.Visibility = Visibility.Visible;
        target.IsEnabled = true;
        Panel.SetZIndex(target, 1);
        if (!animate || !SystemParameters.ClientAreaAnimation) return;

        var duration = TimeSpan.FromMilliseconds(220);
        DoubleAnimation Slide(double from, double to) => new(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        if (previous is not null && previous != target)
        {
            previous.Visibility = Visibility.Visible;
            var leave = Slide(0, -offset);
            leave.Completed += (_, _) =>
            {
                if (version == _transitionVersion) previous.Visibility = Visibility.Collapsed;
            };
            ((TranslateTransform)previous.RenderTransform).BeginAnimation(TranslateTransform.YProperty, leave);
            previous.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
        }
        ((TranslateTransform)target.RenderTransform).BeginAnimation(TranslateTransform.YProperty, Slide(offset, 0));
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }
    private static BitmapImage? LoadIcon() => File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "YumeShelfIcon.png")) ? new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "YumeShelfIcon.png"))) : null;
}
