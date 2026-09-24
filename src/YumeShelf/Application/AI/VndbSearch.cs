using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YumeShelf.Application.AI;

public sealed record AiSource(string Id, string Title, string Url, string Description, string? Released, DateTimeOffset RetrievedAt);

public sealed partial class VndbSearch
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    public VndbSearch(HttpClient? client = null) => _client = client ?? Client;
    public async Task<IReadOnlyList<AiSource>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var json = await QueryAsync(query, "title,alttitle,description,released", cancellationToken);
        return Results(json).Select(ReadSource).ToArray();
    }
    private async Task<JsonDocument> QueryAsync(string query, string fields, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidOperationException("检索词为空或过长。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Fixed public endpoint; no caller-supplied URL, redirect, cookies or model API credentials.
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { filters = new[] { "search", "=", query }, fields, results = 5 }), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"VNDB 检索返回 HTTP {(int)response.StatusCode}，尚未取得资料。");
        if (response.Content.Headers.ContentLength > 256 * 1024) throw new InvalidOperationException("检索响应过大，已停止读取。");
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (output.Length + read > 256 * 1024) throw new InvalidOperationException("检索响应过大，已停止读取。");
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
    private static IEnumerable<JsonElement> Results(JsonDocument json)
    {
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("检索响应格式无效。");
        return results.EnumerateArray().Take(5);
    }
    private static AiSource ReadSource(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("检索条目格式无效。");
        var id = YumeAgent.Text(row, "id", 30);
        if (!Regex.IsMatch(id, @"\Av[1-9][0-9]{0,15}\z", RegexOptions.CultureInvariant)) throw new InvalidOperationException("检索来源标识无效。");
        var title = YumeAgent.Text(row, "title", 500);
        if (row.TryGetProperty("alttitle", out var alt) && alt.ValueKind == JsonValueKind.String && alt.GetString()!.Length is > 0 and <= 500) title += " / " + alt.GetString();
        var description = row.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString()! : "";
        var released = row.TryGetProperty("released", out var date) && date.ValueKind == JsonValueKind.String ? date.GetString() : null;
        return new(id, title, "https://vndb.org/" + id, description[..Math.Min(1000, description.Length)], released is { Length: <= 20 } ? released : null, DateTimeOffset.UtcNow);
    }
}
