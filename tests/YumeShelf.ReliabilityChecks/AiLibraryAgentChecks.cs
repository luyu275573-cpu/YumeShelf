using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static AiAssistantViewModel Librarian(MainWindowViewModel host, AppSettings settings, VndbSearch? search = null) => new(settings,
        library: () => host.Games.Select(AiGameSummary.From).ToArray(), selected: () => host.SelectedGame is null ? null : AiGameSummary.From(host.SelectedGame),
        applyDraft: host.ApplyAiDraft, undoDraft: host.UndoAiDraft, search: search, applyUpdate: host.ApplyAiUpdate, scan: host.ScanAiGamesAsync, import: host.ImportAiGamesAsync);

    private static void AiLibraryAgentChecks()
    {
        var directory = Folder("librarian"); var libraryFile = Path.Combine(directory, "library.json");
        var sakura = GameAt(FakeExe(Folder("librarian-sakura"))); sakura.Title = "樱之诗V1.2"; sakura.Description = "原简介"; sakura.Engine = "BGI";
        var another = GameAt(FakeExe(Folder("librarian-another"))); another.Title = "另一个游戏";
        new JsonGameStore(libraryFile).Save([sakura, another]); var host = Vm(libraryFile); host.SelectedGame = host.Games[1];
        var target = host.Games[0]; var before = File.ReadAllText(libraryFile); var png = File.ReadAllBytes(VisionFixture());
        var posts = 0; var downloads = 0; var previewTimeout = false;
        using var client = new HttpClient(new SearchHandler(request =>
        {
            if (request.Method != HttpMethod.Post) { downloads++; if (previewTimeout) throw new TaskCanceledException("Synthetic thumbnail timeout"); return CoverBytes(png); }
            posts++;
            return CoverJson(new { id = "v562", title = "Sakura no Uta", alttitle = "サクラノ詩", description = "Source synopsis.", released = "2015-10-23",
                image = new { url = CoverFixture.ImageUrl, thumbnail = CoverFixture.ThumbnailUrl, sexual = 0, violence = 0 } });
        }));
        var search = new VndbSearch(client);
        Check(AiLibraryTasks.Search(host.Games.Select(AiGameSummary.From).ToArray(), "帮我更新樱之诗的基本信息").Single().Id == sakura.Id,
            "local title hints find a named game despite the local version suffix and surrounding request text");
        using (var server = new MockServer(["update"], Tool("prepare_game_update", new { id = sakura.Id, query = "樱之诗", fields = new[] { "Cover", "Description", "ReleaseDate", "Engine", "GameType" } })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint), search);
            Send(vm, "更新樱之诗的封面、简介、日期、引擎和类型");
            Check(vm.AllowLibrary && vm.AllowNetwork && vm.Draft?.Original.Id == sakura.Id && vm.GameContext?.Id == sakura.Id && vm.HasCoverPreview && vm.AcceptCover,
                "a named target overrides a different UI selection without association buttons or permission toggles");
            Check(server.Requests.Count == 2 && posts == 1 && downloads == 1 && vm.Draft!.Fields.Select(f => f.Field).Order().SequenceEqual(new[] { "Description", "ReleaseDate" }),
                "one composite tool collects text and cover, previews automatically, and leaves unsupported engine and genre values untouched");
            Check(File.ReadAllText(libraryFile) == before && target.CoverPath is null, "agent research and preview do not mutate the library before confirmation");
            Check(!server.Requests.First().Contains("ExecutablePath") && !server.Requests.First().Contains("PRIVATE") && vm.Research!.Original.Id == sakura.Id,
                "automatic library access provides bounded summaries rather than executable paths or launch data");
            LibrarianLayout(vm);
            Directory.CreateDirectory(libraryFile + ".tmp"); CoverCommand(vm, vm.ApplyReviewCommand);
            Check(vm.HasReview && target.Description == "原简介" && target.CoverPath is null && File.ReadAllText(libraryFile) == before &&
                Directory.GetFiles(Path.Combine(directory, "Covers")).Length == 0, "failed combined save rolls back both metadata and cover without partial success");
            Directory.Delete(libraryFile + ".tmp"); CoverCommand(vm, vm.ApplyReviewCommand);
            var saved = new JsonGameStore(libraryFile).Load(true).Single(g => g.Id == sakura.Id);
            Check(!vm.HasReview && vm.CanUndoReview && saved.Description == "Source synopsis." && saved.ReleaseDate == "2015-10-23" && saved.ReleaseYear == 2015 && File.Exists(saved.CoverPath),
                "one confirmation persists selected metadata and cover together, including full release date");
            Check(ReferenceEquals(target, host.Games[0]) && target.Engine == "BGI" && target.GameType == "" && host.Games[1].Description == "",
                "combined update preserves the original game object and does not touch unselected fields or other games");
            target.TotalPlaySeconds = 999; vm.UndoDraftCommand.Execute(null);
            Check(!vm.CanUndoReview && target.Description == "原简介" && target.CoverPath is null && target.ReleaseDate == "" && target.ReleaseYear is null &&
                new JsonGameStore(libraryFile).Load(true).First().TotalPlaySeconds == 999, "single undo restores the entire combined card edit while preserving newer play time");
            vm.Query = "待发送的问题"; vm.NewConversationCommand.Execute(null);
            Check(vm.GameContext is null && !vm.HasReview && vm.Conversation.Count == 0 && vm.Query == "待发送的问题",
                "new conversation clears the previous implicit target while preserving unsent input");
        }

        previewTimeout = true;
        using (var server = new MockServer(["update"], Tool("prepare_game_update", new { id = sakura.Id, query = "樱之诗", fields = new[] { "Cover", "Description" } })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint), search); Send(vm, "更新樱之诗的简介和封面");
            Check(vm.Draft?.Fields.Single().Field == "Description" && !vm.HasCoverPreview && vm.HasCoverChoices && vm.Query == "" &&
                vm.Conversation.Last().State == "已完成" && server.Requests.Count == 2,
                "optional thumbnail timeout preserves completed research and permits text-only review without another model call");
            CoverCommand(vm, vm.ApplyReviewCommand);
            Check(target.Description == "Source synopsis." && target.CoverPath is null, "text-only confirmation after thumbnail timeout leaves the existing cover intact");
            vm.UndoDraftCommand.Execute(null);
        }
        previewTimeout = false;

        using (var server = new MockServer(["update"], Tool("library_search", new { query = "樱之诗" }), Tool("game_details", new { id = sakura.Id }),
            Tool("propose_metadata", new { id = sakura.Id, fields = new { Title = "樱之诗", GameType = "视觉小说", Engine = "BGI", Tags = "日语,剧情", ReleaseDate = "2015-10" }, reason = "用户明确给出的修订值。" })))
        {
            host.SelectedGame = null; var vm = Librarian(host, AiSettings(server.Endpoint));
            Send(vm, "把樱之诗的名字改为樱之诗，类型设为视觉小说，标签日语和剧情，发行日期2015年10月");
            Check(server.Requests.Count == 4 && vm.Draft?.Original.Id == sakura.Id && vm.GameContext?.Id == sakura.Id && target.GameType == "",
                "agent can search, inspect, and prepare a named card update without any main-window selection");
            foreach (var field in vm.Draft!.Fields) field.Accepted = true;
            CoverCommand(vm, vm.ApplyReviewCommand);
            Check(target.GameType == "视觉小说" && target.Title == "樱之诗" && target.ReleaseDate == "2015-10" && target.Tags.SequenceEqual(new[] { "日语", "剧情" }),
                "explicit user corrections support title, genre, tags and partial dates through the same commit");
            host.SearchText = "视觉小说"; Check(host.GamesView.Cast<object>().Count() == 1, "game type participates in library search"); host.SearchText = "";
            vm.UndoDraftCommand.Execute(null);
        }
        Reject<InvalidOperationException>(() => AiMetadataDraft.ValidateField("ReleaseDate", "2025-02-30"), "invalid calendar dates cannot enter an AI card draft");
        var wrong = new AiMetadataDraft(AiGameSummary.From(target), [new("ReleaseDate", "", "2015-10-23") { Accepted = true }, new("ReleaseYear", "", "2016") { Accepted = true }], "test");
        Check(host.ApplyAiUpdate(wrong, null, null)?.Contains("冲突") == true && target.ReleaseYear is null, "conflicting release year and date are rejected atomically");

        using (var server = new MockServer(["update"], Tool("propose_metadata", new { id = Guid.NewGuid(), fields = new { Title = "不存在的作品" }, reason = "test" })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint)); Send(vm, "修改一个不存在的游戏");
            Check(!vm.HasReview && vm.Status.Contains("不在当前游戏库") && host.Games.Count == 2, "a fabricated target ID cannot create or overwrite a game");
        }
        var ambiguousLibrary = new[] { AiGameSummary.From(target), AiGameSummary.From(target) with { Id = Guid.NewGuid(), Title = "樱之诗V2.0" } };
        using (var server = new MockServer(["update"], Tool("library_search", new { query = "樱之诗" }), Route("clarify")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => ambiguousLibrary); Send(vm, "更新樱之诗");
            Check(server.Requests.Count == 3 && vm.GameContext is null && !vm.HasReview, "ambiguous matches remain a clarification rather than silently binding the first game");
        }
        var sources = new[] { CoverFixture.Source, CoverFixture.Source with { Id = "v563", Title = "另一版本", Url = "https://vndb.org/v563", Released = "2016-01-01" } };
        var research = new AiGameResearch(AiGameSummary.From(target), ["ReleaseDate"], sources, []);
        Check(research.Select(sources[1]).Fields.Single().Proposed == "2016-01-01", "source selection changes the proposed values to the selected version only");
        Reject<InvalidOperationException>(() => research.Select(sources[1] with { Id = "v999" }), "an unreturned source cannot be selected into a card draft");
        LibrarianScanChecks();
    }

    private static void LibrarianScanChecks()
    {
        var root = Folder("librarian-scan"); Directory.CreateDirectory(Path.Combine(root, "renpy")); Directory.CreateDirectory(Path.Combine(root, "game"));
        var exe = FakeExe(root, "Novel.exe"); File.WriteAllText(Path.Combine(root, "game", "script.rpy"), "label start:\n return");
        var file = Path.Combine(Folder("librarian-import-library"), "library.json"); var host = Vm(file);
        using (var server = new MockServer(["scan"], Tool("scan_games", new { path = root, mode = "visual_novel" })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint)); Send(vm, $"扫描并添加这个文件夹：\"{root}\"");
            Check(vm.ScanDraft?.Result.Count == 1 && host.Games.Count == 0 && !File.Exists(file) && server.Requests.Count == 2,
                "a verified folder request runs the real scanner and returns local candidates without importing or a generative answer");
            var page = new AiAssistantPage { DataContext = vm }; var window = new Window { Content = page };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground"); Render((FrameworkElement)page.Content, "librarian-import-small.png", 680, 540); window.Close();
            vm.ScanDraft!.Result[0].IsSelected = false; CoverCommand(vm, vm.ApplyImportCommand);
            Check(host.Games.Count == 0 && vm.Status.Contains("选取"), "empty candidate selection cannot import anything");
            vm.ScanDraft.Result[0].IsSelected = true; Directory.CreateDirectory(file + ".tmp"); CoverCommand(vm, vm.ApplyImportCommand);
            Check(host.Games.Count == 0 && vm.HasScanDraft && vm.Status.Contains("失败 1"), "import save failure retains candidates and reports failure instead of success");
            Directory.Delete(file + ".tmp"); CoverCommand(vm, vm.ApplyImportCommand);
            Check(host.Games.Count == 1 && !vm.HasScanDraft && File.Exists(exe) && new JsonGameStore(file).Load(true).Single().ExecutablePath == exe,
                "confirmed conversation import uses persistent shared import logic without executing the game");
        }
        using (var server = new MockServer(["scan"], Tool("scan_games", new { path = root, mode = "visual_novel" })))
        {
            var vm = Librarian(host, AiSettings(server.Endpoint)); Send(vm, $"再扫描 \"{root}\"");
            Check(vm.ScanDraft?.Result.Count == 0 && host.Games.Count == 1, "conversational rescan excludes already imported games");
        }
        var parent = Path.GetDirectoryName(root)!;
        Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath(parent, $"扫描 \"{root}\""), "model cannot broaden a specified folder to its parent");
        Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath(root, "搜索游戏"), "paths invented by the model are rejected before file access");
        Reject<InvalidOperationException>(() => AiLibraryTasks.ValidateScanPath("\\\\server\\share", "扫描 \\\\server\\share"), "UNC and remote shares cannot be used as local scanning targets");
        Check(AiLibraryTasks.ValidateScanPath(exe, $"添加 \"{exe}\"") == exe, "an explicitly supplied local EXE is accepted without executing it");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Reject<OperationCanceledException>(() => Await(host.ScanAiGamesAsync(root, GameScanMode.VisualNovel, canceled.Token)), "local conversational scan propagates cancellation");
    }

    private static void LibrarianLayout(AiAssistantViewModel vm)
    {
        var page = new AiAssistantPage { DataContext = vm }; var window = new Window { Content = page }; window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        var root = (FrameworkElement)page.Content; Render(root, "librarian-combined-review.png", 920, 700);
        Render(root, "librarian-combined-small.png", 680, 540);
        var buttons = Descendants(root).OfType<Button>().Select(b => b.Content as string).ToArray();
        Check(buttons.Count(x => x == "选择图片") == 1 && !buttons.Any(x => x is "关联选中游戏" or "取消关联" or "粘贴截图" or "查找封面") &&
            !Descendants(root).OfType<CheckBox>().Any(c => c.Content as string is "联网资料检索" or "允许查询游戏库摘要"),
            "conversation page removes association, permission, paste and standalone cover controls while keeping one image chooser");
        var scroll = (ScrollViewer)page.FindName("ConversationScroll"); var confirm = (Button)page.FindName("ApplyCoverButton");
        confirm.BringIntoView(); root.UpdateLayout(); Render(root, "librarian-confirm-small.png", 680, 540);
        Check(Bounds(confirm, scroll).Bottom <= scroll.ActualHeight && ((TextBox)page.FindName("QueryEditor")).ActualHeight >= 60,
            "combined review confirmation is reachable without displacing the chat composer");
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true }); Render(root, "librarian-combined-night.png", 920, 700);
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings()); window.Close();
    }
}
