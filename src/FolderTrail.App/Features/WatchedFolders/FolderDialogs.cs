using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.App.Features.WatchedFolders;

public sealed partial class AddFolderDialogViewModel : DialogViewModel
{
    private readonly WatchedFolderService _folders;
    private readonly DiagnosticLog _log;
    private int _checkVersion;

    public AddFolderDialogViewModel(string path, WatchedFolderService folders, DiagnosticLog log)
    {
        _folders = folders;
        _log = log;
        Path = WindowsPath.TryCanonicalize(path, out var canonical) ? canonical : path;
    }

    public override string Title => "Watch a folder";

    public string Path { get; }

    public string FolderName => WindowsPath.TryCanonicalize(Path, out var canonical) ? WindowsPath.GetName(canonical) : Path;

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckMessage), nameof(HasProblem), nameof(RequiresMerge), nameof(RelatedPaths), nameof(ConfirmText), nameof(HasRelatedPaths))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial AddFolderCheck? Check { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; private set; }

    public string CheckMessage => Check?.Message ?? "Checking the folder…";

    public bool HasProblem => Check is { CanAdd: false };

    public bool RequiresMerge => Check?.RequiresMerge == true;

    public IReadOnlyList<string> RelatedPaths => Check?.RelatedRoots.Select(r => r.DisplayPath).ToList() ?? [];

    public bool HasRelatedPaths => RelatedPaths.Count > 0;

    public string ConfirmText => RequiresMerge ? "Watch and merge" : "Start watching";

    protected override void OnOpened() => _ = RunCheckAsync();

    partial void OnIncludeSubfoldersChanged(bool value) => _ = RunCheckAsync();

    private bool CanConfirm() => !IsBusy && Check is { CanAdd: true };

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        IsBusy = true;
        try
        {
            var result = await _folders.AddAsync(Path, IncludeSubfolders, mergeContainedRoots: RequiresMerge);
            if (result.Succeeded)
            {
                Close(result);
                return;
            }

            Check = result.Check;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Adding a folder failed.", exception);
            Check = new AddFolderCheck(AddFolderOutcome.Error, Path, "The folder couldn't be added: " + exception.Message, []);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunCheckAsync()
    {
        var version = ++_checkVersion;
        Check = null;
        try
        {
            var check = await _folders.CheckAsync(Path, IncludeSubfolders);
            if (version == _checkVersion)
            {
                Check = check;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Checking a folder failed.", exception);
            if (version == _checkVersion)
            {
                Check = new AddFolderCheck(AddFolderOutcome.Error, Path, "The folder couldn't be checked: " + exception.Message, []);
            }
        }
    }
}

public sealed record RemovalChoice(RemovalMode Mode);

public sealed partial class RemoveFolderDialogViewModel : DialogViewModel
{
    public RemoveFolderDialogViewModel(string name, string path, long historyCount)
    {
        Name = name;
        Path = path;
        HistoryCount = historyCount;
    }

    public override string Title => $"Stop watching \"{Name}\"?";

    public string Name { get; }

    public string Path { get; }

    public long HistoryCount { get; }

    public string HistoryText => HistoryCount == 0
        ? "This folder has no recorded activity yet."
        : $"This folder has {Format.Count(HistoryCount, "activity record", "activity records")}.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepHistory), nameof(ConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool DeleteHistory { get; set; }

    public bool KeepHistory
    {
        get => !DeleteHistory;
        set => DeleteHistory = !value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool DeletionConfirmed { get; set; }

    public string ConfirmText => DeleteHistory ? "Stop watching and delete history" : "Stop watching";

    partial void OnDeleteHistoryChanged(bool value)
    {
        if (!value)
        {
            DeletionConfirmed = false;
        }
    }

    private bool CanConfirm() => !DeleteHistory || DeletionConfirmed;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(new RemovalChoice(DeleteHistory ? RemovalMode.DeleteHistory : RemovalMode.KeepHistory));
}

public sealed record ExclusionEdit(IReadOnlyList<ExclusionRuleInput> Rules);

public sealed partial class ExclusionRowViewModel : ViewModelBase
{
    public ExclusionRowViewModel(string pattern, bool isEnabled, bool isBuiltIn)
    {
        Pattern = pattern;
        IsEnabled = isEnabled;
        IsBuiltIn = isBuiltIn;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial string Pattern { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public bool IsBuiltIn { get; }

    public string? Error => ExclusionPattern.Validate(Pattern);

    public bool HasError => Error is not null;

    public string AutomationName => $"Exclusion {Pattern}";
}

public sealed partial class ExclusionsDialogViewModel : DialogViewModel
{
    private readonly string _title;

    public ExclusionsDialogViewModel(string title, string description, IReadOnlyList<ExclusionRule> rules, bool isGlobal, IReadOnlyList<string> inheritedPatterns)
    {
        _title = title;
        Description = description;
        IsGlobal = isGlobal;
        InheritedPatterns = inheritedPatterns;
        foreach (var rule in rules)
        {
            Rules.Add(new ExclusionRowViewModel(rule.Pattern, rule.IsEnabled, rule.IsBuiltIn));
        }
    }

    public override string Title => _title;

    public string Description { get; }

    public bool IsGlobal { get; }

    public IReadOnlyList<string> InheritedPatterns { get; }

    public bool HasInherited => !IsGlobal && InheritedPatterns.Count > 0;

    public string InheritedText => $"Global exclusions from Settings also apply: {string.Join(", ", InheritedPatterns.Take(12))}{(InheritedPatterns.Count > 12 ? ", …" : string.Empty)}";

    public ObservableCollection<ExclusionRowViewModel> Rules { get; } = new();

    public bool IsEmpty => Rules.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewPatternError))]
    [NotifyCanExecuteChangedFor(nameof(AddPatternCommand))]
    public partial string NewPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    public string? NewPatternError => string.IsNullOrWhiteSpace(NewPattern)
        ? null
        : ExclusionPattern.Validate(NewPattern) ?? (IsDuplicate(NewPattern) ? "This pattern is already in the list." : null);

    public string SyntaxHelp =>
        "A name such as node_modules or *.tmp matches at any depth. A path such as build/output or docs/**/*.pdf is relative to the watched folder. * and ? match within a name; ** spans folders. Matching ignores case.";

    private bool CanAddPattern() => !string.IsNullOrWhiteSpace(NewPattern) && NewPatternError is null;

    [RelayCommand(CanExecute = nameof(CanAddPattern))]
    private void AddPattern()
    {
        Rules.Add(new ExclusionRowViewModel(ExclusionPattern.Normalize(NewPattern)!, true, false));
        NewPattern = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void RemoveRule(ExclusionRowViewModel row)
    {
        Rules.Remove(row);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void RestoreSuggested()
    {
        foreach (var pattern in ExclusionPattern.Suggested)
        {
            var existing = Rules.FirstOrDefault(r => string.Equals(ExclusionPattern.Normalize(r.Pattern), pattern, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Rules.Add(new ExclusionRowViewModel(pattern, true, true));
            }
            else
            {
                existing.IsEnabled = true;
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Save()
    {
        var invalid = Rules.FirstOrDefault(r => r.HasError);
        if (invalid is not null)
        {
            SaveError = $"Fix \"{invalid.Pattern}\" first: {invalid.Error}";
            return;
        }

        Close(new ExclusionEdit(Rules.Select(r => new ExclusionRuleInput(r.Pattern, r.IsEnabled, r.IsBuiltIn)).ToList()));
    }

    private bool IsDuplicate(string pattern)
    {
        var normalized = ExclusionPattern.Normalize(pattern);
        return Rules.Any(r => string.Equals(ExclusionPattern.Normalize(r.Pattern), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
