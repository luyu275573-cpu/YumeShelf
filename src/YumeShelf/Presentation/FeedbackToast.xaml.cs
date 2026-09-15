using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YumeShelf.Common;

namespace YumeShelf.Presentation;

public partial class FeedbackToast : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private OperationFeedback? _feedback;
    private int _sequence;

    public FeedbackToast()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(); };
        _timer.Tick += (_, _) => { _timer.Stop(); _feedback?.Dismiss(_sequence); };
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => RestartTimer();
        IsKeyboardFocusWithinChanged += (_, _) => { _timer.Stop(); RestartTimer(); };
    }

    private void Attach()
    {
        Detach();
        _feedback = DataContext as OperationFeedback;
        if (_feedback is not null) _feedback.PropertyChanged += FeedbackChanged;
        Present();
    }

    private void Detach()
    {
        _timer.Stop();
        if (_feedback is not null) _feedback.PropertyChanged -= FeedbackChanged;
        _feedback = null;
    }

    private void FeedbackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationFeedback.Current)) Present();
    }

    private void Present()
    {
        _timer.Stop();
        if (_feedback?.Current is not { } notice) return;
        _sequence = notice.Sequence;
        if (SystemParameters.ClientAreaAnimation)
            Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        RestartTimer();
        UIElementAutomationPeer.FromElement(NoticeText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RestartTimer()
    {
        if (_feedback?.Current?.AutoDismiss == true && !IsMouseOver && !IsKeyboardFocusWithin) _timer.Start();
    }
}
