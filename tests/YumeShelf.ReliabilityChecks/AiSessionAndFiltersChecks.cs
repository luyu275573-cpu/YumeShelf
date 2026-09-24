using System.IO;
using System.Text.Json;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void AiSessionAndFiltersChecks()
    {
        var folder = Folder("session-and-filters");
        var game = GameAt(FakeExe(folder)); game.Title = "收藏样本"; game.IsFavorite = true;
        var second = GameAt(FakeExe(folder, "second.exe")); second.Title = "游玩样本"; second.TotalPlaySeconds = 60; second.LastLaunchStatus = "Running";
        var missing = GameAt(Path.Combine(folder, "missing.exe")); missing.Title = "失效样本"; missing.IsFavorite = true;
        AiGameSummary[] Snapshot() => new[] { game, second, missing }.Select(AiGameSummary.From).ToArray();
        var result = new AiLibraryQuery("list", Favorite: true, Played: false, State: "available").Execute(Snapshot(), true);
        Check(result.Contains("有 1 个") && result.Contains(game.Title) && !result.Contains(second.Title) && !result.Contains(missing.Title),
            "favorite, unplayed and availability filters intersect using current local records");
        result = new AiLibraryQuery("list", State: "missing").Execute(Snapshot(), true);
        Check(result.Contains(missing.Title) && result.Contains("不可访问") && !result.Contains(folder), "missing launch query reports inaccessible files without leaking paths or deleting entries");
        Check(new AiLibraryQuery("count", Played: true, State: "running").Execute(Snapshot(), true).Contains("有 1 个"), "running and played query uses actual recorded state");
        second.LastLaunchStatus = "Completed";
        Check(new AiLibraryQuery("count", State: "running").Execute(Snapshot(), true).Contains("有 0 个"), "next query observes a changed running state");
        var serialized = JsonSerializer.Serialize(Snapshot());
        Check(!serialized.Contains("LocalState") && !serialized.Contains("IsFavorite") && !serialized.Contains("HasPlayed") && !serialized.Contains(folder),
            "private play and availability filters are not uploaded in the model library summary");
        using (var server = new MockServer(["query"], Tool("library_query", new { operation = "list", favorite = true, played = false })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: Snapshot);
            Send(vm, "列出我收藏但没有游玩记录的游戏");
            Check(vm.Conversation.Last().Content.Contains("有 2 个") && vm.Status.Contains("工具 1 次"), "agent accepts combined boolean filters and renders verified results");
        }
        using (var server = new MockServer(["query"], Tool("library_query", new { operation = "list", favorite = "false" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: Snapshot); Send(vm, "看看未收藏游戏");
            Check(vm.Status.Contains("布尔值"), "string booleans cannot silently change a query filter");
        }
        Reject<InvalidOperationException>(() => new AiLibraryQuery("list", State: "deleted").Execute(Snapshot(), true), "unknown status filter is rejected");

        var path = Path.Combine(folder, "conversation.dat");
        var store = new AiSessionStore(path);
        var settings = new AppSettings();
        var first = new AiAssistantViewModel(settings, library: Snapshot, sessionStore: store);
        Send(first, "我的游戏库有哪些游戏"); first.Query = "待发送草稿"; first.SaveSession();
        var restored = new AiAssistantViewModel(settings, library: Snapshot, sessionStore: store);
        Check(restored.Conversation.Last().Content.Contains(game.Title) && restored.Query == "待发送草稿" && !restored.IsBusy && !restored.HasReview,
            "session restores text and unsent input without replaying requests or write approvals");
        Check(!File.ReadAllText(path).Contains(game.Title) && !File.ReadAllText(path).Contains("待发送草稿") && store.Load()!.History.Length == 2,
            "session uses Windows user protection and retains bounded completed context");
        var snapshot = store.Load()!;
        store.Save(snapshot with { HadPending = true, Messages = [new(true, "识别图中作品", "处理中", true)], History = [] });
        var pending = new AiAssistantViewModel(settings, library: Snapshot, sessionStore: store);
        Check(pending.Conversation.Single().State.Contains("附图未保存") && !pending.HasImage && !pending.HasReview && !pending.HasScanDraft && !pending.HasDocument,
            "restored image conversations disclose missing attachments and cannot restore executable tasks");
        pending.NewConversationCommand.Execute(null);
        Check(new AiAssistantViewModel(settings, sessionStore: store).Conversation.Count == 0, "new conversation clears persisted transcript across restart");
        store.Save(snapshot);
        var changed = new AiAssistantViewModel(settings with { AiModel = "other-model" }, sessionStore: store); changed.SaveSession();
        Check(store.Load()!.History.Length == 0 && changed.Conversation.Count > 0, "configuration change preserves visible transcript but isolates model context");
        File.WriteAllText(path, "corrupt-original");
        var damaged = new AiAssistantViewModel(settings, sessionStore: store); damaged.SaveSession();
        Check(damaged.Status.Contains("读取失败") && File.ReadAllText(path) == "corrupt-original", "damaged session is not overwritten by an empty automatic save");
        damaged.NewConversationCommand.Execute(null);
        Check(store.Load()!.Messages.Length == 0, "explicit new conversation recovers a damaged session");
        Reject<InvalidDataException>(() => store.Save(snapshot with { History = [new("system", "malicious")] }), "session rejects persisted system instructions");
        var blockedPath = Path.Combine(folder, "blocked-session"); Directory.CreateDirectory(blockedPath);
        var feedback = new YumeShelf.Common.OperationFeedback();
        var blocked = new AiAssistantViewModel(settings, feedback, sessionStore: new AiSessionStore(blockedPath));
        blocked.SaveSession();
        Check(feedback.Current?.Message.Contains("未能保存") == true, "session save failure is visible and does not crash or report successful persistence");
    }
}
