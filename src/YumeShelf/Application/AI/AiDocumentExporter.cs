using System.IO;
using System.Net;
using System.Text;

namespace YumeShelf.Application.AI;

public static class AiDocumentExporter
{
    public static string Html(string markdown) => "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Yume 游戏资料</title><style>body{max-width:850px;margin:48px auto;padding:24px;font:16px/1.8 system-ui}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style><h1>Yume 游戏资料草稿</h1><p>模型整理，需人工核对；实际来源见正文。</p><pre>" + WebUtility.HtmlEncode(markdown) + "</pre></html>";
    public static void Save(string path, string markdown)
    {
        if (markdown.Length > 24000) throw new InvalidDataException("资料草稿超过导出长度上限。");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".md" or ".html")) throw new InvalidDataException("请选择 .md 或 .html 文档格式。");
        var text = extension == ".html" ? Html(markdown) : "<!-- Yume 资料草稿：模型整理，需人工核对；实际来源见正文。 -->\n\n" + markdown;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
