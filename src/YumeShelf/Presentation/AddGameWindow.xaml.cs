using System.Windows;
using System.Windows.Controls;
using System.IO;
using YumeShelf.Application;
using YumeShelf.Common;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace YumeShelf.Presentation;

public enum GameAddOutcome { Added, Duplicate, Failed }

public partial class AddGameWindow : Window
{
    private readonly AddGameViewModel _vm = new();
    private readonly GameScanService _scanner = new();
    private readonly Func<string, GameAddOutcome> _addPath;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GameImportResult>>? _addBatch;
    private CancellationTokenSource? _scanCancellation;
    private bool _closed;
    private int _addedCount;
    public string? ResultMessage { get; private set; }
    public FeedbackKind ResultKind { get; private set; }

    public AddGameWindow(Func<string, GameAddOutcome> addPath, Func<IReadOnlyList<string>, CancellationToken, Task<GameImportResult>>? addBatch = null)
    {
        _addPath = addPath;
        _addBatch = addBatch;
        DataContext = _vm;
        InitializeComponent();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AddGameViewModel.SearchRoot))
            {
                _vm.Candidates.Clear();
                _vm.Status = "搜索范围已更改，请重新扫描。";
                UpdateActionLabel();
            }
        };
        Closed += (_, _) => { _closed = true; _scanCancellation?.Cancel(); };
    }

    private void UpdateActionLabel()
    {
        if (ActionButton is not null)
            ActionButton.Content = ModeTabs.SelectedIndex == 1 ? "选择并添加"
                : _vm.IsScanning ? "正在扫描…" : _vm.Candidates.Count > 0 ? "确认添加" : "开始扫描";
    }

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionLabel();

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要搜索的磁盘或游戏文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) _vm.SearchRoot = dialog.FolderName;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsScanning) return;
        if (ModeTabs.SelectedIndex == 1)
        {
            var dialog = new Win32OpenFileDialog { Filter = "Windows 游戏程序 (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) await AddSelectedAsync([dialog.FileName]);
            else ShowResult("已取消选择，未添加游戏。", FeedbackKind.Info);
            return;
        }

        if (_vm.Candidates.Count > 0)
        {
            await AddSelectedAsync(_vm.Candidates.Where(x => x.IsSelected).Select(x => x.ExecutablePath).ToArray());
            return;
        }
        if (!Directory.Exists(_vm.SearchRoot)) { ShowResult("搜索路径不存在，请选择有效文件夹。", FeedbackKind.Error); return; }
        _vm.IsScanning = true;
        UpdateActionLabel();
        ShowResult("正在扫描所选目录，请稍候…", FeedbackKind.Progress);
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        try
        {
            var found = await _scanner.ScanAsync(_vm.SearchRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellation.Token);
            if (_closed) return;
            foreach (var item in found) _vm.Candidates.Add(item);
            ShowResult(found.Summary + (found.Count == 0 ? "可以更换目录或使用手动添加。" : "请勾选后点击“确认添加”。"), found.IsIncomplete ? FeedbackKind.Warning : FeedbackKind.Info);
        }
        catch (OperationCanceledException) { if (!_closed) ShowResult("扫描已停止，未添加游戏。", FeedbackKind.Info); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!_closed) ShowResult("扫描失败，请检查搜索路径和目录读取权限后重试。", FeedbackKind.Error); }
        finally { _scanCancellation = null; _vm.IsScanning = false; UpdateActionLabel(); }
    }

    // Shared by manual and automatic import so neither can silently ignore duplicates or failed saves.
    public void AddSelected(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { ShowResult("请至少勾选一个游戏后再确认添加。", FeedbackKind.Warning); return; }
        var skipped = 0;
        var failed = 0;
        foreach (var path in paths)
        {
            var result = _addPath(path);
            if (result == GameAddOutcome.Added) _addedCount++;
            else if (result == GameAddOutcome.Duplicate) skipped++;
            else failed++;
        }
        CompleteImport(skipped, failed);
    }

    private async Task AddSelectedAsync(IReadOnlyList<string> paths)
    {
        if (_addBatch is null) { AddSelected(paths); return; }
        if (paths.Count == 0) { ShowResult("请至少勾选一个游戏后再确认添加。", FeedbackKind.Warning); return; }
        _vm.IsScanning = true;
        ActionButton.Content = "正在添加…";
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        ShowResult("正在读取本地资料并添加游戏…", FeedbackKind.Progress);
        try
        {
            var result = await _addBatch(paths, cancellation.Token);
            if (_closed) return;
            _addedCount += result.Added;
            CompleteImport(result.Duplicates, result.Failed);
        }
        catch (OperationCanceledException) { if (!_closed) ShowResult("已取消添加。", FeedbackKind.Info); }
        finally { _scanCancellation = null; _vm.IsScanning = false; UpdateActionLabel(); }
    }

    private void CompleteImport(int skipped, int failed)
    {
        ResultKind = failed > 0 ? FeedbackKind.Error : _addedCount > 0 ? FeedbackKind.Success : FeedbackKind.Info;
        ResultMessage = $"已添加 {_addedCount} 个游戏；本次跳过 {skipped} 个重复项，{failed} 项失败。";
        if (failed > 0) ResultMessage += " 请检查启动文件是否存在及游戏库写入权限后重试。";
        ShowResult(ResultMessage, ResultKind);
        // Keep partial failures available for review and retry; successful saves are already in the library.
        if (failed == 0 && _addedCount > 0) DialogResult = true;
    }

    private void ShowResult(string text, FeedbackKind kind)
    {
        _vm.Status = text;
        _vm.Feedback.Show(text, kind);
    }
}
