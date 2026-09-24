using System.IO;
using System.Text.Json;
using YumeShelf.Application.AI;

namespace YumeShelf.Infrastructure;

public sealed record SavedAiMessage(bool IsUser, string Content, string State, bool HadImage);
public sealed record AiSessionSnapshot(int Version, string Configuration, SavedAiMessage[] Messages,
    AiMessage[] History, string Query, Guid? ContextId, bool HadPending);

// One bounded session, protected for the current Windows user. Never serialize attachments,
// credentials, executable paths from the library, tool plans, or write approvals.
public sealed class AiSessionStore(string path)
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    public AiSessionSnapshot? Load()
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("会话文件超过大小限制。");
        using var reader = new StreamReader(stream);
        var text = SecureSecretStore.Unprotect(reader.ReadToEnd());
        if (string.IsNullOrEmpty(text)) throw new InvalidDataException("会话文件无法解密。");
        var snapshot = JsonSerializer.Deserialize<AiSessionSnapshot>(text) ?? throw new InvalidDataException("会话文件为空。");
        Validate(snapshot);
        return snapshot;
    }

    public void Save(AiSessionSnapshot snapshot)
    {
        Validate(snapshot);
        var text = SecureSecretStore.Protect(JsonSerializer.Serialize(snapshot));
        if (text.Length > MaxFileBytes) throw new InvalidDataException("会话文件超过大小限制。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }

    private static void Validate(AiSessionSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Configuration is null || snapshot.Configuration.Length > 128 ||
            snapshot.Messages is null || snapshot.Messages.Length > 40 || snapshot.History is null || snapshot.History.Length > 12 ||
            snapshot.Query is null || snapshot.Query.Length > 6000 ||
            snapshot.Messages.Any(m => m is null || m.Content is null || m.Content.Length > 24000 || m.State is null || m.State.Length > 1000) ||
            snapshot.History.Any(m => m is null || m.Image is not null || m.Role is not ("user" or "assistant") || m.Content is null || m.Content.Length > 24000) ||
            snapshot.Messages.Sum(m => m.Content.Length) > 160000 || snapshot.History.Sum(m => m.Content.Length) > 18000)
            throw new InvalidDataException("会话文件格式或长度无效。");
    }
}
