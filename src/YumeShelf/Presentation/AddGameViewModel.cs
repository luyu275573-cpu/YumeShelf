using System.Collections.ObjectModel;
using System.IO;
using YumeShelf.Application;
using YumeShelf.Common;

namespace YumeShelf.Presentation;

public sealed class AddGameViewModel : ObservableObject
{
    private string _searchRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private bool _isScanning;
    private GameScanMode _scanMode;
    private string _status = "选择一个磁盘或游戏文件夹开始扫描。";
    public string SearchRoot { get => _searchRoot; set => SetProperty(ref _searchRoot, value); }
    public bool IsScanning { get => _isScanning; set => SetProperty(ref _isScanning, value); }
    public GameScanMode ScanMode
    {
        get => _scanMode;
        set
        {
            if (!IsScanning && SetProperty(ref _scanMode, value)) OnPropertyChanged(nameof(ScanModeDescription));
        }
    }
    public string ScanModeDescription => ScanMode == GameScanMode.Expanded
        ? "同时查找 RPG Maker、WOLF RPG、Unity、GameMaker 等游戏。扩展候选需手动勾选，玩法类型请自行核对。"
        : "优先查找视觉小说；其他引擎需有额外的 Galgame 特征。找不到 ACT、SLG 或 RPG 时可切换扩展扫描。";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public ObservableCollection<GameScanCandidate> Candidates { get; } = [];
    public OperationFeedback Feedback { get; } = new();
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public AddGameViewModel()
    {
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        ClearAllCommand = new RelayCommand(_ => SetAll(false));
    }
    private void SetAll(bool value) { foreach (var item in Candidates) item.IsSelected = value; OnPropertyChanged(nameof(Candidates)); }
}
