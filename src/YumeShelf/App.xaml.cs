using System.Windows;
using YumeShelf.Common;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

namespace YumeShelf;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Application lifetime; OnExit releases the mutex on its owning dispatcher thread.")]
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--verify-package")
        {
            try { PackageVerification.Run(e.Args[1]); Shutdown(0); }
            catch { Shutdown(2); }
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("application.unhandled", args.Exception);
            System.Windows.MessageBox.Show("应用遇到未处理的问题，将退出以避免继续修改数据。请查看应用日志，重新打开后检查游戏库。", "YumeShelf", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        try
        {
            _instance = new SingleInstanceGuard();
            if (!_instance.IsPrimary) { SingleInstanceGuard.ActivateExistingWindow(); Shutdown(); return; }
            AppLog.Write("application.start");
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("application.start-failed", ex);
            System.Windows.MessageBox.Show("无法启动应用，请检查应用数据目录的访问权限。原游戏库不会被自动清空。", "YumeShelf", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        AppLog.Write("application.exit");
        base.OnExit(e);
    }
}
