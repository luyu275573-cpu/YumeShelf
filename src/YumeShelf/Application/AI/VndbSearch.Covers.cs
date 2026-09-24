using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using YumeShelf.Common;

namespace YumeShelf.Application.AI;

public sealed record AiCoverCandidate(AiSource Source, string ImageUrl, string ThumbnailUrl);
public sealed record AiCoverDraft(AiGameSummary Original, IReadOnlyList<AiCoverCandidate> Candidates);
public sealed record AiResearchResult(IReadOnlyList<AiSource> Sources, IReadOnlyList<AiCoverCandidate> Covers);

public sealed partial class VndbSearch
{
    public async Task<IReadOnlyList<AiCoverCandidate>> SearchCoversAsync(string query, CancellationToken token)
        => (await SearchGameAsync(query, token)).Covers;

    public async Task<AiResearchResult> SearchGameAsync(string query, CancellationToken token)
    {
        using var json = await QueryAsync(query, "title,alttitle,description,released,image.url,image.thumbnail,image.sexual,image.violence", token);
        var covers = new List<AiCoverCandidate>();
        var sources = new List<AiSource>();
        foreach (var row in Results(json))
        {
            var source = ReadSource(row);
            sources.Add(source);
            if (!row.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object) continue;
            // Only ordinary cover art is offered; missing or explicit-content ratings are not previewed.
            if (!SafeRating(image, "sexual") || !SafeRating(image, "violence")) continue;
            var full = YumeAgent.Text(image, "url", 200);
            var thumb = YumeAgent.Text(image, "thumbnail", 200);
            if (!IsCoverUrl(full, false) || !IsCoverUrl(thumb, true)) continue;
            if (covers.All(c => c.ImageUrl != full)) covers.Add(new(source, full, thumb));
        }
        return new(sources, covers);
    }

    private static bool SafeRating(JsonElement image, string name) => image.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var rating) && double.IsFinite(rating) && rating >= 0 && rating < 1;

    public static bool IsCoverUrl(string url, bool thumbnail) => url.Length <= 200 &&
        Regex.IsMatch(url, thumbnail ? @"\Ahttps://t\.vndb\.org/cv\.t/[0-9]{2}/[1-9][0-9]{0,11}\.(jpg|png)\z" :
            @"\Ahttps://t\.vndb\.org/cv/[0-9]{2}/[1-9][0-9]{0,11}\.(jpg|png)\z", RegexOptions.CultureInvariant);

    public async Task<AiImageAttachment> DownloadCoverAsync(AiCoverCandidate candidate, bool thumbnail, CancellationToken token)
    {
        var url = thumbnail ? candidate.ThumbnailUrl : candidate.ImageUrl;
        if (!IsCoverUrl(url, thumbnail)) throw new InvalidOperationException("封面地址不在允许的 VNDB 图片来源内。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Dedicated public client: no API key, cookies, arbitrary hosts or redirects.
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"封面下载返回 HTTP {(int)response.StatusCode}，原封面未改变。");
        if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("封面服务没有返回有效的 JPG/PNG 图片。");
        var limit = thumbnail ? 2 * 1024 * 1024 : 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("封面图片超过下载上限。");
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("封面图片超过下载上限。");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (!(bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) &&
            !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new InvalidDataException("封面文件格式不正确，已停止处理。");
        var prepared = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return AiImageAttachment.FromBitmap(GameImageLoader.Load(stream, thumbnail ? 480 : AiLimits.ImageEdge), "VNDB 封面");
        }, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return prepared;
    }
}
