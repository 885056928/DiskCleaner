using System.Windows;
using DiskCleaner.Services;

namespace DiskCleaner;

public partial class ConfirmCleanupWindow : Window
{
    public ConfirmCleanupWindow(int count, long bytes, string? filePath = null)
    {
        InitializeComponent();
        Summary.Text = $"将删除 {count:N0} 个文件，文件大小合计 {DiskFile.FormatSize(bytes)}。";
        if (filePath is not null)
        {
            Title = "确认删除文件";
            FilePath.Text = filePath;
            FilePath.Visibility = Visibility.Visible;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs args) => DialogResult = true;
}
