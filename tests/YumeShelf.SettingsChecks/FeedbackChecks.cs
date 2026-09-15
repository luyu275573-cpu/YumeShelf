using System.IO;
using System.Windows;
using System.Windows.Controls;
using YumeShelf.Application;
using YumeShelf.Common;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void CheckOperationFeedback(string output)
    {
        var libraryPath = Path.Combine(output, "feedback-library.json");
        var settingsPath = Path.Combine(output, "feedback-settings.json");
        var store = new AppSettingsStore(settingsPath);
        store.Save(new AppSettings());
        var games = new JsonGameStore(libraryPath);
        var vm = new MainWindowViewModel(new GameLibraryService(games), new GameLaunchService(), store);
        var main = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var root = (FrameworkElement)main.Content;
        vm.OpenSettingsCommand.Execute(null);
        vm.SettingsEditor.PageTransparency = 0.38;
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Success && store.Load().PageTransparency == 0.38,
            "save confirmation produces visible success only after persistence");
        var first = vm.Feedback.Current!.Sequence;
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        Check(vm.Feedback.Current!.Sequence > first, "repeating the same confirmation presents a fresh notice");
        vm.Feedback.Dismiss(first);
        Check(vm.Feedback.IsVisible, "old dismissal cannot hide a newer notice");
        Render(root, Path.Combine(output, "feedback-settings-success.png"), 1200, 720);
        var toast = (FeedbackToast)main.FindName("OperationToast");
        CheckInside(toast, root, "success toast stays inside shell");
        Check(toast.Visibility == Visibility.Visible, "success toast actually renders");

        var savedText = File.ReadAllText(settingsPath);
        Directory.CreateDirectory(settingsPath + ".tmp");
        vm.SettingsEditor.PageTransparency = 0.88;
        Check(vm.SettingsEditor.SaveNotice.Contains("未保存"), "editing clears the previous saved status");
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Error && File.ReadAllText(settingsPath) == savedText &&
              vm.SettingsEditor.PageTransparency == 0.88 && vm.SettingsEditor.SaveNotice.Contains("保存失败"),
            "failed settings save keeps draft and reports consistent error status");
        Render(root, Path.Combine(output, "feedback-settings-error-small.png"), 900, 580);
        CheckInside(toast, root, "error toast wraps within narrow shell");
        Directory.Delete(settingsPath + ".tmp");
        vm.SettingsEditor.CancelCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Info && vm.SettingsEditor.PageTransparency == 0.38,
            "cancel gives a distinct result and restores saved values");
        vm.SettingsEditor.TestAiModelCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Warning && !vm.SettingsEditor.IsAiTesting,
            "missing model credentials produces actionable feedback without network");

        string CreateExecutable(string folder)
        {
            var directory = Path.Combine(output, folder);
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "game.exe");
            File.WriteAllBytes(executable, [0x4D, 0x5A]); // Import only; never executed.
            return executable;
        }
        var executable = CreateExecutable("反馈测试游戏一");
        var secondExecutable = CreateExecutable("反馈测试游戏二");
        Check(vm.AddGameFromPath(executable) == GameAddOutcome.Added && games.Load().Count == 1,
            "added result requires successful library save");
        Check(vm.AddGameFromPath(executable) == GameAddOutcome.Duplicate && games.Load().Count == 1,
            "duplicate import has a distinct outcome");
        Directory.CreateDirectory(libraryPath + ".tmp");
        Check(vm.AddGameFromPath(secondExecutable) == GameAddOutcome.Failed && vm.Games.Count == 1 && games.Load().Count == 1,
            "failed import reports failure and does not create an unsaved duplicate");
        vm.SelectedGame = vm.Games.Single();
        vm.ToggleFavoriteCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Error && vm.HasUnsavedLibraryChanges &&
              vm.Feedback.Current.Action == vm.RetrySaveCommand && !games.Load().Single().IsFavorite,
            "failed save cannot be overwritten by favorite success and offers retry");
        vm.RefreshLibraryCommand.Execute(null);
        Check(vm.HasUnsavedLibraryChanges && vm.Games.Single().IsFavorite && vm.Feedback.Current?.Kind == FeedbackKind.Warning,
            "refresh cannot erase a pending modification");
        Render(root, Path.Combine(output, "feedback-library-retry.png"), 1200, 720);
        Directory.Delete(libraryPath + ".tmp");
        vm.RetrySaveCommand.Execute(null);
        Check(!vm.HasUnsavedLibraryChanges && vm.Feedback.Current?.Kind == FeedbackKind.Success && games.Load().Single().IsFavorite,
            "retry persists retained changes before reporting success");
        vm.RefreshLibraryCommand.Execute(null);
        Check(vm.Feedback.Current?.Message.Contains("已刷新") == true, "refresh reports resulting item count");
        File.WriteAllText(libraryPath, "{broken");
        vm.RefreshLibraryCommand.Execute(null);
        Check(vm.Games.Count == 1 && vm.Feedback.Current?.Kind == FeedbackKind.Error,
            "corrupt library refresh reports error and retains the current list");
        games.Save(vm.Games);
        vm.SelectedGame = vm.Games.Single();
        vm.SelectedGame.ExecutablePath = Path.Combine(output, "missing.exe");
        vm.LaunchGameCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Error && vm.Feedback.Current.Message.Contains("无法启动"),
            "missing launch file reports failure");
        vm.SelectedGame.ExecutablePath = executable;

        var addWindow = new AddGameWindow(vm.AddGameFromPath);
        addWindow.AddSelected([]);
        var addVm = (AddGameViewModel)addWindow.DataContext;
        Check(addVm.Feedback.Current?.Kind == FeedbackKind.Warning, "empty selection explains why nothing was added");
        addWindow.AddSelected([executable]);
        Check(addWindow.ResultKind == FeedbackKind.Info && addWindow.ResultMessage!.Contains("1 个重复项"),
            "manual duplicate receives explicit feedback");
        addWindow.AddSelected([secondExecutable, executable, Path.Combine(output, "missing-import.exe")]);
        Check(addWindow.ResultKind == FeedbackKind.Error && addWindow.ResultMessage!.Contains("已添加 1 个游戏") &&
              addWindow.ResultMessage.Contains("1 个重复项") && addWindow.ResultMessage.Contains("1 项失败") && vm.Games.Count == 2,
            "batch import reports added, duplicate and failed counts without hiding partial failure");
        Render((FrameworkElement)addWindow.Content, Path.Combine(output, "feedback-add-partial.png"), 720, 560);
        addWindow.Close();

        vm.OpenAiAssistantCommand.Execute(null);
        vm.AiAssistant.Query = "介绍一部视觉小说";
        vm.AiAssistant.SendCommand.Execute(null);
        Check(vm.Feedback.Current?.Kind == FeedbackKind.Warning && !vm.AiAssistant.IsBusy && vm.AiAssistant.Query.Length > 0,
            "AI configuration failure is visible and preserves the question");
        vm.OpenSettingsCommand.Execute(null);
        vm.SettingsEditor.NightMode = true;
        vm.SettingsEditor.ConfirmCommand.Execute(null);
        Render(root, Path.Combine(output, "feedback-settings-success-night.png"), 1200, 720);
        vm.SettingsEditor.NightMode = false;
        vm.SettingsEditor.ConfirmCommand.Execute(null);

        main.Show();
        Pump(TimeSpan.FromMilliseconds(80));
        vm.Feedback.Show("重复保存计时测试");
        Pump(TimeSpan.FromMilliseconds(2000));
        vm.Feedback.Show("重复保存计时测试");
        Pump(TimeSpan.FromMilliseconds(2200));
        Check(vm.Feedback.IsVisible, "repeated result restarts automatic dismissal timer");
        Pump(TimeSpan.FromMilliseconds(2000));
        Check(!vm.Feedback.IsVisible, "success feedback dismisses automatically");
        vm.Feedback.Show("错误保留测试", FeedbackKind.Error);
        Pump(TimeSpan.FromMilliseconds(4200));
        Check(vm.Feedback.IsVisible, "error feedback persists for review");
        vm.Feedback.DismissCommand.Execute(null);
        Check(!vm.Feedback.IsVisible, "feedback can be dismissed explicitly");
        main.Close();
    }
}
