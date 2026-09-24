using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YumeShelf.Application.AI;

public static class AiCapabilityChecks
{
    private static readonly Dictionary<string, string> Results = new();
    private static string Identity(string endpoint, string key, string model, string kind) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"adapter-1\n{endpoint.Trim().TrimEnd('/')}\n{key}\n{model.Trim()}\n{kind}")));
    public static string Status(string endpoint, string key, string model, string kind)
    {
        lock (Results) return Results.GetValueOrDefault(Identity(endpoint, key, model, kind), "未验证");
    }
    public static void Record(string endpoint, string key, string model, string kind, string status)
    {
        lock (Results)
        {
            if (Results.Count >= 64) Results.Clear();
            Results[Identity(endpoint, key, model, kind)] = status;
        }
    }
    public static async Task TestVisionAsync(string endpoint, string key, string model, TimeSpan timeout, CancellationToken token)
    {
        var code = RandomNumberGenerator.GetInt32(1000, 10000).ToString(CultureInfo.InvariantCulture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 360, 140));
            dc.DrawText(new FormattedText(code, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 78, Brushes.Black, 1), new Point(75, 20));
        }
        var bitmap = new RenderTargetBitmap(360, 140, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var attachment = AiImageAttachment.FromBitmap(bitmap, "视觉能力测试");
        var result = await new OpenAiCompatibleProvider().CompleteAsync(endpoint, key, model,
            [new("system", "执行图片读取测试，只回复图片中的四位数字，不要说明。"), new("user", "图片中央的数字是什么？", attachment)],
            timeout, token, new AiCompletionOptions(0, 256));
        if (result.Trim() != code) throw new InvalidOperationException("服务已响应，但未正确读取测试图。视觉能力尚未验证，请检查模型或重试。");
    }
    public static async Task TestProtocolAsync(string endpoint, string key, string model, TimeSpan timeout, CancellationToken token)
    {
        var text = await new OpenAiCompatibleProvider().CompleteAsync(endpoint, key, model,
            [new("system", "只返回 JSON：{\"route\":\"tool\",\"tool\":\"test_echo\",\"arguments\":{\"text\":\"YUME\"}}。这是无副作用的协议测试。"), new("user", "请按指定协议调用测试工具。")],
            timeout, token, new AiCompletionOptions(0, 256));
        using var doc = YumeAgent.ParseObject(text, 2000);
        var root = doc.RootElement;
        if (YumeAgent.Text(root, "route", 10) != "tool" || YumeAgent.Text(root, "tool", 30) != "test_echo" ||
            !root.TryGetProperty("arguments", out var args) || args.ValueKind != System.Text.Json.JsonValueKind.Object || YumeAgent.Text(args, "text", 10) != "YUME")
            throw new InvalidOperationException("结构化工具协议未通过，请尝试支持 JSON 指令的模型。");
    }
}
