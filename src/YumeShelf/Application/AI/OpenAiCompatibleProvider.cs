using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YumeShelf.Application.AI;

public sealed record AiMessage(string Role, string Content);
public sealed record AiCompletionOptions(double Temperature = 0.2, int? MaxTokens = null, bool JsonObject = false);
public static class AiLimits
{
    public const int QuestionCharacters = 6000;
    public const int ContextCharacters = 18000;
    public const int ResponseBytes = 1024 * 1024;
    public const int AnswerCharacters = 12000;
    public const int ClarificationCharacters = 240;
    public const int VisibleMessages = 80;
}
public sealed class AiConfigurationException(string message) : InvalidOperationException(message);
public sealed class AiTruncatedException(string partialAnswer) : InvalidOperationException("回答达到长度上限，尚未完成。请缩小问题范围后重试。")
{
    public string PartialAnswer { get; } = partialAnswer;
}

public sealed class OpenAiCompatibleProvider
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new AiConfigurationException("云端 API 请使用 HTTPS 地址；只有本机 localhost/回环服务可使用 HTTP。地址不能含账号、查询参数或片段。");
        return uri;
    }
    public static void ValidateConfiguration(string endpoint, string model)
    {
        _ = ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200) throw new AiConfigurationException("请填写有效的模型名称（不超过 200 字符）。");
    }

    public async Task<string> CompleteAsync(string endpoint, string apiKey, string model, IReadOnlyList<AiMessage> messages, TimeSpan timeout, CancellationToken cancellationToken = default, AiCompletionOptions? options = null)
    {
        ValidateConfiguration(endpoint, model);
        if (messages.Sum(x => (long)x.Content.Length) > AiLimits.ContextCharacters + AiLimits.QuestionCharacters + 4000)
            throw new AiConfigurationException("问题和上下文过长，请精简后重试。");
        options ??= new AiCompletionOptions();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = options.Temperature
        };
        if (options.MaxTokens is int maxTokens) payload["max_tokens"] = maxTokens;
        if (options.JsonObject) payload["response_format"] = new { type = "json_object" };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ValidateEndpoint(endpoint), "chat/completions"))
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var json = await SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
            || choices[0].ValueKind != JsonValueKind.Object
            || !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
            throw new InvalidOperationException("AI 服务没有返回有效文本回答，请检查模型兼容性。");
        var answer = content.GetString()!;
        if (answer.Length > AiLimits.AnswerCharacters) throw new InvalidOperationException("服务返回的回答过长，已停止接收，请缩小问题范围。");
        if (choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
        {
            var reason = finish.GetString();
            if (reason == "length") throw new AiTruncatedException(answer);
            if (reason is not ("stop" or null)) throw new InvalidOperationException("模型未正常完成文本回答，请检查服务限制或模型兼容性。");
        }
        return answer;
    }

    public Task<string> TestModelAsync(string endpoint, string apiKey, string model, TimeSpan timeout, CancellationToken cancellationToken = default)
        => CompleteAsync(endpoint, apiKey, model,
            [new AiMessage("system", "只回复：连接测试成功"), new AiMessage("user", "请进行连接测试。")],
            timeout, cancellationToken, new AiCompletionOptions(0, 32));

    public async Task TestConnectionAsync(string endpoint, string apiKey, string model, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(endpoint, model);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ValidateEndpoint(endpoint), "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var json = await SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("服务响应不是有效的 OpenAI 兼容模型列表。");
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "API Key 无效或已过期，请重新填写。",
                HttpStatusCode.Forbidden => "当前 Key 没有调用权限，请检查服务权限。",
                HttpStatusCode.NotFound => "API 路径或模型不存在，请检查服务地址与模型名称。",
                HttpStatusCode.TooManyRequests => "服务限流或配额不足，请稍后重试或检查余额。",
                _ when (int)response.StatusCode is >= 300 and < 400 => "服务要求重定向，请直接填写最终 HTTPS API 地址。",
                _ => $"AI 服务返回 HTTP {(int)response.StatusCode}，请稍后重试。"
            });
        if (response.Content.Headers.ContentLength > AiLimits.ResponseBytes) throw new InvalidOperationException("服务响应过大，已中止读取。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > AiLimits.ResponseBytes) throw new InvalidOperationException("服务响应过大，已中止读取。");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
