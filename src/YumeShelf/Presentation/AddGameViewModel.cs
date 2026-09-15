using System.Collections.ObjectModel;
using System.IO;
using YumeShelf.Application;
using YumeShelf.Common;

namespace YumeShelf.Presentation;

public sealed class AddGameViewModel : ObservableObject
{
    private string _searchRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private bool _isScanning;
    private string _status = "选择一个磁盘或游戏文件夹开始扫描。";
    public string SearchRoot { get => _searchRoot; set => SetProperty(ref _searchRoot, value); }
    public bool IsScanning { get => _isScanning; set => SetProperty(ref _isScanning, value); }
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
