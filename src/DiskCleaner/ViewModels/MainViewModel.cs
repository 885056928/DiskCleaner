using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels;

internal sealed class MainViewModel : INotifyPropertyChanged
{
    internal MainViewModel(Func<int, long, string?, bool> confirmCleanup)
    {
        this.confirmCleanup = confirmCleanup;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        categories = new[]
        {
            new CleanupCategory("用户临时文件", "当前账户的应用临时文件", Under(local, "Temp"), true),
            new CleanupCategory("Windows 临时文件", "系统服务和安装程序的临时文件", Under(windows, "Temp"), true),
            new CleanupCategory("DirectX 着色器缓存", "图形应用可重新生成的缓存", Under(local, "D3DSCache"), true),
            new CleanupCategory("资源管理器缩略图缓存", "图片和视频的缩略图数据库",
                Under(local, @"Microsoft\Windows\Explorer"), false, "thumbcache_*.db")
        }.Where(category => category.Root.StartsWith(DriveRoot, StringComparison.OrdinalIgnoreCase)).ToArray();

        excludedLargePaths = new[] { windows, DriveRoot + "System Volume Information", DriveRoot + "$Recycle.Bin",
            DriveRoot + "pagefile.sys", DriveRoot + "swapfile.sys", DriveRoot + "hiberfil.sys" }
            .Where(path => path.Length > 0).ToArray();
        protectedLargeDirectories =
        [
            windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        ];

        ScanCleanupCommand = new AsyncRelayCommand(ExecuteScanCleanup, CanStart);
        ScanLargeCommand = new AsyncRelayCommand(ExecuteScanLarge, CanStart);
        CleanCommand = new AsyncRelayCommand(ExecuteClean, CanClean);
        DeleteLargeCommand = new AsyncRelayCommand(ExecuteDeleteLarge, CanDeleteLarge);
        CancelScanCommand = new RelayCommand(ExecuteCancelScan, CanCancelScan);
        LocateCommand = new RelayCommand(ExecuteLocate, CanLocate);

        ResetCleanupGroups();
        RefreshDrive();
    }

    #region 属性

    internal sealed record SizeOption(string Label, long Bytes);

    private const string DriveRoot = @"C:\";
    private const int LargeFileLimit = 500;
    private const long Megabyte = 1024 * 1024;
    private readonly Func<int, long, string?, bool> confirmCleanup;
    private readonly CleanupCategory[] categories;
    private readonly string[] excludedLargePaths;
    private readonly string[] protectedLargeDirectories;
    private CancellationTokenSource? scanCancellation;
    private bool isBusy;
    private bool isCleaning;
    private bool hasCleanupResults;
    private string driveUsage = "暂时无法读取 C 盘容量";
    private string driveFree = "—";
    private double drivePercent;
    private string cleanupStatus = "点击「扫描清理项」查看可清理文件。";
    private string largeStatus = "点击「扫描大文件」查看文件列表。";
    private string progressText = "";
    private IReadOnlyList<CleanupGroupViewModel> cleanupGroups = [];
    private IReadOnlyList<DiskFile> previewFiles = [];
    private ObservableCollection<DiskFile> largeFiles = [];
    private CleanupGroupViewModel? selectedGroup;
    private IReadOnlyList<DiskFile> selectedLargeFiles = [];
    private int thresholdIndex;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DriveUsage { get => driveUsage; private set => Set(ref driveUsage, value); }
    public string DriveFree { get => driveFree; private set => Set(ref driveFree, value); }
    public double DrivePercent { get => drivePercent; private set => Set(ref drivePercent, value); }
    public string CleanupStatus { get => cleanupStatus; private set => Set(ref cleanupStatus, value); }
    public string LargeStatus { get => largeStatus; private set => Set(ref largeStatus, value); }
    public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
    public bool IsBusy => isBusy;
    public bool CanEdit => !isBusy;
    public bool CanCancel => scanCancellation is not null;
    internal bool IsCleaning => isCleaning;
    public string SelectedSummary
    {
        get
        {
            var selected = CleanupGroups.Where(group => group.IsChecked).ToArray();
            var count = selected.Sum(group => group.Group.Files.Count);
            return $"已选 {count:N0} 个文件 · {DiskFile.FormatSize(selected.Sum(group => group.Bytes))}";
        }
    }

