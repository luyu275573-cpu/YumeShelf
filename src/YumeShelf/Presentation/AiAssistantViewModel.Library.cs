using System.Net.Http;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Common;

namespace YumeShelf.Presentation;

public sealed partial class AiAssistantViewModel
{
    private readonly Func<AiMetadataDraft, AiCoverCandidate?, AiImageAttachment?, string?>? _applyUpdate;
    private readonly Func<string, GameScanMode, CancellationToken, Task<GameScanResult>>? _scan;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GameImportResult>>? _import;
    private Guid? _lastSelectionId;
    private AiGameResearch? _research;
    private AiScanDraft? _scanDraft;
    private bool _acceptCover;
    public AiGameResearch? Research { get => _research; private set { SetProperty(ref _research, value); OnPropertyChanged(nameof(HasResearch)); RefreshLibraryCommands(); } }
    public bool HasResearch => Research?.Sources.Count > 0;
    public bool HasSourceChoices => Research?.Sources.Count > 1;
    public bool HasCoverChoices => CoverDraft?.Candidates.Count > 0 && (CoverDraft.Candidates.Count > 1 || !HasCoverPreview);
    public bool CanUndoReview => _canUndo;
    public AiScanDraft? ScanDraft { get => _scanDraft; private set { SetProperty(ref _scanDraft, value); OnPropertyChanged(nameof(HasScanDraft)); RefreshLibraryCommands(); } }
    public bool HasScanDraft => ScanDraft is not null;
    public bool HasReview => Draft is not null || CoverDraft is not null || HasResearch;
    public string ReviewTitle => "修改游戏卡片 · " + (Draft?.Original.Title ?? CoverDraft?.Original.Title ?? Research?.Original.Title);
    public bool AcceptCover { get => _acceptCover; set { SetProperty(ref _acceptCover, value); RefreshLibraryCommands(); } }
    public RelayCommand SelectResearchSourceCommand { get; private set; } = null!;
    public RelayCommand ApplyReviewCommand { get; private set; } = null!;
    public RelayCommand DismissReviewCommand { get; private set; } = null!;
    public RelayCommand ApplyImportCommand { get; private set; } = null!;
    public RelayCommand DismissImportCommand { get; private set; } = null!;

