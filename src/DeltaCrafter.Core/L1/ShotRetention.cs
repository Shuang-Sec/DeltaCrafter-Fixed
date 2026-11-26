using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>
/// 诊断截图目录(%LocalAppData%\DeltaCrafterFixed\shots)的自动留存清理。
/// 日志已限制保留 14 天，但失败截图单张 3–8MB 若不限额会持续膨胀到数百 MB。
/// 按诊断组（同一次转储生成的 .png/.txt/.crop.png/.json）成组清理：
/// 保留最近 14 天内且不超过 80 组诊断现场，仅处理本目录内的诊断文件扩展名，不触碰子目录及其他文件。
/// </summary>
public static class ShotRetention
{
    public const int DefaultMaxRetainedGroups = 80;
    public const int DefaultMaxAgeDays = 14;
    private static readonly string[] DiagnosticExtensions = [".png", ".txt", ".json"];

    public static void TryPrune(string shotsDir, ILogger? log = null,
        int maxRetainedGroups = DefaultMaxRetainedGroups, int maxAgeDays = DefaultMaxAgeDays)
    {
        try
        {
            int removed = Prune(shotsDir, DateTimeOffset.UtcNow, maxRetainedGroups, maxAgeDays);
            if (removed > 0)
                log?.Information("已按留存策略清理 {Count} 个过期诊断截图文件。", removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Debug(ex, "清理过期诊断截图时出现可忽略的 IO 异常。");
        }
    }

    internal static int Prune(string shotsDir, DateTimeOffset nowUtc,
        int maxRetainedGroups = DefaultMaxRetainedGroups, int maxAgeDays = DefaultMaxAgeDays)
    {
        if (string.IsNullOrWhiteSpace(shotsDir) || !Directory.Exists(shotsDir))
            return 0;
        maxRetainedGroups = Math.Max(1, maxRetainedGroups);
        maxAgeDays = Math.Max(1, maxAgeDays);
        var cutoff = nowUtc.AddDays(-maxAgeDays);

        var groups = new Dictionary<string, (DateTimeOffset LatestWrite, List<FileInfo> Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(shotsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (!DiagnosticExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
                continue;
            string key = GroupKey(info.Name);
            var writeTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (!groups.TryGetValue(key, out var entry))
                groups[key] = (writeTime, [info]);
            else
            {
                entry.Files.Add(info);
                if (writeTime > entry.LatestWrite)
                    groups[key] = (writeTime, entry.Files);
            }
        }

        int deleted = 0;
        int kept = 0;
        foreach (var group in groups.Values.OrderByDescending(g => g.LatestWrite))
        {
            bool keep = kept < maxRetainedGroups && group.LatestWrite >= cutoff;
            if (keep)
            {
                kept++;
                continue;
            }
            foreach (var file in group.Files)
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }

    internal static string GroupKey(string fileName)
    {
        if (fileName.EndsWith(".crop.png", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".crop.png".Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
