using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using YumeShelf.Infrastructure;

namespace YumeShelf.Common;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _owns;
    private bool _disposed;
    public bool IsPrimary => _owns;
    public SingleInstanceGuard(string? name = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        _mutex = new Mutex(false, name ?? @"Local\YumeShelf-" + identity.User?.Value);
        try { _owns = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owns = true; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_owns) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _disposed = true;
    }

    public static void ActivateExistingWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var handle = process.MainWindowHandle;
                    ShowWindow(handle, IsIconic(handle) ? 9 : 5);
                    SetForegroundWindow(handle);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { AppLog.Write("window.activate-failed", ex); }
            }
        }
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
