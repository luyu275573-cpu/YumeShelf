using System.Collections;
using System.Diagnostics;
using System.IO;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public enum GameScanMode { VisualNovel, Expanded }

public sealed class GameScanCandidate(string executablePath, string title, string engine, string reason, int score, bool requiresReview = false) : ObservableObject
{
    private bool _isSelected = !requiresReview;
    public string ExecutablePath { get; } = executablePath;
    public string Title { get; } = title;
    public string Engine { get; } = engine;
    public string Reason { get; } = reason;
    public int Score { get; } = score;
    public bool RequiresReview { get; } = requiresReview;
    public string ReviewLabel => RequiresReview ? "待确认" : "推荐入口";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record GameScanResult(IReadOnlyList<GameScanCandidate> Candidates, int VisitedDirectories, int SkippedDirectories, IReadOnlyList<string> Limits) : IReadOnlyList<GameScanCandidate>
{
    public bool IsIncomplete => Limits.Count > 0 || SkippedDirectories > 0;
    public string Summary => $"已检查 {VisitedDirectories} 个目录，发现 {Count} 个候选。"
        + (IsIncomplete ? $"扫描不完整：{string.Join("；", Limits)}{(SkippedDirectories > 0 ? $"；跳过 {SkippedDirectories} 个不可读取或链接目录" : "")}。可缩小范围继续扫描。" : "");
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
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", ".runtime", "node_modules", "$RECYCLE.BIN", "System Volume Information" };

    public Task<GameScanResult> ScanAsync(string root, ISet<string> existingPaths, CancellationToken token = default, GameScanMode mode = GameScanMode.VisualNovel)
        => Task.Run(() => Scan(root, existingPaths, token, mode), token);

    private static GameScanResult Scan(string root, ISet<string> existingPaths, CancellationToken token, GameScanMode mode)
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
                foreach (var candidate in FindCandidates(directory, entries.Take(MaxEntriesPerDirectory).ToArray(), mode, token, limits))
                {
                    if (existingPaths.Any(path => GameIdentity.AreSame(path, candidate.ExecutablePath))) continue;
                    if (results.Count == MaxResults) { limits.Add($"达到 {MaxResults} 个候选上限"); break; }
                    results.Add(candidate);
                }
                foreach (var child in entries.Take(MaxEntriesPerDirectory).Where(Directory.Exists))
                {
                    if (ExcludedDirectories.Contains(Path.GetFileName(child))) continue;
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

    private static IEnumerable<GameScanCandidate> FindCandidates(string directory, string[] entries, GameScanMode mode, CancellationToken token, ISet<string> limits)
    {
        var executables = entries.Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && IsPlainFile(path) && !IsTool(Path.GetFileNameWithoutExtension(path))).ToArray();
        if (executables.Length == 0) return [];
        var match = InspectEngine(directory, limits);
        var names = entries.Select(Path.GetFileName).OfType<string>().ToArray();
        var context = ContextScore(directory, names);
        var unknownWithContext = match.Name == "未知引擎" && context >= 2 && entries.Count(path => IsGameResource(path) && IsPlainFile(path)) >= 3;
        if (!match.IsVisualNovel && !(match.HasGameStructure && (mode == GameScanMode.Expanded || context >= 2)) && !unknownWithContext) return [];

        var structuredBinary = match.Name == "自定义视觉小说引擎";
        var candidates = new List<GameScanCandidate>();
        foreach (var entry in executables)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(entry);
            if (structuredBinary && !HasFile(directory, name + ".bin")) continue;
            if (match.Name == "Unity" && !HasUnityData(directory, name)) continue;
            var score = match.IsVisualNovel ? 9 : 5;
            if (name.Contains("chs", StringComparison.OrdinalIgnoreCase) || name.Contains("汉化", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (match.Name == "BGI" && name.StartsWith("bgi", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (name.Equals("game", StringComparison.OrdinalIgnoreCase) || name.Equals("start", StringComparison.OrdinalIgnoreCase)) score++;
            if (name.StartsWith("RPG_RT", StringComparison.OrdinalIgnoreCase) && match.Name == "RPG Maker 2000/2003") score += 2;
            if (name.Equals(match.Name + "Engine", StringComparison.OrdinalIgnoreCase) || name.Equals(match.Name, StringComparison.OrdinalIgnoreCase)) score += 2;
            var reason = unknownWithContext ? "存档、日文/汉化与资源组合；引擎未确认，请核对启动文件"
                : match.Evidence + (match.IsVisualNovel ? "" : "；玩法和 Galgame 属性未确认，请核对");
            candidates.Add(new(entry, name, match.Name, reason, score, requiresReview: !match.IsVisualNovel));
        }
        // ponytail: shared-resource engines use one ranked entry; custom BIN and Unity keep independently paired entries.
        var ranked = candidates.OrderByDescending(x => x.Score).ThenBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        if (match.Name != "Unity" && !structuredBinary && match.Name != "未知引擎") return ranked.Take(1).ToArray();
        var accepted = new List<GameScanCandidate>();
        foreach (var candidate in ranked)
            if (!accepted.Any(other => GameIdentity.AreSame(other.ExecutablePath, candidate.ExecutablePath))) accepted.Add(candidate);
        return accepted;
    }

    public static string DetectEngine(string directory) => InspectEngine(directory).Name;

    private sealed record EngineMatch(string Name, string Evidence, bool IsVisualNovel = false, bool HasGameStructure = false);

    private static EngineMatch InspectEngine(string directory, ISet<string>? limits = null)
    {
        bool Has(string file) => HasFile(directory, file);
        bool Dir(string path) => IsPlainRelativePath(directory, path) && Directory.Exists(Path.Combine(directory, path));
        IEnumerable<string> Files(string pattern, string subdirectory = "")
        {
            if (subdirectory.Length > 0 && !Dir(subdirectory)) return [];
            return Directory.EnumerateFiles(Path.Combine(directory, subdirectory), pattern).Take(MaxEntriesPerDirectory).Where(IsPlainFile);
        }
        bool ArchiveMatches(IEnumerable<string> paths, Func<string, bool> matches)
        {
            var files = paths.Take(33).ToArray();
            if (files.Length > 32) limits?.Add("部分目录超过 32 个引擎资源包，已限制文件头检查");
            return files.Take(32).Any(matches);
        }

        if (Dir("renpy") && Dir("game")) return new("Ren'Py", "renpy 与 game 运行目录", IsVisualNovel: true);
        if (Has("BGI.gdb") || Has("BGI.kdb") || Has("BGI.hvl") || (Has("sysgrp.arc") && Has("sysprg.arc") && Files("data*.arc").Any()))
            return new("BGI", "BGI 专用文件或系统资源组合", IsVisualNovel: true);
        if (Files("*.xp3").Any() || (Has("startup.tjs") && Files("*.ks").Any()))
            return new("KiriKiri", "XP3 资源包或 startup.tjs 与 KS 脚本组合", IsVisualNovel: true);
        if ((Dir("tyrano") && Dir("data/scenario")) || (Dir("www/tyrano") && Dir("www/data/scenario")))
            return new("TyranoScript", "Tyrano 运行目录与场景脚本目录", IsVisualNovel: true);
        if (Has("script.bin") && Has("bg.bin") && (Has("voc.bin") || Has("snd.bin")))
            return new("自定义视觉小说引擎", "脚本、背景、声音 BIN 与同名启动文件", IsVisualNovel: true);
        if ((Has("nscript.dat") || Has("nscr_sec.dat") || Has("0.txt") || Has("00.txt")) && (Files("*.nsa").Any() || Files("*.sar").Any()))
            return new("NScripter / ONScripter", "NScripter 脚本与 NSA/SAR 资源包", IsVisualNovel: true);
        if (Has("RealLive.exe") && Has("Seen.txt")) return new("RealLive", "RealLive 启动程序与 Seen 场景数据", IsVisualNovel: true);
        if (Has("SiglusEngine.exe") && Has("Scene.pck")) return new("Siglus", "SiglusEngine 与 Scene 场景包", IsVisualNovel: true);
        if (ArchiveMatches(Files("*.pfs").Concat(Files("*.pfs", "data")), path => HasHeader(path, "pf2"u8) || HasHeader(path, "pf6"u8) || HasHeader(path, "pf8"u8)))
            return new("Artemis", "PFS 资源包文件头（pf2/pf6/pf8）", IsVisualNovel: true);
        if (ArchiveMatches(Files("*.ypf").Concat(Files("*.ypf", "pac")), path => HasHeader(path, "YPF\0"u8)))
            return new("YU-RIS", "YPF 资源包文件头", IsVisualNovel: true);

        if (Has("RPG_RT.exe") && Has("RPG_RT.ldb") && Has("RPG_RT.lmt"))
            return new("RPG Maker 2000/2003", "RPG_RT 程序、数据库与地图树", HasGameStructure: true);
        if (Has("Game.ini"))
        {
            if (Files("*.rgss3a").Any() || (Has("Data/System.rvdata2") && Has("Data/Scripts.rvdata2")))
                return new("RPG Maker VX Ace", "Game.ini 与 RGSS3／rvdata2 游戏数据", HasGameStructure: true);
            if (Files("*.rgss2a").Any() || (Has("Data/System.rvdata") && Has("Data/Scripts.rvdata")))
                return new("RPG Maker VX", "Game.ini 与 RGSS2／rvdata 游戏数据", HasGameStructure: true);
            if (Files("*.rgssad").Any() || (Has("Data/System.rxdata") && Has("Data/Scripts.rxdata")))
                return new("RPG Maker XP", "Game.ini 与 RGSS／rxdata 游戏数据", HasGameStructure: true);
        }
        foreach (var webRoot in new[] { "", "www/" })
        {
            if (!Has("nw.dll") || !Has(webRoot + "index.html") || !Has(webRoot + "data/System.json")) continue;
            if (Has(webRoot + "js/rmmz_core.js")) return new("RPG Maker MZ", "NW.js、MZ 核心脚本与 System 游戏数据库", HasGameStructure: true);
            if (Has(webRoot + "js/rpg_core.js")) return new("RPG Maker MV", "NW.js、MV 核心脚本与 System 游戏数据库", HasGameStructure: true);
        }
        if ((Has("Game.exe") && Has("Data.wolf")) || (Has("Data/BasicData/Game.dat") && Has("Data/BasicData/SysDatabase.dat")))
            return new("WOLF RPG Editor", "WOLF 游戏资源包或 BasicData 系统数据库", HasGameStructure: true);
        if (Has("UnityPlayer.dll") || Has("GameAssembly.dll"))
            return new("Unity", "Unity 运行库与启动文件对应的 _Data 资源", HasGameStructure: true);
        if (Has("data.win") && HasHeader(Path.Combine(directory, "data.win"), "FORM"u8) && HasHeader(Path.Combine(directory, "data.win"), "GEN8"u8, 8))
            return new("GameMaker", "data.win 游戏资源文件头（FORM/GEN8）", HasGameStructure: true);
        if (Has("nw.dll") && (Has("package.json") || Has("www/package.json"))) return new("NW.js", "仅识别到 NW.js 运行框架，未确认游戏资源");
        return new("未知引擎", "未匹配专用引擎结构");
    }

    private static bool HasUnityData(string directory, string executableStem)
        => HasFile(directory, executableStem + "_Data/globalgamemanagers") || HasFile(directory, executableStem + "_Data/data.unity3d");

    private static bool IsTool(string name) => ExcludedNames.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))
        || name.Equals("Editor", StringComparison.OrdinalIgnoreCase) || name.Equals("GameEditor", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WolfEditor", StringComparison.OrdinalIgnoreCase) || name.Equals("GameMaker", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Unity", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static bool HasFile(string directory, string relative) => IsPlainRelativePath(directory, relative) && File.Exists(Path.Combine(directory, relative));
    private static bool IsPlainRelativePath(string directory, string relative)
    {
        foreach (var part in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            directory = Path.Combine(directory, part);
            if ((!File.Exists(directory) && !Directory.Exists(directory)) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static bool HasHeader(string path, ReadOnlySpan<byte> signature, int offset = 0)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset + signature.Length) return false;
            stream.Position = offset;
            Span<byte> header = stackalloc byte[signature.Length];
            stream.ReadExactly(header);
            return header.SequenceEqual(signature);
        }
        catch (EndOfStreamException) { return false; }
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
