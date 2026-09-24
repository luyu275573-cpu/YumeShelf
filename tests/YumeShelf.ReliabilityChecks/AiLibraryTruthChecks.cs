using System.Diagnostics;
using System.Text.Json;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void AiLibraryTruthChecks()
    {
        var games = Enumerable.Range(1, 27).Select(i => new AiGameSummary(Guid.NewGuid(), $"真实作品{i:00}", "BGI", 2015, "", "剧情")
            { GameType = i % 2 == 0 ? "RPG" : "视觉小说" }).ToArray();
        IReadOnlyList<AiGameSummary> current = games.Take(3).ToArray();
        using (var server = new MockServer(Route("answer"), new Reply(200, Completion("共有5个游戏：ATRI、Summer Pockets、Muv-Luv、G线上的魔王、千恋万花"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => current);
            var watch = Stopwatch.StartNew(); Send(vm, "我现在的库里有几个游戏"); watch.Stop();
            Check(server.Requests.IsEmpty && vm.Conversation.Last().Content == "你的游戏库目前共有 3 个游戏。" && vm.Status.Contains("模型 0 次"),
                "reported question reads the live three-game library without requesting the fabricated five-game model answer");
            Console.WriteLine($"MEASURE: local library count = {watch.ElapsedMilliseconds} ms, model requests = 0");
            current = games.Take(7).ToArray(); Send(vm, "我的游戏库有哪些游戏？");
            Check(server.Requests.IsEmpty && vm.Conversation.Last().Content.Contains("共有 7 个游戏") && current.All(g => vm.Conversation.Last().Content.Contains(g.Title)),
                "local list uses the new snapshot after a library change rather than the earlier three-game conversation");
            current = []; Send(vm, "我有多少游戏");
            Check(vm.Conversation.Last().Content == "你的游戏库目前共有 0 个游戏。", "an actually empty library reports zero without a model guess");
        }
        var offline = new AiAssistantViewModel(new AppSettings { AiApiBaseUrl = "https://unused.invalid", AiApiKeyProtected = "" }, library: () => games);
        Send(offline, "我现在的库里有几个游戏");
        Check(offline.Conversation.Last().Content == "你的游戏库目前共有 27 个游戏。" && offline.Query == "", "simple library facts work offline without configuring an API key");
        var unavailable = new AiAssistantViewModel(new AppSettings(), library: () => games, libraryAvailable: () => false);
        Send(unavailable, "我的游戏库里有多少游戏");
        Check(unavailable.Conversation.Last().Content == AiLibraryQuery.Unavailable, "an unreadable or pending library is reported as unavailable, never as an empty library");
        offline.AllowLibrary = false; Send(offline, "我的游戏库有哪些游戏");
        Check(offline.Conversation.Last().Content == AiLibraryQuery.Unavailable, "direct read respects an unavailable library boundary");

        Check(new[] { "请告诉我，我的库里有多少游戏，顺便推荐一部", "我的库里有多少RPG游戏", "我的游戏库里的游戏有几个结局", "把游戏库里的游戏都删除", "图片里有几个游戏", "‘我现在的库里有几个游戏’用日语怎么说" }
            .All(q => AiLibraryQuery.TryDirect(q) is null), "local shortcut does not steal filtered, mixed, destructive, image or quoted-language requests");
        using (var server = new MockServer(["query"], Tool("library_query", new { operation = "count", field = "type", query = "RPG" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "帮我数数收藏库里登记为RPG的作品");
            Check(server.Requests.Count == 2 && vm.Conversation.Last().Content.Contains("共 27 个游戏") && vm.Conversation.Last().Content.Contains("有 13 个"),
                "semantic filtered count validates the task and renders stored types without a generative answer");
        }
        using (var server = new MockServer(["query"], Tool("library_query", new { operation = "list", offset = 20 })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "从第21项开始继续列出库里的作品");
            var answer = vm.Conversation.Last().Content;
            Check(answer.Contains("共有 27 个游戏") && answer.Contains("第 21–27 项，共 27 项") && games.Skip(20).All(g => answer.Contains(g.Title)) && !answer.Contains(games[0].Title),
                "paged library list separates total size, offset and returned rows without inventing entries");
        }
        var skipped = new Reply(200, Completion("{\"route\":\"answer\",\"answer_basis\":\"library\"}"));
        using (var server = new MockServer(["query"], skipped, Tool("library_query", new { operation = "list" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games.Take(3).ToArray()); Send(vm, "盘点一下我收录的作品");
            Check(server.Requests.Count == 3 && vm.Conversation.Last().Content.Contains("共有 3 个游戏") && vm.Status.Contains("工具 1 次"),
                "a model that bypasses library tools is redirected to a verified query rather than allowed to generate facts");
        }
        using (var server = new MockServer(["query"], skipped, skipped, new Reply(200, Completion("不能显示的假名单"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "盘点一下我收录的作品");
            Check(server.Requests.Count == 3 && vm.Status.Contains("避免编造") && !vm.Conversation.Last().Content.Contains("假名单") && vm.Query.Length > 0,
                "repeated ungrounded library answers stop after one repair attempt and retain the input");
        }
        using (var server = new MockServer(["query"], new Reply(200, Completion("{\"route\":\"answer\"}")), new Reply(200, Completion("{\"route\":\"answer\"}"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "盘点一下我的收藏");
            Check(server.Requests.Count == 3 && vm.Status.Contains("避免编造"), "missing answer basis cannot silently bypass the local evidence check");
        }
        using (var server = new MockServer(["query"], Tool("library_search", new { query = "真实作品" }), Tool("library_query", new { operation = "count" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "核对我的作品目录总量");
            var evidence = Messages(server.Requests.Last()).Last().GetProperty("content").GetString()!;
            using var json = JsonDocument.Parse(evidence[evidence.IndexOf('{')..]);
            Check(json.RootElement.GetProperty("totalCount").GetInt32() == 27 && json.RootElement.GetProperty("matchedCount").GetInt32() == 27 &&
                json.RootElement.GetProperty("returnedCount").GetInt32() == 5 && json.RootElement.GetProperty("hasMore").GetBoolean() && vm.Conversation.Last().Content.Contains("共有 27 个游戏"),
                "five search candidates are explicitly partial and cannot masquerade as the full library count");
        }
        using (var server = new MockServer(["knowledge"], Route("answer"), new Reply(200, Completion("这是作品相关的说明。"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games.Take(3).ToArray()); Send(vm, "视觉小说是什么");
            Check(new[] { server.Requests.First(), server.Requests.Last() }.All(body => Messages(body)[0].GetProperty("content").GetString()!.Contains("\"totalCount\":3")),
                "both routing and final answering carry the same authoritative local count even without a title match or tool result");
            Check(!server.Requests.ElementAt(1).Contains("totalCount") && !server.Requests.ElementAt(1).Contains("answer_basis"),
                "independent task verification does not inherit the router's proposed answer basis or library hints");
        }
        using (var server = new MockServer(["query"], Tool("library_query", new { operation = "list", titles = new[] { "伪造作品" } })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "盘点我的游戏");
            Check(vm.Status.Contains("未允许") && !vm.Conversation.Last().Content.Contains("伪造作品"), "query tools reject model-supplied titles and return only local records");
        }
        Reject<InvalidOperationException>(() => new AiLibraryQuery("count", Field: "type").Execute(games, true), "an empty type filter is rejected instead of accidentally counting the entire library");
    }
}
