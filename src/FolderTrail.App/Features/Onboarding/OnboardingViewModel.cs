using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Features.WatchedFolders;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Features.Onboarding;

/// <summary>First-run explanation. Nothing is watched until the user explicitly chooses a folder.</summary>
public sealed partial class OnboardingViewModel(SettingsService settings, WatchedFoldersViewModel folders, INotifier notifier, DiagnosticLog log) : ViewModelBase
{
    public event EventHandler? Completed;

    [RelayCommand]
    private async Task ChooseFolder()
    {
        if (await folders.AddFolderInteractiveAsync())
        {
            await CompleteAsync();
        }
    }

    [RelayCommand]
    private Task Skip() => CompleteAsync();

    private async Task CompleteAsync()
    {
        try
        {
            await settings.UpdateAsync(s => s with { OnboardingCompleted = true });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Error("UI", "Saving onboarding state failed.", exception);
            notifier.Notify("Couldn't save your choice", exception.Message, NoticeKind.Error);
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
