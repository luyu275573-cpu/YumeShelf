using System.IO;

namespace YumeShelf.Infrastructure;

public static class AppLog
{
    private static readonly object Gate = new();
    public static string DirectoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YumeShelf", "logs");

    // Exclude exception messages, requests, keys, paths and user content.
    public static void Write(string operation, Exception? error = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "application.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, Path.Combine(DirectoryPath, "application.previous.log"), true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation} {error?.GetType().Name} {error?.HResult:X8}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Diagnostics must not prevent recovery. */ }
        }
    }
}
