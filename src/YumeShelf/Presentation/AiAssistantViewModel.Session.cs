using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YumeShelf.Infrastructure;
using YumeShelf.Common;

namespace YumeShelf.Presentation;

public sealed partial class AiAssistantViewModel
{
    private readonly AiSessionStore? _sessionStore;
    private bool _sessionReadFailed;
    private string ConfigurationIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { _settings.AiApiBaseUrl, _settings.AiModel, _settings.AiVisionModel, _settings.AiApiKeyProtected }))));

    private void RestoreSession()
    {
        if (_sessionStore is null) return;
        try
        {
            var saved = _sessionStore.Load();
            if (saved is null) return;
            Conversation.Clear();
            foreach (var message in saved.Messages)
                Conversation.Add(new(message.IsUser, message.Content)
                { State = message.HadImage ? "历史附图未保存；如需重新识别请再次选图。" : message.State });
            Query = saved.Query;
            if (saved.Configuration == ConfigurationIdentity && !saved.HadPending)
            {
                _history.AddRange(saved.History);
                if (_libraryAvailable()) GameContext = _library().FirstOrDefault(g => g.Id == saved.ContextId);
            }
            Status = "已恢复本机文字会话。图片、来源卡、待确认操作和撤销不恢复；历史库信息需重新查询。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            _sessionReadFailed = true;
            Status = "历史会话读取失败，原文件保留；点击新对话可重建，本地游戏库不受影响。";
            AppLog.Write("ai.session-load-failed", ex);
        }
    }

    public void SaveSession()
    {
        if (_sessionStore is null || _sessionReadFailed) return;
        try
        {
            var pending = IsBusy || HasReview || HasScanDraft || HasDocument;
            var messages = Conversation.TakeLast(40).Select(m => new SavedAiMessage(m.IsUser, Limit(m.Content, 24000),
                IsBusy && (ReferenceEquals(m, _retryReply) || ReferenceEquals(m, _retryUser)) ? "关闭时未完成，未自动重试。" : Limit(m.State, 1000),
                m.HasImage || m.State.Contains("历史附图未保存", StringComparison.Ordinal))).ToArray();
            while (messages.Sum(m => m.Content.Length) > 160000) messages = messages.Skip(2).ToArray();
            // Drafts/approvals are not persisted. Their model history must not survive as authorization.
            var history = pending ? [] : _history.TakeLast(12).ToArray();
            while (history.Sum(m => m.Content.Length) > 18000) history = history.Skip(2).ToArray();
            _sessionStore.Save(new(1, ConfigurationIdentity, messages, history, Limit(Query, 6000), pending ? null : GameContext?.Id, pending));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            AppLog.Write("ai.session-save-failed", ex);
            _feedback.Show("文字会话未能保存，请复制需要保留的内容；游戏库保存不受影响。", FeedbackKind.Warning);
        }
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];
}
