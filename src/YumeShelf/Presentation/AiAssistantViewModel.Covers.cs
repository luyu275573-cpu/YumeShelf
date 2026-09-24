using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using YumeShelf.Application.AI;
using YumeShelf.Common;
using YumeShelf.Infrastructure;

namespace YumeShelf.Presentation;

public sealed partial class AiAssistantViewModel
{
    private readonly VndbSearch _search;
    private readonly Func<AiCoverDraft, AiCoverCandidate, AiImageAttachment, string?>? _applyCover;
    private readonly Func<string?>? _undoCover;
    private AiCoverDraft? _coverDraft;
    private AiCoverCandidate? _selectedCover;
    private AiImageAttachment? _coverPreview;
    private bool _canUndoCover;
    private bool _isCoverSearchOpen;
    private string _coverQuery = "";

    public RelayCommand FindCoverCommand { get; private set; } = null!;
    public RelayCommand PreviewCoverCommand { get; private set; } = null!;
    public RelayCommand ApplyCoverCommand { get; private set; } = null!;
    public RelayCommand DismissCoverCommand { get; private set; } = null!;
    public RelayCommand UndoCoverCommand { get; private set; } = null!;
    public bool IsCoverSearchOpen { get => _isCoverSearchOpen; set => SetProperty(ref _isCoverSearchOpen, value); }
    public string CoverQuery { get => _coverQuery; set { if (SetProperty(ref _coverQuery, value)) FindCoverCommand?.RaiseCanExecuteChanged(); } }
    public AiCoverDraft? CoverDraft
    {
        get => _coverDraft;
        private set { SetProperty(ref _coverDraft, value); SelectedCover = null; CoverPreview = null; AcceptCover = false; OnPropertyChanged(nameof(HasCoverDraft)); RefreshCoverCommands(); RefreshLibraryCommands(); }
    }
    public bool HasCoverDraft => CoverDraft is not null;
    public AiCoverCandidate? SelectedCover { get => _selectedCover; private set { SetProperty(ref _selectedCover, value); RefreshCoverCommands(); } }
    public AiImageAttachment? CoverPreview { get => _coverPreview; private set { SetProperty(ref _coverPreview, value); OnPropertyChanged(nameof(HasCoverPreview)); RefreshCoverCommands(); RefreshLibraryCommands(); } }
    public bool HasCoverPreview => CoverPreview is not null;

    private void InitializeCoverCommands()
    {
        FindCoverCommand = new RelayCommand(_ => _ = FindCoverAsync(), _ => CanEditInput);
        PreviewCoverCommand = new RelayCommand(p => { if (p is AiCoverCandidate candidate) _ = PreviewCoverAsync(candidate); }, _ => CanEditInput && AllowNetwork && HasCoverDraft);
        ApplyCoverCommand = new RelayCommand(_ => _ = ApplyCoverAsync(), _ => CanEditInput && AllowNetwork && HasCoverPreview && _applyCover is not null);
        DismissCoverCommand = new RelayCommand(_ => { if (CanEditInput) { CoverDraft = null; Status = "已放弃封面候选，原封面未改变。"; } }, _ => CanEditInput && HasCoverDraft);
        UndoCoverCommand = new RelayCommand(_ => UndoCover(), _ => CanEditInput && _canUndoCover && _undoCover is not null);
    }
    private void RefreshCoverCommands()
    {
        FindCoverCommand?.RaiseCanExecuteChanged(); PreviewCoverCommand?.RaiseCanExecuteChanged(); ApplyCoverCommand?.RaiseCanExecuteChanged();
        DismissCoverCommand?.RaiseCanExecuteChanged(); UndoCoverCommand?.RaiseCanExecuteChanged();
    }
    private static string DefaultCoverQuery(string title) => Regex.Replace(title, @"\s*[vV]\d+(?:\.\d+)*(?:\s*)$", "").Trim();

