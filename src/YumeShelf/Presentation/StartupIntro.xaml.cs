using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YumeShelf.Presentation;

public partial class StartupIntro : UserControl
{
    private Storyboard? _storyboard;
    private bool _finished;
    private bool _started;
    public event EventHandler? Finished;

    public StartupIntro()
    {
        InitializeComponent();
        Unloaded += (_, _) => Stop();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Finish();
        };
    }

    public void SetIcon(ImageSource? icon) => IntroIcon.Source = icon;

    public void Start(bool animate)
    {
        if (_started || _finished) return;
        _started = true;
        if (!animate) { Finish(); return; }
        _storyboard = new Storyboard { Duration = TimeSpan.FromMilliseconds(2300) };
        Fade(IntroIcon, 0, 1, 0, 320);
        Fade(AppNameText, 0, 1, 140, 480);
        Fade(TaglineText, 0, 1, 680, 480);
        Fade(this, 1, 0, 2060, 240);
        _storyboard.Completed += (_, _) => Finish();
        _storyboard.Begin(this, true);
        Focus();
    }

    private void Fade(UIElement target, double from, double to, int start, int duration)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
        {
            BeginTime = TimeSpan.FromMilliseconds(start),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        target.Opacity = from;
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));
        _storyboard!.Children.Add(animation);
    }

    public void Skip() => Finish();
    private void EnterLibrary_Click(object sender, RoutedEventArgs e) => Finish();
    private void Finish()
    {
        if (_finished) return;
        Stop();
        Visibility = Visibility.Collapsed;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // Closing the window stops the clock without a delayed callback reopening UI.
    public void Stop()
    {
        _finished = true;
        _storyboard?.Remove(this);
        _storyboard = null;
    }
}
