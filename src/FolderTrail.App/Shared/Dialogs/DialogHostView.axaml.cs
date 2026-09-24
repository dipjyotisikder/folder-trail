using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FolderTrail.App.Shared.Dialogs;

public partial class DialogHostView : UserControl
{
    private DialogHostViewModel? _host;
    private IInputElement? _focusBeforeDialog;

    public DialogHostView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_host is not null)
        {
            _host.PropertyChanged -= OnHostPropertyChanged;
        }

        _host = DataContext as DialogHostViewModel;
        if (_host is not null)
        {
            _host.PropertyChanged += OnHostPropertyChanged;
        }
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogHostViewModel.Current))
        {
            return;
        }

        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        if (_host?.Current is not null)
        {
            _focusBeforeDialog = focusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(FocusFirstControl, DispatcherPriority.Loaded);
        }
        else
        {
            var restore = _focusBeforeDialog;
            _focusBeforeDialog = null;
            Dispatcher.UIThread.Post(() => restore?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }
    }

    private void FocusFirstControl()
    {
        var target = Card.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible
                                       && element is TextBox or CheckBox or RadioButton or Button or ComboBox or ToggleSwitch);
        target?.Focus(NavigationMethod.Tab);
    }
}
