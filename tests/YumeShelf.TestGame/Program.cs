using System.Text;

var durationSeconds = 10;
var extraArguments = new List<string>();

for (var index = 0; index < args.Length; index++)
{
    if (args[index].Equals("--duration", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length &&
        int.TryParse(args[++index], out var parsedDuration))
    {
        durationSeconds = Math.Clamp(parsedDuration, 1, 300);
        continue;
    }

    extraArguments.Add(args[index]);
}

var startedAt = DateTimeOffset.Now;
var logPath = Path.Combine(Environment.CurrentDirectory, "test-game-run.log");
var report = new StringBuilder()
    .AppendLine("YumeShelf 本地测试程序")
    .AppendLine($"StartedAt={startedAt:O}")
    .AppendLine($"WorkingDirectory={Environment.CurrentDirectory}")
    .AppendLine($"ProcessId={Environment.ProcessId}")
    .AppendLine($"DurationSeconds={durationSeconds}")
    .AppendLine($"Arguments={string.Join(" | ", extraArguments)}")
    .ToString();

await File.WriteAllTextAsync(logPath, report, Encoding.UTF8);
Console.WriteLine(report);
Console.WriteLine($"测试程序将在 {durationSeconds} 秒后退出。");
await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
Console.WriteLine("测试程序已正常退出。");
