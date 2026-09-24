using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private static string Delta(string? content = null, string? finish = null, string? reasoning = null)
        => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content, reasoning_content = reasoning }, finish_reason = finish } } }, StreamJsonOptions) + "\n\n";
    private const string Done = "data: [DONE]\n\n";
    private static Reply Sse(params StreamPart[] parts) => new(200, "", ContentType: "text/event-stream; charset=utf-8", Parts: parts, Chunked: true);

    private static void AiStreamingChecks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = "第一段🌸";
        var second = "\n第二段：推荐先体验共通线。";
        var multiline = "data: {\"choices\":[\r\ndata: {\"index\":0,\"delta\":{\"content\":\"第一段🌸\"}}\r\ndata: ]}\r\n\r\n";
        using (var server = new MockServer(Route("answer"), Sse(
            new("\uFEFF: keep-alive\r\n\r\n" + Delta(reasoning: "private reasoning must stay hidden") + "data:\n\n" + multiline),
            new(Delta(second).Replace("\n", "\r") + Delta(finish: "stop") + "data: {\"choices\":[],\"usage\":{}}\n\n" + Done, Release: release.Task)) with { FragmentSize = 1 }, Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            vm.Conversation.Add(new(false, string.Concat(Enumerable.Repeat("之前的作品介绍。\n", 80))));
            var page = new AiAssistantPage { DataContext = vm };
            var host = new Window { Content = page };
            var root = (FrameworkElement)page.Content;
            vm.Query = "推荐一部视觉小说"; vm.SendCommand.Execute(null);
            PumpUntil(() => vm.Conversation.Last().Content == first || !vm.IsBusy);
            Check(vm.IsBusy && vm.Conversation.Last().Content == first && !release.Task.IsCompleted && vm.CancelCommand.CanExecute(null),
                "real SSE text is visible before the server releases the remaining response");
            Render(root, "ai-streaming-first.png", 900, 580);
            Check(Descendants(root).OfType<TextBox>().Any(x => x.IsReadOnly && x.Text == first), "WPF message binding renders the partial answer while the request is active");
            var scroll = (ScrollViewer)page.FindName("ConversationScroll");
            scroll.ScrollToVerticalOffset(0);
            Render(root, "ai-streaming-reading-history.png", 900, 580);
            release.SetResult(); PumpUntil(() => !vm.IsBusy);
            Render(root, "ai-streaming-complete.png", 900, 580);
            Check(vm.Conversation.Last().Content == first + second && vm.Conversation.Last().State == "已完成" && vm.Query == "",
                "fragmented UTF-8, multiline SSE, CR/LF/CRLF, BOM, heartbeat and usage chunks form one exact answer");
            Check(scroll.VerticalOffset < 1, "stream updates do not drag a reader away from older messages");
            var requests = server.Requests.ToArray();
            using var routeRequest = JsonDocument.Parse(requests[0]);
            using var answerRequest = JsonDocument.Parse(requests[1]);
            Check(!routeRequest.RootElement.TryGetProperty("stream", out _) && answerRequest.RootElement.GetProperty("stream").GetBoolean(),
                "semantic router remains bounded JSON while the answer explicitly requests streaming");
            scroll.ScrollToEnd(); Render(root, "ai-streaming-at-bottom.png", 900, 580);
            Send(vm, "你能做什么");
            Render(root, "ai-streaming-follow.png", 900, 580);
            Check(scroll.ScrollableHeight - scroll.VerticalOffset < 24, "a reader at the bottom continues following new answers");
            Check(Messages(server.Requests.Last()).Any(x => x.GetProperty("role").GetString() == "assistant" && x.GetProperty("content").GetString() == first + second),
                "only the completed concatenated answer is stored in model history");
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
            Render(root, "ai-streaming-night.png", 900, 580);
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings());
            host.Close();
        }

        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var server = new MockServer(Route("answer"), Sse(new(Delta("已生成部分")), new(Delta("旧请求迟到内容") + Delta(finish: "stop") + Done, Release: late.Task)),
            Route("answer"), Sse(new StreamPart(Delta("重试后的完整回答") + Delta(finish: "stop") + Done))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); vm.Query = "停止后重试"; vm.SendCommand.Execute(null);
            PumpUntil(() => vm.Conversation.Last().Content.Length > 0 || !vm.IsBusy);
            var ids = vm.Conversation.Select(x => x.Id).ToArray();
            vm.Cancel(); PumpUntil(() => !vm.IsBusy);
            Check(vm.Conversation.Last().Content == "已生成部分" && vm.Status.Contains("已停止") && vm.Query == "停止后重试",
                "cancelling an active stream preserves visible text and the original question");
            late.SetResult(); vm.RetryCommand.Execute(null); PumpUntil(() => !vm.IsBusy);
            Check(vm.Conversation.Last().Content == "重试后的完整回答" && ids.SequenceEqual(vm.Conversation.Select(x => x.Id)) && vm.Query.Length == 0,
                "stream retry reuses bubbles and late content cannot pollute the replacement answer");
            Check(server.Requests.Count == 4 && Messages(server.Requests.Last()).Length == 2, "cancelled partial answer is not sent as successful history and no automatic duplicate request occurs");
        }

        using (var server = new MockServer(Route("answer", 2200), Sse(new(Delta("超时前的正文")), new(Delta("太晚了") + Done, Delay: 3100))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            var watch = System.Diagnostics.Stopwatch.StartNew(); Send(vm, "流式总期限");
            Check(vm.Status.Contains("超时") && vm.Conversation.Last().Content == "超时前的正文" && watch.Elapsed.TotalSeconds < 6.5,
                "the original task deadline covers routing and the entire stream while retaining partial text");
        }

        foreach (var ending in new[] { "", "data: {not-json}\n\n", "data: {\"error\":{\"message\":\"STREAM_PRIVATE_DETAIL\"}}\n\n",
            Delta(finish: "content_filter") + Done, Delta(finish: "stop") + "data: {\"choices\":" })
        {
            using var server = new MockServer(Route("answer"), Sse(new StreamPart(Delta("保留的正文") + ending)), Route("intro"));
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "流中断测试");
            Check(vm.Query == "流中断测试" && vm.Conversation.Last().Content == "保留的正文" && vm.Conversation.Last().State.Contains("未完成") &&
                !vm.Status.Contains("STREAM_PRIVATE_DETAIL"), "EOF, malformed event, stream error, filtering or partial final event stays incomplete: " + ending.Length);
            Send(vm, "新的问题");
            Check(Messages(server.Requests.Last()).Length == 2, "incomplete stream never enters successful model history: " + ending.Length);
        }

        foreach (var content in new[] { Delta("长度受限", "length") + Done, Delta(new string('x', AiLimits.AnswerCharacters + 1)) + Done })
        {
            using var server = new MockServer(Route("answer"), Sse(new StreamPart(content)));
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "回答长度测试");
            Check(vm.Conversation.Last().Content.Length is > 0 and <= AiLimits.AnswerCharacters && vm.Status.Contains("长度上限") && vm.Query.Length > 0,
                "stream finish length and local character budget retain bounded partial answers");
        }

        foreach (var content in new[] { Delta(finish: "stop") + Done, ":" + new string('x', 128 * 1024 + 1),
            string.Concat(Enumerable.Repeat(":" + new string('x', 1024) + "\n\n", 1030)) })
        {
            using var server = new MockServer(Route("answer"), Sse(new StreamPart(content)));
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "流式资源限制");
            Check(vm.Query.Length > 0 && vm.Conversation.Last().State != "已完成" && server.Requests.Count == 2,
                "empty answers, oversized events and cumulative SSE bytes are rejected without retry: " + content.Length);
        }

        using (var server = new MockServer(Route("answer"), new Reply(200, Completion("兼容服务的完整回答"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "兼容响应");
            Check(vm.Conversation.Last().Content == "兼容服务的完整回答" && vm.Conversation.Last().State.Contains("非流式") && vm.Query == "" && server.Requests.Count == 2,
                "JSON-only compatibility fallback is explicit and consumes the original response without another request");
        }
        using (var server = new MockServer(new Reply(200, Completion("{\"route\":\"answer\"", "length"))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Send(vm, "路由长度测试");
            Check(vm.Conversation.Last().Content.Length == 0 && vm.Status.Contains("问题判断未完成") && server.Requests.Count == 1,
                "truncated internal routing JSON is never displayed as an assistant answer");
        }
    }
}