    private void InitializeLibraryCommands()
    {
        SelectResearchSourceCommand = new RelayCommand(p => { if (p is AiSource source) _ = ChooseResearchSourceAsync(source); }, _ => CanEditInput && HasResearch);
        ApplyReviewCommand = new RelayCommand(_ => _ = ApplyReviewAsync(), _ => CanEditInput && (Draft is not null || CoverDraft is not null));
        DismissReviewCommand = new RelayCommand(_ => { if (!CanEditInput) return; Draft = null; CoverDraft = null; Research = null; Status = "已放弃本次修改，游戏库未改变。"; }, _ => CanEditInput && HasReview);
        ApplyImportCommand = new RelayCommand(_ => _ = ApplyImportAsync(), _ => CanEditInput && ScanDraft?.Result.Count > 0 && _import is not null);
        DismissImportCommand = new RelayCommand(_ => { if (CanEditInput) { ScanDraft = null; Status = "已放弃入库候选。"; } }, _ => CanEditInput && HasScanDraft);
    }
    private void RefreshLibraryCommands()
    {
        OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(HasSourceChoices)); OnPropertyChanged(nameof(HasCoverChoices)); OnPropertyChanged(nameof(CanUndoReview));
        SelectResearchSourceCommand?.RaiseCanExecuteChanged(); ApplyReviewCommand?.RaiseCanExecuteChanged(); DismissReviewCommand?.RaiseCanExecuteChanged();
        ApplyImportCommand?.RaiseCanExecuteChanged(); DismissImportCommand?.RaiseCanExecuteChanged();
    }
    private void SelectResearchSource(AiSource source)
    {
        if (Research is null || !Research.Sources.Contains(source)) return;
        var covers = Research.Covers.Where(c => c.Source.Id == source.Id).ToArray();
        Draft = Research.Select(source) with { Covers = covers };
        CoverDraft = covers.Length == 0 ? null : new(Research.Original, covers);
        DraftEvidence = "检索来源已取得；请核对作品版本，并勾选要保存的字段。"; OnPropertyChanged(nameof(DraftEvidence));
    }
    private async Task ChooseResearchSourceAsync(AiSource source)
    {
        if (!CanEditInput || Research is null || !Research.Sources.Contains(source)) return;
        SelectResearchSource(source);
        await CoverOperationAsync("正在准备修改预览…", async token =>
        { await LoadReviewPreviewAsync(token, token); Status = "资料已准备，请核对并确认保存。"; });
    }
    private async Task LoadReviewPreviewAsync(CancellationToken token, CancellationToken userCancellation)
    {
        if (CoverDraft?.Candidates.Count != 1 || !AllowNetwork) return;
        try
        {
            var candidate = CoverDraft.Candidates[0];
            var preview = await _search.DownloadCoverAsync(candidate, true, token);
            token.ThrowIfCancellationRequested(); SelectedCover = candidate; CoverPreview = preview; AcceptCover = true;
        }
        catch (Exception ex) when ((ex is OperationCanceledException && !userCancellation.IsCancellationRequested) || ex is HttpRequestException || GameImageLoader.IsImageError(ex))
        { _feedback.Show("封面预览未取得，文字资料仍可审核；可点击候选重试图片。", FeedbackKind.Warning); }
    }
    private async Task ApplyReviewAsync()
    {
        if (!CanEditInput) return;
        var draft = Draft ?? (CoverDraft is null ? null : new AiMetadataDraft(CoverDraft.Original, [], "封面候选") { Covers = CoverDraft.Candidates });
        if (draft is null || _applyUpdate is null) { ShowValidation("当前没有可保存的修改。"); return; }
        var candidate = AcceptCover && HasCoverPreview ? SelectedCover : null;
        if (!draft.Fields.Any(f => f.Accepted) && candidate is null) { ShowValidation("请选取要保存的字段或封面。"); return; }
        var target = _library().FirstOrDefault(g => g.Id == draft.Original.Id);
        if (target is null || target.Revision != draft.Original.Revision) { ShowValidation("目标游戏已移除或资料发生变化，请重新生成修改。"); return; }
        await CoverOperationAsync("正在保存游戏卡片…", async token =>
        {
            AiImageAttachment? image = null;
            if (candidate is not null)
            {
                if (!AllowNetwork) { ShowValidation("本轮联网不可用，封面未下载。"); return; }
                image = await _search.DownloadCoverAsync(candidate, false, token);
            }
            token.ThrowIfCancellationRequested();
            var error = _applyUpdate(draft, candidate, image);
            if (error is not null) { ShowValidation(error); return; }
            _canUndo = true; ClearContextHistory(false); Draft = null; CoverDraft = null; Research = null;
            GameContext = _library().FirstOrDefault(g => g.Id == draft.Original.Id);
            Status = $"已更新《{GameContext?.Title ?? draft.Original.Title}》的游戏卡片，可撤销本次修改。";
            _history.Add(new("user", "用户已在修改卡片中确认保存所选内容。"));
            _history.Add(new("assistant", Status + "\n已保存字段：" + string.Join("、", draft.Fields.Where(f => f.Accepted).Select(f => f.Label)) + (candidate is null ? "" : "、封面")));
            Conversation.Add(new(false, Status)); _feedback.Show(Status);
        });
    }
    private async Task ApplyImportAsync()
    {
        if (!CanEditInput || ScanDraft is null || _import is null) return;
        var paths = ScanDraft.Result.Where(c => c.IsSelected).Select(c => c.ExecutablePath).ToArray();
        if (paths.Length == 0) { ShowValidation("请选取需要入库的游戏。"); return; }
        await CoverOperationAsync("正在添加选中的游戏…", async token =>
        {
            var result = await _import(paths, token);
            if (result.Failed == 0) ScanDraft = null;
            _history.Clear();
            Status = $"入库完成：新增 {result.Added} 项，已存在 {result.Duplicates} 项，失败 {result.Failed} 项。";
            _history.Add(new("user", "用户已确认添加选中的本地游戏。")); _history.Add(new("assistant", Status));
            Conversation.Add(new(false, Status)); _feedback.Show(Status, result.Failed == 0 ? FeedbackKind.Success : FeedbackKind.Warning);
        });
    }
}
