using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Presentation;

public sealed class AiAssistantViewModel : ObservableObject
{
    private AppSettings _settings;
    private readonly List<AiMessage> _history = [];
    private CancellationTokenSource? _activeCancellation;
    private string _query = string.Empty;
    private string _status = "就绪";
    private bool _isBusy;
    private AiConversationItem? _retryUser;
    private AiConversationItem? _retryReply;
    private readonly OperationFeedback _feedback;

    public AiAssistantViewModel(AppSettings settings, OperationFeedback? feedback = null)
    {
        _feedback = feedback ?? new OperationFeedback();
        _settings = settings.Normalize();
        Conversation = [new AiConversationItem(false, "请告诉我你想了解的 Galgame、视觉小说，或 YumeShelf 的使用问题。")];
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Query));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        RetryCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsBusy && _retryUser is not null && Query.Trim() == _retryUser.Content);
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
    public ObservableCollection<AiConversationItem> Conversation { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            SendCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); RetryCommand.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }

    private async Task SendAsync()
    {
        if (IsBusy) return;
        var question = Query.Trim();
        if (question.Length == 0) { ShowValidation("请输入问题。"); return; }
        if (question.Length > AiLimits.QuestionCharacters) { ShowValidation($"问题超过 {AiLimits.QuestionCharacters} 字符，请精简后发送。"); return; }
        var settings = _settings;
        var key = SecureSecretStore.Unprotect(settings.AiApiKeyProtected);
        if (string.IsNullOrWhiteSpace(key)) { ShowValidation("请先在设置中配置 API Key。"); return; }
        try { OpenAiCompatibleProvider.ValidateConfiguration(settings.AiApiBaseUrl, settings.AiModel); }
        catch (AiConfigurationException ex) { ShowValidation(ex.Message); return; }

        using var userCancellation = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.AiTimeoutSeconds));
        _activeCancellation = userCancellation;
        IsBusy = true; Status = "理解问题…";
        if (_retryUser is null || _retryUser.Content != question)
        {
            _retryUser = new AiConversationItem(true, question);
            _retryReply = new AiConversationItem(false, "");
            Conversation.Add(_retryUser); Conversation.Add(_retryReply);
        }
        var userMessage = _retryUser;
        var reply = _retryReply!;
        userMessage.State = "处理中"; reply.State = "理解问题…"; reply.Content = "";
        TrimConversation();
        try
        {
            var provider = new OpenAiCompatibleProvider();
            var timeout = TimeSpan.FromSeconds(settings.AiTimeoutSeconds);
            var routeText = await provider.CompleteAsync(settings.AiApiBaseUrl, key, settings.AiModel,
                BuildMessages(RouterInstructions, question), timeout, deadline.Token, new AiCompletionOptions(0.1, 200));
            deadline.Token.ThrowIfCancellationRequested();
            var route = ParseRoute(routeText);
            string answer;
            if (route.Route == "out_of_scope") answer = "我是 Yume，只处理 Galgame、视觉小说及 YumeShelf 使用相关的问题。";
            else if (route.Route == "intro") answer = "我是 Yume，专注于 Galgame、视觉小说资料、攻略、启动排错和 YumeShelf 使用。";
            else if (route.Route == "clarify") answer = route.Clarification ?? "请补充具体的作品名称或你想了解的内容。";
            else
            {
                Status = route.Route is "research" or "metadata" or "document" ? "整理问题（当前尚未接入联网检索）…" : "整理回答…";
                reply.State = Status;
                answer = await provider.CompleteAsync(settings.AiApiBaseUrl, key, settings.AiModel,
                    BuildMessages(AgentInstructions, question), timeout, deadline.Token, new AiCompletionOptions(0.2, 1200));
            }
            deadline.Token.ThrowIfCancellationRequested();
            reply.Content = answer; reply.State = "已完成"; userMessage.State = "已回答";
            if (route.Route != "out_of_scope")
            {
                _history.Add(new AiMessage("user", question)); _history.Add(new AiMessage("assistant", answer));
                while (_history.Count > 12 || _history.Sum(x => x.Content.Length) > AiLimits.ContextCharacters) _history.RemoveRange(0, 2);
            }
            _retryUser = null; _retryReply = null; Query = ""; Status = "完成";
        }
        catch (OperationCanceledException)
        {
            Fail(userCancellation.IsCancellationRequested ? "已停止，问题已保留。" : "请求超时，问题已保留，请重试。", userCancellation.IsCancellationRequested ? FeedbackKind.Info : FeedbackKind.Error);
        }
        catch (AiTruncatedException ex)
        {
            reply.Content = ex.PartialAnswer;
            Fail("回答达到长度上限，尚未完成。请缩小问题范围或分步提问。", FeedbackKind.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            AppLog.Write("ai.request-failed", ex);
            Fail(ex is InvalidOperationException ? ex.Message : "无法完成回答，请检查网络和服务配置后重试。", FeedbackKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_activeCancellation, userCancellation)) _activeCancellation = null;
            IsBusy = false;
        }
        void Fail(string message, FeedbackKind kind)
        {
            Query = question; Status = message;
            userMessage.State = "未完成"; reply.State = message;
            _feedback.Show(message, kind, RetryCommand, "重试");
        }
    }

    private IReadOnlyList<AiMessage> BuildMessages(string instruction, string question)
    {
        var messages = new List<AiMessage> { new("system", instruction) };
        messages.AddRange(_history); messages.Add(new("user", question));
        return messages;
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

    private static RouteResult ParseRoute(string text)
    {
        if (text.Length > 2048) throw new InvalidOperationException("任务分类响应过长，请重试。");
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) json = json.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("route", out var field) || field.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("任务分类响应无效，请重试。");
        var route = field.GetString();
        if (route is not ("intro" or "out_of_scope" or "clarify" or "answer" or "research" or "metadata" or "document"))
            throw new InvalidOperationException("任务分类响应无效，请重试。");
        string? clarification = null;
        if (root.TryGetProperty("clarification", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > AiLimits.ClarificationCharacters)
                throw new InvalidOperationException("澄清问题格式无效或过长，请重试。");
            clarification = string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
        }
        return new RouteResult(route, clarification);
    }
    private sealed record RouteResult(string Route, string? Clarification);
    public sealed class AiConversationItem(bool isUser, string content) : ObservableObject
    {
        private string _content = content;
        private string _state = "";
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsUser { get; } = isUser;
        public string Content { get => _content; set => SetProperty(ref _content, value); }
        public string State { get => _state; set => SetProperty(ref _state, value); }
        public string RoleLabel => IsUser ? "用户" : "Yume";
    }

    private const string RouterInstructions = "你是 Yume 的任务路由器。只判断当前请求的意图和 Galgame 范围，不回答问题，不输出解释或思维过程。结合历史理解指代。只输出 JSON：{\"route\":\"intro|out_of_scope|clarify|answer|research|metadata|document\",\"clarification\":null}。intro=问候/询问能力；out_of_scope=实质无关；clarify=作品或任务不足以处理；answer=已有知识可回答；research=需要联网资料；metadata=补全游戏字段；document=生成资料页。出现 Galgame 名称不等于整个请求相关，混合请求按主要相关任务处理。";
    private const string AgentInstructions = "你是 Yume，YumeShelf 内置的 Galgame 与视觉小说专用助手。只处理 Galgame、视觉小说、作品资料、推荐、攻略、版本汉化、相关启动排错及 YumeShelf 使用。结合历史理解省略表达；不相关内容只简短说明服务范围。默认中文，先给结论，默认避免关键剧透。当前不能访问网页、执行本地命令、读取任意文件、启动或关闭游戏、修改文件或未经确认写入游戏库。没有检索结果不能声称已联网查证；未知信息要明确说明，不编造来源或成功记录。";
    public void Cancel() => _activeCancellation?.Cancel();
    public void UpdateSettings(AppSettings settings) => _settings = settings.Normalize();
}
