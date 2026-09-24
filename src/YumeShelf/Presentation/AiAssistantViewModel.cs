using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YumeShelf.Application.AI;
using YumeShelf.Application;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Presentation;

public sealed partial class AiAssistantViewModel : ObservableObject
{
    private AppSettings _settings;
    private readonly List<AiMessage> _history = [];
    private CancellationTokenSource? _activeCancellation;
    private string _query = string.Empty;
    private string _status = "就绪";
    private bool _isBusy;
    private bool _isPreparingImage;
    private AiImageAttachment? _image;
    private AiConversationItem? _retryUser;
    private AiConversationItem? _retryReply;
    private readonly OperationFeedback _feedback;
    private readonly Func<IReadOnlyList<AiGameSummary>> _library;
    private readonly Func<bool> _libraryAvailable;
    private readonly bool _verifyTaskPlan;
    private readonly Func<AiGameSummary?> _selected;
    private readonly Func<AiMetadataDraft, string?>? _applyDraft;
    private readonly Func<string?>? _undoDraft;
    private AiGameSummary? _gameContext;
    private bool _allowLibrary = true;
    private bool _allowNetwork = true;
    private int _configurationVersion;
    private AiMetadataDraft? _draft;
    private string? _document;
    private bool _canUndo;
    private string _retryContext = "";

