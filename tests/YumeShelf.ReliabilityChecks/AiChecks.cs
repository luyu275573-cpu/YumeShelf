using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private const string DummyKey = "reliability-test-dummy-key";
    private static string Completion(string text, string finish = "stop") => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text }, finish_reason = finish } } });
    private static Reply Route(string route, int delay = 0) => new(200, Completion(JsonSerializer.Serialize(new { route, answer_basis = "general", clarification = (string?)null, observations = "合成游戏测试画面，标题为 YUME VISION TEST，无法确认真实作品。" })), delay);
    private static AppSettings AiSettings(string endpoint) => new() { AiApiBaseUrl = endpoint, AiApiKeyProtected = SecureSecretStore.Protect(DummyKey), AiModel = "test-model", AiTimeoutSeconds = 5, AiRequestTimeoutSeconds = 5, AiTaskTimeoutSeconds = 5 };
    private static void Send(AiAssistantViewModel vm, string? query = null)
    {
        if (query is not null) vm.Query = query;
        vm.SendCommand.Execute(null);
        PumpUntil(() => !vm.IsBusy, 10000);
    }
    private static JsonElement[] Messages(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static void AiChecks()
    {
        foreach (var bad in new[] { "http://example.com/v1", "ftp://example.com", "https://user:password@example.com/v1", "https://example.com/v1?q=x", "https://example.com/v1#x" })
            Reject<AiConfigurationException>(() => OpenAiCompatibleProvider.ValidateEndpoint(bad), "reject unsafe API endpoint: " + bad);
        Check(OpenAiCompatibleProvider.ValidateEndpoint("https://example.com/v1").Scheme == "https" &&
            OpenAiCompatibleProvider.ValidateEndpoint("http://127.0.0.1:1000/v1").IsLoopback, "HTTPS cloud and HTTP loopback are permitted");

        using (var server = new MockServer(new Reply(200, Completion("partial", "length"))))
        {
            try
            {
                Await(new OpenAiCompatibleProvider().CompleteAsync(server.Endpoint, DummyKey, "test", [new("user", "test")], TimeSpan.FromSeconds(5)));
                throw new InvalidOperationException("Truncated response accepted");
            }
            catch (AiTruncatedException ex) { Check(ex.PartialAnswer == "partial", "provider exposes truncated text without claiming completion"); }
            Check(server.Headers.Single().Contains("Authorization: Bearer " + DummyKey, StringComparison.OrdinalIgnoreCase), "loopback mock uses only disposable test credential");
        }
        using (var redirectTarget = new MockServer())
        using (var redirect = new MockServer(new Reply(302, "", Location: redirectTarget.Endpoint)))
        {
            Reject<InvalidOperationException>(() => Await(new OpenAiCompatibleProvider().CompleteAsync(redirect.Endpoint, DummyKey, "test", [new("user", "test")], TimeSpan.FromSeconds(5))), "redirect is refused");
            Check(redirectTarget.Requests.IsEmpty, "credential is not forwarded to redirect destination");
        }
        foreach (var response in new[] { new Reply(200, "{}"), new Reply(200, "not-json"), new Reply(200, Completion("")), new Reply(200, Completion(new string('x', 13000))), new Reply(200, new string('x', AiLimits.ResponseBytes + 1)), new Reply(200, new string('x', AiLimits.ResponseBytes + 1), OmitLength: true) })
        {
            using var server = new MockServer(response);
            var failed = false;
            try { Await(new OpenAiCompatibleProvider().CompleteAsync(server.Endpoint, DummyKey, "test", [new("user", "test")], TimeSpan.FromSeconds(5))); }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException) { failed = true; }
            Check(failed, "malformed/empty/oversized AI response rejected, bytes=" + response.Body.Length + ", header=" + !response.OmitLength);
        }
        using (var server = new MockServer(new Reply(500, "do not show this raw server detail"), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            Send(vm, "你好");
            var ids = vm.Conversation.Select(x => x.Id).ToArray();
            Check(vm.Query == "你好" && vm.Conversation.Last().State.Contains("HTTP 500") && !vm.Status.Contains("raw server"), "failed answer retains question with safe error state");
            vm.RetryCommand.Execute(null); PumpUntil(() => !vm.IsBusy);
            Check(ids.SequenceEqual(vm.Conversation.Select(x => x.Id)) && vm.Query == "" && vm.Conversation.Last().State == "已完成", "retry reuses original user and assistant bubbles");
        }
        using (var server = new MockServer(Route("intro", 500), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); vm.Query = "取消测试";
            vm.SendCommand.Execute(null); PumpUntil(() => server.Requests.Count == 1);
            vm.Cancel(); PumpUntil(() => !vm.IsBusy);
            var count = vm.Conversation.Count;
            Check(vm.Status.Contains("已停止") && vm.Query == "取消测试", "cancel keeps recoverable stopped task");
            Send(vm);
            Check(vm.Query == "" && vm.Conversation.Count == count, "cancelled task retries without duplicates");
        }
        using (var server = new MockServer(Route("answer", 2800), new Reply(200, Completion("late"), 2800)))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            var timer = System.Diagnostics.Stopwatch.StartNew(); Send(vm, "任务总期限");
            Check(vm.Status.Contains("超时") && timer.Elapsed.TotalSeconds < 6.5 && vm.Query.Length > 0 && server.Requests.Count == 2, "configured deadline covers routing plus answer, not each stage separately");
        }
        using (var server = new MockServer(Route("answer"), new Reply(200, Completion("partial answer", "length")), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            Send(vm, "长问题");
            Check(vm.Query == "长问题" && vm.Conversation.Last().Content == "partial answer" && vm.Conversation.Last().State.Contains("尚未完成"), "truncated answer remains visibly incomplete");
            Send(vm, "新问题");
            Check(Messages(server.Requests.Last()).Length == 2, "failed/truncated turns never enter model history");
        }
        using (var server = new MockServer())
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            Send(vm, new string('x', AiLimits.QuestionCharacters + 1));
            Check(server.Requests.IsEmpty && vm.Conversation.Count == 1 && vm.Status.Contains("超过"), "over-budget question produces no API request");
        }
        using (var server = new MockServer(new Reply(200, Completion(JsonSerializer.Serialize(new { route = "clarify", clarification = new string('x', 241) }))), Route("out_of_scope"), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            Send(vm, "模糊问题");
            Check(vm.Query.Length > 0 && vm.Status.Contains("过长"), "overlong router clarification is rejected");
            Send(vm, "非相关问题");
            Check(vm.Conversation.Last().Content.Contains("只处理") && server.Requests.Count == 2, "out-of-scope route uses local brief response without second model call");
            Send(vm, "新问题");
            Check(Messages(server.Requests.Last()).Length == 2, "out-of-scope response is excluded from history");
        }
        var replies = Enumerable.Range(0, 42).SelectMany(_ => new[] { Route("answer"), new Reply(200, Completion(new string('答', 4000))) }).ToArray();
        using (var server = new MockServer(replies))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            for (var i = 0; i < 42; i++) Send(vm, "作品问题" + i + new string('问', 1000));
            var messages = Messages(server.Requests.Last());
            var history = messages.Skip(1).SkipLast(1).ToArray();
            Check(history.Length <= 12 && history.Sum(x => x.GetProperty("content").GetString()!.Length) <= AiLimits.ContextCharacters &&
                history.Length % 2 == 0 && history.Select((x, i) => x.GetProperty("role").GetString() == (i % 2 == 0 ? "user" : "assistant")).All(x => x),
                "history trims complete turns to count and character budgets");
            Check(vm.Conversation.Count <= AiLimits.VisibleMessages && vm.Conversation[0].IsUser && !vm.Conversation.Last().IsUser, "visible conversation is bounded without orphan replies");
        }
        var saves = 0;
        var settings = new SettingsViewModel(AiSettings("https://example.com/v1"), _ => saves++);
        var page = new SettingsPage { DataContext = settings };
        settings.SelectedSection = "AI 检索";
        Render((System.Windows.FrameworkElement)page.Content, "ai-settings.png", 920, 700);
        settings.AiApiBaseUrl = "https://another.example/v1";
        Check(settings.AiApiKey == "" && Descendants(page).OfType<PasswordBox>().All(x => x.Password.Length == 0), "changing API origin clears both draft credential and visible password box");
        settings.AiTimeoutSeconds = 0; settings.ConfirmCommand.Execute(null);
        Check(saves == 0 && settings.ErrorMessage.Contains("5–120"), "invalid timeout cannot be silently saved");
        settings.AiTimeoutSeconds = 120; settings.ConfirmCommand.Execute(null);
        Check(saves == 1, "documented maximum timeout can be saved");
        Check(!Directory.GetFiles(AppLog.DirectoryPath).Any(p => File.ReadAllText(p).Contains(DummyKey)), "diagnostic logs contain no dummy credential");
    }

    private sealed record StreamPart(string Text, int Delay = 0, Task? Release = null);
    private sealed record Reply(int Code, string Body, int Delay = 0, bool OmitLength = false, string? Location = null,
        string ContentType = "application/json", StreamPart[]? Parts = null, int FragmentSize = 8192, bool Chunked = false);
    // Scripted loopback HTTP only. No account credentials and no real AI service.
    private sealed class MockServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Headers { get; } = new();
        public string Endpoint { get; }
        public MockServer(string[] tasks, params Reply[] replies)
            : this(replies.Take(1).Concat([TaskPlan(tasks)]).Concat(replies.Skip(1)).ToArray()) { }
        public MockServer(params Reply[] replies)
        {
            _listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _worker = Task.Run(async () =>
            {
                try
                {
                    foreach (var reply in replies)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        client.NoDelay = true;
                        using var stream = client.GetStream();
                        try
                        {
                            var headerBytes = new List<byte>(); var one = new byte[1];
                            while (headerBytes.Count < 32768)
                            {
                                if (await stream.ReadAsync(one, _stop.Token) == 0) throw new IOException("Empty mock request");
                                headerBytes.Add(one[0]);
                                if (headerBytes.Count >= 4 && headerBytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                            }
                            var header = Encoding.ASCII.GetString(headerBytes.ToArray()); Headers.Enqueue(header);
                            var lengthLine = header.Split("\r\n").FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                            var body = new byte[lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim())];
                            await stream.ReadExactlyAsync(body, _stop.Token);
                            Requests.Enqueue(Encoding.UTF8.GetString(body));
                            await Task.Delay(reply.Delay, _stop.Token);
                            var parts = reply.Parts ?? [new StreamPart(reply.Body)];
                            var length = reply.Chunked ? "Transfer-Encoding: chunked\r\n" : reply.OmitLength ? "" : $"Content-Length: {parts.Sum(x => Encoding.UTF8.GetByteCount(x.Text))}\r\n";
                            var location = reply.Location is null ? "" : $"Location: {reply.Location}\r\n";
                            var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Code} Result\r\nContent-Type: {reply.ContentType}\r\n{length}{location}Connection: close\r\n\r\n");
                            await stream.WriteAsync(responseHeader, _stop.Token);
                            foreach (var part in parts)
                            {
                                if (part.Release is not null) await part.Release.WaitAsync(_stop.Token);
                                if (part.Delay > 0) await Task.Delay(part.Delay, _stop.Token);
                                var bytes = Encoding.UTF8.GetBytes(part.Text);
                                for (var offset = 0; offset < bytes.Length;)
                                {
                                    var count = Math.Min(reply.FragmentSize, bytes.Length - offset);
                                    if (reply.Chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes($"{count:X}\r\n"), _stop.Token);
                                    await stream.WriteAsync(bytes.AsMemory(offset, count), _stop.Token);
                                    if (reply.Chunked) await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                                    offset += count;
                                }
                            }
                            if (reply.Chunked) await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
                        }
                        catch (IOException) { /* Expected when the client cancels or stops at a byte limit. */ }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            _worker.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
