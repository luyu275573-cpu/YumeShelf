using System.IO;

namespace YumeShelf.Application;

public sealed record GameScanCandidate(string ExecutablePath, string Title, string Engine, string Reason, int Score)
{
    public bool IsSelected { get; set; } = true;
}

public sealed class GameScanService
{
    private static readonly string[] ExcludedNames = ["unins", "uninstall", "setup", "install", "crash", "updater", "update", "launcher", "bhvc", "cfg", "config", "configure", "patch", "patcher", "redist", "vcredist", "dxsetup", "unitycrashhandler", "senddmp"];
    private static readonly string[] StrongFiles = ["UnityPlayer.dll", "GameAssembly.dll", "data.win", "nw.dll", "package.json", "RPG_RT.exe"];

    public Task<IReadOnlyList<GameScanCandidate>> ScanAsync(string root, ISet<string> existingPaths, CancellationToken token = default)
        => Task.Run(() => Scan(root, existingPaths, token), token);

    private static IReadOnlyList<GameScanCandidate> Scan(string root, ISet<string> existingPaths, CancellationToken token)
    {
        var results = new List<GameScanCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Queue<(string Path, int Depth)>();
        var visitedDirectories = 0;
        if (Directory.Exists(root)) dirs.Enqueue((Path.GetFullPath(root), 0));
        while (dirs.Count > 0 && visitedDirectories++ < 10000 && results.Count < 500)
        {
            token.ThrowIfCancellationRequested();
            var (dir, depth) = dirs.Dequeue();
            try
            {
                var files = Directory.EnumerateFiles(dir, "*.exe").ToArray();
                foreach (var file in files)
                {
                    if (results.Count >= 200) break;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (ExcludedNames.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    var full = Path.GetFullPath(file);
                    if (existingPaths.Contains(full) || !seen.Add(full)) continue;
                    var (score, engine, reason) = Score(dir, file, name);
                    if (score >= 2) results.Add(new(full, name, engine, reason, score));
                }
                if (depth < 5)
                    foreach (var child in Directory.EnumerateDirectories(dir)) dirs.Enqueue((child, depth + 1));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return results.GroupBy(x => Path.GetDirectoryName(x.ExecutablePath) ?? x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Title.Contains("chs", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Title).First())
            .OrderByDescending(x => x.Score).ThenBy(x => x.Title).ToArray();
    }

    private static (int Score, string Engine, string Reason) Score(string dir, string executablePath, string name)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var file in Directory.EnumerateFiles(dir).Take(80)) files.Add(Path.GetFileName(file)); } catch { }
        var lowerDir = dir.ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(executablePath);
        var resourceCount = files.Count(file => IsGameResource(file));
        var hasMatchingResource = files.Any(file => Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase)
            && !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var gameContext = HasGameContext(dir, files, name);
        var directoryEvidence = GetDirectoryEvidence(dir, files);
        var (score, engine, reason) = files.Contains("UnityPlayer.dll") || files.Contains("GameAssembly.dll") ? (8, "Unity", "检测到 Unity 游戏组件")
            : files.Contains("BGI.gdb") || files.Contains("BGI.kdb") || files.Contains("BGI.hvl") || files.Any(x => x.StartsWith("data", StringComparison.OrdinalIgnoreCase) && x.EndsWith(".arc", StringComparison.OrdinalIgnoreCase)) ? (9, "BGI", "检测到 BGI 游戏数据库和 ARC 资源")
            : files.Contains("data.win") ? (8, "GameMaker", "检测到 data.win 资源")
            : files.Contains("RPG_RT.exe") ? (8, "RPG Maker", "检测到 RPG Maker 运行文件")
            : files.Any(x => x.EndsWith(".xp3", StringComparison.OrdinalIgnoreCase)) ? (8, "KiriKiri", "检测到 XP3 资源包")
            : files.Any(x => x.EndsWith(".ks", StringComparison.OrdinalIgnoreCase)) || lowerDir.Contains("tyranoscript") ? (7, "TyranoScript", "检测到视觉小说脚本资源")
            : files.Contains("nw.dll") && files.Contains("package.json") ? (7, "NW.js", "检测到 NW.js 游戏组件")
            : hasMatchingResource && resourceCount >= 3 && gameContext ? (5, "自定义视觉小说引擎", "检测到与启动文件同名的游戏资源")
            : (0, "未知引擎", "");
        var hints = new List<string>();
        var lowerName = name.ToLowerInvariant();
        if (lowerName is "game" or "start" or "play" or "main" || lowerName.EndsWith("_chs") || lowerName.EndsWith("chs")) { score += 2; hints.Add("启动文件名称符合游戏入口特征"); }
        if (hasMatchingResource && resourceCount >= 3) { score += 2; hints.Add("启动文件存在同名资源文件"); }
        foreach (var evidence in directoryEvidence)
        {
            score += 1;
            hints.Add(evidence);
        }
        if (engine == "BGI" && (lowerName == "bgi" || lowerName.Contains("bgi") || lowerName.Contains("chs"))) { score += 2; hints.Add("优先识别 BGI 主启动文件"); }
        try
        {
            var length = new FileInfo(executablePath).Length;
            if (length >= 256 * 1024) { score++; hints.Add("启动文件体积符合游戏程序特征"); }
        }
        catch (IOException) { }
        if (hints.Count > 0) reason = string.IsNullOrWhiteSpace(reason) ? string.Join("；", hints) : reason + "；" + string.Join("；", hints);
        if (name.Contains("game", StringComparison.OrdinalIgnoreCase) || name.Contains("novel", StringComparison.OrdinalIgnoreCase) || name.Contains("visual", StringComparison.OrdinalIgnoreCase) || name.Contains("story", StringComparison.OrdinalIgnoreCase)) score++;
        if (lowerDir.Contains("galgame") || lowerDir.Contains("visual novel") || lowerDir.Contains("\u89c6\u89c9\u5c0f\u8bf4")) score++;
        if (score > 0 && string.IsNullOrWhiteSpace(reason)) reason = "名称或目录符合视觉小说特征";
        if (engine == "未知引擎" && !gameContext) return (0, engine, string.Empty);
        return (score, engine, reason);
    }

