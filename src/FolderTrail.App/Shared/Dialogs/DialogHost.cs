using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FolderTrail.App.Shared.Dialogs;

/// <summary>
/// A modal dialog rendered inside the main window. The rest of the window is disabled while it's open, focus stays
/// inside it, and Escape cancels.
/// </summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private TaskCompletionSource<object?>? _completion;

    public abstract string Title { get; }

    internal Task<object?> Open()
    {
        _completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnOpened();
        return _completion.Task;
    }

    protected virtual void OnOpened()
    {
    }

    protected void Close(object? result) => _completion?.TrySetResult(result);

    [RelayCommand]
    public void Cancel() => Close(null);
}

public interface IDialogService
{
    /// <returns>The dialog's result, or default when it was cancelled.</returns>
    Task<TResult?> ShowAsync<TResult>(DialogViewModel dialog);
}

public sealed partial class DialogHostViewModel : ViewModelBase, IDialogService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(Title))]
    public partial DialogViewModel? Current { get; private set; }

    public bool IsOpen => Current is not null;

    public string Title => Current?.Title ?? string.Empty;

    public async Task<TResult?> ShowAsync<TResult>(DialogViewModel dialog)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            var completion = dialog.Open();
            Current = dialog;
            var result = await completion;
            return result is TResult typed ? typed : default;
        }
        finally
        {
            Current = null;
            _oneAtATime.Release();
        }
    }

    [RelayCommand]
    private void CancelCurrent() => Current?.Cancel();
}

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive = false, string? acknowledgement = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDestructive = isDestructive;
        Acknowledgement = acknowledgement;
    }

    public override string Title { get; }

    public string Message { get; }

    public string ConfirmText { get; }

    public bool IsDestructive { get; }

    /// <summary>When set, the user must tick this statement before confirming.</summary>
    public string? Acknowledgement { get; }

    public bool RequiresAcknowledgement => Acknowledgement is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsAcknowledged { get; set; }

    private bool CanConfirm() => !RequiresAcknowledgement || IsAcknowledged;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(true);
}
