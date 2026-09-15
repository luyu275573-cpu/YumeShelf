using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YumeShelf.Application.AI;

public sealed record AiMessage(string Role, string Content);
public sealed record AiCompletionOptions(double Temperature = 0.2, int? MaxTokens = null, bool JsonObject = false);

public sealed class OpenAiCompatibleProvider
{
    private readonly HttpClient _httpClient = new();

    public async Task<string> CompleteAsync(string endpoint, string apiKey, string model, IReadOnlyList<AiMessage> messages, TimeSpan timeout, CancellationToken cancellationToken = default, AiCompletionOptions? options = null)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var uri)) throw new InvalidOperationException("API 地址无效。");
        options ??= new AiCompletionOptions();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = options.Temperature
        };
        if (options.MaxTokens is int maxTokens) payload["max_tokens"] = maxTokens;
        if (options.JsonObject) payload["response_format"] = new { type = "json_object" };
        var body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var response = await _httpClient.SendAsync(request, timeoutSource.Token);
        var text = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AI 服务返回 HTTP {(int)response.StatusCode}。");
        using var json = JsonDocument.Parse(text);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("AI 服务没有返回有效回答。");
        var message = choices[0].TryGetProperty("message", out var messageElement) ? messageElement : default;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
            throw new InvalidOperationException("AI 服务返回了空回答。");
        return content.GetString()!;
    }

    public Task<string> TestModelAsync(string endpoint, string apiKey, string model, TimeSpan timeout, CancellationToken cancellationToken = default)
        => CompleteAsync(endpoint, apiKey, model,
            [new AiMessage("system", "只回复：连接测试成功"), new AiMessage("user", "请进行连接测试。")],
            timeout, cancellationToken, new AiCompletionOptions(0, 32));

    public async Task TestConnectionAsync(string endpoint, string apiKey, string model, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/models", UriKind.Absolute, out var uri)) throw new InvalidOperationException("API 地址无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var response = await _httpClient.SendAsync(request, timeoutSource.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"服务返回 HTTP {(int)response.StatusCode}，请检查地址和 API Key。\n模型：{model}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutSource.Token));
        if (!document.RootElement.TryGetProperty("data", out _)) throw new InvalidOperationException("服务响应格式不是 OpenAI 兼容格式。");
    }
}
