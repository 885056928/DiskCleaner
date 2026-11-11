using System.ComponentModel;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels;

internal sealed class CleanupGroupViewModel : INotifyPropertyChanged
{
    internal CleanupGroupViewModel(CleanupGroup group, bool scanned)
    {
        Group = group;
        Bytes = group.Files.Sum(file => file.File.Size);
        SizeText = scanned ? DiskFile.FormatSize(Bytes) : "—";
        CountText = scanned ? GetCountText(group) : "待扫描";
    }

    #region 属性

    private bool isChecked;

    internal CleanupGroup Group { get; }
    internal long Bytes { get; }
    public string Name => Group.Category.Name;
    public string Description => Group.Category.Description;
    public string Root => Group.Category.Root;
    public bool HasFiles => Group.Files.Count > 0;
    public string SizeText { get; }
    public string CountText { get; }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    #region 方法

    private static string GetCountText(CleanupGroup group)
    {
        return group.Skipped > 0
            ? $"{group.Files.Count:N0} 个文件 · 跳过 {group.Skipped:N0} 处"
            : $"{group.Files.Count:N0} 个文件";
    }

    #endregion
}
