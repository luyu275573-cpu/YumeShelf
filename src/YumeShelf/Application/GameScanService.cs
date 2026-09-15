using System.Collections;
using System.Diagnostics;
using System.IO;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public sealed class GameScanCandidate(string executablePath, string title, string engine, string reason, int score) : ObservableObject
{
    private bool _isSelected = true;
    public string ExecutablePath { get; } = executablePath;
    public string Title { get; } = title;
    public string Engine { get; } = engine;
    public string Reason { get; } = reason;
    public int Score { get; } = score;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record GameScanResult(IReadOnlyList<GameScanCandidate> Candidates, int VisitedDirectories, int SkippedDirectories, IReadOnlyList<string> Limits) : IReadOnlyList<GameScanCandidate>
{
    public bool IsIncomplete => Limits.Count > 0 || SkippedDirectories > 0;
    public string Summary => $"已检查 {VisitedDirectories} 个目录，发现 {Count} 个候选。"
        + (IsIncomplete ? $"扫描不完整：{string.Join("；", Limits)}{(SkippedDirectories > 0 ? $"；跳过 {SkippedDirectories} 个无权限或链接目录" : "")}。可缩小范围继续扫描。" : "");
    public int Count => Candidates.Count;
    public GameScanCandidate this[int index] => Candidates[index];
    public IEnumerator<GameScanCandidate> GetEnumerator() => Candidates.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class GameScanService
{
    public const int MaxDepth = 12;
    public const int MaxDirectories = 10000;
    public const int MaxResults = 500;
    private const int MaxEntriesPerDirectory = 4096;
    private static readonly string[] ExcludedNames = ["unins", "uninstall", "setup", "install", "crash", "unitycrashhandler", "updater", "update", "bhvc", "cfg", "config", "configure", "patch", "patcher", "redist", "vcredist", "dxsetup", "senddmp"];

    public Task<GameScanResult> ScanAsync(string root, ISet<string> existingPaths, CancellationToken token = default)
        => Task.Run(() => Scan(root, existingPaths, token), token);

    private static GameScanResult Scan(string root, ISet<string> existingPaths, CancellationToken token)
    {
        var results = new List<GameScanCandidate>();
        var queue = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limits = new HashSet<string>();
        var skipped = 0;
        var watch = Stopwatch.StartNew();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("搜索目录不存在。");
        queue.Enqueue((Path.GetFullPath(root), 0));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (visited.Count >= MaxDirectories) { limits.Add($"达到 {MaxDirectories} 个目录上限"); break; }
            if (results.Count >= MaxResults) { limits.Add($"达到 {MaxResults} 个候选上限"); break; }
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) { limits.Add("达到 60 秒扫描时限"); break; }
            var (directory, depth) = queue.Dequeue();
            if (!visited.Add(directory)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                var entries = Directory.EnumerateFileSystemEntries(directory).Take(MaxEntriesPerDirectory + 1).ToArray();
                if (entries.Length > MaxEntriesPerDirectory) limits.Add("部分目录超过 4096 个文件，已限制枚举");
                var engine = DetectEngine(directory);
                var names = entries.Select(Path.GetFileName).OfType<string>().ToArray();
                var context = ContextScore(directory, names);
                var resources = names.Count(IsGameResource);
                var structuredBinary = engine == "自定义视觉小说引擎";
                var dedicated = engine is "Ren'Py" or "BGI" or "KiriKiri" or "TyranoScript" || structuredBinary;
                var candidates = new List<GameScanCandidate>();
                foreach (var entry in entries.Take(MaxEntriesPerDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(entry), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(entry);
                    if (ExcludedNames.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)) || existingPaths.Contains(entry)) continue;
                    if (structuredBinary && !File.Exists(Path.ChangeExtension(entry, ".bin"))) continue;
                    if (!dedicated && (context < 2 || (engine == "未知引擎" && resources < 3))) continue;
                    var effectiveEngine = engine == "未知引擎" ? "自定义视觉小说引擎" : engine;
                    var score = dedicated ? 9 : 5;
                    var chineseEntry = name.Contains("chs", StringComparison.OrdinalIgnoreCase) || name.Contains("汉化", StringComparison.OrdinalIgnoreCase);
                    if (chineseEntry) score += 3;
                    if (engine == "BGI" && name.StartsWith("bgi", StringComparison.OrdinalIgnoreCase)) score += 2;
                    if (name.Equals("game", StringComparison.OrdinalIgnoreCase) || name.Equals("start", StringComparison.OrdinalIgnoreCase)) score++;
                    candidates.Add(new(entry, name, effectiveEngine, dedicated ? $"检测到 {engine} 视觉小说结构" : "检测到存档、日文/汉化或视觉小说资源组合，建议核对入口", score));
                }
                // A directory may contain standalone games. Only rank alternative entries for known engines.
                var accepted = new List<GameScanCandidate>();
                foreach (var candidate in dedicated && !structuredBinary ? candidates.OrderByDescending(x => x.Score).Take(1) : candidates.OrderByDescending(x => x.Score))
                {
                    if (accepted.Any(other => GameIdentity.AreSame(other.ExecutablePath, candidate.ExecutablePath))) continue;
                    if (results.Count == MaxResults) { limits.Add($"达到 {MaxResults} 个候选上限"); break; }
                    results.Add(candidate); accepted.Add(candidate);
                }
                foreach (var child in entries.Take(MaxEntriesPerDirectory).Where(Directory.Exists))
                {
                    if (depth >= MaxDepth) { limits.Add($"达到 {MaxDepth} 层目录深度上限"); break; }
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            { skipped++; AppLog.Write("scan.directory-skipped", ex); }
        }
        AppLog.Write("scan.completed");
        return new(results.OrderByDescending(x => x.Score).ThenBy(x => x.Title).ToArray(), visited.Count, skipped, limits.ToArray());
    }

    public static string DetectEngine(string directory)
    {
        bool Has(string file) => File.Exists(Path.Combine(directory, file));
        bool Any(string pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any();
        if (Directory.Exists(Path.Combine(directory, "renpy")) && Directory.Exists(Path.Combine(directory, "game"))) return "Ren'Py";
        if (Has("BGI.gdb") || Has("BGI.kdb") || Has("BGI.hvl") || Any("data*.arc")) return "BGI";
        if (Any("*.xp3")) return "KiriKiri";
        if (Any("*.ks") || Directory.Exists(Path.Combine(directory, "tyrano")) || Directory.Exists(Path.Combine(directory, "data", "scenario"))) return "TyranoScript";
        if (Has("script.bin") && Has("bg.bin") && (Has("voc.bin") || Has("snd.bin"))) return "自定义视觉小说引擎";
        if (Has("UnityPlayer.dll") || Has("GameAssembly.dll")) return "Unity";
        if (Has("data.win")) return "GameMaker";
        if (Has("RPG_RT.exe")) return "RPG Maker";
        if (Has("nw.dll") && Has("package.json")) return "NW.js";
        return "未知引擎";
    }

    private static int ContextScore(string directory, IReadOnlyList<string> names)
    {
        var score = 0;
        if (names.Any(x => x.Contains("savedata", StringComparison.OrdinalIgnoreCase) || x.Equals("save", StringComparison.OrdinalIgnoreCase))) score++;
        if (names.Any(x => x.Contains("汉化") || x.Contains("中文") || x.Contains("chs", StringComparison.OrdinalIgnoreCase))) score++;
        if (Path.GetFileName(directory).Any(c => c is >= '\u3040' and <= '\u30ff') || names.Any(x => x.Any(c => c is >= '\u3040' and <= '\u30ff'))) score++;
        if (directory.Contains("visual novel", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(directory).Contains("galgame", StringComparison.OrdinalIgnoreCase)) score++;
        return score;
    }

    private static bool IsGameResource(string file)
        => Path.GetExtension(file).ToLowerInvariant() is ".arc" or ".bin" or ".pak" or ".dat";
}
