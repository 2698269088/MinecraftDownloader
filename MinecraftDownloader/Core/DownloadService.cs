using System.IO;

namespace MinecraftDownloader.Core;

public sealed record DownloadItem(string Url, string PathName, string? Sha1, long Size)
{
    public string TargetPath => Path.Combine(AppPaths.GameDir,
        PathName.Replace('/', Path.DirectorySeparatorChar));
}

public sealed class DownloadReport
{
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public long TotalBytes { get; init; }
    public long CompletedBytes { get; init; }
    public string CurrentFile { get; init; } = "";
    public double Percent => TotalBytes > 0 ? Math.Min(100, CompletedBytes * 100.0 / TotalBytes) : 0;
}

public static class DownloadService
{
    private sealed class Reporter(IProgress<DownloadReport>? progress, int totalFiles, long totalBytes)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _perFile = new();
        private int _completedFiles;
        private DateTime _last = DateTime.MinValue;

        public void FileProgress(string path, long done, string name, bool force = false)
        {
            lock (_lock)
            {
                _perFile[path] = done;
                Emit(name, force);
            }
        }

        public void FileCompleted(string path, long size, string name)
        {
            lock (_lock)
            {
                _perFile[path] = Math.Max(size, _perFile.GetValueOrDefault(path));
                _completedFiles++;
                Emit(name, true);
            }
        }

        private void Emit(string name, bool force)
        {
            if (progress is null)
                return;
            var now = DateTime.UtcNow;
            if (!force && now - _last < TimeSpan.FromMilliseconds(100))
                return;
            _last = now;
            progress.Report(new DownloadReport
            {
                TotalFiles = totalFiles,
                CompletedFiles = _completedFiles,
                TotalBytes = totalBytes,
                CompletedBytes = _perFile.Values.Sum(),
                CurrentFile = name
            });
        }
    }

    public static async Task DownloadAllAsync(IReadOnlyCollection<DownloadItem> items,
        IProgress<DownloadReport>? progress, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;

        // 资源索引中同一个 hash 可能对应多个文件名，必须去重，避免并发写同一个目标文件。
        items = items
            .GroupBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var totalBytes = items.Sum(x => Math.Max(0, x.Size));
        var reporter = new Reporter(progress, items.Count, totalBytes);
        using var semaphore = new SemaphoreSlim(Math.Clamp(LauncherConfig.Current.DownloadThreads, 1, 64));
        var tasks = new List<Task>(items.Count);

        foreach (var item in items)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var name = Path.GetFileName(item.TargetPath);
                    if (File.Exists(item.TargetPath) && item.Size > 0 &&
                        new FileInfo(item.TargetPath).Length == item.Size)
                    {
                        reporter.FileCompleted(item.TargetPath, item.Size, name);
                        return;
                    }

                    var perFile = new SynchronousProgress<(long Done, long Total)>(p =>
                        reporter.FileProgress(item.TargetPath, p.Done, name));

                    await HttpService.DownloadFileAsync(item.Url, item.TargetPath, item.Sha1, perFile, ct)
                        .ConfigureAwait(false);
                    reporter.FileCompleted(item.TargetPath,
                        Math.Max(item.Size, new FileInfo(item.TargetPath).Length), name);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        progress?.Report(new DownloadReport
        {
            TotalFiles = items.Count,
            CompletedFiles = items.Count,
            TotalBytes = totalBytes,
            CompletedBytes = totalBytes
        });
    }
}

internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
