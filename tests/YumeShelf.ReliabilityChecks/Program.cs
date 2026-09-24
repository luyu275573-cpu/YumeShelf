using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Domain;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static string Output = "";
    private static readonly List<string> Passed = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--instance-check")
        {
            using var instance = new SingleInstanceGuard(args[1]);
            return instance.IsPrimary ? 0 : 77;
        }
        Output = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Output);
        AppLog.DirectoryPath = Path.Combine(Output, "logs");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YumeShelf;component/Resources/Colors.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YumeShelf;component/Resources/Styles.xaml", UriKind.Relative) });
        app.Resources["BooleanToVisibilityConverter"] = new YumeShelf.Common.BooleanToVisibilityConverter();
        app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        app.Resources["InverseBooleanConverter"] = new InverseBooleanConverter();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            if (args.Length == 1 && args[0] == "--cover-source-check") { CoverSourceCheck(); return 0; }
            StorageChecks();
            ScanChecks();
            ScanCoverageChecks();
            UiChecks();
            StartupChecks();
            AiChecks();
            AiStreamingChecks();
            AiVisionChecks();
            YumeUpgradeChecks();
            AiCoverChecks();
            AiLibraryAgentChecks();
            AiLibraryTruthChecks();
            AiFitChecks();
            AiSessionAndFiltersChecks();
            if (args.Length == 0) throw new ArgumentException("Pass the built YumeShelf.TestGame.exe path.");
            LaunchChecks(Path.GetFullPath(args[0]));
            if (args.Length > 1) RealScan(args.Skip(1));
            Check(errors.Errors.Count == 0, "WPF bindings have no errors");
            File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(Passed, JsonOptions));
            Console.WriteLine($"ALL {Passed.Count} CHECKS PASSED. EVIDENCE: {Output}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Output, "failure.txt"), ex.ToString() + "\n" + string.Join("\n", errors.Errors));
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine("EVIDENCE: " + Output);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Passed.Add(message);
        Console.WriteLine("PASS: " + message);
    }
    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + ": " + message);
    }
    private static string Folder(string name) { var path = Path.Combine(Output, name); Directory.CreateDirectory(path); return path; }
    private static string FakeExe(string folder, string name = "game.exe")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x4D, 0x5A]); // Import fixtures only, never executed.
        return path;
    }
    private static Game GameAt(string path) => new() { Title = Path.GetFileNameWithoutExtension(path), ExecutablePath = path, RootPath = Path.GetDirectoryName(path)!, WorkingDirectory = Path.GetDirectoryName(path)! };
    private static MainWindowViewModel Vm(string path) => new(new GameLibraryService(new JsonGameStore(path)), new GameLaunchService(), new AppSettingsStore(Path.Combine(Output, Guid.NewGuid() + "-settings.json")));

    private static void StorageChecks()
    {
        var exe = FakeExe(Folder("import"));
        foreach (var bad in new[] { "[{\"Title\":\"old-game\"", "[{}]", "[null]", "null", "{}" })
        {
            var file = Path.Combine(Output, Guid.NewGuid() + ".json");
            File.WriteAllText(file, bad);
            var vm = Vm(file);
            Check(vm.IsLibraryReadOnly && vm.AddGameFromPath(exe) == GameAddOutcome.Failed && File.ReadAllText(file) == bad, "invalid library is recoverable and never silently overwritten: " + bad);
        }
        var partialFile = Path.Combine(Output, "partial.json");
        var oversizedPath = Path.Combine(Output, "oversized-library.json");
        using (var large = File.Create(oversizedPath)) large.SetLength(17 * 1024 * 1024);
        Check(Vm(oversizedPath).IsLibraryReadOnly, "oversized library opens safely in recovery mode");
        var unstablePath = Path.Combine(Output, "missing-id.json");
        File.WriteAllText(unstablePath, JsonSerializer.Serialize(new[] { new { ExecutablePath = exe } }));
        Check(Vm(unstablePath).IsLibraryReadOnly, "missing stable ID cannot silently change on refresh");
        var game = GameAt(exe);
        File.WriteAllText(partialFile, "[" + JsonSerializer.Serialize(game) + ",null," + JsonSerializer.Serialize(game) + "]");
        var store = new JsonGameStore(partialFile);
        var valid = store.Load();
        Check(valid.Count == 1 && store.IsReadOnly, "valid records survive malformed/duplicate neighbors");
        Reject<InvalidDataException>(() => store.Save(valid), "partial library blocks normal writes");
        var original = File.ReadAllText(partialFile);
        store.Recover(valid);
        Check(!store.IsReadOnly && store.Load(true).Count == 1 && Directory.GetFiles(Output, "partial.json.recovery-*.json").Any(p => File.ReadAllText(p) == original), "explicit recovery preserves exact original");
        game.Title = "updated";
        store.Save([game]);
        Check(new JsonGameStore(partialFile + ".bak").Load(true)[0].Title != "updated", "save keeps previous valid snapshot");

        var lockedPath = Path.Combine(Output, "locked.json");
        File.WriteAllText(lockedPath, JsonSerializer.Serialize(new[] { game }));
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedStore = new JsonGameStore(lockedPath);
            Check(lockedStore.Load().Count == 0 && lockedStore.IsReadOnly, "locked library pauses writes");
            Reject<InvalidDataException>(() => lockedStore.Save([]), "locked library cannot become empty");
        }

        var concurrentPath = Path.Combine(Output, "concurrent.json");
        var first = Vm(concurrentPath);
        var second = Vm(concurrentPath);
        first.AddGameFromPath(exe);
        Check(second.AddGameFromPath(FakeExe(Folder("second"))) == GameAddOutcome.Failed &&
            new JsonGameStore(concurrentPath).Load(true).Single().ExecutablePath == exe, "stale writer cannot overwrite new library data");

        var dir = Folder("standalone");
        var library = Path.Combine(Output, "identity.json");
        var identity = Vm(library);
        var a = FakeExe(dir, "a.exe"); var b = FakeExe(dir, "b.exe");
        Check(identity.AddGameFromPath(a) == GameAddOutcome.Added && identity.Games.Count == 1 && identity.GamesView.Cast<Game>().Count() == 1, "one import creates one data item and one view item");
        Check(identity.AddGameFromPath(a.ToUpperInvariant()) == GameAddOutcome.Duplicate && identity.AddGameFromPath(b) == GameAddOutcome.Added, "exact path deduplicates; separate same-directory games remain");
        var bgi = Folder("bgi-identity");
        File.WriteAllText(Path.Combine(bgi, "BGI.gdb"), "fixture");
        var bgi1 = FakeExe(bgi, "BGI.exe"); var bgi2 = FakeExe(bgi, "BGI_CHS.exe");
        Check(identity.AddGameFromPath(bgi1) == GameAddOutcome.Added && identity.AddGameFromPath(bgi2) == GameAddOutcome.Duplicate, "known BGI alternate entry shares identity");
        var beforeBatch = File.ReadAllText(library);
        var batch = identity.AddGamesAsync([FakeExe(dir, "c.exe"), FakeExe(dir, "d.exe"), a], CancellationToken.None);
        Await(batch);
        Check(batch.Result == new GameImportResult(2, 1, 0) && File.ReadAllText(library + ".bak") == beforeBatch &&
            identity.Games.Count == identity.GamesView.Cast<Game>().Count(), "batch commits once, counts duplicates, keeps view consistent");

        var beforeFailure = File.ReadAllText(library);
        var blocked = library + ".tmp";
        Directory.CreateDirectory(blocked);
        Check(!identity.RemoveGame(identity.Games[0]) && File.ReadAllText(library) == beforeFailure && File.Exists(a), "failed remove preserves library and game files");
        identity.SelectedGame = identity.Games[0];
        identity.ToggleFavoriteCommand.Execute(null);
        Check(identity.HasUnsavedLibraryChanges, "failed edit remains pending");
        var closeChoice = MessageBoxResult.Cancel;
        var window = new MainWindow(identity, () => closeChoice) { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show(); window.Close();
        Check(!closed, "cancel closing retains pending edit");
        closeChoice = MessageBoxResult.Yes; window.Close();
        Check(!closed, "failed retry prevents close");
        Directory.Delete(blocked); // Test-created empty obstruction only.
        window.Close();
        Check(closed && !identity.HasUnsavedLibraryChanges && new JsonGameStore(library).Load(true)[0].IsFavorite, "successful retry saves before close");

        var name = @"Local\YumeShelf-Test-" + Guid.NewGuid();
        using (var guard = new SingleInstanceGuard(name))
            Check(guard.IsPrimary && InstanceChild(name) == 77, "second process is rejected by single-instance mutex");
        Check(InstanceChild(name) == 0, "instance mutex is released on exit");
    }

    private static int InstanceChild(string name)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--instance-check"); start.ArgumentList.Add(name);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(5000)) throw new TimeoutException("Instance child");
        return child.ExitCode;
    }

    private static void ScanChecks()
    {
        var root = Folder("scan");
        var renpy = Path.Combine(root, "ExampleRenpy");
        Directory.CreateDirectory(Path.Combine(renpy, "renpy"));
        Directory.CreateDirectory(Path.Combine(renpy, "game"));
        var re = FakeExe(renpy, "Novel.exe");
        File.WriteAllText(Path.Combine(renpy, "game", "script.rpy"), "label start:\n return");
        var generic = Path.Combine(root, "GenericViewer"); Directory.CreateDirectory(generic);
        FakeExe(generic, "Viewer.exe"); File.WriteAllText(Path.Combine(generic, "UnityPlayer.dll"), "");
        var deep = root;
        for (var i = 0; i < 6; i++) { deep = Path.Combine(deep, "level"); Directory.CreateDirectory(deep); }
        var bg = FakeExe(deep); File.WriteAllText(Path.Combine(deep, "BGI.gdb"), "");
        for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(deep, $"other-{i}.txt"), "");
        var result = Await(new GameScanService().ScanAsync(root, new HashSet<string>()));
        Check(result.Count == 2 && result.Any(x => x.ExecutablePath == re && x.Engine == "Ren'Py") &&
            result.Any(x => x.ExecutablePath == bg) && !result.IsIncomplete, "Ren'Py and deep BGI found; generic Unity excluded; >80 files safe");
        var vm = Vm(Path.Combine(Output, "renpy.json")); vm.AddGameFromPath(re);
        Check(vm.Games[0].Engine == "Ren'Py", "manual import shares engine detector");
        var binary = Path.Combine(root, "廃村少女 番外"); Directory.CreateDirectory(binary);
        foreach (var file in new[] { "bg.bin", "script.bin", "voc.bin", "episode.bin", "episode_chs.bin" }) File.WriteAllText(Path.Combine(binary, file), "");
        var originalEntry = FakeExe(binary, "episode.exe"); var translatedEntry = FakeExe(binary, "episode_chs.exe");
        FakeExe(binary, "configure.exe");
        var binaryScan = Await(new GameScanService().ScanAsync(binary, new HashSet<string>()));
        Check(binaryScan.Count == 1 && binaryScan[0].ExecutablePath == translatedEntry && binaryScan[0].Engine == GameScanService.DetectEngine(binary), "script/background/voice binary structure works without kana or saves and prefers translation");
        Check(GameIdentity.AreSame(originalEntry, translatedEntry), "confirmed translated entry shares identity with original");
        var notices = 0; result[0].PropertyChanged += (_, e) => { if (e.PropertyName == "IsSelected") notices++; };
        result[0].IsSelected = false;
        Check(notices == 1, "candidate selection notifies the UI");
        for (var i = 6; i < GameScanService.MaxDepth + 2; i++) { deep = Path.Combine(deep, "deeper"); Directory.CreateDirectory(deep); }
        var incomplete = Await(new GameScanService().ScanAsync(root, new HashSet<string>()));
        Check(incomplete.IsIncomplete && incomplete.Summary.Contains("深度上限"), "depth cutoff explicitly reports incomplete scan");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => Await(new GameScanService().ScanAsync(root, new HashSet<string>(), cancelled.Token)), "scan cancellation propagates");
    }

    private static void UiChecks()
    {
        var dir = Folder("ui-game"); var exe = FakeExe(dir);
        var invalid = Path.Combine(dir, "cover.png"); File.WriteAllText(invalid, "not an image");
        Check(OfflineGameMetadataService.Read(exe).CoverPath != invalid, "invalid offline cover is excluded");
        var converter = new CoverImageConverter();
        Check(converter.Convert(invalid, typeof(ImageSource), null!, CultureInfo.InvariantCulture) is BitmapImage,
            "invalid/missing cover uses packaged default");
        var image = GameImageLoader.Load(GameImageLoader.DefaultCover, 160);
        Check(image.PixelWidth <= 160 && image.IsFrozen, "cover decoding uses frozen thumbnail");
        var tooLarge = Path.Combine(dir, "huge.png");
        using (var f = File.Create(tooLarge)) f.SetLength(33 * 1024 * 1024);
        Reject<InvalidDataException>(() => GameImageLoader.Load(tooLarge), "oversized cover rejected before decoding");

        var game = GameAt(exe); game.TotalPlaySeconds = 42; game.IsFavorite = true;
        var id = game.Id;
        var editor = new GameEditorWindow(game);
        var moved = FakeExe(Folder("moved-game"), "new.exe");
        Check(editor.SelectExecutable(moved) && game.ExecutablePath == exe, "relink preview does not mutate before save");
        Check(editor.TryApply() && game.Id == id && game.TotalPlaySeconds == 42 && game.IsFavorite &&
            game.ExecutablePath == moved && game.RootPath == Path.GetDirectoryName(moved) && game.WorkingDirectory == game.RootPath, "relink preserves identity/statistics and updates all paths");
        ((TextBox)editor.FindName("CoverBox")).Text = invalid;
        Check(!editor.TryApply(), "editor refuses unreadable cover");
        ((TextBox)editor.FindName("CoverBox")).Text = "";
        ((TextBox)editor.FindName("DescriptionBox")).Text = string.Concat(Enumerable.Repeat("很长的游戏简介。\n", 100));
        var editorRoot = (FrameworkElement)editor.Content;
        Render(editorRoot, "editor-small.png", 460, 500);
        var save = Descendants(editorRoot).OfType<Button>().Single(b => b.Content as string == "保存");
        Check(Bounds(save, editorRoot).Bottom <= editorRoot.ActualHeight &&
            Descendants(editorRoot).OfType<ScrollViewer>().Any(s => s.ExtentHeight > s.ViewportHeight), "small editor keeps save visible and long form scrollable");
        editor.Close();
        var duplicateEditor = new GameEditorWindow(game, _ => true);
        Check(!duplicateEditor.SelectExecutable(exe), "editor refuses existing library executable");
        duplicateEditor.Close();

        var library = Path.Combine(Output, "ui-library.json");
        var vm = Vm(library); vm.AddGameFromPath(exe); vm.SelectedGame!.Description = string.Concat(Enumerable.Repeat("长篇简介与布局验证。\n", 100));
        var window = new MainWindow(vm);
        var root = (FrameworkElement)window.Content;
        Render(root, "library-small.png", 900, 580);
        Check(root.ActualWidth == 900 && root.ActualHeight == 580 && Descendants(root).OfType<TextBlock>().Any(t => t.Text == vm.SelectedGame.Description), "small render uses exact viewport and displays the selected description");
        Check(Descendants(root).OfType<ScrollViewer>().Any(s => s.ExtentHeight > s.ViewportHeight && s.ActualHeight < 580), "small detail panel scrolls long description");
        vm.AddGameFromPath(moved); vm.Games[0].Title = "Z"; vm.Games[1].Title = "A";
        vm.SelectedSort = "游戏名称";
        Check(vm.GamesView.Cast<Game>().First().Title == "A", "sort changes actual displayed order");
        var missing = GameAt(Path.Combine(Output, "missing.exe"));
        Check(missing.Availability == "启动文件缺失" && game.LaunchStatusText == "未游玩", "availability and launch status use truthful Chinese labels");
        vm.SettingsEditor.NightMode = true; vm.SettingsEditor.ConfirmCommand.Execute(null);
        Render(root, "library-night.png", 1200, 720);
        var refresh = Descendants(root).OfType<Button>().Single(b => b.ToolTip as string == "刷新游戏库");
        var resources = System.Windows.Application.Current.Resources;
        Check(refresh.Background is SolidColorBrush brush && brush.Color != Colors.White &&
            Contrast((SolidColorBrush)resources["OnAccentBrush"], (SolidColorBrush)resources["AccentBrush"]) >= 4.5 &&
            Contrast((SolidColorBrush)resources["AccentTextBrush"], (SolidColorBrush)resources["AccentSoftBrush"]) >= 4.5 &&
            Contrast((SolidColorBrush)resources["PrimaryTextBrush"], (SolidColorBrush)resources["CardBackground"]) >= 4.5, "night controls follow theme with readable text contrast");
        vm.SettingsEditor.NightMode = false;
        vm.SettingsEditor.ClearBackgroundCommand.Execute(null);
        Check(vm.SettingsEditor.PreviewImage is not null, "reset previews the actual default background");
        vm.SettingsEditor.NightMode = false; vm.SettingsEditor.ConfirmCommand.Execute(null);

        // Representative 500-entry library; keep results measurable, not a universal stress guarantee.
        var many = Enumerable.Range(0, 500).Select(i => new Game { Title = $"Game {i:D4}", ExecutablePath = Path.Combine(dir, $"entry{i}.exe"), RootPath = dir, WorkingDirectory = dir }).ToArray();
        var largePath = Path.Combine(Output, "large-library.json"); new JsonGameStore(largePath).Save(many);
        var watch = Stopwatch.StartNew(); var largeVm = Vm(largePath);
        var largeWindow = new MainWindow(largeVm);
        Render((FrameworkElement)largeWindow.Content, "library-500.png", 1200, 720);
        Check(Descendants((FrameworkElement)largeWindow.Content).OfType<TextBlock>().Any(t => t.Text == "未选择游戏") && ((SolidColorBrush)resources["CoverPlaceholderBrush"]).Color.R == ((SolidColorBrush)resources["CoverPlaceholderBrush"]).Color.G, "empty detail panel explains selection and uses neutral theme");
        Check(largeVm.VisibleGames.Count == MainWindowViewModel.PageSize && largeVm.PageCount == 9, "large libraries create only one page of visuals");
        largeVm.SelectedSort = "游戏名称";
        largeVm.NextPageCommand.Execute(null);
        Check(largeVm.VisibleGames[0].Title == "Game 0060" && largeVm.SelectedGame == largeVm.VisibleGames[0], "paging changes results and selection together");
        largeVm.SearchText = "Game 0499";
        Check(largeVm.GamesView.Cast<Game>().Count() == 1 && largeVm.VisibleGames.Single().Title == "Game 0499" && !largeVm.HasMultiplePages, "500-entry library filters all pages accurately");
        Console.WriteLine($"MEASURE: load/render/filter 500 entries = {watch.ElapsedMilliseconds} ms");
        largeWindow.Close(); window.Close();
        Check(AiAssistantPage.ShouldSend(Key.Enter, ModifierKeys.None, false) &&
            !AiAssistantPage.ShouldSend(Key.Enter, ModifierKeys.Shift, false) &&
            !AiAssistantPage.ShouldSend(Key.Enter, ModifierKeys.None, true), "Enter sends; Shift+Enter and IME composition do not");
    }

    private static double Contrast(SolidColorBrush a, SolidColorBrush b)
    {
        static double L(Color c)
        {
            static double C(byte x) { var v = x / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(c.R) + 0.7152 * C(c.G) + 0.0722 * C(c.B);
        }
        return (Math.Max(L(a.Color), L(b.Color)) + 0.05) / (Math.Min(L(a.Color), L(b.Color)) + 0.05);
    }
    private static void LaunchChecks(string exe)
    {
        Check(File.Exists(exe), "benign test executable exists");
        var path = Path.Combine(Output, "launch.json");
        var game = GameAt(exe); game.WorkingDirectory = Folder("test-game-working"); game.LaunchArguments = "--duration 2";
        new JsonGameStore(path).Save([game]);
        var vm = Vm(path); vm.SelectedGame = vm.Games[0];
        vm.LaunchGameCommand.Execute(null); vm.RefreshLibraryCommand.Execute(null);
        var current = vm.Games[0];
        var aiDraft = new AiMetadataDraft(AiGameSummary.From(current), [new AiFieldSuggestion("Title", current.Title, "AI 审核后的测试游戏") { Accepted = true }], "隔离回归资料") { Covers = [CoverFixture] };
        Check(vm.ApplyAiUpdate(aiDraft, CoverFixture, AiImageAttachment.FromFile(VisionFixture())) is null && ReferenceEquals(current, vm.Games[0]),
            "combined card update preserves the running game instance and launch watcher");
        var runningCover = current.CoverPath;
        PumpUntil(() => vm.Games[0].LastPlayedAt is not null, 12000);
        var saved = new JsonGameStore(path).Load(true)[0];
        Check(saved.LastLaunchStatus == "Completed" && saved.TotalPlaySeconds >= 1 && saved.LastPlayedAt is not null, "real process exit after refresh persists statistics on current game");
        Check(saved.CoverPath == runningCover && File.Exists(runningCover) && vm.UndoAiDraft() is null &&
            new JsonGameStore(path).Load(true)[0].TotalPlaySeconds == saved.TotalPlaySeconds,
            "game exit preserves the downloaded cover and cover undo preserves final play statistics");
        Check(saved.Title == "AI 审核后的测试游戏" && current.Title == game.Title && new JsonGameStore(path).Load(true)[0].TotalPlaySeconds == saved.TotalPlaySeconds,
            "process exit preserves AI metadata and undo preserves newly recorded play time");
        vm.LaunchGameCommand.Execute(null);
        Check(vm.RemoveGame(vm.Games[0]), "running entry can be removed without deleting executable");
        var timer = Stopwatch.StartNew(); PumpUntil(() => timer.ElapsedMilliseconds > 3000);
        Check(vm.Games.Count == 0 && new JsonGameStore(path).Load(true).Count == 0 && File.Exists(exe), "removed running entry is never recreated on process exit");
        game.LastLaunchStatus = "Running"; new JsonGameStore(path).Save([game]);
        Check(Vm(path).Games[0].LastLaunchStatus == "Interrupted", "stale running status is labeled interrupted after restart");
    }
    private static void RealScan(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var result = Await(new GameScanService().ScanAsync(root, new HashSet<string>()), 70000);
            File.WriteAllText(Path.Combine(Output, "real-scan-" + Guid.NewGuid() + ".json"), JsonSerializer.Serialize(new { root, result.Summary, result.Candidates }, JsonOptions));
            Check(result.Count > 0, "read-only real-game scan found candidates: " + root);
            foreach (var item in result) Console.WriteLine($"CANDIDATE: {item.Engine}: {item.ExecutablePath}");
        }
    }
    private static T Await<T>(Task<T> task, int timeout = 15000) { PumpUntil(() => task.IsCompleted, timeout); return task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done, int timeout = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Check deadline exceeded");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static Rect Bounds(FrameworkElement item, FrameworkElement root) => item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
    private static void Render(FrameworkElement root, string name, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(root);
        var composite = new DrawingVisual();
        using (var drawing = composite.RenderOpen())
        {
            drawing.DrawRectangle(Window.GetWindow(root)?.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawImage(rendered, new Rect(0, 0, width, height));
        }
        rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(composite);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
        using var file = File.Create(Path.Combine(Output, name)); png.Save(file);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
