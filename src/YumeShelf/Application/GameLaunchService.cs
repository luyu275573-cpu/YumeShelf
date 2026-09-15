using System.Diagnostics;
using System.IO;
using YumeShelf.Domain;

namespace YumeShelf.Application;

public sealed class GameLaunchService
{
    public Process Start(Game game)
    {
        if (!File.Exists(game.ExecutablePath)) throw new FileNotFoundException("找不到游戏启动文件。", game.ExecutablePath);

        var workingDirectory = string.IsNullOrWhiteSpace(game.WorkingDirectory)
            ? Path.GetDirectoryName(game.ExecutablePath)
            : game.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"找不到游戏工作目录：{workingDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            WorkingDirectory = workingDirectory,
            Arguments = game.LaunchArguments,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Windows 未返回有效的游戏进程。");
    }
}
