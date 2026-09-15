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
using YumeShelf.Common;
using YumeShelf.Domain;

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
        var uniqueGames = DeduplicateGames(loadedGames);
        Games = new ObservableCollection<Game>(uniqueGames);
        Games.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LibrarySummary));
            RefreshView();
        };
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        GamesView.SortDescriptions.Add(new SortDescription(nameof(Game.AddedAt), ListSortDirection.Descending));

        FilterOptions = ["全部类型", "已收藏", "最近游玩"];
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string));
        AddGameCommand = new RelayCommand(_ => AddGame());
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());
        RetrySaveCommand = new RelayCommand(_ => { if (SaveGames()) Notify("游戏库已重新保存。所有待保存修改已写入本机。"); }, _ => HasUnsavedLibraryChanges);
        LaunchGameCommand = new RelayCommand(_ => LaunchSelectedGame(), _ => HasSelectedGame);
        EditGameCommand = new RelayCommand(EditSelectedGame, parameter => parameter is Game || HasSelectedGame);
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(), _ => HasSelectedGame);
        RemoveGameCommand = new RelayCommand(RemoveSelectedGame, parameter => parameter is Game || HasSelectedGame);
        OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => HasSelectedGame);
        OpenSettingsCommand = new RelayCommand(_ => Navigate("Settings"));
        OpenAiAssistantCommand = new RelayCommand(_ => Navigate("AI"));
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => HasSearchText);

        if (uniqueGames.Count != loadedGames.Count) SaveGames();
        RefreshView();
    }

    public ObservableCollection<Game> Games { get; }
    public RelayCommand OpenAiAssistantCommand { get; }
    public ICollectionView GamesView { get; }
    public IReadOnlyList<string> FilterOptions { get; }
    public string ActiveNavigation { get => _activeNavigation; private set => SetProperty(ref _activeNavigation, value); }
    public SettingsViewModel SettingsEditor => _settingsEditor ??= CreateSettingsEditor();
    public AiAssistantViewModel AiAssistant => _aiAssistant ??= new AiAssistantViewModel(_settings, Feedback);
    public OperationFeedback Feedback { get; } = new();
    public bool HasUnsavedLibraryChanges { get => _hasUnsavedLibraryChanges; private set { SetProperty(ref _hasUnsavedLibraryChanges, value); RetrySaveCommand?.RaiseCanExecuteChanged(); } }
    public RelayCommand RetrySaveCommand { get; }

    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value)) return;
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
            if (SetProperty(ref _selectedFilter, value)) RefreshView();
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
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
        var searchable = string.Join(' ', game.Title, game.Engine, game.Description, string.Join(' ', game.Tags));
        return searchable.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshView()
    {
        GamesView.Refresh();
        _filteredCount = GamesView.Cast<Game>().Count();
        OnPropertyChanged(nameof(HasFilteredGames));
        ClearSearchCommand.RaiseCanExecuteChanged();
    }

    private void AddGame()
    {
        var dialog = new AddGameWindow(AddGameFromPath) { Owner = System.Windows.Application.Current.MainWindow };
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
            var selectedPath = SelectedGame?.ExecutablePath;
            var loaded = _libraryService.Load(throwOnError: true);
            var unique = DeduplicateGames(loaded);
            if (unique.Count != loaded.Count && !SaveGames(unique)) return;
            Games.Clear();
            foreach (var game in unique) Games.Add(game);
            SelectedGame = Games.FirstOrDefault(game => string.Equals(game.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? Games.FirstOrDefault();
            RefreshView();
            Notify($"游戏库已刷新，共 {Games.Count} 个项目。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Notify("游戏库读取失败，当前列表已保留。请检查数据文件和读取权限。", FeedbackKind.Error);
        }
    }

    public GameAddOutcome AddGameFromPath(string path)
    {
        try
        {
            var executablePath = Path.GetFullPath(path);
            if (!File.Exists(executablePath)) return GameAddOutcome.Failed;
            var rootPath = Path.GetDirectoryName(executablePath) ?? string.Empty;
            var engine = DetectEngine(executablePath);
            var metadata = OfflineGameMetadataService.Read(executablePath);
            if (Games.Any(game => string.Equals(game.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(game.RootPath, rootPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(game.Engine, engine, StringComparison.OrdinalIgnoreCase))))
            {
                return GameAddOutcome.Duplicate;
            }

            var game = new Game
            {
                Title = metadata.Title,
                RootPath = rootPath,
                ExecutablePath = executablePath,
                WorkingDirectory = rootPath,
                Engine = engine,
                Description = "请编辑游戏条目补充简介。",
                Tags = ["未分类"]
            };
            game.CoverPath = metadata.CoverPath;

            if (!SaveGames(Games.Append(game))) return GameAddOutcome.Failed;
            Games.Add(game);
            SelectedGame = game;
            StatusMessage = $"已添加 {game.Title}";
            return GameAddOutcome.Added;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return GameAddOutcome.Failed; }
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

        var dialog = new GameEditorWindow(game) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) { Notify("已取消编辑，游戏信息未修改。", FeedbackKind.Info); return; }

        game.UpdatedAt = DateTimeOffset.Now;
        var saved = SaveGames();
        OnPropertyChanged(nameof(FavoriteGlyph));
        RefreshView();
        if (saved) Notify($"{game.Title} 的游戏信息已保存。");
    }

    private async Task MonitorProcessAsync(Game game, Process process)
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            await process.WaitForExitAsync();
            var duration = Math.Max(0, (long)(DateTimeOffset.Now - startedAt).TotalSeconds);
            game.TotalPlaySeconds += duration;
            game.LastPlayedAt = DateTimeOffset.Now;
            game.LastLaunchStatus = process.ExitCode == 0 ? "Completed" : "Exited";
            if (SaveGames()) Notify($"{game.Title} 已结束，本次游玩 {FormatDuration(duration)}，记录已保存。", FeedbackKind.Info);
            RefreshView();
        }
        catch (InvalidOperationException)
        {
            game.LastLaunchStatus = "Unknown";
            SaveGames();
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

        if (!SaveGames(Games.Where(item => item != game))) return;
        Games.Remove(game);
        SelectedGame = Games.FirstOrDefault();
        Notify($"已从库中移除 {game.Title}。游戏文件未删除。");
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            if (snapshot is null) HasUnsavedLibraryChanges = true;
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

    private static IReadOnlyList<Game> DeduplicateGames(IEnumerable<Game> games)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectoryEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Game>();
        foreach (var game in games)
        {
            var executable = Path.GetFullPath(game.ExecutablePath);
            var directoryKey = $"{Path.GetFullPath(game.RootPath)}|{game.Engine}";
            if (!seenPaths.Add(executable) || (game.Engine.Equals("BGI", StringComparison.OrdinalIgnoreCase) && !seenDirectoryEngines.Add(directoryKey))) continue;
            result.Add(game);
        }
        return result;
    }

    private void RaiseCommandStates()
    {
        LaunchGameCommand.RaiseCanExecuteChanged();
        EditGameCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        RemoveGameCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static string DetectEngine(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (File.Exists(Path.Combine(directory, "renpy"))) return "Ren'Py";
        if (File.Exists(Path.Combine(directory, "UnityPlayer.dll"))) return "Unity";
        if (File.Exists(Path.Combine(directory, "BGI.gdb")) || File.Exists(Path.Combine(directory, "BGI.kdb")) || File.Exists(Path.Combine(directory, "BGI.hvl")) || Directory.EnumerateFiles(directory, "data*.arc").Any()) return "BGI";
        return "未知引擎";
    }

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟"
            : $"{Math.Max(1, duration.Minutes)} 分钟";
    }
}
