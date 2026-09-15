namespace YumeShelf.Common;

public enum FeedbackKind { Success, Info, Warning, Error, Progress }

public sealed record OperationNotice(int Sequence, string Message, FeedbackKind Kind, RelayCommand? Action = null, string? ActionLabel = null)
{
    public string Title => Kind switch
    {
        FeedbackKind.Success => "操作成功",
        FeedbackKind.Error => "操作未完成",
        FeedbackKind.Warning => "请注意",
        FeedbackKind.Progress => "正在处理",
        _ => "操作提示"
    };
    public string Glyph => Kind switch { FeedbackKind.Success => "✓", FeedbackKind.Error => "!", FeedbackKind.Warning => "!", FeedbackKind.Progress => "…", _ => "i" };
    public bool AutoDismiss => Kind is FeedbackKind.Success or FeedbackKind.Info;
    public bool HasAction => Action is not null;
}

public sealed class OperationFeedback : ObservableObject
{
    private int _sequence;
    private OperationNotice? _current;
    public OperationNotice? Current { get => _current; private set { SetProperty(ref _current, value); OnPropertyChanged(nameof(IsVisible)); } }
    public bool IsVisible => Current is not null;
    public RelayCommand DismissCommand { get; }
    public OperationFeedback() => DismissCommand = new RelayCommand(_ => Dismiss());
    public void Show(string message, FeedbackKind kind = FeedbackKind.Success, RelayCommand? action = null, string? actionLabel = null)
        => Current = new OperationNotice(++_sequence, message, kind, action, actionLabel);
    public void Dismiss(int? sequence = null)
    {
        if (sequence is null || Current?.Sequence == sequence) Current = null;
    }
}
