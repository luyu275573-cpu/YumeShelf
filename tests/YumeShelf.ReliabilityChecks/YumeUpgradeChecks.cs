using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Domain;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static Reply Tool(string name, object arguments) => new(200, Completion(JsonSerializer.Serialize(new { route = "tool", tool = name, arguments })));
    private static Reply TaskPlan(params string[] tasks) => new(200, Completion(JsonSerializer.Serialize(new { tasks })));
    private static void YumeUpgradeChecks()
    {
        var pixels = new byte[] { 80, 120, 200, 0, 100, 50, 200, 0 };
        var transparent = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var dib = new System.Windows.DataObject();
        dib.SetData(System.Windows.DataFormats.Dib, new byte[] { 1 });
        dib.SetData(System.Windows.DataFormats.Bitmap, transparent);
        var restored = AiClipboardImage.Read(dib)!;
        var repaired = new byte[8]; restored.CopyPixels(repaired, 8, 0);
        Check(repaired[3] == 255 && repaired[7] == 255 && repaired[0] == pixels[0], "DIB fallback repairs zero alpha before image scaling without losing RGB");
        var pngBitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 50, 100, 80, 50, 100, 200, 255 }, 8);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pngBitmap)); encoder.Save(stream);
        stream.Position = stream.Length;
        dib.SetData("image/png", stream);
        var preferred = AiClipboardImage.Read(dib)!;
        var copied = new byte[8]; new FormatConvertedBitmap(preferred, PixelFormats.Bgra32, null, 0).CopyPixels(copied, 8, 0);
        Check(copied[3] == 80 && copied[7] == 255 && stream.Position == stream.Length, "native PNG wins over broken DIB and preserves legitimate transparency and stream ownership");
        Reject<InvalidDataException>(() => AiImageAttachment.FromBitmap(transparent), "fully transparent encoded image is rejected before uploading");
        var onlyPng = new System.Windows.DataObject(); onlyPng.SetData("PNG", stream.ToArray());
        Check(AiClipboardImage.ContainsImage(onlyPng) && AiClipboardImage.Read(onlyPng)?.PixelWidth == 2, "PNG-only clipboard is accepted without relying on ContainsImage bitmap conversion");
        var ordinaryText = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "普通文字");
        Check(!AiClipboardImage.ContainsImage(ordinaryText), "plain clipboard text stays on native paste path");
        Check(YumeSkills.Ids.Count == 4 && YumeSkills.Vision.Contains("vision") && YumeAgent.ToolNames.Count == 9,
            "release skill resources and tool registrations are present without external files");
        Reject<InvalidOperationException>(() => YumeAgent.ParseObject("{\"route\":\"answer\",\"route\":\"tool\"}", 2000), "duplicate protocol keys cannot change the selected action");

        var file = Path.Combine(Output, "yume-integration-library.json");
        var game = GameAt(FakeExe(Folder("yume-integration"))); game.Title = "测试视觉小说"; game.Description = "原始简介";
        game.LaunchArguments = "PRIVATE_ARGUMENT"; game.TotalPlaySeconds = 987;
        new JsonGameStore(file).Save([game]);
        var host = Vm(file); host.SelectedGame = host.Games.Single();
        var snapshot = AiGameSummary.From(host.SelectedGame);
        Check(!JsonSerializer.Serialize(snapshot).Contains("PRIVATE_ARGUMENT") && !JsonSerializer.Serialize(snapshot).Contains(game.ExecutablePath) &&
              !JsonSerializer.Serialize(snapshot).Contains(snapshot.Revision), "game summaries exclude paths, arguments, play time and private revision hashes");
        using (var server = new MockServer(["details"], Tool("library_search", new { query = "测试" }),
                   Tool("game_details", new { id = game.Id }), Route("answer"), new Reply(200, Completion("库中有这部作品。"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => host.Games.Select(AiGameSummary.From).ToArray());
            vm.AllowLibrary = true; Send(vm, "找一下库中的测试游戏");
            Check(vm.Conversation.Last().Content.Contains("原始简介") && vm.Conversation.Last().Content.Contains(game.Title) && server.Requests.Count == 4 && vm.Status.Contains("工具 2 次"),
                "agent can search the authorized local library, read a result, then answer and finish");
            Check(server.Requests.All(body => !body.Contains("PRIVATE_ARGUMENT") && !body.Contains("ExecutablePath") && !body.Contains("TotalPlaySeconds")),
                "every multi-step request respects the library data boundary");
            vm.AllowLibrary = false;
            Check(!vm.IsBusy && !vm.RetryCommand.CanExecute(null), "revoking library access clears the pending retry context");
        }
        using (var server = new MockServer(["query"], Tool("library_search", new { query = "测试" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [snapshot]) { AllowLibrary = false }; Send(vm, "查一下我的库");
            Check(vm.Status.Contains("没有可查询") && server.Requests.Count == 2, "an explicitly unavailable library is not read by a model tool");
        }
        using (var server = new MockServer(Tool("shell", new { command = "never-execute" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "测试越权");
            Check(vm.Status.Contains("未注册") && server.Requests.Count == 1, "unregistered execution tool is blocked without side effects");
        }
        using (var server = new MockServer(["query"], Tool("library_search", new { query = "测试" }), Tool("library_search", new { query = "测试" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [snapshot]) { AllowLibrary = true };
            Send(vm, "重复工具"); Check(vm.Status.Contains("重复") && server.Requests.Count == 3, "repeated tool request terminates rather than looping");
        }
        using (var server = new MockServer(["query"], Enumerable.Range(0, 5).Select(i => Tool("library_search", new { query = "测试" + i })).ToArray()))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [snapshot]) { AllowLibrary = true };
            Send(vm, "工具上限"); Check(vm.Status.Contains("工具次数上限") && server.Requests.Count == 6, "tool budget stops changing queries too");
        }

        using (var server = new MockServer(["update"], Tool("propose_metadata", new { fields = new { Description = "模型建议简介", Engine = "测试引擎" }, reason = "用户提供线索，未联网核实。" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => host.Games.Select(AiGameSummary.From).ToArray(),
                selected: () => AiGameSummary.From(host.SelectedGame!), applyDraft: host.ApplyAiDraft, undoDraft: host.UndoAiDraft);
            vm.AttachGameCommand.Execute(null);
            Send(vm, "整理这部作品的简介");
            Check(vm.HasDraft && host.SelectedGame!.Description == "原始简介" && vm.Draft!.Fields.All(f => !f.Accepted), "metadata tool only creates unchecked suggestions and never writes the library");
            vm.ApplyDraftCommand.Execute(null);
            Check(vm.HasDraft && vm.Status.Contains("勾选"), "applying with no checked fields makes no changes");
            var page = new AiAssistantPage { DataContext = vm };
            var reviewHost = new Window { Content = page };
            reviewHost.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            Render((FrameworkElement)page.Content, "yume-metadata-review.png", 920, 700);
            Render((FrameworkElement)page.Content, "yume-metadata-review-small.png", 680, 540);
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
            Render((FrameworkElement)page.Content, "yume-metadata-review-night.png", 920, 700);
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings());
            vm.Draft!.Fields.Single(f => f.Field == "Description").Accepted = true;
            Directory.CreateDirectory(file + ".tmp");
            vm.ApplyDraftCommand.Execute(null);
            Check(vm.HasDraft && host.SelectedGame!.Description == "原始简介" && new JsonGameStore(file).Load().Single().Description == "原始简介",
                "failed suggestion save leaves both live object and disk untouched");
            Directory.Delete(file + ".tmp");
            vm.ApplyDraftCommand.Execute(null);
            Check(!vm.HasDraft && host.SelectedGame!.Description == "模型建议简介" && host.SelectedGame.Engine == "" &&
                  host.SelectedGame.TotalPlaySeconds == 987 && host.SelectedGame.LaunchArguments == "PRIVATE_ARGUMENT",
                "confirmed single-field save refreshes live library and preserves unselected metadata, identity and launch statistics");
            Check(new JsonGameStore(file).Load().Single().Description == "模型建议简介", "AI-approved metadata survives library reload");
            vm.UndoDraftCommand.Execute(null);
            Check(host.SelectedGame!.Description == "原始简介" && new JsonGameStore(file).Load().Single().Description == "原始简介", "AI metadata update can be undone through the normal store");
        }
        var stale = new AiMetadataDraft(AiGameSummary.From(host.SelectedGame!), [new("Description", "原始简介", "旧建议") { Accepted = true }], "测试");
        host.SelectedGame!.Description = "用户后续手动编辑";
        Check(host.ApplyAiDraft(stale)?.Contains("发生变化") == true && host.SelectedGame.Description == "用户后续手动编辑", "stale AI draft cannot overwrite a newer manual edit");
        host.Games.Clear();
        Check(host.ApplyAiDraft(stale)?.Contains("已从库中移除") == true, "removed game cannot be resurrected by an AI draft");

        using (var server = new MockServer(Tool("create_document", new { markdown = "# 游戏资料\n<script>alert(1)</script>\n![image](https://example.com/track)" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "生成游戏资料页");
            Check(vm.HasDocument && vm.Document!.Contains("# 游戏资料"), "document tool returns a preview draft without writing a file");
            var export = Path.Combine(Output, "yume-document.html"); AiDocumentExporter.Save(export, vm.Document!);
            var html = File.ReadAllText(export);
            Check(!html.Contains("<script>") && !html.Contains("<img") && html.Contains("default-src 'none'"), "exported HTML renders untrusted text without executing scripts or loading remote images");
            Reject<InvalidDataException>(() => AiDocumentExporter.Save(Path.Combine(Output, "blocked.exe"), "text"), "export cannot write executable file formats");
        }
        AiCapabilityChecks.Record("https://example.com/v1", DummyKey, "vision", "vision", "已验证");
        Check(AiCapabilityChecks.Status("https://example.com/v1", DummyKey, "vision", "vision") == "已验证" &&
              AiCapabilityChecks.Status("https://example.com/v1", "other-key", "vision", "vision") == "未验证" &&
              AiCapabilityChecks.Status("https://example.com/v1", DummyKey, "other-model", "vision") == "未验证", "capability results are scoped to endpoint, credential, model and adapter identity");
        using (var server = new MockServer(Tool("test_echo", new { text = "YUME" })))
        {
            Await(AiCapabilityChecks.TestProtocolAsync(server.Endpoint, DummyKey, "test-model", TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
            Check(server.Requests.Count == 1, "tool capability probe validates structured instructions without executing a production tool");
        }
        Check(!Directory.GetFiles(AppLog.DirectoryPath).Any(p => File.ReadAllText(p).Contains(DummyKey) || File.ReadAllText(p).Contains("PRIVATE_ARGUMENT")), "upgrade diagnostics never record credentials or library launch arguments");
        SearchAndDeadlineChecks(snapshot);
    }

    private sealed class SearchHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private static void SearchAndDeadlineChecks(AiGameSummary snapshot)
    {
        var handler = new SearchHandler(request =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://api.vndb.org/kana/vn" && request.Headers.Authorization is null,
                "search targets only fixed VNDB endpoint and never forwards model credentials");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"id\":\"v4\",\"title\":\"CLANNAD\",\"alttitle\":null,\"description\":\"Test evidence\",\"released\":\"2004-04-28\"}]}") };
        });
        using var client = new HttpClient(handler);
        var search = new VndbSearch(client);
        using (var server = new MockServer(Tool("search_vndb", new { query = "CLANNAD" }), Route("answer"), new Reply(200, Completion("作品资料 [v4]"))))
        {
            var agent = new YumeAgent(search);
            var result = Await(agent.RunAsync(AiSettings(server.Endpoint), DummyKey, "查询作品", null, [], [], null, _ => { }, _ => { }, CancellationToken.None, true));
            Check(result.Sources.Count == 1 && result.Sources[0].Url == "https://vndb.org/v4" && handler.Calls == 1 && result.Answer.Contains("[v4]"),
                "real tool loop integrates bounded search evidence and returns traceable source cards");
        }
        using (var server = new MockServer(Tool("search_vndb", new { query = "CLANNAD" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)) { AllowNetwork = false }; Send(vm, "查询作品");
            Check(vm.Status.Contains("未启用联网") && handler.Calls == 1, "disabled network option blocks search before any external request");
        }
        using (var badClient = new HttpClient(new SearchHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent("{\"results\":[{\"id\":\"../../secret\",\"title\":\"unsafe\"}]}") })))
            Reject<InvalidOperationException>(() => Await(new VndbSearch(badClient).SearchAsync("test", CancellationToken.None)), "malformed source IDs cannot become arbitrary URLs");
        using (var server = new MockServer(Route("answer", 2700), new Reply(200, Completion("两个阶段共享更长总期限"), 2700)))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint) with { AiTaskTimeoutSeconds = 12 }); Send(vm, "分层超时");
            Check(vm.Query == "" && vm.Conversation.Last().Content.Contains("两个阶段"), "per-request timeout no longer cuts off two healthy stages whose combined duration exceeds it");
        }
        using (var server = new MockServer(new Reply(200, Completion("not-the-random-code"))))
        {
            var settings = new SettingsViewModel(AiSettings(server.Endpoint), _ => { });
            settings.TestAiVisionCommand.Execute(null); PumpUntil(() => !settings.IsAiTesting);
            Check(settings.AiStatus.Contains("未正确读取") && server.Requests.Single().Contains("image_url"),
                "vision test sends a synthetic image and rejects text-only success or hallucinated digits");
        }
        using (var server = new MockServer(Route("intro", 400), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [snapshot]);
            vm.Query = "你好"; vm.SendCommand.Execute(null); PumpUntil(() => server.Requests.Count == 1);
            vm.UpdateSettings(AiSettings(server.Endpoint) with { AiModel = "new-model" }); PumpUntil(() => !vm.IsBusy);
            Send(vm, "新的服务配置");
            Check(Messages(server.Requests.Last()).Length == 2, "changing AI configuration prevents the active old result from entering the new service history");
        }
    }
}
