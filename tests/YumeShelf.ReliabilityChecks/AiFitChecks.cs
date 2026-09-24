using System.IO;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void AiFitChecks()
    {
        var games = Enumerable.Range(1, 3).Select(i => new AiGameSummary(Guid.NewGuid(), $"审查游戏{i}", "BGI", 2015, "库中真实简介", "剧情")).ToArray();
        var update = Tool("propose_metadata", new { id = games[0].Id, fields = new { Description = "组合任务简介" }, reason = "用户指定值" });
        using (var server = new MockServer(["query"], Route("answer"), Tool("library_query", new { operation = "list" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games);
            Send(vm, "帮我盘点一下我收录了哪些作品");
            Check(server.Requests.Count == 3 && vm.Conversation.Last().Content.Contains("共有 3 个游戏") && games.All(g => vm.Conversation.Last().Content.Contains(g.Title)),
                "F1: general answer declaration cannot bypass independently verified library intent");
        }
        using (var server = new MockServer(["query"], Route("answer"), Route("answer"), new Reply(200, Completion("伪造名单"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "核对我的收藏目录");
            Check(server.Requests.Count == 3 && vm.Status.Contains("避免编造") && !vm.Conversation.Last().Content.Contains("伪造名单"),
                "F1: repeated general declarations stop before fabricated prose is requested");
        }
        using (var server = new MockServer(update, new Reply(200, Completion("{\"tasks\":[\"shell\"]}"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "修改简介");
            Check(!vm.HasReview && vm.Status.Contains("核对包含未知"), "invalid task verification fails closed before executing a proposed tool");
        }
        using (var server = new MockServer(["details"], Tool("game_details", new { id = games[0].Id }),
            new Reply(200, Completion("{\"route\":\"answer\",\"answer_basis\":\"library\"}")), new Reply(200, Completion("模型伪造简介"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "把审查游戏1在库里存的简介告诉我");
            Check(server.Requests.Count == 3 && vm.Conversation.Last().Content.Contains("库中真实简介") && !vm.Conversation.Last().Content.Contains("模型伪造简介") && vm.Conversation.Last().State == "已完成",
                "F3: game_details plus library answer renders exact stored fields without model rewriting");
        }
        using (var server = new MockServer(["query", "update"], Tool("library_query", new { operation = "list" }), update))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "列出库里的游戏，并把审查游戏1的简介改成组合任务简介");
            Check(server.Requests.Count == 3 && vm.HasReview && vm.Draft?.Fields.Single().Proposed == "组合任务简介" && vm.Conversation.Last().Content.Contains("共有 3 个游戏") && vm.Status.StartsWith("完成"),
                "F4: query and update both produce results before the composite task completes");
        }
        using (var server = new MockServer(["details", "update"], Tool("game_details", new { id = games[0].Id }), update))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "先展示原简介，再准备新简介");
            Check(vm.HasReview && vm.Conversation.Last().Content.Contains("库中真实简介"), "composite details and update retains the detail output alongside the draft");
        }
        using (var server = new MockServer(["query", "update"], Tool("library_query", new { operation = "list" }), new Reply(500, "synthetic failure")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games); Send(vm, "列出作品并修改简介");
            Check(vm.Conversation.Last().Content.Contains("共有 3 个游戏") && vm.Conversation.Last().Content.Contains("尚未完成") && vm.Conversation.Last().State == "部分完成" && vm.Query.Length > 0 && !vm.HasReview,
                "failed composite follow-up retains query and reports missing update without false success");
        }
        using (var server = new MockServer(["update", "knowledge"], update, Route("answer", 5000)))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games);
            vm.Query = "修改简介并介绍同类作品"; vm.SendCommand.Execute(null); PumpUntil(() => server.Requests.Count == 3 || !vm.IsBusy);
            vm.Cancel(); PumpUntil(() => !vm.IsBusy);
            Check(vm.HasReview && vm.Draft!.Fields.Single().Proposed == "组合任务简介" && vm.Conversation.Last().State == "部分完成" && vm.Query.Length > 0,
                "cancelled follow-up retains the prepared card edit and original question");
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var server = new MockServer(["knowledge"], Route("answer"), Sse(new(Delta("先显示")), new(Delta("后显示") + Done, Release: release.Task))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => games);
            vm.Query = "视觉小说是什么"; vm.SendCommand.Execute(null); PumpUntil(() => vm.Conversation.Last().Content == "先显示" || !vm.IsBusy);
            var streamed = vm.IsBusy && vm.Conversation.Last().Content == "先显示";
            release.SetResult(); PumpUntil(() => !vm.IsBusy);
            Check(streamed && vm.Conversation.Last().Content == "先显示后显示" && server.Requests.Count == 3,
                "normal knowledge answers remain genuinely streamed after task verification");
        }
        FitPendingAndClearChecks(); FitPathChecks();
    }

    private static void FitPendingAndClearChecks()
    {
        var file = Path.Combine(Folder("fit-library"), "library.json");
        var game = GameAt(FakeExe(Folder("fit-game"))); game.Title = "审查游戏"; game.Description = "原简介";
        game.Tags = ["剧情"]; game.Engine = "BGI"; game.GameType = "视觉小说"; game.ReleaseYear = 2015; game.ReleaseDate = "2015-10-23";
        new JsonGameStore(file).Save([game]); var host = Vm(file); var target = host.Games.Single();
        var root = Folder("fit pending scan"); var candidate = new GameScanCandidate(FakeExe(root), "待添加", "RenPy", "fixture", 100);
        using (var server = new MockServer(
            Tool("create_document", new { markdown = "# 资料草稿" }), TaskPlan("document"),
            Tool("scan_games", new { path = root }), TaskPlan("scan"),
            Tool("propose_metadata", new { id = game.Id, fields = new { Description = "新的简介" }, reason = "用户指定" }), TaskPlan("update"),
            Tool("propose_metadata", new { id = game.Id, fields = new { Description = "不能覆盖原选择" }, reason = "用户指定" }), TaskPlan("update")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => host.Games.Select(AiGameSummary.From).ToArray(),
                applyUpdate: host.ApplyAiUpdate, undoDraft: host.UndoAiDraft,
                scan: (_, _, _) => Task.FromResult(new GameScanResult([candidate], 1, 0, [])));
            Send(vm, "生成资料文档"); var document = vm.Document; var evidence = vm.DocumentEvidence;
            Send(vm, $"扫描 \"{root}\""); var scan = vm.ScanDraft; candidate.IsSelected = false;
            Send(vm, "把审查游戏的简介改为新的简介"); var draft = vm.Draft!; draft.Fields.Single().Accepted = true;
            Send(vm, "我现在的库里有几个游戏");
            Check(ReferenceEquals(vm.Draft, draft) && draft.Fields.Single().Accepted && vm.Document == document && vm.DocumentEvidence == evidence &&
                ReferenceEquals(vm.ScanDraft, scan) && !candidate.IsSelected && server.Requests.Count == 6,
                "F2: query preserves checked fields, document evidence and scan selection without new model calls");
            Send(vm, "把简介再改为不能覆盖原选择");
            Check(ReferenceEquals(vm.Draft, draft) && draft.Fields.Single().Accepted && vm.Status.Contains("已有同类待确认") && vm.Query.Length > 0,
                "a same-kind result cannot silently replace an existing checked review");
            CoverCommand(vm, vm.ApplyReviewCommand);
            Check(target.Description == "新的简介" && !vm.HasReview && vm.Document == document && ReferenceEquals(vm.ScanDraft, scan),
                "card save consumes only its review while keeping document and import drafts");
            vm.UndoDraftCommand.Execute(null);
            Check(target.Description == "原简介" && vm.Document == document && ReferenceEquals(vm.ScanDraft, scan), "undo preserves unrelated pending results");
            vm.DismissDocumentCommand.Execute(null);
            Check(!vm.HasDocument && ReferenceEquals(vm.ScanDraft, scan), "document dismissal preserves import candidates");
        }
        using (var server = new MockServer(["update"], Tool("propose_metadata", new
        { id = game.Id, fields = new { Description = "", Tags = "", Engine = "", GameType = "", ReleaseDate = "", ReleaseYear = "" }, reason = "用户明确要求清空" })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint)); Send(vm, "清空审查游戏的简介、标签、引擎、类型和发行日期年份");
            Check(vm.Draft?.Fields.Count == 6 && vm.Draft.Fields.All(f => f.Proposed == "" && f.ProposedDisplay == "（清空此项）") && target.Description == "原简介",
                "F5: optional-field clearing is explicitly shown in an uncommitted review");
            foreach (var field in vm.Draft!.Fields) field.Accepted = true;
            CoverCommand(vm, vm.ApplyReviewCommand); var stored = new JsonGameStore(file).Load(true).Single();
            Check(stored.Description == "" && stored.Tags.Count == 0 && stored.Engine == "" && stored.GameType == "" && stored.ReleaseYear is null && stored.ReleaseDate == "" && stored.Title == game.Title,
                "F5: confirmed clearing persists across reload without altering the mandatory title");
            vm.UndoDraftCommand.Execute(null); stored = new JsonGameStore(file).Load(true).Single();
            Check(stored.Description == "原简介" && stored.Tags.SequenceEqual(game.Tags) && stored.Engine == "BGI" && stored.GameType == "视觉小说" && stored.ReleaseYear == 2015 && stored.ReleaseDate == "2015-10-23",
                "F5: undo restores all cleared fields including linked date and year");
        }
        foreach (var field in new[] { "Title", "ExecutablePath" })
            Reject<InvalidOperationException>(() => AiMetadataDraft.ValidateField(field, ""), "mandatory or unauthorized fields cannot be cleared: " + field);
        using (var server = new MockServer(["update"], Tool("propose_metadata", new { id = game.Id, fields = new { Description = "待确认简介" }, reason = "用户指定" })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint)); Send(vm, "修改审查游戏简介"); var draft = vm.Draft!; draft.Fields.Single().Accepted = true;
            target.Description = "后续手动编辑";
            Send(vm, "我现在的库里有几个游戏"); CoverCommand(vm, vm.ApplyReviewCommand);
            Check(ReferenceEquals(vm.Draft, draft) && draft.Fields.Single().Accepted && target.Description == "后续手动编辑" && vm.Status.Contains("发生变化"),
                "manual edits preserve old review visibility but block stale saving after a follow-up query");
        }
    }

    private static void FitPathChecks()
    {
        var shortPath = Folder("fit scan target"); var fullPath = Folder("fit scan target extra");
        foreach (var (open, close) in new[] { ("\"", "\""), ("'", "'"), ("`", "`"), ("“", "”"), ("‘", "’"), ("", "") })
        {
            var question = $"扫描 {open}{fullPath}{close}";
            Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath(shortPath, question), "F6: reject a shorter authorized-path prefix: " + open);
            Check(AiLibraryTasks.ValidateScanPath(fullPath, question) == fullPath, "F6: accept exact complete path with spaces: " + open);
        }
        Check(AiLibraryTasks.ValidateScanPath(fullPath, $"扫描 \"{fullPath.Replace('\\', '/').ToUpperInvariant()}/\"") == fullPath,
            "equivalent separators, case and trailing slash resolve to the same exact target");
        Check(AiLibraryTasks.ValidateScanPath(shortPath, $"扫描 \"{fullPath}\" 和 \"{shortPath}\"") == shortPath,
            "independently quoted second path authorizes that exact target");
        Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath(shortPath, $"扫描 {shortPath} 然后整理游戏"), "ambiguous bare-path prose cannot authorize a guessed prefix");
        Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath(shortPath, $"扫描 \"{fullPath}\\{shortPath}\""), "embedded drive prefix cannot authorize a nested substring");
    }
}
