using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace DiskCleaner.Services;

internal sealed record CleanupCategory(string Name, string Description, string Root,
    bool Recursive, string Pattern = "*");

internal sealed record DiskFile(string FullPath, long Size, DateTime ModifiedUtc)
{
    internal FileSnapshot? Snapshot { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    public string SizeText => FormatSize(Size);
    public string DeletionStatus => Snapshot is null ? "不可删除" : "";

    internal static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
            _ => $"{bytes:N0} B"
        };
    }
}

internal sealed record CleanupFile(DiskFile File, FileSnapshot Snapshot)
{
    internal string FullPath => File.FullPath;
}

internal sealed record CleanupGroup(CleanupCategory Category, IReadOnlyList<CleanupFile> Files, int Skipped);
internal sealed record CleanupScan(IReadOnlyList<CleanupGroup> Groups);
internal sealed record LargeFileScan(IReadOnlyList<DiskFile> Files, long Matched, int Inaccessible);
internal sealed record CleanupResult(int Deleted, int Skipped, long Bytes);

internal static class DiskScanner
{
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = FileSnapshot.UnsafeAttributes
    };

    internal static CleanupScan ScanCleanup(IReadOnlyList<CleanupCategory> categories,
        CancellationToken cancellation, IProgress<string>? progress = null)
    {
        var groups = new List<CleanupGroup>();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"正在扫描{category.Name}…");
            var files = new List<CleanupFile>();
            var skipped = 0;

            if (CanScanCleanupRoot(category.Root, out var inaccessible))
            {
                skipped += Walk(category.Root, category.Recursive, [], cancellation, file =>
                {
                    if (file.LastWriteTimeUtc > cutoff || file.CreationTimeUtc > cutoff
                        || !FileSystemName.MatchesSimpleExpression(category.Pattern, file.Name, ignoreCase: true)
                        || seen.Contains(file.FullName))
                    {
                        return;
                    }

                    var snapshot = FileSnapshot.Read(file.FullName);
                    if (snapshot is null)
                    {
                        skipped++;
                        return;
                    }

                    // 扫描时以句柄读到的时间为准，避免误纳入刚被替换的文件。
                    var modified = DateTime.FromFileTimeUtc(snapshot.Value.Modified);
                    if (modified <= cutoff && DateTime.FromFileTimeUtc(snapshot.Value.Created) <= cutoff)
                    {
                        files.Add(new CleanupFile(new DiskFile(file.FullName, snapshot.Value.Size, modified),
                            snapshot.Value));
                        seen.Add(file.FullName);
                    }
                });
            }
            else if (inaccessible)
            {
                skipped++;
            }

            groups.Add(new CleanupGroup(category, files, skipped));
        }

        return new CleanupScan(groups);
    }

    internal static LargeFileScan ScanLarge(string root, long minimumBytes, int limit,
        IEnumerable<string> excludedPaths, CancellationToken cancellation, IProgress<string>? progress = null,
        IEnumerable<string>? protectedDirectories = null)
    {
        var excluded = new HashSet<string>(excludedPaths, StringComparer.OrdinalIgnoreCase);
        var largest = new PriorityQueue<DiskFile, long>();
        long matched = 0;
        long visited = 0;
        var reportTimer = Stopwatch.StartNew();

        var inaccessible = Walk(root, true, excluded, cancellation, file =>
        {
            visited++;
            if (progress is not null && reportTimer.ElapsedMilliseconds >= 200)
            {
                progress.Report($"已检查 {visited:N0} 个文件，找到 {matched:N0} 个大文件…");
                reportTimer.Restart();
            }

            if ((file.Attributes & FileAttributes.System) != 0 || file.Length < minimumBytes)
            {
                return;
            }

            matched++;
            // 只保留最大的若干项，避免全盘扫描积累过多结果。
            if (largest.Count < limit || (largest.TryPeek(out _, out var smallest) && file.Length > smallest))
            {
                var entry = new DiskFile(file.FullName, file.Length, file.LastWriteTimeUtc);
                if (largest.Count == limit)
                {
                    largest.Dequeue();
                }

                largest.Enqueue(entry, entry.Size);
            }
        });

        var protectedPrefixes = (protectedDirectories ?? []).Where(path => path.Length > 0)
            .Select(path => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).ToArray();
        var files = largest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(file => file.Size)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(file => CaptureLargeFileSnapshot(file, protectedPrefixes, cancellation))
            .ToArray();
        return new LargeFileScan(files, matched, inaccessible);
    }

    internal static (CleanupResult Result, IReadOnlyList<DiskFile> DeletedFiles) DeleteLargeFiles(
        IReadOnlyList<DiskFile> files, IProgress<string>? progress = null)
    {
        var candidates = files.Where(file => file.Snapshot.HasValue)
            .Select(file => new CleanupFile(file, file.Snapshot!.Value)).ToArray();
        var deleted = new List<DiskFile>();
        var result = Clean(candidates, progress, deleted);
        return (result with { Skipped = files.Count - result.Deleted }, deleted);
    }

    internal static CleanupResult Clean(IReadOnlyList<CleanupFile> files, IProgress<string>? progress = null,
        ICollection<DiskFile>? deletedFiles = null)
    {
        var deleted = 0;
        long bytes = 0;
        var reportTimer = Stopwatch.StartNew();
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (file.Snapshot.DeleteIfUnchanged(file.FullPath))
            {
                deleted++;
                bytes += file.File.Size;
                deletedFiles?.Add(file.File);
            }

            if (progress is not null && reportTimer.ElapsedMilliseconds >= 200)
            {
                progress.Report($"已处理 {index + 1:N0} / {files.Count:N0} 个文件…");
                reportTimer.Restart();
            }
        }

        return new CleanupResult(deleted, files.Count - deleted, bytes);
    }

    private static DiskFile CaptureLargeFileSnapshot(DiskFile file, string[] protectedPrefixes,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (protectedPrefixes.Any(prefix => file.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return file;
        }

        // 只为实际展示的文件记录身份。文件若在枚举后发生变化，须重新扫描才能删除。
        var snapshot = FileSnapshot.Read(file.FullPath);
        if (snapshot is not { } identity || identity.Size != file.Size
            || identity.Modified != file.ModifiedUtc.ToFileTimeUtc())
        {
            return file;
        }

        return file with { Snapshot = snapshot };
    }

    private static int Walk(string root, bool recursive, IReadOnlyCollection<string> excluded,
        CancellationToken cancellation, Action<FileInfo> visit)
    {
        var skipped = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory.FullName) & FileSnapshot.UnsafeAttributes) != 0)
                {
                    continue;
                }

                foreach (var entry in directory.EnumerateFileSystemInfos("*", DirectoryOptions))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (excluded.Contains(entry.FullName))
                    {
                        continue;
                    }

                    if (entry is FileInfo file)
                    {
                        visit(file);
                    }
                    else if (recursive && entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return skipped;
    }

    private static bool CanScanCleanupRoot(string root, out bool inaccessible)
    {
        inaccessible = false;
        try
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var attributes = File.GetAttributes(directory.FullName);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileSnapshot.UnsafeAttributes) != 0)
                {
                    inaccessible = true;
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inaccessible = true;
            return false;
        }
    }
}
