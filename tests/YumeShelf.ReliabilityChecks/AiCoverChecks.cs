using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static readonly AiCoverCandidate CoverFixture = new(new AiSource("v562", "Sakura no Uta / サクラノ詩－櫻の森の上を舞う－", "https://vndb.org/v562", "", "2015-10-23", DateTimeOffset.UtcNow),
        "https://t.vndb.org/cv/63/79663.jpg", "https://t.vndb.org/cv.t/63/79663.jpg");

    private static object CoverRow(string id = "v562", double? sexual = 0, string? url = null) => new
    {
        id, title = CoverFixture.Source.Title, released = "2015-10-23",
        image = new { url = url ?? CoverFixture.ImageUrl, thumbnail = CoverFixture.ThumbnailUrl, sexual, violence = 0 }
    };
    private static HttpResponseMessage CoverJson(params object[] rows) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { results = rows })) };
    private static HttpResponseMessage CoverBytes(byte[] bytes, string mime = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime); return response;
    }
    private static void CoverCommand(AiAssistantViewModel vm, RelayCommand command, object? parameter = null)
    { command.Execute(parameter); PumpUntil(() => !vm.IsBusy); }

    private static void AiCoverChecks()
    {
        var png = File.ReadAllBytes(VisionFixture());
        var requests = new List<string>();
        var queryBodies = new List<string>();
        Func<HttpResponseMessage> imageResponse = () => CoverBytes(png);
        using var client = new HttpClient(new SearchHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Check(request.Headers.Authorization is null, "public cover request never carries the model credential");
            if (request.Method == HttpMethod.Post)
            {
                queryBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return CoverJson(CoverRow());
            }
            return imageResponse();
        }));
        var search = new VndbSearch(client);
        var dir = Folder("cover-library"); var file = Path.Combine(dir, "library.json");
        var game = GameAt(FakeExe(Folder("cover-game"))); game.Title = "樱之诗V1.2";
        game.Description = "保留简介"; game.LaunchArguments = "PRIVATE_COVER_ARGUMENT"; game.TotalPlaySeconds = 123;
        new JsonGameStore(file).Save([game]);
        var host = Vm(file); host.SelectedGame = host.Games.Single(); var live = host.SelectedGame;
        using var unusedModel = new MockServer();
        var vm = new AiAssistantViewModel(AiSettings(unusedModel.Endpoint) with { AiApiKeyProtected = "" },
            library: () => host.Games.Select(AiGameSummary.From).ToArray(), selected: () => host.SelectedGame is null ? null : AiGameSummary.From(host.SelectedGame),
            applyCover: host.ApplyAiCover, undoCover: host.UndoAiCover, search: search) { AllowNetwork = false };
        CoverCommand(vm, vm.FindCoverCommand);
        Check(requests.Count == 0 && vm.Status.Contains("联网"), "direct cover search requires network opt-in before querying");
        vm.AllowNetwork = true; CoverCommand(vm, vm.FindCoverCommand);
        Check(requests.Count == 0 && vm.Status.Contains("关联"), "direct cover search requires a selected library target");
        vm.AttachGameCommand.Execute(null); CoverCommand(vm, vm.FindCoverCommand);
        using (var query = JsonDocument.Parse(queryBodies.Single()))
            Check(query.RootElement.GetProperty("filters")[2].GetString() == "樱之诗", "default cover query omits a trailing local version suffix");
        Check(vm.HasCoverDraft && vm.CoverDraft!.Candidates.Count == 1 && !vm.HasCoverPreview && !vm.ApplyCoverCommand.CanExecute(null) &&
            unusedModel.Requests.IsEmpty && requests.Count == 1, "direct search needs no API key and returns candidates without downloading images or calling a model");
        var before = File.ReadAllText(file); var candidate = vm.CoverDraft!.Candidates.Single();
        CoverCommand(vm, vm.PreviewCoverCommand, candidate);
        Check(vm.HasCoverPreview && vm.ApplyCoverCommand.CanExecute(null) && requests.Last() == candidate.ThumbnailUrl && File.ReadAllText(file) == before &&
            !Directory.Exists(Path.Combine(dir, "Covers")), "preview downloads only a thumbnail into memory and leaves library and cache untouched");
        CoverLayoutChecks(vm);

        imageResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        CoverCommand(vm, vm.ApplyCoverCommand);
        Check(vm.HasCoverPreview && vm.Status.Contains("403") && File.ReadAllText(file) == before && !Directory.Exists(Path.Combine(dir, "Covers")),
            "failed original download keeps the selected preview and old cover without creating a file");
        imageResponse = () => CoverBytes(png);

        Directory.CreateDirectory(file + ".tmp");
        CoverCommand(vm, vm.ApplyCoverCommand);
        Check(vm.HasCoverPreview && vm.Status.Contains("保存失败") && live!.CoverPath == game.CoverPath && File.ReadAllText(file) == before &&
            Directory.GetFiles(Path.Combine(dir, "Covers")).Length == 0, "failed library save retains preview and old cover and removes the new uncommitted PNG");
        Directory.Delete(file + ".tmp");
        CoverCommand(vm, vm.ApplyCoverCommand);
        var savedPath = live!.CoverPath!;
        var saved = new JsonGameStore(file).Load(true).Single();
        Check(!vm.HasCoverDraft && vm.UndoCoverCommand.CanExecute(null) && File.Exists(savedPath) && requests.Last() == candidate.ImageUrl &&
            saved.CoverPath == savedPath && ReferenceEquals(live, host.Games.Single()), "confirmed cover downloads the original, persists it, refreshes the same game object and enables undo");
        Check(saved.Title == game.Title && saved.Description == game.Description && saved.TotalPlaySeconds == 123 && saved.LaunchArguments == game.LaunchArguments &&
            saved.ExecutablePath == game.ExecutablePath && Directory.GetFiles(game.RootPath).Length == 1, "cover replacement preserves game files, metadata, executable, arguments and statistics");
        var cleanBytes = File.ReadAllBytes(savedPath);
        Check(!System.Text.Encoding.UTF8.GetString(cleanBytes).Contains("VISION_PRIVATE_METADATA") && cleanBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "saved cover is re-encoded PNG with source metadata stripped");
        var converter = new CoverImageConverter();
        var picture = converter.Convert(savedPath, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
        Check(picture is BitmapSource && !JsonSerializer.Serialize(AiGameSummary.From(live)).Contains("CoverPath"), "new local cover renders and its path is excluded from AI summaries");
        live.TotalPlaySeconds = 456;
        Directory.CreateDirectory(file + ".tmp"); vm.UndoCoverCommand.Execute(null);
        Check(live.CoverPath == savedPath && vm.UndoCoverCommand.CanExecute(null), "failed undo preserves the current cover and remains retryable");
        Directory.Delete(file + ".tmp"); vm.UndoCoverCommand.Execute(null);
        Check(live.CoverPath == game.CoverPath && new JsonGameStore(file).Load(true).Single().TotalPlaySeconds == 456 && File.Exists(savedPath) &&
            !vm.UndoCoverCommand.CanExecute(null), "undo restores default cover without rolling back new play time or removing files used by backup");

        // Preserve an existing user-selected image and reject stale proposals on both write paths.
        var originalImagePath = VisionFixture(); live.CoverPath = originalImagePath; var oldImage = File.ReadAllBytes(originalImagePath);
        vm.AttachGameCommand.Execute(null); CoverCommand(vm, vm.FindCoverCommand); CoverCommand(vm, vm.PreviewCoverCommand, vm.CoverDraft!.Candidates.Single());
        var stale = vm.CoverDraft!; var prepared = vm.CoverPreview!;
        live.Description = "人工改动"; var callCount = requests.Count;
        CoverCommand(vm, vm.ApplyCoverCommand);
        Check(requests.Count == callCount && vm.Status.Contains("变化") && host.ApplyAiCover(stale, stale.Candidates.Single(), prepared)?.Contains("变化") == true,
            "stale cover is rejected before download and again at the store boundary");
        vm.AttachGameCommand.Execute(null); CoverCommand(vm, vm.FindCoverCommand); CoverCommand(vm, vm.PreviewCoverCommand, vm.CoverDraft!.Candidates.Single());
        CoverCommand(vm, vm.ApplyCoverCommand); live.CoverPath = "manual-new.png"; vm.UndoCoverCommand.Execute(null);
        Check(live.CoverPath == "manual-new.png" && vm.Status.Contains("变化") && File.ReadAllBytes(originalImagePath).SequenceEqual(oldImage),
            "undo cannot overwrite later manual cover selection and never overwrites the old image file");
        Check(host.RemoveGame(live) && host.ApplyAiCover(stale, stale.Candidates.Single(), prepared)?.Contains("移除") == true,
            "a cover draft cannot recreate a removed game");

        foreach (var invalidUrl in new[] { "http://t.vndb.org/cv/63/79663.jpg", "https://localhost/cv/63/79663.jpg", "https://t.vndb.org.evil/cv/63/79663.jpg", candidate.ImageUrl + "?token=x", candidate.ImageUrl + "\n", "https://t.vndb.org/cv/63/../secret.jpg" })
            Check(!VndbSearch.IsCoverUrl(invalidUrl, false), "unsafe cover URL is rejected: " + invalidUrl.Trim());
        using (var mixedClient = new HttpClient(new SearchHandler(_ => CoverJson(CoverRow(), CoverRow("v563"), CoverRow("v564", 2), CoverRow("v565", null), CoverRow("v566", 0, "https://evil.example/cover.jpg")))))
            Check(Await(new VndbSearch(mixedClient).SearchCoversAsync("test", CancellationToken.None)).Count == 1,
                "cover candidates deduplicate images and exclude explicit, unrated and arbitrary-host images");
        using (var emptyClient = new HttpClient(new SearchHandler(_ => CoverJson(new { id = "v1", title = "No image", image = (object?)null }))))
            Check(Await(new VndbSearch(emptyClient).SearchCoversAsync("test", CancellationToken.None)).Count == 0, "missing cover produces an empty result without inventing an image");
        imageResponse = () => CoverBytes(png, "text/html");
        Reject<InvalidDataException>(() => Await(search.DownloadCoverAsync(candidate, false, CancellationToken.None)), "cover downloader rejects a non-image MIME type");
        imageResponse = () => CoverBytes([1, 2, 3, 4]);
        Reject<InvalidDataException>(() => Await(search.DownloadCoverAsync(candidate, false, CancellationToken.None)), "cover downloader checks file signature before image decoding");
        imageResponse = () => CoverBytes(new byte[8 * 1024 * 1024 + 1]);
        Reject<InvalidDataException>(() => Await(search.DownloadCoverAsync(candidate, false, CancellationToken.None)), "oversized full cover is rejected before decoding");
        imageResponse = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnboundedHeaderContent(new byte[2 * 1024 * 1024 + 1]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"); return response;
        };
        Reject<InvalidDataException>(() => Await(search.DownloadCoverAsync(candidate, true, CancellationToken.None)), "thumbnail limit also applies when Content-Length is absent");
        imageResponse = () => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/tracker") } };
        Reject<InvalidOperationException>(() => Await(search.DownloadCoverAsync(candidate, false, CancellationToken.None)), "redirect cover response is rejected rather than accepted as an image");

        using (var cancelClient = new HttpClient(new CancelCoverHandler()))
        {
            var canceled = new AiAssistantViewModel(new AppSettings(), library: () => [stale.Original], selected: () => stale.Original, search: new VndbSearch(cancelClient)) { AllowNetwork = true };
            canceled.AttachGameCommand.Execute(null); canceled.FindCoverCommand.Execute(null);
            Check(canceled.IsBusy && !canceled.FindCoverCommand.CanExecute(null) && !canceled.SendCommand.CanExecute(null), "active cover operation locks duplicate requests and AI sending");
            canceled.Cancel(); PumpUntil(() => !canceled.IsBusy);
            Check(!canceled.HasCoverDraft && canceled.Status.Contains("停止") && canceled.CanEditInput, "canceling a cover query returns to idle without library writes");
        }
        CoverAgentChecks(search, requests, stale.Original);
    }

    private sealed class CancelCoverHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }

    private sealed class UnboundedHeaderContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private static void CoverAgentChecks(VndbSearch search, List<string> requests, AiGameSummary selected)
    {
        var before = requests.Count;
        using (var server = new MockServer(["update"], Tool("find_cover", new { query = "樱之诗" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [selected], selected: () => selected, search: search) { AllowNetwork = true };
            vm.AttachGameCommand.Execute(null); Send(vm, "帮我找封面换掉默认图");
            Check(vm.HasCoverDraft && vm.IsCoverSearchOpen && !vm.HasCoverPreview && server.Requests.Count == 2 && requests.Count == before + 1 &&
                vm.Conversation.Last().Sources.Count == 1 && vm.Status.Contains("工具 1 次"), "verified find_cover ends with a review draft after routing, task validation and one search without writing");
            vm.AllowNetwork = false;
            Check(!vm.HasCoverDraft && !vm.PreviewCoverCommand.CanExecute(null), "revoking network consent clears candidates and disables downloading");
        }
        before = requests.Count;
        foreach (var (network, target) in new[] { (false, selected), (true, (AiGameSummary?)null) })
        using (var server = new MockServer(["update"], Tool("find_cover", new { query = "樱之诗" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [selected], selected: () => target, search: search) { AllowNetwork = network };
            if (target is not null) vm.AttachGameCommand.Execute(null);
            Send(vm, "找封面"); Check(!vm.HasCoverDraft && requests.Count == before, "cover tool cannot bypass network permission or missing game association");
        }
        using (var server = new MockServer(["update"], Tool("find_cover", new { query = "樱之诗", url = "https://evil.example" })))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint), library: () => [selected], selected: () => selected, search: search) { AllowNetwork = true };
            vm.AttachGameCommand.Execute(null); Send(vm, "找封面");
            Check(vm.Status.Contains("未允许") && requests.Count == before, "model cannot supply an arbitrary image URL through tool arguments");
        }
        using (var server = new MockServer(Route("research"), Route("research")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)) { AllowNetwork = true }; Send(vm, "找资料");
            Check(server.Requests.Count == 2 && vm.Status.Contains("停止重复判断"), "planner with no executable search stops after two classifications instead of exhausting six calls");
        }
        using (var server = new MockServer(Route("unsupported")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "帮我安装游戏补丁");
            Check(server.Requests.Count == 1 && vm.Conversation.Last().Content.Contains("尚未开放"), "unsupported action gives an honest short capability response without extra generation");
        }
    }

    private static void CoverLayoutChecks(AiAssistantViewModel vm)
    {
        var page = new AiAssistantPage { DataContext = vm }; var window = new Window { Content = page };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        var root = (FrameworkElement)page.Content; var scroll = (ScrollViewer)page.FindName("ConversationScroll");
        Render(root, "cover-review.png", 920, 700);
        Render(root, "cover-review-small.png", 680, 540);
        var confirm = (Button)page.FindName("ApplyCoverButton"); confirm.BringIntoView(); root.UpdateLayout();
        Render(root, "cover-confirm-small.png", 680, 540);
        var bounds = Bounds(confirm, scroll);
        Check(bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight && confirm.IsEnabled && scroll.ActualHeight > 120,
            "small cover review can scroll to the full confirmation button while input stays fixed");
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
        Render(root, "cover-review-night.png", 920, 700);
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings()); window.Close();
    }

    // Explicit opt-in: normal regression never queries public services or a real model.
    private static void CoverSourceCheck()
    {
        var search = new VndbSearch(); var candidates = Await(search.SearchCoversAsync("樱之诗", CancellationToken.None), 25000);
        var selected = candidates.Single(c => c.Source.Id == "v562");
        var preview = Await(search.DownloadCoverAsync(selected, true, CancellationToken.None), 25000);
        var full = Await(search.DownloadCoverAsync(selected, false, CancellationToken.None), 25000);
        Check(preview.ByteCount > 0 && full.Width > 0 && full.Height > 0, "public VNDB v562 cover query, thumbnail and original decode successfully");
        File.WriteAllText(Path.Combine(Output, "public-cover-source.json"), JsonSerializer.Serialize(new { selected.Source, selected.ImageUrl, full.Width, full.Height, full.ByteCount }, JsonOptions));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(full.Preview));
        using var file = File.Create(Path.Combine(Output, "public-cover-preview.png")); encoder.Save(file);
        Console.WriteLine("PUBLIC COVER EVIDENCE: " + Output);
    }
}