    private async Task FindCoverAsync()
    {
        if (!CanEditInput) return;
        if (!AllowNetwork) { ShowValidation("请先勾选“联网资料检索”，再查找封面。"); return; }
        if (GameContext is null) { ShowValidation("请先在库中选中游戏，再点击“关联选中游戏”。"); return; }
        var original = _library().FirstOrDefault(g => g.Id == GameContext.Id);
        if (original is null) { ShowValidation("关联的游戏已从库中移除，请重新关联。"); return; }
        var query = CoverQuery.Trim();
        if (query.Length is 0 or > 200) { ShowValidation("请输入不超过200字符的作品名，可使用日文名或英文名。"); return; }
        if (GameContext.Revision != original.Revision) ClearContextHistory();
        GameContext = original; CoverDraft = null; IsCoverSearchOpen = true;
        await CoverOperationAsync("正在查找 VNDB 封面…", async token =>
        {
            var candidates = await _search.SearchCoversAsync(query, token);
            token.ThrowIfCancellationRequested();
            CoverDraft = new(original, candidates);
            Status = candidates.Count == 0 ? "未找到可用的普通封面，原封面未改变。请换用日文名或英文名重试。" :
                $"找到 {candidates.Count} 个封面候选。请核对作品，预览后确认更换。";
        });
    }
    private async Task PreviewCoverAsync(AiCoverCandidate candidate)
    {
        if (!CanEditInput || !AllowNetwork || CoverDraft is null || !CoverDraft.Candidates.Contains(candidate)) return;
        SelectedCover = null; CoverPreview = null;
        await CoverOperationAsync("正在加载封面预览…", async token =>
        {
            var preview = await _search.DownloadCoverAsync(candidate, true, token);
            token.ThrowIfCancellationRequested();
            SelectedCover = candidate; CoverPreview = preview;
            AcceptCover = true;
            Status = "预览已加载。请核对目标作品及所选内容，再确认保存。";
        });
    }
    private async Task ApplyCoverAsync()
    {
        if (!CanEditInput || !AllowNetwork || CoverDraft is null || SelectedCover is null || CoverPreview is null || _applyCover is null) return;
        var draft = CoverDraft; var candidate = SelectedCover;
        var current = _library().FirstOrDefault(g => g.Id == draft.Original.Id);
        if (current is null || current.Revision != draft.Original.Revision) { ShowValidation("目标游戏已移除或资料/封面发生变化，请重新查找后确认。"); return; }
        await CoverOperationAsync("正在下载并保存封面…", async token =>
        {
            var image = await _search.DownloadCoverAsync(candidate, false, token);
            token.ThrowIfCancellationRequested();
            var error = _applyCover(draft, candidate, image);
            if (error is not null) { ShowValidation(error); return; }
            _canUndoCover = true; ClearContextHistory();
            GameContext = _library().FirstOrDefault(g => g.Id == draft.Original.Id);
            Status = "封面已更换并保存到游戏库，可撤销本次更换。"; _feedback.Show(Status);
        });
    }
    private void UndoCover()
    {
        if (!CanEditInput || !_canUndoCover || _undoCover is null) return;
        var error = _undoCover();
        if (error is not null) { ShowValidation(error); return; }
        _canUndoCover = false; var id = GameContext?.Id; ClearContextHistory();
        GameContext = _library().FirstOrDefault(g => g.Id == id);
        RefreshCoverCommands(); Status = "已恢复更换前的封面，游戏库已保存。"; _feedback.Show(Status);
    }
    private async Task CoverOperationAsync(string status, Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource(); _activeCancellation = cancellation;
        IsBusy = true; Status = status;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { ShowValidation(cancellation.IsCancellationRequested ? "操作已停止，尚未提交的修改未保存。" : "请求超时，尚未提交的修改未保存。可重试。"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || GameImageLoader.IsImageError(ex))
        {
            AppLog.Write("ai.library-operation-failed", ex);
            ShowValidation(ex is InvalidOperationException or InvalidDataException ? ex.Message : "操作未完成，请检查网络、图片或数据目录权限后重试。已取得的候选仍可核对。");
        }
        finally { if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null; IsBusy = false; SaveSession(); }
    }
}
