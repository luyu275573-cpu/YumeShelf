using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace YumeShelf.Application.AI;

public sealed record AiTextChunk(string Text, bool IsStreaming = true);

public sealed partial class OpenAiCompatibleProvider
{
    public async IAsyncEnumerable<AiTextChunk> StreamAsync(string endpoint, string apiKey, string model,
        IReadOnlyList<AiMessage> messages, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, AiCompletionOptions? options = null)
    {
        using var request = CreateCompletionRequest(endpoint, apiKey, model, messages, options, streaming: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        ValidateResponse(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            // Some compatible services ignore stream=true. Consume this response once; never repeat a paid request.
            using var json = await ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            yield return new AiTextChunk(ReadAnswer(json.RootElement), IsStreaming: false);
            yield break;
        }
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("服务未返回支持的流式或 JSON 回答，请检查 API 与模型兼容性。");

        var answer = new StringBuilder();
        var finished = false;
        var done = false;
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await foreach (var data in ReadEventsAsync(stream, deadline.Token).ConfigureAwait(false))
        {
            deadline.Token.ThrowIfCancellationRequested();
            deadline.CancelAfter(timeout); // Inactivity deadline; caller retains the absolute whole-task deadline.
            if (string.IsNullOrWhiteSpace(data)) continue;
            if (data.Trim() == "[DONE]") { done = true; break; }
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                throw new InvalidOperationException("AI 流式服务返回错误，回答未完成，请稍后重试。");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AI 流式响应格式无效，回答未完成。");
            if (choices.GetArrayLength() == 0) continue; // Optional usage-only chunk.
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("AI 流式回答格式无效。");
            if (choice.TryGetProperty("index", out var index) && (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number) || number != 0))
                throw new InvalidOperationException("AI 返回了不支持的多候选流式回答。");
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                // Only public answer text is displayed. Role, reasoning_content and other metadata are not an answer.
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    if (content.ValueKind != JsonValueKind.String) throw new InvalidOperationException("AI 流式正文不是有效文本。");
                    var text = content.GetString()!;
                    if (text.Length > 0)
                    {
                        if (finished) throw new InvalidOperationException("AI 在完成标记后继续发送正文，回答未确认完成。");
                        if (answer.Length + text.Length > AiLimits.AnswerCharacters)
                        {
                            answer.Append(text.AsSpan(0, AiLimits.AnswerCharacters - answer.Length));
                            throw new AiTruncatedException(answer.ToString());
                        }
                        answer.Append(text);
                        yield return new AiTextChunk(text);
                    }
                }
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
            {
                if (finish.ValueKind != JsonValueKind.String) throw new InvalidOperationException("AI 流式完成标记无效。");
                if (finish.GetString() == "length") throw new AiTruncatedException(answer.ToString());
                if (finish.GetString() != "stop") throw new InvalidOperationException("模型未正常完成文本回答，请检查服务限制或模型兼容性。");
                finished = true;
            }
        }
        deadline.Token.ThrowIfCancellationRequested();
        if (!finished && !done) throw new IOException("AI stream ended without a completion marker.");
        if (string.IsNullOrWhiteSpace(answer.ToString())) throw new InvalidOperationException("AI 服务没有返回有效文本回答，请检查模型兼容性。");
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        // ponytail: bounded UTF-8 SSE reader for .NET 8; no SDK or extra runtime needed.
        const int maxEventCharacters = 128 * 1024;
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var line = new StringBuilder();
        var data = new StringBuilder();
        var totalBytes = 0;
        var previousCr = false;
        var firstCharacter = true;
        while (true)
        {
            var count = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
            totalBytes += count;
            if (totalBytes > AiLimits.ResponseBytes) throw new InvalidOperationException("服务响应过大，已中止读取。");
            var charCount = decoder.GetChars(bytes, 0, count, chars, 0, flush: count == 0);
            for (var i = 0; i < charCount; i++)
            {
                var character = chars[i];
                if (firstCharacter) { firstCharacter = false; if (character == '\uFEFF') continue; }
                if (character == '\n' && previousCr) { previousCr = false; continue; }
                previousCr = character == '\r';
                if (character is '\r' or '\n')
                {
                    var completed = CompleteLine();
                    if (completed is not null) yield return completed;
                }
                else
                {
                    if (line.Length + data.Length >= maxEventCharacters) throw new InvalidOperationException("单条 AI 流式事件过大，已中止读取。");
                    line.Append(character);
                }
            }
            if (count == 0) break;
        }
        // An event must end with an empty line. A cut-off JSON event is never treated as a completed response.
        if (data.Length > 0 || line.ToString().StartsWith("data:", StringComparison.Ordinal))
            throw new IOException("AI stream ended in the middle of an event.");

        string? CompleteLine()
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) return null;
                var completed = data.ToString(0, data.Length - 1);
                data.Clear();
                return completed;
            }
            var value = line.ToString();
            line.Clear();
            if (value == "data" || value.StartsWith("data:", StringComparison.Ordinal))
            {
                var start = value.Length > 4 ? 5 : 4;
                if (start < value.Length && value[start] == ' ') start++;
                data.Append(value.AsSpan(start));
                data.Append('\n');
                if (data.Length > maxEventCharacters) throw new InvalidOperationException("单条 AI 流式事件过大，已中止读取。");
            }
            return null;
        }
    }
}
