using System.Windows.Input;

namespace DiskCleaner.ViewModels;

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    void ICommand.Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            // ICommand 没有异步签名，执行方法负责等待磁盘操作并呈现异常。
            _ = execute();
        }
    }

    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
