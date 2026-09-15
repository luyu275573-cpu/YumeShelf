using YumeShelf.Common;
using System.IO;
using System.Text.Json.Serialization;

namespace YumeShelf.Domain;

public sealed class Game : ObservableObject
{
    private string _title = string.Empty;
    private string _rootPath = string.Empty;
    private string _executablePath = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _launchArguments = string.Empty;
    private string _engine = string.Empty;
    private string _description = string.Empty;
    private int? _releaseYear;
    private string? _coverPath;
    private bool _isFavorite;
    private DateTimeOffset? _lastPlayedAt;
    private long _totalPlaySeconds;
    private string? _lastLaunchStatus;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string RootPath { get => _rootPath; set => SetProperty(ref _rootPath, value); }
    public string ExecutablePath { get => _executablePath; set { if (SetProperty(ref _executablePath, value)) RefreshAvailability(); } }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public string LaunchArguments { get => _launchArguments; set => SetProperty(ref _launchArguments, value); }
    public string Engine { get => _engine; set => SetProperty(ref _engine, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public int? ReleaseYear { get => _releaseYear; set => SetProperty(ref _releaseYear, value); }
    public string? CoverPath { get => _coverPath; set => SetProperty(ref _coverPath, value); }
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPlayedAt { get => _lastPlayedAt; set => SetProperty(ref _lastPlayedAt, value); }
    public long TotalPlaySeconds { get => _totalPlaySeconds; set => SetProperty(ref _totalPlaySeconds, value); }
    public string? LastLaunchStatus { get => _lastLaunchStatus; set { if (SetProperty(ref _lastLaunchStatus, value)) RefreshAvailability(); } }
    [JsonIgnore] public string Availability => !File.Exists(ExecutablePath) ? "启动文件缺失" : LastLaunchStatus == "Running" ? "运行中" : "可启动";
    [JsonIgnore]
    public string LaunchStatusText => LastLaunchStatus switch
    {
        "Running" => "运行中",
        "Completed" => "正常结束",
        "Exited" => "已退出",
        "Failed" => "启动失败",
        "Interrupted" => "记录中断",
        "Unknown" => "状态未知",
        _ => "未游玩"
    };
    public void RefreshAvailability() { OnPropertyChanged(nameof(Availability)); OnPropertyChanged(nameof(LaunchStatusText)); OnPropertyChanged(nameof(CoverPath)); }
}