    public IReadOnlyList<CleanupGroupViewModel> CleanupGroups
    {
        get => cleanupGroups;
        private set => Set(ref cleanupGroups, value);
    }

    public CleanupGroupViewModel? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (Set(ref selectedGroup, value))
            {
                PreviewFiles = value?.Group.Files.Select(item => item.File)
                    .OrderByDescending(file => file.Size).ToArray() ?? [];
            }
        }
    }

    public IReadOnlyList<DiskFile> PreviewFiles { get => previewFiles; private set => Set(ref previewFiles, value); }
    public ObservableCollection<DiskFile> LargeFiles { get => largeFiles; private set => Set(ref largeFiles, value); }

    public string LargeSelectionSummary
    {
        get
        {
            if (selectedLargeFiles.Count == 0)
            {
                return "单击选择，按住 Ctrl 或 Shift 多选";
            }

            var protectedCount = selectedLargeFiles.Count(file => file.Snapshot is null);
            var summary = $"已选 {selectedLargeFiles.Count:N0} 个文件 · {DiskFile.FormatSize(selectedLargeFiles.Sum(file => file.Size))}";
            return protectedCount > 0 ? $"{summary} · {protectedCount:N0} 个不可删除" : summary;
        }
    }

    public IReadOnlyList<SizeOption> SizeOptions { get; } =
    [
        new("100 MB", 100 * Megabyte),
        new("500 MB", 500 * Megabyte),
        new("1 GB", 1024 * Megabyte)
    ];

    public int ThresholdIndex
    {
        get => thresholdIndex;
        set
        {
            if (value >= 0 && value < SizeOptions.Count && Set(ref thresholdIndex, value))
            {
                ReplaceLargeFiles([]);
                LargeStatus = "已更改最小大小，请重新扫描。";
            }
        }
    }

    #endregion

    #region 命令

    public AsyncRelayCommand ScanCleanupCommand { get; }
    public AsyncRelayCommand ScanLargeCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public AsyncRelayCommand DeleteLargeCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand LocateCommand { get; }

    #endregion

    #region 方法

    private static string Under(string root, string relative) => root.Length == 0 ? "" : Path.Combine(root, relative);

    private bool CanStart() => !IsBusy;
    private bool CanClean() => !IsBusy && hasCleanupResults && CleanupGroups.Any(group => group.IsChecked);
    private bool CanCancelScan() => scanCancellation is { IsCancellationRequested: false };
    private bool CanDeleteLarge() => !IsBusy && selectedLargeFiles.Any(file => file.Snapshot.HasValue);
    private bool CanLocate() => !IsBusy && selectedLargeFiles.Count == 1;

    private async Task ExecuteScanCleanup()
    {
        var progress = new Progress<string>(UpdateProgress);
        await RunScanAsync("正在准备清理扫描…", async token =>
        {
            var result = await Task.Run(() => DiskScanner.ScanCleanup(categories, token, progress), token);
            token.ThrowIfCancellationRequested();
            SetCleanupGroups(result);
            hasCleanupResults = true;
            var count = result.Groups.Sum(group => group.Files.Count);
            var skipped = result.Groups.Sum(group => group.Skipped);
            CleanupStatus = count > 0
                ? $"找到 {count:N0} 个文件 · {DiskFile.FormatSize(CleanupGroups.Sum(group => group.Bytes))}。选择类别查看明细。"
                : "扫描完成，没有符合条件的文件。";
            if (skipped > 0)
            {
                CleanupStatus += $" 有 {skipped:N0} 处无法读取或受保护。";
            }
        }, UpdateCleanupStatus);
    }

    private async Task ExecuteScanLarge()
    {
        var minimumBytes = SizeOptions[ThresholdIndex].Bytes;
        var progress = new Progress<string>(UpdateProgress);
        await RunScanAsync("正在准备扫描 C 盘…", async token =>
        {
            var result = await Task.Run(() =>
                DiskScanner.ScanLarge(DriveRoot, minimumBytes, LargeFileLimit, excludedLargePaths, token, progress,
                    protectedLargeDirectories), token);
            token.ThrowIfCancellationRequested();
            ReplaceLargeFiles(result.Files);
            LargeStatus = result.Matched switch
            {
                0 => "扫描完成，没有达到所选大小的文件。",
                > LargeFileLimit => $"找到 {result.Matched:N0} 个文件，显示最大的 {LargeFileLimit} 个。",
                _ => $"找到 {result.Matched:N0} 个文件，按大小从大到小排列。"
            };
            if (result.Inaccessible > 0)
            {
                LargeStatus += $" 跳过 {result.Inaccessible:N0} 处无法读取的位置。";
            }
        }, UpdateLargeStatus);
    }

    private async Task RunScanAsync(string startText, Func<CancellationToken, Task> scan, Action<string> setStatus)
    {
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        SetBusy(true, startText);
        try
        {
            // 文件系统枚举是同步操作，放到工作线程避免阻塞 WPF 消息循环。
            await scan(cancellation.Token);
            RefreshDrive();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            setStatus("扫描已取消。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            setStatus($"扫描失败 · {exception.Message}");
        }
        finally
        {
            scanCancellation = null;
            SetBusy(false, "");
        }
    }

    private void ExecuteCancelScan()
    {
        scanCancellation?.Cancel();
        ProgressText = "正在取消扫描…";
        CancelScanCommand.Refresh();
    }

    private async Task ExecuteClean()
    {
        var files = CleanupGroups.Where(group => group.IsChecked).SelectMany(group => group.Group.Files).ToArray();
        if (!confirmCleanup(files.Length, files.Sum(file => file.File.Size), null))
        {
            return;
        }

        isCleaning = true;
        SetBusy(true, "正在清理已选文件…");
        try
        {
            var progress = new Progress<string>(UpdateProgress);
            var result = await Task.Run(() => DiskScanner.Clean(files, progress));
            CleanupStatus = $"已删除 {result.Deleted:N0} 个文件 · 文件大小合计 {DiskFile.FormatSize(result.Bytes)}。";
            if (result.Skipped > 0)
            {
                CleanupStatus += $" 跳过 {result.Skipped:N0} 个已变化、占用或受保护的文件。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CleanupStatus = $"清理中断 · {exception.Message}";
        }
        finally
        {
            // 清理后作废旧快照，下一次清理须以新扫描结果为准。
            hasCleanupResults = false;
            ResetCleanupGroups();
            ReplaceLargeFiles([]);
            LargeStatus = "清理后请重新扫描大文件。";
            RefreshDrive();
            isCleaning = false;
            SetBusy(false, "");
        }
    }

    internal void SetLargeSelection(IReadOnlyList<DiskFile> files)
    {
        selectedLargeFiles = files;
        Notify(nameof(LargeSelectionSummary));
        DeleteLargeCommand.Refresh();
        LocateCommand.Refresh();
    }

    private void ReplaceLargeFiles(IEnumerable<DiskFile> files)
    {
        SetLargeSelection([]);
        LargeFiles = new ObservableCollection<DiskFile>(files);
    }

    private async Task ExecuteDeleteLarge()
    {
        var files = selectedLargeFiles;
        var candidates = files.Where(file => file.Snapshot.HasValue).ToArray();
        var singlePath = candidates.Length == 1 ? candidates[0].FullPath : null;
        if (!confirmCleanup(candidates.Length, candidates.Sum(file => file.Size), singlePath))
        {
            return;
        }

        isCleaning = true;
        SetBusy(true, "正在删除所选大文件…");
        try
        {
            var progress = new Progress<string>(UpdateProgress);
            var deletion = await Task.Run(() => DiskScanner.DeleteLargeFiles(files, progress));
            ApplyLargeDeletion(deletion.DeletedFiles);
            var result = deletion.Result;
            LargeStatus = $"已删除 {result.Deleted:N0} 个文件 · 文件大小合计 {DiskFile.FormatSize(result.Bytes)}。";
            if (result.Skipped > 0)
            {
                LargeStatus += $" 跳过 {result.Skipped:N0} 个已变化、占用或受保护的文件。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LargeStatus = $"删除中断 · {exception.Message}";
        }
        finally
        {
            RefreshDrive();
            isCleaning = false;
            SetBusy(false, "");
        }
    }

    private void ApplyLargeDeletion(IReadOnlyList<DiskFile> deleted)
    {
        // 逐项更新，保留其余结果、表格排序和未能删除的选中项。
        foreach (var file in deleted)
        {
            LargeFiles.Remove(file);
        }

        if (hasCleanupResults && deleted.Count > 0)
        {
            var paths = deleted.Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (CleanupGroups.Any(group => group.Group.Files.Any(file => paths.Contains(file.FullPath))))
            {
                hasCleanupResults = false;
                ResetCleanupGroups();
                CleanupStatus = "部分清理项已在大文件页删除，请重新扫描清理项。";
            }
        }
    }

    private void ExecuteLocate()
    {
        var path = selectedLargeFiles.Count == 1 ? selectedLargeFiles[0].FullPath : null;
        if (path is null || !File.Exists(path))
        {
            LargeStatus = "无法访问所选文件。请重新扫描。";
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{path}\"");
            using var process = Process.Start(startInfo);
        }
        catch (Win32Exception exception)
        {
            LargeStatus = $"无法打开资源管理器 · {exception.Message}";
        }
    }

    private void ResetCleanupGroups()
    {
        SetCleanupGroups(new CleanupScan(categories.Select(category =>
            new CleanupGroup(category, [], 0)).ToArray()), scanned: false);
    }

    private void SetCleanupGroups(CleanupScan scan, bool scanned = true)
    {
        foreach (var group in CleanupGroups)
        {
            group.PropertyChanged -= OnGroupChanged;
        }

        var activeName = SelectedGroup?.Name;
        var groups = scan.Groups.Select(group => new CleanupGroupViewModel(group, scanned)).ToArray();
        foreach (var group in groups)
        {
            group.PropertyChanged += OnGroupChanged;
        }

        CleanupGroups = groups;
        SelectedGroup = groups.FirstOrDefault(group => group.Name == activeName)
            ?? groups.FirstOrDefault(group => group.HasFiles) ?? groups.FirstOrDefault();
        Notify(nameof(SelectedSummary));
        CleanCommand.Refresh();
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        Notify(nameof(SelectedSummary));
        CleanCommand.Refresh();
    }

    private void RefreshDrive()
    {
        try
        {
            var drive = new DriveInfo(DriveRoot);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            DriveUsage = $"已用 {DiskFile.FormatSize(used)} / 共 {DiskFile.FormatSize(drive.TotalSize)}";
            DriveFree = DiskFile.FormatSize(drive.AvailableFreeSpace);
            DrivePercent = 100.0 * used / drive.TotalSize;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DriveUsage = "暂时无法读取 C 盘容量";
            DriveFree = "—";
            DrivePercent = 0;
        }
    }

    private void SetBusy(bool value, string text)
    {
        isBusy = value;
        ProgressText = text;
        Notify(nameof(IsBusy));
        Notify(nameof(CanEdit));
        Notify(nameof(CanCancel));
        ScanCleanupCommand.Refresh();
        ScanLargeCommand.Refresh();
        CancelScanCommand.Refresh();
        CleanCommand.Refresh();
        DeleteLargeCommand.Refresh();
        LocateCommand.Refresh();
    }

    private void UpdateProgress(string text)
    {
        if (IsBusy && scanCancellation is not { IsCancellationRequested: true })
        {
            ProgressText = text;
        }
    }

    private void UpdateCleanupStatus(string text) => CleanupStatus = text;
    private void UpdateLargeStatus(string text) => LargeStatus = text;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(property);
        return true;
    }

    private void Notify(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    #endregion
}