    public AiAssistantViewModel(AppSettings settings, OperationFeedback? feedback = null,
        Func<IReadOnlyList<AiGameSummary>>? library = null, Func<AiGameSummary?>? selected = null,
        Func<AiMetadataDraft, string?>? applyDraft = null, Func<string?>? undoDraft = null,
        Func<AiCoverDraft, AiCoverCandidate, AiImageAttachment, string?>? applyCover = null, Func<string?>? undoCover = null, VndbSearch? search = null,
        Func<AiMetadataDraft, AiCoverCandidate?, AiImageAttachment?, string?>? applyUpdate = null,
        Func<string, GameScanMode, CancellationToken, Task<GameScanResult>>? scan = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<GameImportResult>>? import = null, Func<bool>? libraryAvailable = null,
        AiSessionStore? sessionStore = null)
    {
        _feedback = feedback ?? new OperationFeedback();
        _sessionStore = sessionStore;
        _settings = settings.Normalize();
        _library = library ?? (() => []); _selected = selected ?? (() => null);
        _libraryAvailable = libraryAvailable ?? (() => true);
        _verifyTaskPlan = library is not null;
        _applyDraft = applyDraft; _undoDraft = undoDraft;
        _applyCover = applyCover; _undoCover = undoCover; _search = search ?? new VndbSearch();
        _applyUpdate = applyUpdate; _scan = scan; _import = import;
        Conversation = [new AiConversationItem(false, "我是 Yume，你的本地游戏库助手。可以直接告诉我“补充樱之诗的封面和简介”，或给我一个文件夹查找游戏。也可以选择图片或按 Ctrl+V 粘贴截图。修改与入库结果会先展示给你，确认后保存。")];
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanEditInput && (!string.IsNullOrWhiteSpace(Query) || HasImage));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        RetryCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanEditInput && MatchesRetry(EffectiveQuestion, Image));
        RemoveImageCommand = new RelayCommand(_ => { if (CanEditInput) { Image = null; Status = "已移除待发送的图片。"; } }, _ => CanEditInput && HasImage);
        AttachGameCommand = new RelayCommand(_ => AttachGame(), _ => CanEditInput);
        ClearGameCommand = new RelayCommand(_ => { if (CanEditInput) { GameContext = null; ClearContextHistory(); } }, _ => CanEditInput && GameContext is not null);
        ApplyDraftCommand = new RelayCommand(_ => ApplyDraft(), _ => CanEditInput && Draft is not null && _applyDraft is not null);
        DismissDraftCommand = new RelayCommand(_ => { if (CanEditInput) Draft = null; }, _ => CanEditInput && Draft is not null);
        UndoDraftCommand = new RelayCommand(_ => UndoDraft(), _ => CanEditInput && _canUndo && _undoDraft is not null);
        ExportDocumentCommand = new RelayCommand(_ => ExportDocument(), _ => CanEditInput && Document is not null);
        DismissDocumentCommand = new RelayCommand(_ => { Document = null; Status = "已放弃资料草稿。"; }, _ => CanEditInput && HasDocument);
        NewConversationCommand = new RelayCommand(_ => { if (!CanEditInput) return; ClearContextHistory(); GameContext = null; _lastSelectionId = null; Conversation.Clear(); _sessionReadFailed = false; Status = "已开始新对话，旧文字记录已清除，待发送内容保留。"; SaveSession(); }, _ => CanEditInput);
        InitializeCoverCommands();
        InitializeLibraryCommands();
        RestoreSession();
    }
    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value)) return;
            OnPropertyChanged(nameof(QueryCount));
            SendCommand.RaiseCanExecuteChanged(); RetryCommand.RaiseCanExecuteChanged();
        }
    }
    public string QueryCount => $"{Query.Length} / {AiLimits.QuestionCharacters}";
    private string EffectiveQuestion => string.IsNullOrWhiteSpace(Query) && HasImage ? DefaultImageQuestion : Query.Trim();
    public AiImageAttachment? Image
    {
        get => _image;
        private set
        {
            if (!SetProperty(ref _image, value)) return;
            OnPropertyChanged(nameof(HasImage)); RefreshCommands();
        }
    }
    public bool HasImage => Image is not null;
    public bool IsPreparingImage { get => _isPreparingImage; private set { if (SetProperty(ref _isPreparingImage, value)) RefreshCommands(); } }
    public bool CanEditInput => !IsBusy && !IsPreparingImage;
    public bool IsInputLocked => !CanEditInput;
    public string ImageSendHint => $"点击发送后，图片将交给配置的 AI 服务（{_settings.AiApiBaseUrl}）分析一次，后续步骤复用文字线索；每轮最多 6 次模型调用。";
    public string VisionModelHint => $"识图模型：{(string.IsNullOrWhiteSpace(_settings.AiVisionModel) ? _settings.AiModel + "（沿用对话模型）" : _settings.AiVisionModel)}；需支持图片输入。";
    public ObservableCollection<AiConversationItem> Conversation { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommands();
        }
    }
    public RelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand RemoveImageCommand { get; }
    public RelayCommand AttachGameCommand { get; }
    public RelayCommand ClearGameCommand { get; }
    public RelayCommand ApplyDraftCommand { get; }
    public RelayCommand DismissDraftCommand { get; }
    public RelayCommand UndoDraftCommand { get; }
    public RelayCommand ExportDocumentCommand { get; }
    public RelayCommand DismissDocumentCommand { get; }
    public RelayCommand NewConversationCommand { get; }
    public AiGameSummary? GameContext { get => _gameContext; private set { SetProperty(ref _gameContext, value); OnPropertyChanged(nameof(GameContextLabel)); RefreshCommands(); } }
    public string GameContextLabel => GameContext is null ? "按作品名或上下文查找游戏" : "当前上下文：" + GameContext.Title;
    public bool AllowLibrary { get => _allowLibrary; set { if (CanEditInput && SetProperty(ref _allowLibrary, value)) ClearContextHistory(); } }
    public bool AllowNetwork { get => _allowNetwork; set { if (CanEditInput && SetProperty(ref _allowNetwork, value)) ClearContextHistory(); } }
    public string DraftEvidence { get; private set; } = "";
    public string DocumentEvidence { get; private set; } = "";
    public AiMetadataDraft? Draft { get => _draft; private set { SetProperty(ref _draft, value); OnPropertyChanged(nameof(HasDraft)); RefreshCommands(); } }
    public bool HasDraft => Draft is not null;
    public string? Document { get => _document; private set { SetProperty(ref _document, value); OnPropertyChanged(nameof(HasDocument)); RefreshCommands(); } }
    public bool HasDocument => Document is not null;
    private string ContextIdentity => $"{AllowLibrary}:{AllowNetwork}:{GameContext?.Id}:{GameContext?.Revision}";

    private void AttachGame()
    {
        if (!CanEditInput) return;
        var selected = _selected();
        if (selected is null) { ShowValidation("请先在游戏库中选中一个游戏。"); return; }
        GameContext = selected; ClearContextHistory();
        CoverQuery = DefaultCoverQuery(selected.Title);
        Status = "已关联游戏摘要，下次发送时提供标题、引擎、年份、简介与标签；不包含本地路径。";
    }
    private void ClearContextHistory(bool discardPending = true)
    {
        _history.Clear(); _retryUser = null; _retryReply = null;
        if (discardPending) { Draft = null; Document = null; CoverDraft = null; Research = null; ScanDraft = null; }
        RetryCommand.RaiseCanExecuteChanged();
    }
    private void ApplyDraft()
    {
        if (!CanEditInput || Draft is null || _applyDraft is null) return;
        try
        {
            var error = _applyDraft(Draft);
            if (error is not null) { ShowValidation(error); return; }
            _canUndo = true; Draft = null; _history.Clear(); GameContext = null;
            Status = "已保存勾选的游戏资料，可撤销本次修改。"; _feedback.Show(Status);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowValidation("资料建议无法应用，请重新生成后审核。"); }
    }
    private void UndoDraft()
    {
        if (!CanEditInput || !_canUndo || _undoDraft is null) return;
        var error = _undoDraft?.Invoke();
        if (error is not null) { ShowValidation(error); return; }
        _canUndo = false; var id = GameContext?.Id; ClearContextHistory(false); GameContext = _library().FirstOrDefault(g => g.Id == id); RefreshCommands();
        Status = "已撤销上次游戏卡片修改。"; _feedback.Show(Status); Conversation.Add(new(false, Status));
        _history.Add(new("user", "用户已确认撤销上次游戏卡片修改。")); _history.Add(new("assistant", Status));
        SaveSession();
    }
    private void ExportDocument()
    {
        if (!CanEditInput || Document is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出 Yume 资料草稿", Filter = "Markdown 文档|*.md|HTML 文档|*.html", FileName = "Yume-游戏资料", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            AiDocumentExporter.Save(dialog.FileName, Document);
            Status = "资料草稿已导出。"; _feedback.Show(Status);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { ShowValidation(ex is InvalidDataException ? ex.Message : "导出失败，请检查保存位置与写入权限。"); }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditInput)); OnPropertyChanged(nameof(IsInputLocked));
        SendCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged(); RemoveImageCommand.RaiseCanExecuteChanged();
        AttachGameCommand?.RaiseCanExecuteChanged(); ClearGameCommand?.RaiseCanExecuteChanged();
        ApplyDraftCommand?.RaiseCanExecuteChanged(); DismissDraftCommand?.RaiseCanExecuteChanged();
        UndoDraftCommand?.RaiseCanExecuteChanged(); ExportDocumentCommand?.RaiseCanExecuteChanged(); DismissDocumentCommand?.RaiseCanExecuteChanged(); NewConversationCommand?.RaiseCanExecuteChanged();
        RefreshCoverCommands();
        RefreshLibraryCommands();
    }
    private bool MatchesRetry(string question, AiImageAttachment? image)
        => _retryUser is not null && _retryUser.Content == question && _retryUser.ImageId == image?.Id && _retryContext == ContextIdentity;

    public Task<bool> SelectImageAsync(string path) => PrepareImageAsync(() => AiImageAttachment.FromFile(path));
    public Task<bool> PasteImageAsync(BitmapSource bitmap)
    {
        if (!CanEditInput) return Task.FromResult(false);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 40_000_000)
        { ShowValidation("图片尺寸无效或超过 4000 万像素，请裁剪后重试。"); return Task.FromResult(false); }
        // Clipboard objects belong to the UI thread; freeze an owned snapshot before decoding in the worker.
        var snapshot = bitmap.IsFrozen ? bitmap : bitmap.CloneCurrentValue(); snapshot.Freeze();
        return PrepareImageAsync(() => AiImageAttachment.FromBitmap(snapshot));
    }
    private async Task<bool> PrepareImageAsync(Func<AiImageAttachment> prepare)
    {
        if (!CanEditInput) return false;
        IsPreparingImage = true; Status = "正在准备图片…";
        try
        {
            Image = await Task.Run(prepare);
            Status = "图片已就绪，可直接发送识别，也可以补充问题。";
            return true;
        }
        catch (Exception ex) when (GameImageLoader.IsImageError(ex))
        {
            ShowValidation(ex is InvalidDataException ? ex.Message : "图片无法读取，请选择有效的 PNG、JPG 或 BMP 图片（不超过 32 MB）。");
            return false;
        }
        finally { IsPreparingImage = false; }
    }
    public void ReportImageError(string message) => ShowValidation(message);

    private async Task SendAsync()
    {
        if (!CanEditInput) return;
        var originalQuery = Query.Trim();
        var question = EffectiveQuestion;
        var attachment = Image;
        // Selection is an implicit context hint; a named library target can override it in the tool call.
        if (_selected() is { } active && active.Id != _lastSelectionId)
        { _lastSelectionId = active.Id; GameContext = active; }
        var libraryAvailable = AllowLibrary && _libraryAvailable();
        var library = libraryAvailable ? _library() : [];
        if (GameContext is not null)
        {
            var current = library.FirstOrDefault(g => g.Id == GameContext.Id);
            if (current is null) { GameContext = null; ClearContextHistory(false); }
            else if (current.Revision != GameContext.Revision) { GameContext = current; ClearContextHistory(false); }
        }
        if (question.Length == 0) { ShowValidation("请输入问题。"); return; }
        if (question.Length > AiLimits.QuestionCharacters) { ShowValidation($"问题超过 {AiLimits.QuestionCharacters} 字符，请精简后发送。"); return; }
        var settings = _settings;
        var configurationVersion = _configurationVersion;
        var model = attachment is not null && !string.IsNullOrWhiteSpace(settings.AiVisionModel) ? settings.AiVisionModel : settings.AiModel;
        var directRead = attachment is null && AiLibraryQuery.TryDirect(question) is not null;
        var key = directRead ? "" : SecureSecretStore.Unprotect(settings.AiApiKeyProtected);
        if (!directRead)
        {
            if (string.IsNullOrWhiteSpace(key)) { ShowValidation("请先在设置中配置 API Key。"); return; }
            try { OpenAiCompatibleProvider.ValidateConfiguration(settings.AiApiBaseUrl, model); }
            catch (AiConfigurationException ex) { ShowValidation(ex.Message); return; }
        }

        using var userCancellation = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.AiTaskTimeoutSeconds));
        _activeCancellation = userCancellation;
        IsBusy = true; Status = attachment is null ? "理解问题…" : "正在查看图片并判断任务范围…";
        if (!MatchesRetry(question, attachment))
        {
            _retryUser = new AiConversationItem(true, question, attachment?.Preview, attachment?.Id);
            _retryReply = new AiConversationItem(false, "");
            _retryContext = ContextIdentity;
            Conversation.Add(_retryUser); Conversation.Add(_retryReply);
        }
        var userMessage = _retryUser!;
        var reply = _retryReply!;
        userMessage.State = "处理中"; reply.State = Status; reply.Content = "";
        TrimConversation();
        var agent = new YumeAgent(_search, _scan);
        var received = new StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stage = "判断任务";
        var refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        refresh.Tick += (_, _) =>
        {
            if (reply.Content.Length != received.Length) reply.Content = received.ToString();
            Status = $"{stage} · {watch.Elapsed.TotalSeconds:0} 秒"; reply.State = Status;
        };
        refresh.Start();
        try
        {
            var result = await agent.RunAsync(settings, key, question, attachment, _history.ToArray(),
                library, GameContext, value => stage = value,
                chunk => { received.Append(chunk.Text); if (received.Length == chunk.Text.Length) reply.Content = received.ToString(); }, deadline.Token, AllowNetwork, _selected(), libraryAvailable, _verifyTaskPlan);
            if (result.Complete) deadline.Token.ThrowIfCancellationRequested();
            received.Clear().Append(result.Answer); reply.Content = result.Answer;
            var newReview = result.Draft is not null || result.Cover is not null || result.Research?.Sources.Count > 0;
            var conflict = (newReview && HasReview) || (result.Scan is not null && HasScanDraft) || (result.Document is not null && HasDocument);
            if (conflict)
                throw new InvalidOperationException("已有同类待确认结果，已保留原来的内容与选择。请先保存或放弃原结果，再重试本次请求。");
            if (newReview)
            {
                Draft = result.Draft;
                CoverDraft = result.Cover;
                Research = result.Research;
            }
            if (result.Document is not null) Document = result.Document;
            if (result.Scan is not null) ScanDraft = result.Scan;
            if (result.Target is not null) GameContext = result.Target;
            if (CoverDraft is not null) { IsCoverSearchOpen = true; CoverQuery = DefaultCoverQuery(CoverDraft.Original.Title); }
            if (newReview && Research?.Sources.Count == 1)
            {
                SelectResearchSource(Research.Sources[0]); stage = "准备修改预览";
                Status = reply.State = stage;
                await LoadReviewPreviewAsync(deadline.Token, userCancellation.Token);
            }
            refresh.Stop();
            reply.State = !result.Complete ? "部分完成" : result.Streaming ? "已完成" : "已完成（服务返回非流式回答）"; userMessage.State = result.Complete ? "已回答" : "部分完成";
            foreach (var source in result.Sources) reply.Sources.Add(source);
            if (newReview) DraftEvidence = result.Sources.Count > 0 ? "本轮已取得 VNDB 来源，请逐项核对；来源不代表每个字段均已证实。" : "模型建议，未联网核实。请核对依据并勾选要保存的字段。";
            if (result.Document is not null) DocumentEvidence = result.Sources.Count > 0 ? "资料草稿 · 含 VNDB 来源，需核对" : "资料草稿 · 未联网核实";
            OnPropertyChanged(nameof(DraftEvidence)); OnPropertyChanged(nameof(DocumentEvidence));
            if (result.Sources.Count > 0 && result.Document is not null)
                Document += "\n\n来源（VNDB）\n" + string.Join("\n", result.Sources.Select(s => $"- [{s.Id}] {s.Title} — {s.Url}（取得于 {s.RetrievedAt:yyyy-MM-dd HH:mm} UTC）"));
            if (result.Remember && configurationVersion == _configurationVersion)
            {
                _history.Add(new AiMessage("user", question + (attachment is null ? "" : "\n[用户曾附图；历史不重发图片。此前识别可能不准确，再次核对画面需重新附图。]")));
                var historyAnswer = result.Answer;
                if (newReview && Draft is not null) historyAnswer += "\n[尚未保存的资料草稿]\n" + string.Join("\n", Draft.Fields.Select(f => $"{f.Label}：{f.ProposedDisplay}")) + "\n依据：" + Draft.Reason;
                if (result.Document is not null) historyAnswer += "\n[尚未保存的资料文档草稿]\n" + result.Document[..Math.Min(result.Document.Length, 6000)];
                _history.Add(new AiMessage("assistant", historyAnswer));
                while (_history.Count > 12 || _history.Sum(x => x.Content.Length) > AiLimits.ContextCharacters) _history.RemoveRange(0, 2);
            }
            _retryUser = null; _retryReply = null; Query = result.Complete ? "" : originalQuery; Image = result.Complete ? null : attachment;
            Status = agent.ModelCalls == 0 ? $"完成 · 本地查询 · 模型 0 次 · 工具 {agent.ToolCalls} 次 · {watch.ElapsedMilliseconds} 毫秒" :
                $"完成 · 模型 {agent.ModelCalls} 次 · 工具 {agent.ToolCalls} 次 · {watch.Elapsed.TotalSeconds:0.#} 秒";
            if (!result.Complete) Status = "部分完成 · 已保留结果和原问题；请核对未完成事项。";
        }
        catch (OperationCanceledException)
        {
            Fail(userCancellation.IsCancellationRequested ? "已停止，问题已保留。" : $"{agent.Stage}超时（已用 {watch.Elapsed.TotalSeconds:0} 秒），本轮未完成。可重试，或在设置中检查模型工具能力与服务响应。", userCancellation.IsCancellationRequested ? FeedbackKind.Info : FeedbackKind.Error);
        }
        catch (AiTruncatedException ex)
        {
            if (agent.Stage is "正在回答" or "等待模型正文") received.Clear().Append(ex.PartialAnswer);
            Fail(agent.Stage is "正在回答" or "等待模型正文" ? "回答达到长度上限，尚未完成。请缩小问题范围或分步提问。" : "问题判断未完成，请重试。", FeedbackKind.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            AppLog.Write("ai.request-failed", ex);
            Fail(ex is InvalidOperationException ? ex.Message : "无法完成回答，请检查网络和服务配置后重试。", FeedbackKind.Error);
        }
        finally
        {
            refresh.Stop();
            AppLog.Write($"ai.finished.models-{agent.ModelCalls}.tools-{agent.ToolCalls}.ms-{watch.ElapsedMilliseconds}");
            if (ReferenceEquals(_activeCancellation, userCancellation)) _activeCancellation = null;
            IsBusy = false;
            SaveSession();
        }
        void Fail(string message, FeedbackKind kind)
        {
            refresh.Stop(); reply.Content = received.ToString();
            if (reply.Content.Length > 0) message += " 已接收的正文已保留，回答未完成。";
            Query = originalQuery; Status = message;
            userMessage.State = "未完成"; reply.State = message;
            _feedback.Show(message, kind, RetryCommand, "重试");
        }
    }

    private void TrimConversation()
    {
        while (Conversation.Count > AiLimits.VisibleMessages)
        {
            var count = Conversation[0].IsUser ? 2 : 1;
            for (var i = 0; i < count && Conversation.Count > 0; i++) Conversation.RemoveAt(0);
        }
    }
    private void ShowValidation(string message) { Status = message; _feedback.Show(message, FeedbackKind.Warning); }

    public sealed class AiConversationItem(bool isUser, string content, BitmapSource? imagePreview = null, Guid? imageId = null) : ObservableObject
    {
        private string _content = content;
        private string _state = "";
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsUser { get; } = isUser;
        public BitmapSource? ImagePreview { get; } = imagePreview;
        public Guid? ImageId { get; } = imageId;
        public bool HasImage => ImagePreview is not null;
        public ObservableCollection<AiSource> Sources { get; } = [];
        public string Content { get => _content; set => SetProperty(ref _content, value); }
        public string State { get => _state; set => SetProperty(ref _state, value); }
        public string RoleLabel => IsUser ? "用户" : "Yume";
    }

    public void Cancel() => _activeCancellation?.Cancel();
    public void UpdateSettings(AppSettings settings)
    {
        if (_settings.AiApiBaseUrl != settings.AiApiBaseUrl || _settings.AiModel != settings.AiModel ||
            _settings.AiVisionModel != settings.AiVisionModel || _settings.AiApiKeyProtected != settings.AiApiKeyProtected)
        {
            _configurationVersion++; _history.Clear();
        }
        _settings = settings.Normalize();
        SaveSession();
        OnPropertyChanged(nameof(ImageSendHint)); OnPropertyChanged(nameof(VisionModelHint));
    }

    private const string DefaultImageQuestion = "请尝试识别图片中的 Galgame 或视觉小说，说明候选作品及画面依据；不确定时请明确说明。";
}
