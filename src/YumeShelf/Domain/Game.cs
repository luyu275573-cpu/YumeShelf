using YumeShelf.Common;

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
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
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
    public string? LastLaunchStatus { get => _lastLaunchStatus; set => SetProperty(ref _lastLaunchStatus, value); }
}

