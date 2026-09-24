using System.IO;
using System.Windows;
using System.Windows.Controls;
using YumeShelf.Application;
using YumeShelf.Common;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void ScanCoverageChecks()
    {
        // Structural export fixtures only: no downloaded games, executable code, or real library writes.
        var root = Folder("engine-coverage");
        var expected = new List<(string Entry, string Engine, bool VisualNovel)>();
        string Sample(string name, string engine, bool visualNovel, string executable, params string[] files)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            foreach (var relative in files)
            {
                var path = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            var entry = FakeExe(directory, executable);
            FakeExe(directory, "Uninstaller.exe");
            FakeExe(directory, "Config.exe");
            FakeExe(directory, "Editor.exe");
            if (engine.Length > 0) expected.Add((entry, engine, visualNovel));
            return directory;
        }

        Sample("ClassicRpg", "RPG Maker 2000/2003", false, "RPG_RT.exe", "RPG_RT.ldb", "RPG_RT.lmt");
        Sample("XpPacked", "RPG Maker XP", false, "Game.exe", "Game.ini", "Game.rgssad");
        Sample("XpLoose", "RPG Maker XP", false, "Game.exe", "Game.ini", "Data/System.rxdata", "Data/Scripts.rxdata");
        Sample("VxPacked", "RPG Maker VX", false, "Game.exe", "Game.ini", "Game.rgss2a");
        Sample("VxLoose", "RPG Maker VX", false, "Game.exe", "Game.ini", "Data/System.rvdata", "Data/Scripts.rvdata");
        Sample("AcePacked", "RPG Maker VX Ace", false, "Game.exe", "Game.ini", "Game.rgss3a");
        Sample("AceLoose", "RPG Maker VX Ace", false, "Game.exe", "Game.ini", "Data/System.rvdata2", "Data/Scripts.rvdata2");
        foreach (var version in new[] { "MV", "MZ" })
            foreach (var webRoot in new[] { "", "www/" })
                Sample(version + (webRoot.Length == 0 ? "Root" : "Web"), "RPG Maker " + version, false, "Game.exe",
                    "nw.dll", webRoot + "index.html", webRoot + "data/System.json", webRoot + "js/" + (version == "MV" ? "rpg_core.js" : "rmmz_core.js"));
        Sample("WolfPacked", "WOLF RPG Editor", false, "Game.exe", "Data.wolf");
        Sample("WolfLoose", "WOLF RPG Editor", false, "Adventure.exe", "Data/BasicData/Game.dat", "Data/BasicData/SysDatabase.dat");
        Sample("Nscript", "NScripter / ONScripter", true, "Novel.exe", "nscript.dat", "arc.nsa");
        Sample("Onscript", "NScripter / ONScripter", true, "onscripter.exe", "0.txt", "arc.sar");
        Sample("RealLive", "RealLive", true, "RealLive.exe", "Seen.txt");
        Sample("Siglus", "Siglus", true, "SiglusEngine.exe", "Scene.pck");
        foreach (var version in new[] { "pf2", "pf6", "pf8" })
        {
            var directory = Sample("Artemis" + version, "Artemis", true, "Novel.exe", "game.pfs");
            File.WriteAllBytes(Path.Combine(directory, "game.pfs"), System.Text.Encoding.ASCII.GetBytes(version + "fixture"));
        }
        foreach (var archiveRoot in new[] { "", "pac/" })
        {
            var directory = Sample("Yuris" + (archiveRoot.Length == 0 ? "Root" : "Pac"), "YU-RIS", true, "Novel.exe", archiveRoot + "data.ypf");
            File.WriteAllBytes(Path.Combine(directory, archiveRoot + "data.ypf"), "YPF\0fixture"u8.ToArray());
        }
        Sample("Kiri", "KiriKiri", true, "Novel.exe", "data.xp3");
        Sample("KiriLoose", "KiriKiri", true, "Novel.exe", "startup.tjs", "first.ks");
        Sample("Tyrano", "TyranoScript", true, "Novel.exe", "tyrano/tyrano.js", "data/scenario/first.ks");
        Sample("TyranoWeb", "TyranoScript", true, "Game.exe", "www/tyrano/tyrano.js", "www/data/scenario/first.ks", "nw.dll", "package.json");
        Sample("TyranoRuntime", "TyranoScript", true, "nw.exe", "tyrano/tyrano.js", "data/scenario/first.ks", "nw.dll", "package.json");
        Sample("BgiArchives", "BGI", true, "BGI.exe", "sysgrp.arc", "sysprg.arc", "data01.arc");
        var gm = Sample("GameMaker", "GameMaker", false, "Game.exe", "data.win");
        File.WriteAllBytes(Path.Combine(gm, "data.win"), "FORM\0\0\0\0GEN8fixture"u8.ToArray());
        var unity = Sample("UnityAction", "Unity", false, "Action.exe", "UnityPlayer.dll", "Action_Data/globalgamemanagers");
        var secondUnity = FakeExe(unity, "Strategy.exe");
        Directory.CreateDirectory(Path.Combine(unity, "Strategy_Data"));
        File.WriteAllText(Path.Combine(unity, "Strategy_Data/data.unity3d"), "fixture");
        expected.Add((secondUnity, "Unity", false));
        FakeExe(unity, "Viewer.exe"); // A sibling EXE must not borrow Action_Data as its own evidence.

        Sample("PlainUnity", "", false, "Viewer.exe", "UnityPlayer.dll");
        Sample("RuntimeOnly", "", false, "Game.exe", "nw.dll", "package.json", "index.html");
        Sample("PartialMv", "", false, "Game.exe", "nw.dll", "www/js/rpg_core.js");
        Sample("RpgExeOnly", "", false, "RPG_RT.exe");
        Sample("RgssDllOnly", "", false, "Game.exe", "RGSS301.dll", "Game.ini");
        Sample("WolfEditorOnly", "", false, "Editor.exe", "Data.wolf");
        Sample("BroadArc", "", false, "Viewer.exe", "data01.arc");
        Sample("LooseKs", "", false, "Viewer.exe", "readme.ks");
        Sample("BadPfs", "", false, "Viewer.exe", "game.pfs");
        Sample("BadYpf", "", false, "Viewer.exe", "game.ypf");
        Sample("BadGameMaker", "", false, "Viewer.exe", "data.win");
        Sample("NscriptWithoutResources", "", false, "Novel.exe", "nscript.dat");
        Sample("SceneWithoutEngine", "", false, "Viewer.exe", "Scene.pck");
        Sample(".runtime/GeneratedFixtures", "", false, "BGI.exe", "BGI.gdb");
        Sample("node_modules/Example", "", false, "BGI.exe", "BGI.gdb");
        Sample(".git/Snapshot", "", false, "BGI.exe", "BGI.gdb");
        var fakeDirectory = Sample("ExeDirectory", "", false, "Uninstaller.exe", "BGI.gdb");
        Directory.CreateDirectory(Path.Combine(fakeDirectory, "game.exe"));

        var scanner = new GameScanService();
        var expanded = Await(scanner.ScanAsync(root, new HashSet<string>(), mode: GameScanMode.Expanded));
        var strict = Await(scanner.ScanAsync(root, new HashSet<string>()));
        Check(expanded.Count == expected.Count && !expanded.IsIncomplete, "expanded mode finds all export fixtures and excludes runtime/editor/partial-resource counterexamples");
        foreach (var sample in expected)
            Check(expanded.Any(x => x.ExecutablePath == sample.Entry && x.Engine == sample.Engine &&
                x.IsSelected == sample.VisualNovel && x.RequiresReview != sample.VisualNovel), "engine evidence and initial selection: " + Path.GetFileName(Path.GetDirectoryName(sample.Entry)));
        Check(strict.Select(x => x.ExecutablePath).Order().SequenceEqual(expected.Where(x => x.VisualNovel).Select(x => x.Entry).Order()),
            "default mode retains visual novels while excluding English ACT/SLG/RPG exports without novel context");
        Check(expanded.All(x => !string.IsNullOrWhiteSpace(x.Reason)) && expanded.Where(x => x.RequiresReview).All(x => x.Reason.Contains("未确认")),
            "non-visual-novel candidates explain uncertainty instead of assigning a gameplay genre");

        var context = Sample("Context", "", false, "Game_chs.exe", "UnityPlayer.dll", "Game_chs_Data/globalgamemanagers", "savedata/slot.dat");
        var contextResult = Await(scanner.ScanAsync(context, new HashSet<string>()));
        Check(contextResult.Count == 1 && contextResult[0].RequiresReview && !contextResult[0].IsSelected, "generic engine with two context categories is offered for explicit review in default mode");
        var fallback = Sample("未知汉化", "", false, "Game_chs.exe", "savedata/slot.dat", "a.dat", "b.bin", "c.pak");
        var fallbackResult = Await(scanner.ScanAsync(fallback, new HashSet<string>()));
        Check(fallbackResult.Count == 1 && fallbackResult[0].Engine == "未知引擎" && fallbackResult[0].RequiresReview,
            "unknown resource heuristics do not falsely label a confirmed visual novel engine");
        var existing = new HashSet<string>(expanded.Select(x => x.ExecutablePath.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        Check(Await(scanner.ScanAsync(unity, existing, mode: GameScanMode.Expanded)).Count == 0, "existing paths remain excluded in expanded mode regardless of casing");
        var import = Vm(Path.Combine(Output, "expanded-import.json"));
        var mv = expected.Single(x => x.Engine == "RPG Maker MV" && Path.GetFileName(Path.GetDirectoryName(x.Entry)) == "MVWeb");
        Check(import.AddGameFromPath(mv.Entry) == GameAddOutcome.Added && import.Games[0].Engine == mv.Engine &&
            import.AddGameFromPath(mv.Entry) == GameAddOutcome.Duplicate, "manual import uses specific RPG engine detection and retains duplicate protection");

        ScanWindowChecks(root, expanded);
        var capped = Folder("engine-header-limit");
        FakeExe(capped);
        for (var i = 0; i < 33; i++) File.WriteAllText(Path.Combine(capped, $"archive{i}.ypf"), "bad header");
        var cappedResult = Await(scanner.ScanAsync(capped, new HashSet<string>()));
        Check(cappedResult.IsIncomplete && cappedResult.Limits.Any(x => x.Contains("文件头")), "resource header budget reports incomplete scanning instead of silent omissions");
        var locked = Folder("engine-locked-resource");
        FakeExe(locked);
        using (var held = new FileStream(Path.Combine(locked, "game.pfs"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedResult = Await(scanner.ScanAsync(locked, new HashSet<string>()));
            Check(lockedResult.IsIncomplete && lockedResult.SkippedDirectories == 1, "unreadable resource header is reported as incomplete rather than no matching games");
        }
        var classic = Path.Combine(root, "ClassicRpg");
        var translated = FakeExe(classic, "RPG_RT_chs.exe");
        Check(Await(scanner.ScanAsync(classic, new HashSet<string>(), mode: GameScanMode.Expanded)).Single().ExecutablePath == translated,
            "translated classic RPG entry outranks the original runtime");
    }

    private static void ScanWindowChecks(string scanRoot, GameScanResult result)
    {
        var imports = 0;
        var window = new AddGameWindow(_ => { imports++; return GameAddOutcome.Duplicate; });
        var vm = (AddGameViewModel)window.DataContext;
        var root = (FrameworkElement)window.Content;
        var selector = (ComboBox)window.FindName("ScanModeBox");
        var action = (Button)window.FindName("ActionButton");
        var list = (ListBox)window.FindName("ResultsList");
        Render(root, "scan-default-small.png", 664, 560);
        Check(vm.ScanMode == GameScanMode.VisualNovel && Equals(selector.SelectedValue, GameScanMode.VisualNovel), "scan UI defaults to visual novel mode with correct enum binding");
        vm.SearchRoot = scanRoot;
        vm.Candidates.Add(result.First(x => x.RequiresReview));
        vm.Feedback.Show("old result");
        selector.SelectedValue = GameScanMode.Expanded;
        Check(vm.ScanMode == GameScanMode.Expanded && vm.Candidates.Count == 0 && !vm.Feedback.IsVisible && action.Content as string == "开始扫描",
            "changing scan mode clears stale candidates, feedback, and confirmation action");
        foreach (var item in result.Where(x => x.RequiresReview).Take(4)) vm.Candidates.Add(item);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(imports == 0 && vm.Feedback.Current?.Kind == FeedbackKind.Warning, "unselected expanded candidates cannot be imported by confirmation");
        vm.Feedback.DismissCommand.Execute(null);
        Render(root, "scan-expanded-small.png", 664, 560);
        Check(Bounds(action, root).Bottom <= root.ActualHeight && Bounds(list, root).Height >= 90 &&
            Bounds(selector, root).Right <= root.ActualWidth, "small scan window keeps mode selector, scrollable results, and confirmation visible");
        vm.SelectAllCommand.Execute(null);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(imports == 4 && window.ResultMessage!.Contains("4 个重复项"), "expanded candidates reach the shared import workflow only after selection and confirmation");
        vm.ClearAllCommand.Execute(null);
        Check(vm.Candidates.All(x => !x.IsSelected), "clear selection also applies to expanded candidates");
        var autoPanel = (Border)window.FindName("AutoPanel");
        var autoHeight = autoPanel.ActualHeight;
        var tabs = (TabControl)window.FindName("ModeTabs");
        tabs.SelectedIndex = 1;
        Render(root, "scan-manual-small.png", 664, 560);
        Check(((Border)window.FindName("ManualPanel")).ActualHeight == autoHeight, "automatic and manual mode panels retain identical height");
        tabs.SelectedIndex = 0;
        vm.IsScanning = true;
        vm.ScanMode = GameScanMode.VisualNovel;
        Render(root, "scan-busy-small.png", 664, 560);
        Check(vm.ScanMode == GameScanMode.Expanded && !selector.IsEnabled && !action.IsEnabled && !list.IsEnabled, "busy scan freezes mode, selections, and duplicate submissions");
        vm.IsScanning = false;
        ((Button)window.FindName("RescanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !vm.IsScanning);
        Check(vm.Candidates.Count == result.Count + 2 && vm.Status.Contains("待确认") && action.Content as string == "确认添加" && imports == 4,
            "rescan uses selected mode, replaces old results, and waits for explicit import");
        vm.Feedback.DismissCommand.Execute(null);
        Render(root, "scan-expanded-wide.png", 784, 640);
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
        Render(root, "scan-expanded-night.png", 664, 560);
        Check(selector.Background == System.Windows.Application.Current.Resources["SearchBackgroundBrush"] &&
            ((Border)window.FindName("AutoPanel")).Background == System.Windows.Application.Current.Resources["CardBackground"],
            "scan selector and mode panels follow the night theme");
        ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings());
        window.Close();
    }
}