    private static bool HasGameContext(string dir, HashSet<string> files, string name)
    {
        var currentDirectoryName = Path.GetFileName(dir);
        return ContainsJapaneseText(currentDirectoryName)
            || name.EndsWith("chs", StringComparison.OrdinalIgnoreCase)
            || files.Any(x => x.Contains("savedata", StringComparison.OrdinalIgnoreCase)
                || x.Contains("汉化", StringComparison.OrdinalIgnoreCase)
                || x.Contains("中文", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> GetDirectoryEvidence(string dir, HashSet<string> files)
    {
        var evidence = new List<string>();
        var names = new List<string>(files);
        try
        {
            names.AddRange(Directory.EnumerateDirectories(dir).Take(40).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x))!);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        if (names.Any(x => x.Contains("savedata", StringComparison.OrdinalIgnoreCase) || x.Contains("save data", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("检测到游戏存档目录");
        if (names.Any(x => x.Contains("汉化", StringComparison.OrdinalIgnoreCase) || x.Contains("中文", StringComparison.OrdinalIgnoreCase) || x.Contains("chs", StringComparison.OrdinalIgnoreCase)))
            evidence.Add("检测到汉化或中文资源标记");
        var currentDirectoryName = Path.GetFileName(dir);
        if (ContainsJapaneseText(currentDirectoryName))
            evidence.Add("目录或文件名包含日文字符");
        if (files.Count(IsGameResource) >= 3)
            evidence.Add("检测到视觉小说常见资源文件");
        return evidence;
    }

    private static bool IsGameResource(string file)
        => file.EndsWith(".arc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsJapaneseText(string? value)
        => value is not null && value.Any(ch => ch >= '\u3040' && ch <= '\u30ff');
}
