using Avalonia.Controls;
using Avalonia.Threading;

namespace FolderTrail.App.Shell;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null)
        {
            _shell.FocusSearchRequested -= OnFocusSearchRequested;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.FocusSearchRequested += OnFocusSearchRequested;
        }
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => TimelinePane.FocusSearch(), DispatcherPriority.Input);
}
