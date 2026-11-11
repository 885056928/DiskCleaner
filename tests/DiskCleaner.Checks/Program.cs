using DiskCleaner.Services;

var sandbox = Path.Combine(Path.GetTempPath(), "DiskCleaner-check-" + Guid.NewGuid().ToString("N"));
var cache = Directory.CreateDirectory(Path.Combine(sandbox, "cache")).FullName;
var outside = Directory.CreateDirectory(Path.Combine(sandbox, "outside")).FullName;

try
{
    var old = CreateOld(cache, "old.tmp", 1024);
    var changed = CreateOld(cache, "changed.tmp", 128);
    var locked = CreateOld(cache, "locked.tmp", 64);
    var renamed = CreateOld(cache, "replaced.tmp", 48);
    var oldTimestamp = File.GetLastWriteTimeUtc(renamed);
    var fresh = Path.Combine(cache, "fresh.tmp");
    File.WriteAllText(fresh, "最近创建的文件");
    var externalFile = CreateOld(outside, "outside.tmp", 4096);

    var direct = new CleanupCategory("临时文件", "", cache, true);
    var scan = DiskScanner.ScanCleanup([direct], CancellationToken.None);
    Check(scan.Groups.Single().Files.Count == 4, "只扫描超过 24 小时的文件");
    var submitted = scan.Groups.Single().Files;

    File.AppendAllText(changed, "扫描后内容变了");
    // 保留原文件对象，验证同路径、同大小、同时间的替换文件仍会被识别。
    File.Move(renamed, Path.Combine(cache, "original.tmp"));
    File.WriteAllBytes(renamed, new byte[48]);
    File.SetCreationTimeUtc(renamed, oldTimestamp);
    File.SetLastWriteTimeUtc(renamed, oldTimestamp);
    using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var result = DiskScanner.Clean(submitted);
        Check(result.Deleted == 1 && result.Skipped == 3 && result.Bytes == 1024, "仅删除原快照且未被占用的文件");
    }
    Check(!File.Exists(old) && File.Exists(fresh) && File.Exists(changed)
        && File.Exists(locked) && File.Exists(renamed), "删除边界和计数正确");

    var nested = Directory.CreateDirectory(Path.Combine(cache, "nested")).FullName;
    CreateOld(nested, "thumbcache_nested.db", 500);
    CreateOld(cache, "thumbcache_32.db", 350);
    CreateOld(cache, "other.db", 350);
    var thumbnails = new CleanupCategory("缩略图", "", cache, false, "thumbcache_*.db");
    Check(DiskScanner.ScanCleanup([thumbnails], CancellationToken.None).Groups.Single().Files.Count == 1,
        "缩略图规则仅接受指定目录中的数据库");

    var swap = Directory.CreateDirectory(Path.Combine(cache, "swap")).FullName;
    var swappedFile = CreateOld(swap, "outside.tmp", 4096);
    var swapScan = DiskScanner.ScanCleanup([direct], CancellationToken.None).Groups.Single().Files
        .Where(file => file.FullPath == swappedFile).ToArray();
    Check(swapScan.Length == 1, "替换测试的源文件进入扫描快照");
    Directory.Move(swap, Path.Combine(cache, "original"));
    Directory.CreateSymbolicLink(swap, outside);
    try
    {
        Check(DiskScanner.Clean(swapScan).Skipped == 1 && File.Exists(externalFile),
            "目录替换为符号链接后保留目录外文件");
        Check(!DiskScanner.ScanCleanup([direct], CancellationToken.None).Groups.Single().Files
            .Any(file => file.FullPath.StartsWith(swap, StringComparison.OrdinalIgnoreCase)),
            "扫描跳过符号链接");
        var large = DiskScanner.ScanLarge(sandbox, 300, 2, [], CancellationToken.None);
        Check(large.Matched > 2 && large.Files.Count == 2
            && large.Files[0].Size == 4096 && large.Files[1].Size == 4096,
            "大文件按大小降序截取，保留总匹配数");
    }
    finally
    {
        Directory.Delete(swap);
    }

    var largeFolder = Directory.CreateDirectory(Path.Combine(sandbox, "large")).FullName;
    var protectedFolder = Directory.CreateDirectory(Path.Combine(largeFolder, "protected")).FullName;
    var first = CreateOld(largeFolder, "first.bin", 8192);
    var second = CreateOld(largeFolder, "second.bin", 4096);
    var unselected = CreateOld(largeFolder, "unselected.bin", 2048);
    var modified = CreateOld(largeFolder, "modified.bin", 1024);
    var protectedFile = CreateOld(protectedFolder, "protected.bin", 512);
    var largeScan = DiskScanner.ScanLarge(largeFolder, 1, 10, [], CancellationToken.None,
        protectedDirectories: [protectedFolder]);
    Check(largeScan.Files.Count == 5 && largeScan.Files.Single(file => file.FullPath == protectedFile).Snapshot is null,
        "受保护的大文件可查看，但没有删除快照");
    var firstResult = DiskScanner.DeleteLargeFiles([largeScan.Files.Single(file => file.FullPath == first)]);
    Check(firstResult.Result.Deleted == 1 && !File.Exists(first) && File.Exists(second),
        "单选删除仅处理一个文件");
    File.AppendAllText(modified, "扫描后修改");
    var batch = largeScan.Files.Where(file => file.FullPath == second || file.FullPath == modified
        || file.FullPath == protectedFile).ToArray();
    var batchResult = DiskScanner.DeleteLargeFiles(batch);
    Check(batchResult.Result.Deleted == 1 && batchResult.Result.Skipped == 2
        && batchResult.DeletedFiles.Single().FullPath == second && !File.Exists(second),
        "多选删除报告成功项，跳过扫描后修改和受保护的文件");
    Check(File.Exists(unselected) && File.Exists(modified) && File.Exists(protectedFile),
        "未选文件和跳过的文件仍然存在");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        DiskScanner.ScanLarge(sandbox, 1, 2, [], cancellation.Token);
        throw new InvalidOperationException("扫描没有响应取消");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("PASS 扫描响应取消");
    }
}
finally
{
    Directory.Delete(sandbox, recursive: true);
}

static string CreateOld(string directory, string name, int size)
{
    var path = Path.Combine(directory, name);
    var timestamp = DateTime.UtcNow.Date.AddDays(-2);
    File.WriteAllBytes(path, new byte[size]);
    File.SetCreationTimeUtc(path, timestamp);
    File.SetLastWriteTimeUtc(path, timestamp);
    return path;
}

static void Check(bool condition, string scenario)
{
    if (!condition)
    {
        throw new InvalidOperationException(scenario);
    }

    Console.WriteLine($"PASS {scenario}");
}
