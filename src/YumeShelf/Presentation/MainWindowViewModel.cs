using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Text.Json;
using Microsoft.Win32;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Domain;
using YumeShelf.Infrastructure;

namespace YumeShelf.Presentation;

using MediaBrush = System.Windows.Media.Brush;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly GameLibraryService _libraryService;
    private readonly GameLaunchService _launchService;
    private readonly Infrastructure.AppSettingsStore _settingsStore;
    private Infrastructure.AppSettings _settings = new();
    private readonly HashSet<Guid> _runningGames = [];
    private Game? _selectedGame;
    private string _searchText = string.Empty;
    private string _selectedFilter = "全部类型";
    private string _currentSection = "All";
    private string _statusMessage = "准备就绪";
    private bool _isListView;
    private int _filteredCount;
    private string _activeNavigation = "All";
    private SettingsViewModel? _settingsEditor;
    private AiAssistantViewModel? _aiAssistant;
    private bool _hasUnsavedLibraryChanges;
    private string _selectedSort = "添加时间";
    private bool _isImporting;
    public const int PageSize = 60;
    private int _pageIndex;
    private bool _changingPage;

    public MainWindowViewModel()
        : this(new GameLibraryService(new Infrastructure.JsonGameStore()), new GameLaunchService(), new Infrastructure.AppSettingsStore())
    {
    }

    public MainWindowViewModel(GameLibraryService libraryService, GameLaunchService launchService, Infrastructure.AppSettingsStore? settingsStore = null)
    {
        _libraryService = libraryService;
        _launchService = launchService;
        _settingsStore = settingsStore ?? new Infrastructure.AppSettingsStore();
        ApplyAppearance(_settingsStore.Load());

        var loadedGames = _libraryService.Load();
        Games = new ObservableCollection<Game>(loadedGames);
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        GamesView.SortDescriptions.Add(new SortDescription(nameof(Game.AddedAt), ListSortDirection.Descending));
        // The view processes the collection notification first; never Refresh during that event.
        GamesView.CollectionChanged += (_, _) => UpdateCounts();

        FilterOptions = ["全部类型", "已收藏", "最近游玩"];
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string));
        AddGameCommand = new RelayCommand(_ => AddGame());
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());
        RetrySaveCommand = new RelayCommand(_ => { if (SaveGames()) Notify("游戏库已重新保存。所有待保存修改已写入本机。"); }, _ => HasUnsavedLibraryChanges);
        RecoverLibraryCommand = new RelayCommand(_ => RecoverLibrary());
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        LaunchGameCommand = new RelayCommand(_ => LaunchSelectedGame(), _ => HasSelectedGame);
        EditGameCommand = new RelayCommand(EditSelectedGame, parameter => parameter is Game || HasSelectedGame);
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(), _ => HasSelectedGame);
        RemoveGameCommand = new RelayCommand(RemoveSelectedGame, parameter => parameter is Game || HasSelectedGame);
        OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => HasSelectedGame);
        OpenSettingsCommand = new RelayCommand(_ => Navigate("Settings"));
        OpenAiAssistantCommand = new RelayCommand(_ => Navigate("AI"));
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => HasSearchText);
        PreviousPageCommand = new RelayCommand(_ => ChangePage(-1), _ => _pageIndex > 0);
        NextPageCommand = new RelayCommand(_ => ChangePage(1), _ => _pageIndex + 1 < PageCount);

        foreach (var game in Games.Where(g => g.LastLaunchStatus == "Running")) game.LastLaunchStatus = "Interrupted";
        if (_libraryService.ReadError is not null) ShowLibraryReadError();
        RefreshView();
    }

    public ObservableCollection<Game> Games { get; }
    public RelayCommand OpenAiAssistantCommand { get; }
    public ICollectionView GamesView { get; }
    public IReadOnlyList<Game> VisibleGames { get; private set; } = [];
    public int PageCount => Math.Max(1, (_filteredCount + PageSize - 1) / PageSize);
    public bool HasMultiplePages => PageCount > 1;
    public string PageSummary => $"第 {_pageIndex + 1} / {PageCount} 页";
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public IReadOnlyList<string> FilterOptions { get; }
    public string ActiveNavigation { get => _activeNavigation; private set => SetProperty(ref _activeNavigation, value); }
    public SettingsViewModel SettingsEditor => _settingsEditor ??= CreateSettingsEditor();
    public AiAssistantViewModel AiAssistant => _aiAssistant ??= new AiAssistantViewModel(_settings, Feedback,
        () => Games.Select(AiGameSummary.From).ToArray(), () => SelectedGame is null ? null : AiGameSummary.From(SelectedGame), ApplyAiDraft, UndoAiDraft, ApplyAiCover, UndoAiCover,
        applyUpdate: ApplyAiUpdate, scan: ScanAiGamesAsync, import: ImportAiGamesAsync,
        libraryAvailable: () => _libraryService.ReadError is null && !HasUnsavedLibraryChanges,
        sessionStore: new AiSessionStore(_settingsStore.SessionPath));
    private (AiGameSummary After, Dictionary<string, string> Values, string? CoverBefore, bool CoverChanged)? _aiUndo;

    public string? ApplyAiCover(AiCoverDraft draft, AiCoverCandidate candidate, AiImageAttachment image)
        => ApplyAiUpdate(new(draft.Original, [], "封面修改") { Covers = draft.Candidates }, candidate, image);

    public string? ApplyAiUpdate(AiMetadataDraft draft, AiCoverCandidate? candidate, AiImageAttachment? image)
    {
        var chosen = draft.Fields.Where(f => f.Accepted).ToArray();
        if (chosen.Length == 0 && image is null) return "请勾选要保存的字段或选取封面。";
        if (image is not null && (candidate is null || !draft.Covers.Contains(candidate))) return "请从本轮封面候选中选择图片。";
        var error = CheckAiCoverTarget(draft.Original, out var current);
        if (error is not null) return error;
        var copy = JsonSerializer.Deserialize<Game>(JsonSerializer.Serialize(current))!;
        var oldValues = new Dictionary<string, string>();
        foreach (var field in chosen)
        {
            if (!oldValues.TryAdd(field.Field, OriginalValue(current!, field.Field))) return "资料建议包含重复字段。";
            AiMetadataDraft.SetField(copy, field.Field, field.Proposed);
        }
        if (chosen.Any(f => f.Field == "ReleaseDate") && copy.ReleaseDate.Length >= 4)
        {
            var year = int.Parse(copy.ReleaseDate[..4], System.Globalization.CultureInfo.InvariantCulture);
            if (chosen.Any(f => f.Field == "ReleaseYear") && copy.ReleaseYear != year) return "发行日期与年份冲突，请核对后保存。";
            oldValues.TryAdd("ReleaseYear", OriginalValue(current!, "ReleaseYear")); copy.ReleaseYear = year;
        }
        else if (chosen.Any(f => f.Field == "ReleaseYear") && copy.ReleaseDate.Length >= 4 && copy.ReleaseDate[..4] != copy.ReleaseYear?.ToString())
        { oldValues.TryAdd("ReleaseDate", current!.ReleaseDate); copy.ReleaseDate = ""; }
        string? file = null;
        var committed = false;
        var created = false;
        try
        {
            if (image is not null)
            {
                var directory = Path.Combine(_libraryService.DataDirectory, "Covers");
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return "封面目录不能是链接目录，请检查应用数据位置。";
                file = Path.Combine(directory, $"{current!.Id:N}-{Guid.NewGuid():N}.png");
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; output.Write(image.PngBytes); }
                copy.CoverPath = file;
            }
            copy.UpdatedAt = DateTimeOffset.Now;
            if (!SaveGames(Games.Select(g => g.Id == copy.Id ? copy : g))) return "游戏库保存失败，原资料和封面未改变。请修复后重试。";
            committed = true;
            var previous = current!.CoverPath;
            foreach (var field in oldValues.Keys) AiMetadataDraft.SetField(current, field, OriginalValue(copy, field), true);
            current.CoverPath = copy.CoverPath; current.UpdatedAt = copy.UpdatedAt;
            _aiUndo = (AiGameSummary.From(current), oldValues, previous, image is not null);
            RefreshView();
            return null;
        }
        finally
        {
            if (!committed && created)
            {
                try { if (file is not null && File.Exists(file)) File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("ai.cover-cleanup-failed", ex); }
            }
        }
    }

    public string? UndoAiCover()
        => UndoAiDraft();

    private static string OriginalValue(Game game, string field) => field switch
    {
        "Title" => game.Title, "Engine" => game.Engine, "Description" => game.Description,
        "ReleaseYear" => game.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        "ReleaseDate" => game.ReleaseDate, "GameType" => game.GameType, "Tags" => JsonSerializer.Serialize(game.Tags),
        _ => throw new InvalidOperationException("不允许修改该字段。")
    };

    public string? UndoAiDraft()
    {
        if (_aiUndo is not { } undo) return "没有可撤销的游戏卡片修改。";
        var error = CheckAiCoverTarget(undo.After, out var current);
        if (error is not null) return error;
        var copy = JsonSerializer.Deserialize<Game>(JsonSerializer.Serialize(current))!;
        foreach (var pair in undo.Values) AiMetadataDraft.SetField(copy, pair.Key, pair.Value, true);
        if (undo.CoverChanged) copy.CoverPath = undo.CoverBefore;
        copy.UpdatedAt = DateTimeOffset.Now;
        if (!SaveGames(Games.Select(g => g.Id == copy.Id ? copy : g))) return "撤销未保存，当前资料和封面保持不变。";
        foreach (var pair in undo.Values) AiMetadataDraft.SetField(current!, pair.Key, pair.Value, true);
        current!.CoverPath = copy.CoverPath; current.UpdatedAt = copy.UpdatedAt;
        _aiUndo = null;
        // Retain both images: the library backup or an earlier manual selection can still reference them.
        RefreshView();
        return null;
    }

    private string? CheckAiCoverTarget(AiGameSummary original, out Game? current)
    {
        current = Games.FirstOrDefault(g => g.Id == original.Id);
        if (IsLibraryReadOnly || HasUnsavedLibraryChanges) return "游戏库只读或存在未保存修改，请先处理游戏库状态。";
        if (current is null) return "目标游戏已从库中移除，封面未修改。";
        return AiGameSummary.From(current).Revision != original.Revision ? "游戏资料或封面已发生变化，请重新查找并确认，避免覆盖新修改。" : null;
    }

    public string? ApplyAiDraft(AiMetadataDraft draft)
        => ApplyAiUpdate(draft, null, null);

    public async Task<GameScanResult> ScanAiGamesAsync(string path, GameScanMode mode, CancellationToken token)
    {
        if (File.Exists(path))
        {
            var game = await Task.Run(() => PrepareGame(path), token);
            token.ThrowIfCancellationRequested();
            var matches = game is null || Games.Any(g => GameIdentity.AreSame(g.ExecutablePath, path)) ? Array.Empty<GameScanCandidate>() :
                [new GameScanCandidate(path, game.Title, game.Engine, "用户提供的启动文件，请确认是游戏入口", 100)];
            return new(matches, 1, 0, []);
        }
        var existing = Games.Select(g => g.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await new GameScanService().ScanAsync(path, existing, token, mode);
    }
    public Task<GameImportResult> ImportAiGamesAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (HasUnsavedLibraryChanges || IsLibraryReadOnly || IsImporting) throw new InvalidOperationException("游戏库只读、正在导入或存在未保存修改，请处理后重试。");
        return AddGamesAsync(paths, token);
    }
    public OperationFeedback Feedback { get; } = new();
    public bool HasUnsavedLibraryChanges { get => _hasUnsavedLibraryChanges; private set { SetProperty(ref _hasUnsavedLibraryChanges, value); RetrySaveCommand?.RaiseCanExecuteChanged(); } }
    public RelayCommand RetrySaveCommand { get; }
    public RelayCommand RecoverLibraryCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public bool IsLibraryReadOnly => _libraryService.IsReadOnly;
    public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }
    public IReadOnlyList<string> SortOptions { get; } = ["添加时间", "游戏名称", "最近游玩"];
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value)) return;
            _pageIndex = 0;
            using (GamesView.DeferRefresh())
            {
                GamesView.SortDescriptions.Clear();
                GamesView.SortDescriptions.Add(value switch
                {
                    "游戏名称" => new SortDescription(nameof(Game.Title), ListSortDirection.Ascending),
                    "最近游玩" => new SortDescription(nameof(Game.LastPlayedAt), ListSortDirection.Descending),
                    _ => new SortDescription(nameof(Game.AddedAt), ListSortDirection.Descending)
                });
            }
        }
    }

    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_changingPage) return;
            if (!SetProperty(ref _selectedGame, value)) return;
            if (value is not null && !VisibleGames.Contains(value))
            {
                var index = GamesView.Cast<Game>().ToList().IndexOf(value);
                if (index >= 0) { _pageIndex = index / PageSize; RefreshPage(); }
            }
            OnPropertyChanged(nameof(HasSelectedGame));
            OnPropertyChanged(nameof(FavoriteGlyph));
            RaiseCommandStates();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            _pageIndex = 0;
            OnPropertyChanged(nameof(HasSearchText));
            RefreshView();
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value)) { _pageIndex = 0; RefreshView(); }
        }
    }

    public string CurrentSection
    {
        get => _currentSection;
        private set => SetProperty(ref _currentSection, value);
    }

    public string CurrentSectionTitle => CurrentSection switch
    {
        "Recent" => "最近游玩",
        "Favorites" => "收藏",
        _ => "全部游戏"
    };

    public string LibrarySummary => $"{Games.Count} 个已添加项目";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsListView { get => _isListView; set => SetProperty(ref _isListView, value); }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool HasFilteredGames => _filteredCount > 0;
    public string FavoriteGlyph => SelectedGame?.IsFavorite == true ? "♥" : "♡";
    public ImageSource? BackgroundImage { get; private set; }
    public double BackgroundOpacity => _settings.BackgroundOpacity;
    public double BackgroundBlur => _settings.BackgroundBlur;
    public double PageTransparency => _settings.PageTransparency;
    public bool SimpleLayout => _settings.SimpleLayout;
    public bool FullLayout => !SimpleLayout;
    public GridLength DetailColumnWidth => new(SimpleLayout ? 0 : 316);
    public MediaBrush ShellPanelBrush => PanelBrush("ShellBackground");
    public MediaBrush SidebarPanelBrush => PanelBrush("SidebarBackground");
    public MediaBrush DetailPanelBrush => PanelBrush("DetailBackground");

    private MediaBrush PanelBrush(string key)
    {
        var brush = System.Windows.Application.Current.Resources[key] as SolidColorBrush;
        if (brush is null) return new SolidColorBrush(Colors.Transparent);
        var alpha = BackgroundImage is null ? (byte)255 : (byte)Math.Clamp(255 - (int)(153 * PageTransparency), 102, 255);
        return new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B));
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand AddGameCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }
    public RelayCommand LaunchGameCommand { get; }
    public RelayCommand EditGameCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand RemoveGameCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    private void ApplyAppearance(Infrastructure.AppSettings settings)
    {
        _settings = settings;
        ThemePalette.Apply(System.Windows.Application.Current.Resources, settings);
        try { BackgroundImage = BackgroundImageLoader.Load(settings.BackgroundImagePath ?? BuiltInBackgrounds.Resolve(settings.ColorPalette, settings.NightMode)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            BackgroundImage = null;
            StatusMessage = "背景图片无法读取，已使用默认背景。可在设置中重新选择。";
        }
        OnPropertyChanged(nameof(BackgroundImage));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BackgroundBlur));
        OnPropertyChanged(nameof(PageTransparency));
        OnPropertyChanged(nameof(SimpleLayout));
        OnPropertyChanged(nameof(FullLayout));
        OnPropertyChanged(nameof(DetailColumnWidth));
        OnPropertyChanged(nameof(ShellPanelBrush));
        OnPropertyChanged(nameof(SidebarPanelBrush));
        OnPropertyChanged(nameof(DetailPanelBrush));
    }

    private SettingsViewModel CreateSettingsEditor(string notice = "切换页面保留草稿，确认后生效")
    {
        var editor = new SettingsViewModel(_settings, settings =>
        {
            settings = settings.Normalize();
            _settingsStore.Save(settings);
            ApplyAppearance(settings);
            _aiAssistant?.UpdateSettings(settings);
            StatusMessage = "设置已保存";
        }, Feedback)
        { SaveNotice = notice };
        editor.CloseRequested += accepted =>
        {
            editor.CancelPendingTest();
            _settingsEditor = CreateSettingsEditor(accepted ? "设置已保存" : "已恢复保存的设置");
            _settingsEditor.SelectedSection = editor.SelectedSection;
            OnPropertyChanged(nameof(SettingsEditor));
        };
        return editor;
    }

    public void CancelPendingRequests()
    {
        _aiAssistant?.SaveSession();
        _aiAssistant?.Cancel();
        _settingsEditor?.CancelPendingTest();
    }

    private void Navigate(string? section)
    {
        if (section is "AI" or "Settings")
        {
            ActiveNavigation = section;
            return;
        }
        var returningToLibrary = ActiveNavigation is "AI" or "Settings" && section == CurrentSection;
        CurrentSection = section switch
        {
            "Recent" => "Recent",
            "Favorites" => "Favorites",
            _ => "All"
        };
        if (!returningToLibrary)
            SelectedFilter = CurrentSection switch
            {
                "Recent" => "最近游玩",
                "Favorites" => "已收藏",
                _ => "全部类型"
            };
        OnPropertyChanged(nameof(CurrentSectionTitle));
        ActiveNavigation = CurrentSection;
    }

    private bool FilterGame(object item)
    {
        if (item is not Game game) return false;
        if (SelectedFilter == "已收藏" && !game.IsFavorite) return false;
        if (SelectedFilter == "最近游玩" && game.LastPlayedAt is null) return false;

        var query = SearchText.Trim();
        if (query.Length == 0) return true;
        var searchable = string.Join(' ', game.Title, game.Engine, game.GameType, game.Description, string.Join(' ', game.Tags));
        return searchable.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshView()
    {
        GamesView.Refresh();
        foreach (var game in Games) game.RefreshAvailability();
        UpdateCounts();
        ClearSearchCommand.RaiseCanExecuteChanged();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(LibrarySummary));
        _filteredCount = GamesView.Cast<Game>().Count();
        OnPropertyChanged(nameof(HasFilteredGames));
        RefreshPage();
    }

    private void RefreshPage()
    {
        _pageIndex = Math.Clamp(_pageIndex, 0, PageCount - 1);
        // Bound WPF visual creation without a custom virtualizing panel or new dependency.
        _changingPage = true;
        try
        {
            VisibleGames = GamesView.Cast<Game>().Skip(_pageIndex * PageSize).Take(PageSize).ToArray();
            OnPropertyChanged(nameof(VisibleGames));
        }
        finally { _changingPage = false; }
        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(PageSummary));
        PreviousPageCommand?.RaiseCanExecuteChanged(); NextPageCommand?.RaiseCanExecuteChanged();
    }

    private void ChangePage(int offset)
    {
        _pageIndex = Math.Clamp(_pageIndex + offset, 0, PageCount - 1);
        RefreshPage();
        SelectedGame = VisibleGames.FirstOrDefault();
    }

    private void AddGame()
    {
        if (IsLibraryReadOnly) { ShowLibraryReadError(); return; }
        var dialog = new AddGameWindow(AddGameFromPath, AddGamesAsync) { Owner = System.Windows.Application.Current.MainWindow };
        dialog.ShowDialog();
        if (dialog.ResultMessage is not null) Notify(dialog.ResultMessage, dialog.ResultKind);
    }

    private void RefreshLibrary()
    {
        if (HasUnsavedLibraryChanges)
        {
            Feedback.Show("游戏库还有未保存的修改，请先重新保存，以免刷新丢失修改。", FeedbackKind.Warning, RetrySaveCommand, "重新保存");
            return;
        }
        try
        {
            var selectedId = SelectedGame?.Id;
            var loaded = _libraryService.Load(throwOnError: true);
            Games.Clear();
            foreach (var game in loaded)
            {
                if (game.LastLaunchStatus == "Running" && !_runningGames.Contains(game.Id)) game.LastLaunchStatus = "Interrupted";
                Games.Add(game);
            }
            OnPropertyChanged(nameof(IsLibraryReadOnly));
            SelectedGame = Games.FirstOrDefault(game => game.Id == selectedId)
                ?? Games.FirstOrDefault();
            RefreshView();
            Notify($"游戏库已刷新，共 {Games.Count} 个项目。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            AppLog.Write("library.refresh-failed", ex);
            ShowLibraryReadError();
        }
    }

    public GameAddOutcome AddGameFromPath(string path)
    {
        var result = CommitImport([PrepareGame(path)]);
        return result.Added > 0 ? GameAddOutcome.Added : result.Duplicates > 0 ? GameAddOutcome.Duplicate : GameAddOutcome.Failed;
    }

    public async Task<GameImportResult> AddGamesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (IsImporting || IsLibraryReadOnly) return new(0, 0, paths.Count);
        IsImporting = true;
        try
        {
            var prepared = await Task.Run(() => paths.Select(path => { cancellationToken.ThrowIfCancellationRequested(); return PrepareGame(path); }).ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CommitImport(prepared);
        }
        finally { IsImporting = false; }
    }

    private static Game? PrepareGame(string path)
    {
        try
        {
            var executable = Path.GetFullPath(path);
            if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var directory = Path.GetDirectoryName(executable)!;
            var metadata = OfflineGameMetadataService.Read(executable);
            return new Game
            {
                Title = metadata.Title,
                RootPath = directory,
                ExecutablePath = executable,
                WorkingDirectory = directory,
                Engine = GameScanService.DetectEngine(directory),
                CoverPath = metadata.CoverPath,
                Tags = ["未分类"]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { AppLog.Write("game.import-failed", ex); return null; }
    }

    private GameImportResult CommitImport(IReadOnlyList<Game?> prepared)
    {
        if (IsLibraryReadOnly) { ShowLibraryReadError(); return new(0, 0, prepared.Count); }
        var pending = new List<Game>();
        var duplicates = 0;
        foreach (var game in prepared.OfType<Game>())
        {
            if (Games.Concat(pending).Any(existing => GameIdentity.AreSame(existing.ExecutablePath, game.ExecutablePath))) duplicates++;
            else pending.Add(game);
        }
        var failed = prepared.Count - duplicates - pending.Count;
        if (pending.Count > 0)
        {
            if (!SaveGames(Games.Concat(pending))) return new(0, duplicates, failed + pending.Count);
            foreach (var game in pending) Games.Add(game);
            SelectedGame = pending[^1];
            UpdateCounts();
        }
        return new(pending.Count, duplicates, failed);
    }

    private void LaunchSelectedGame()
    {
        if (SelectedGame is null) return;
        if (!_runningGames.Add(SelectedGame.Id)) { Notify("这个游戏已经在运行中。", FeedbackKind.Info); return; }

        var game = SelectedGame;
        try
        {
            var process = _launchService.Start(game);
            game.LastLaunchStatus = "Running";
            if (SaveGames()) Notify($"已启动 {game.Title}。");
            _ = MonitorProcessAsync(game, process);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _runningGames.Remove(game.Id);
            game.LastLaunchStatus = "Failed";
            if (SaveGames()) Notify($"无法启动：{exception.Message}", FeedbackKind.Error);
            else
            {
                StatusMessage = $"无法启动：{exception.Message}。运行状态也未能保存，请检查后重新保存。";
                Feedback.Show(StatusMessage, FeedbackKind.Error, RetrySaveCommand, "重新保存");
            }
        }
    }

    private void EditSelectedGame(object? parameter)
    {
        var game = parameter as Game ?? SelectedGame;
        if (game is null) return;

        var dialog = new GameEditorWindow(game, candidate => Games.Any(other => other.Id != game.Id && GameIdentity.AreSame(other.ExecutablePath, candidate)), _runningGames.Contains(game.Id)) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) { Notify("已取消编辑，游戏信息未修改。", FeedbackKind.Info); return; }

        game.UpdatedAt = DateTimeOffset.Now;
        var saved = SaveGames();
        OnPropertyChanged(nameof(FavoriteGlyph));
        RefreshView();
        if (saved) Notify($"{game.Title} 的游戏信息已保存。");
    }

    private async Task MonitorProcessAsync(Game game, Process process)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await process.WaitForExitAsync();
            var duration = Math.Max(0, (long)watch.Elapsed.TotalSeconds);
            var current = Games.FirstOrDefault(item => item.Id == game.Id);
            if (current is null) return; // Removing a running entry must never recreate it on exit.
            game = current;
            game.TotalPlaySeconds += duration;
            game.LastPlayedAt = DateTimeOffset.Now;
            game.LastLaunchStatus = process.ExitCode == 0 ? "Completed" : "Exited";
            if (SaveGames()) Notify($"{game.Title} 已结束，本次游玩 {FormatDuration(duration)}，记录已保存。", FeedbackKind.Info);
            RefreshView();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AppLog.Write("game.monitor-failed", ex);
            var current = Games.FirstOrDefault(item => item.Id == game.Id);
            if (current is not null) { current.LastLaunchStatus = "Unknown"; SaveGames(); }
        }
        finally
        {
            process.Dispose();
            _runningGames.Remove(game.Id);
        }
    }

    private void ToggleFavorite()
    {
        if (SelectedGame is null) return;
        SelectedGame.IsFavorite = !SelectedGame.IsFavorite;
        SelectedGame.UpdatedAt = DateTimeOffset.Now;
        var saved = SaveGames();
        if (saved) Notify(SelectedGame.IsFavorite ? $"已收藏 {SelectedGame.Title}。" : $"已取消收藏 {SelectedGame.Title}。");
        OnPropertyChanged(nameof(FavoriteGlyph));
        RefreshView();
    }

    private void RemoveSelectedGame(object? parameter)
    {
        var game = parameter as Game ?? SelectedGame;
        if (game is null) return;
        var result = System.Windows.MessageBox.Show($"从库中移除“{game.Title}”？\n游戏文件不会被删除。", "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { Notify("已取消移除，游戏仍保留在库中。", FeedbackKind.Info); return; }

        RemoveGame(game);
    }

    public bool RemoveGame(Game game)
    {
        var current = Games.FirstOrDefault(item => item.Id == game.Id);
        if (current is null || !SaveGames(Games.Where(item => item.Id != game.Id))) return false;
        Games.Remove(current);
        SelectedGame = Games.FirstOrDefault();
        Notify($"已从库中移除 {game.Title}。游戏文件未删除。");
        return true;
    }

    private void OpenSelectedFolder()
    {
        if (SelectedGame is null || !Directory.Exists(SelectedGame.RootPath))
        {
            Notify("游戏目录不存在，请检查游戏是否已移动或删除。", FeedbackKind.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{SelectedGame.RootPath}\"",
                UseShellExecute = true
            });
            Notify("已请求在资源管理器中打开游戏目录。", FeedbackKind.Info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        { Notify("无法打开游戏目录，请检查目录和系统资源管理器。", FeedbackKind.Error); }
    }

    private bool SaveGames(IEnumerable<Game>? snapshot = null)
    {
        try { _libraryService.Save(snapshot ?? Games); HasUnsavedLibraryChanges = false; return true; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write("library.save-failed", exception);
            if (snapshot is null) HasUnsavedLibraryChanges = true;
            if (_libraryService.IsReadOnly) { ShowLibraryReadError(); return false; }
            if (exception is LibraryConflictException)
            {
                StatusMessage = exception.Message;
                Feedback.Show(StatusMessage, FeedbackKind.Error, RecoverLibraryCommand, "处理数据冲突");
                return false;
            }
            StatusMessage = HasUnsavedLibraryChanges
                ? "游戏库未保存，修改仍保留在本次运行中。请检查写入权限后重新保存。"
                : "游戏库未保存，本次操作未应用。请检查数据目录写入权限后重试。";
            Feedback.Show(StatusMessage, FeedbackKind.Error, HasUnsavedLibraryChanges ? RetrySaveCommand : null, "重新保存");
            return false;
        }
    }

    private void Notify(string message, FeedbackKind kind = FeedbackKind.Success)
    {
        StatusMessage = message;
        Feedback.Show(message, kind);
    }

    private void ShowLibraryReadError()
    {
        OnPropertyChanged(nameof(IsLibraryReadOnly));
        StatusMessage = _libraryService.ReadError ?? "游戏库读取失败，当前列表已保留。";
        Feedback.Show(StatusMessage, FeedbackKind.Error, RecoverLibraryCommand, "恢复数据");
    }

    private void RecoverLibrary()
    {
        var choice = System.Windows.MessageBox.Show($"将当前可读取的 {Games.Count} 个项目保存为游戏库？\n原文件会另存为恢复副本，不会删除。\n\n选择“否”打开数据目录，可先检查 library.json.bak 等备份。", "恢复游戏库", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.No) { OpenDataFolder(); return; }
        if (choice != MessageBoxResult.Yes) return;
        try
        {
            _libraryService.Recover(Games);
            HasUnsavedLibraryChanges = false;
            OnPropertyChanged(nameof(IsLibraryReadOnly));
            Notify("当前列表已保存，原游戏库已保留恢复副本。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { AppLog.Write("library.recovery-failed", ex); Notify("恢复未完成，请检查数据目录权限。当前列表和原文件已保留。", FeedbackKind.Error); }
    }

    private void OpenDataFolder()
    {
        try { Directory.CreateDirectory(_libraryService.DataDirectory); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _libraryService.DataDirectory }, UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { AppLog.Write("library.open-data-failed", ex); Notify("无法打开数据目录。", FeedbackKind.Error); }
    }

    public bool TrySavePendingChanges() => !HasUnsavedLibraryChanges || SaveGames();

    private void RaiseCommandStates()
    {
        LaunchGameCommand.RaiseCanExecuteChanged();
        EditGameCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        RemoveGameCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟"
            : seconds < 60 ? $"{seconds} 秒" : $"{duration.Minutes} 分钟";
    }
}
