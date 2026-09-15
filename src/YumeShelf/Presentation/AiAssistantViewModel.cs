using System.Collections.ObjectModel;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Presentation;
public sealed class AiAssistantViewModel : ObservableObject
{
    private AppSettings _settings; private readonly List<AiMessage> _history = []; private CancellationTokenSource? _activeCancellation; private string _query = string.Empty; private string _status = "就绪"; private bool _isBusy;
    private readonly OperationFeedback _feedback;
    public AiAssistantViewModel(AppSettings settings, OperationFeedback? feedback = null)
    {
        _feedback = feedback ?? new OperationFeedback();
        _settings = settings;
        Conversation = [new AiConversationItem(false, "请告诉我你想了解的 Galgame、视觉小说，或 YumeShelf 的使用问题。")];
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Query));
        CancelCommand = new RelayCommand(_ => _activeCancellation?.Cancel(), _ => IsBusy);
    }
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) { SendCommand.RaiseCanExecuteChanged(); } } }
    public ObservableCollection<AiConversationItem> Conversation { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { SendCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); } } }
    public RelayCommand SendCommand { get; } public RelayCommand CancelCommand { get; }
    private async Task SendAsync()
    {
        if (IsBusy) return;
        var userQuery = Query.Trim();
        if (userQuery.Length == 0) { Status = "请输入问题。"; _feedback.Show(Status, FeedbackKind.Info); return; }
        // A settings save affects the next turn, never half of an active turn.
        var settings = _settings;
        var key = SecureSecretStore.Unprotect(settings.AiApiKeyProtected);
        if (string.IsNullOrWhiteSpace(key)) { Status = "请先在设置中配置 API Key。"; _feedback.Show(Status, FeedbackKind.Warning); return; }
        using var cancellation = new CancellationTokenSource();
        _activeCancellation = cancellation; IsBusy = true; Status = "理解问题…";
        Conversation.Add(new AiConversationItem(true, userQuery));
        try
        {
            var provider = new OpenAiCompatibleProvider();
            var timeout = TimeSpan.FromSeconds(Math.Min(settings.AiTimeoutSeconds, 60));
            var routeMessages = new List<AiMessage> { new("system", RouterInstructions) };
            routeMessages.AddRange(_history.TakeLast(12));
            routeMessages.Add(new AiMessage("user", userQuery));
            var routeText = await provider.CompleteAsync(settings.AiApiBaseUrl, key, settings.AiModel, routeMessages, timeout, cancellation.Token, new AiCompletionOptions(0.1, 200));
            var route = ParseRoute(routeText);
            string answer;
            if (route.Route == "out_of_scope") answer = "我是 Yume，只处理 Galgame、视觉小说及 YumeShelf 使用相关的问题。";
            else if (route.Route == "intro") answer = "我是 Yume，专注于 Galgame、视觉小说资料、攻略、启动排错和 YumeShelf 使用。";
            else if (route.Route == "clarify") answer = string.IsNullOrWhiteSpace(route.Clarification) ? "请补充具体的作品名称或你想了解的内容。" : route.Clarification;
            else
            {
                Status = route.Route is "research" or "metadata" or "document" ? "整理问题（当前尚未接入联网检索）…" : "整理回答…";
                var answerMessages = new List<AiMessage> { new("system", AgentInstructions) };
                answerMessages.AddRange(_history.TakeLast(12)); answerMessages.Add(new AiMessage("user", userQuery));
                answer = await provider.CompleteAsync(settings.AiApiBaseUrl, key, settings.AiModel, answerMessages, timeout, cancellation.Token, new AiCompletionOptions(0.2, 1200));
            }
            Conversation.Add(new AiConversationItem(false, answer));
            _history.Add(new AiMessage("user", userQuery)); _history.Add(new AiMessage("assistant", answer));
            Query = string.Empty; Status = "完成";
        }
        catch (OperationCanceledException)
        {
            Query = userQuery;
            Status = cancellation.IsCancellationRequested ? "已停止，问题已保留。" : "请求超时，问题已保留，请重试。";
            _feedback.Show(Status, cancellation.IsCancellationRequested ? FeedbackKind.Info : FeedbackKind.Error);
        }
        catch (Exception) { Query = userQuery; Status = "Yume 暂时无法完成回答，请检查服务配置或重试。问题已保留。"; _feedback.Show(Status, FeedbackKind.Error); }
        finally { if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null; IsBusy = false; }
    }

    private static RouteResult ParseRoute(string text)
    {
        var json = text.Trim(); if (json.StartsWith("```") ) json = json.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();
        using var document = System.Text.Json.JsonDocument.Parse(json); var root = document.RootElement;
        var route = root.TryGetProperty("route", out var routeElement) ? routeElement.GetString() : null;
        var allowed = new[] { "intro", "out_of_scope", "clarify", "answer", "research", "metadata", "document" };
        if (route is null || !allowed.Contains(route, StringComparer.Ordinal)) throw new InvalidOperationException("AI 路由响应无效，请重试。");
        return new RouteResult(route, root.TryGetProperty("clarification", out var clarification) ? clarification.GetString() : null);
    }
    private sealed record RouteResult(string Route, string? Clarification);
    public sealed record AiConversationItem(bool IsUser, string Content)
    {
        public string RoleLabel => IsUser ? "用户" : "Yume";
    }
    private const string RouterInstructions = "你是 Yume 的任务路由器。只判断当前请求的意图和 Galgame 范围，不回答问题，不输出解释或思维过程。结合历史理解指代。只输出 JSON：{\"route\":\"intro|out_of_scope|clarify|answer|research|metadata|document\",\"clarification\":null}。intro=问候/询问能力；out_of_scope=实质无关；clarify=作品或任务不足以处理；answer=已有知识可回答；research=需要联网资料；metadata=补全游戏字段；document=生成资料页。出现 Galgame 名称不等于整个请求相关，混合请求按主要相关任务处理。";
    private const string AgentInstructions = "你是 Yume，YumeShelf 内置的 Galgame 与视觉小说专用助手。只处理 Galgame、视觉小说、作品资料、推荐、攻略、版本汉化、相关启动排错及 YumeShelf 使用。结合历史理解省略表达；不相关内容只简短说明服务范围。默认中文，先给结论，默认避免关键剧透。当前不能访问网页、执行本地命令、读取任意文件、启动或关闭游戏、修改文件或未经确认写入游戏库。没有检索结果不能声称已联网查证；未知信息要明确说明，不编造来源或成功记录。";

    public void Cancel() => _activeCancellation?.Cancel();
    public void UpdateSettings(AppSettings settings) => _settings = settings;
}
