using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;

namespace DiskCleaner;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel(ConfirmCleanup);
        DataContext = viewModel;
    }

    private bool ConfirmCleanup(int count, long bytes, string? filePath)
    {
        var dialog = new ConfirmCleanupWindow(count, bytes, filePath) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void OnLargeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, LargeGrid) && DataContext is MainViewModel model)
        {
            model.SetLargeSelection(LargeGrid.SelectedItems.Cast<DiskFile>().ToArray());
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (viewModel.IsCleaning)
        {
            args.Cancel = true;
            return;
        }

        if (viewModel.CancelScanCommand.CanExecute(null))
        {
            viewModel.CancelScanCommand.Execute(null);
        }
    }
}
