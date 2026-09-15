using System.IO;
using System.Net.Http;
using System.Windows.Media;
using YumeShelf.Common;
using YumeShelf.Infrastructure;
using YumeShelf.Application;
using YumeShelf.Application.AI;

namespace YumeShelf.Presentation;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly Action<AppSettings> _apply;
    private AppSettings _draft;
    private ImageSource? _previewImage;
    private string _errorMessage = string.Empty;
    private string _saveNotice = "切换页面保留草稿，确认后生效";
    private string _selectedSection = "应用美化";
    private string _aiApiKey = string.Empty;
    private bool _isAiTesting;
    private CancellationTokenSource? _testCancellation;
    private string? _credentialOrigin;

    public SettingsViewModel(AppSettings settings, Action<AppSettings> apply, OperationFeedback? feedback = null)
    {
        Feedback = feedback ?? new OperationFeedback();
        _draft = settings.Normalize() with { };
        _apply = apply;
        ClearBackgroundCommand = new RelayCommand(_ => ClearBackground());
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => { CloseRequested?.Invoke(false); Feedback.Show("已取消本次修改，恢复为已保存的设置。", FeedbackKind.Info); });
        _aiApiKey = SecureSecretStore.Unprotect(_draft.AiApiKeyProtected);
        _credentialOrigin = Origin(_draft.AiApiBaseUrl);
        TestAiConnectionCommand = new RelayCommand(_ => _ = TestAiConnectionAsync(), _ => !IsAiTesting);
        TestAiModelCommand = new RelayCommand(_ => _ = TestAiModelAsync(), _ => !IsAiTesting);
        try { PreviewImage = BackgroundImageLoader.Load(_draft.BackgroundImagePath ?? BuiltInBackgrounds.Resolve(_draft.ColorPalette, _draft.NightMode)); }
        catch (Exception exception) when (IsImageError(exception))
        {
            ErrorMessage = "原背景图片无法读取，请重新选择图片或恢复默认背景。";
        }
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ColorPalette) or nameof(NightMode) or nameof(BackgroundFileName)
                or nameof(BackgroundOpacity) or nameof(BackgroundBlur) or nameof(PageTransparency)
                or nameof(SimpleLayout) or nameof(AiApiBaseUrl) or nameof(AiModel) or nameof(AiTimeoutSeconds) or nameof(AiApiKey))
                SaveNotice = "有未保存的修改，点击确认后生效";
        };
    }

    public event Action<bool>? CloseRequested;
    public OperationFeedback Feedback { get; }
    public string SaveNotice { get => _saveNotice; set => SetProperty(ref _saveNotice, value); }
    public IReadOnlyList<string> Sections { get; } = ["应用美化", "AI 检索", "页面分栏"];
    // ColorPalette remains only to load existing built-in background choices from older settings.
    public string ColorPalette { get => _draft.ColorPalette; set { _draft = _draft with { ColorPalette = value }; RefreshPreview(); OnPropertyChanged(); } }
    public bool NightMode { get => _draft.NightMode; set { _draft = _draft with { NightMode = value }; RefreshPreview(); OnPropertyChanged(); } }
    public AppSettings DraftSettings => _draft;
    public string SelectedSection { get => _selectedSection; set => SetProperty(ref _selectedSection, value); }
    public string? BackgroundFileName => Path.GetFileName(_draft.BackgroundImagePath);
    public ImageSource? PreviewImage { get => _previewImage; private set { SetProperty(ref _previewImage, value); OnPropertyChanged(nameof(HasBackground)); } }
    public bool HasBackground => PreviewImage is not null;
    public bool HasCustomBackground => !string.IsNullOrWhiteSpace(_draft.BackgroundImagePath);
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string AiApiBaseUrl
    {
        get => _draft.AiApiBaseUrl;
        set
        {
            if (_draft.AiApiBaseUrl == value) return;
            _draft = _draft with { AiApiBaseUrl = value };
            var origin = Origin(value);
            if (origin is not null && origin != _credentialOrigin && _aiApiKey.Length > 0)
            {
                CancelPendingTest();
                _aiApiKey = string.Empty;
                OnPropertyChanged(nameof(AiApiKey));
                AiStatus = "服务地址已更改，请重新填写该服务的 API Key。";
                OnPropertyChanged(nameof(AiStatus));
                Feedback.Show(AiStatus, FeedbackKind.Warning);
            }
            if (origin is not null) _credentialOrigin = origin;
            OnPropertyChanged();
        }
    }
    public string AiModel { get => _draft.AiModel; set { _draft = _draft with { AiModel = value }; OnPropertyChanged(); } }
    public int AiTimeoutSeconds { get => _draft.AiTimeoutSeconds; set { _draft = _draft with { AiTimeoutSeconds = value }; OnPropertyChanged(); } }
    public string AiApiKey { get => _aiApiKey; set { if (SetProperty(ref _aiApiKey, value)) _credentialOrigin = Origin(AiApiBaseUrl); } }
    public string AiStatus { get; private set; } = "尚未测试连接";
    public bool IsAiTesting { get => _isAiTesting; private set { if (SetProperty(ref _isAiTesting, value)) { TestAiModelCommand.RaiseCanExecuteChanged(); TestAiConnectionCommand.RaiseCanExecuteChanged(); } } }
    public double BackgroundOpacity
    {
        get => _draft.BackgroundOpacity;
        set { _draft = (_draft with { BackgroundOpacity = value }).Normalize(); OnPropertyChanged(); }
    }
    public double BackgroundBlur
    {
        get => _draft.BackgroundBlur;
        set { _draft = (_draft with { BackgroundBlur = value }).Normalize(); OnPropertyChanged(); }
    }
    public double PageTransparency
    {
        get => _draft.PageTransparency;
        set { _draft = (_draft with { PageTransparency = value }).Normalize(); OnPropertyChanged(); }
    }
    public bool SimpleLayout
    {
        get => _draft.SimpleLayout;
        set { _draft = _draft with { SimpleLayout = value }; OnPropertyChanged(); OnPropertyChanged(nameof(FullLayout)); }
    }
    public bool FullLayout { get => !SimpleLayout; set { if (value) SimpleLayout = false; } }
    public RelayCommand ClearBackgroundCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand TestAiConnectionCommand { get; }
    public RelayCommand TestAiModelCommand { get; }

    public void SelectBackground(string path)
    {
        try
        {
            var image = BackgroundImageLoader.Load(path);
            _draft = _draft with { BackgroundImagePath = Path.GetFullPath(path) };
            PreviewImage = image;
            OnPropertyChanged(nameof(BackgroundFileName));
            ErrorMessage = string.Empty;
            Feedback.Show("已选择背景图片；点击“确认并保存”后应用。", FeedbackKind.Info);
        }
        catch (Exception exception) when (IsImageError(exception))
        {
            ErrorMessage = "这张图片无法读取，请选择有效的 PNG、JPG、BMP 或 GIF 图片。";
            Feedback.Show(ErrorMessage, FeedbackKind.Error);
        }
    }

    private void ClearBackground()
    {
        _draft = _draft with { BackgroundImagePath = null };
        RefreshPreview();
        OnPropertyChanged(nameof(BackgroundFileName));
        ErrorMessage = string.Empty;
        if (ColorPalette == "跟随壁纸配色") ColorPalette = "樱粉色";
        Feedback.Show("已预览默认背景；点击“确认并保存”后应用。", FeedbackKind.Info);
    }
    private void RefreshPreview()
    {
        if (HasCustomBackground) return;
        try { PreviewImage = BackgroundImageLoader.Load(BuiltInBackgrounds.Resolve(_draft.ColorPalette, _draft.NightMode)); }
        catch (Exception exception) when (IsImageError(exception)) { ErrorMessage = "内置背景无法读取。"; }
    }

    private void Confirm()
    {
        try
        {
            OpenAiCompatibleProvider.ValidateConfiguration(AiApiBaseUrl, AiModel);
            if (AiTimeoutSeconds is < 5 or > 120) throw new AiConfigurationException("超时时间必须为 5–120 秒。配置尚未保存。");
            // Reload before saving: a selected file may have moved since preview.
            _ = BackgroundImageLoader.Load(_draft.BackgroundImagePath);
            _apply(_draft with { AiApiKeyProtected = SecureSecretStore.Protect(_aiApiKey) });
            ErrorMessage = string.Empty;
            SaveNotice = "设置已保存";
            Feedback.Show("设置已保存并应用。", FeedbackKind.Success);
            CloseRequested?.Invoke(true);
        }
        catch (AiConfigurationException exception)
        {
            ErrorMessage = exception.Message;
            SaveNotice = "保存失败，请检查 AI 配置";
            Feedback.Show(ErrorMessage, FeedbackKind.Error);
        }
        catch (Exception exception) when (IsImageError(exception) || exception is System.Security.Cryptography.CryptographicException)
        {
            AppLog.Write("settings.save-failed", exception);
            ErrorMessage = "设置未保存。请确认背景图片仍可读取，且应用数据目录可写，再重试。";
            SaveNotice = "保存失败，修改尚未应用";
            Feedback.Show(ErrorMessage, FeedbackKind.Error);
        }
    }

    private async Task TestAiConnectionAsync()
    {
        if (IsAiTesting) return;
        if (string.IsNullOrWhiteSpace(AiApiKey)) { AiStatus = "请先填写 API Key。"; OnPropertyChanged(nameof(AiStatus)); Feedback.Show(AiStatus, FeedbackKind.Warning); return; }
        IsAiTesting = true; AiStatus = "正在检查服务…"; OnPropertyChanged(nameof(AiStatus));
        Feedback.Show(AiStatus, FeedbackKind.Progress);
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        try
        {
            await new OpenAiCompatibleProvider().TestConnectionAsync(AiApiBaseUrl, AiApiKey, AiModel, TimeSpan.FromSeconds(Math.Clamp(AiTimeoutSeconds, 5, 120)), cancellation.Token);
            AiStatus = "服务可连接；请继续测试模型回答。";
            if (!cancellation.IsCancellationRequested) Feedback.Show(AiStatus);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException or System.Text.Json.JsonException)
        {
            AppLog.Write("ai.service-test-failed", exception);
            AiStatus = exception is OperationCanceledException ? "服务检查已停止或超时，请重试。" : exception is InvalidOperationException ? exception.Message : "服务检查失败，请检查 API 地址、Key 和网络连接。";
            if (!cancellation.IsCancellationRequested) Feedback.Show(AiStatus, FeedbackKind.Error);
        }
        finally { _testCancellation = null; IsAiTesting = false; OnPropertyChanged(nameof(AiStatus)); }
    }

    private async Task TestAiModelAsync()
    {
        if (IsAiTesting) return;
        if (string.IsNullOrWhiteSpace(AiApiKey)) { AiStatus = "请先填写 API Key。"; OnPropertyChanged(nameof(AiStatus)); Feedback.Show(AiStatus, FeedbackKind.Warning); return; }
        IsAiTesting = true; AiStatus = "正在测试模型回答…"; OnPropertyChanged(nameof(AiStatus));
        Feedback.Show(AiStatus, FeedbackKind.Progress);
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        try
        {
            await new OpenAiCompatibleProvider().TestModelAsync(AiApiBaseUrl, AiApiKey, AiModel, TimeSpan.FromSeconds(Math.Clamp(AiTimeoutSeconds, 5, 120)), cancellation.Token);
            AiStatus = "模型回答测试成功，所填模型可以生成内容。";
            if (!cancellation.IsCancellationRequested) Feedback.Show(AiStatus);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException or System.Text.Json.JsonException)
        {
            AppLog.Write("ai.model-test-failed", exception);
            AiStatus = exception is OperationCanceledException ? "模型测试已停止或超时，请重试。" : exception is InvalidOperationException ? exception.Message : "模型测试失败，请检查模型名称、服务权限和网络连接。";
            if (!cancellation.IsCancellationRequested) Feedback.Show(AiStatus, FeedbackKind.Error);
        }
        finally { _testCancellation = null; IsAiTesting = false; OnPropertyChanged(nameof(AiStatus)); }
    }

    public void CancelPendingTest() => _testCancellation?.Cancel();
    private static string? Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null;

    private static bool IsImageError(Exception exception) => GameImageLoader.IsImageError(exception);
}
