using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FolderTrail.App.Features.Diagnostics;
using FolderTrail.App.Features.Onboarding;
using FolderTrail.App.Features.Timeline;
using FolderTrail.App.Features.WatchedFolders;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Ui;

/// <summary>
/// End-to-end tests of the real window. They run on Avalonia's headless platform with Skia rendering, against the
/// real composition root, database, pipeline and FileSystemWatcher; only pickers, clipboard and shell are faked.
/// </summary>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task First_launch_explains_what_is_recorded_and_watches_nothing()
    {
        await using var ui = await UiHarness.StartAsync();

        Assert.True(ui.Shell.IsOnboardingVisible);
        Assert.False(ui.Shell.IsMainEnabled);
        Assert.Empty(ui.Shell.Folders.Folders);
        Assert.Empty(ui.Get<MonitoringCoordinator>().GetStatuses());
        var onboarding = ui.Window.GetVisualDescendants().OfType<OnboardingView>().Single();
        Assert.True(onboarding.IsEffectivelyVisible);
        var text = string.Join(" ", onboarding.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("What is recorded", text, StringComparison.Ordinal);
        Assert.Contains("What is not recorded", text, StringComparison.Ordinal);
        Assert.Contains("can't restore files", text, StringComparison.Ordinal);

        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);

        Assert.False(ui.Shell.IsOnboardingVisible);
        Assert.Equal("No folders are being watched", ui.Shell.Timeline.EmptyTitle);
        Assert.True((await ui.Get<SettingsService>().LoadAsync(TestContext.Current.CancellationToken)).OnboardingCompleted);
    }

    [AvaloniaFact]
    public async Task Adding_a_folder_from_onboarding_records_live_changes_end_to_end()
    {
        using var watched = new TempDirectory();
        watched.CreateFile("existing.txt");
        watched.CreateFile(@"docs\notes.md");
        await using var ui = await UiHarness.StartAsync();
        ui.Picker.NextPath = watched.Path;

        var choosing = ui.Shell.Onboarding.ChooseFolderCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => ui.Shell.Dialogs.Current is AddFolderDialogViewModel { Check: not null }, "the add-folder dialog has checked the folder");
        var dialog = (AddFolderDialogViewModel)ui.Shell.Dialogs.Current!;
        Assert.Equal(AddFolderOutcome.Ok, dialog.Check!.Outcome);
        Assert.True(dialog.IncludeSubfolders);
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await choosing;

        Assert.False(ui.Shell.IsOnboardingVisible);
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the folder is watched");
        var folder = ui.Shell.Folders.Folders.Single();
        Assert.Same(folder, ui.Shell.Folders.SelectedFolder);
        Assert.Equal("3 items indexed", folder.BaselineText);
        await UiWait.SettleAsync();
        Assert.Empty(ui.Shell.Timeline.Rows);
        Assert.Equal("No activity recorded in this folder yet", ui.Shell.Timeline.EmptyTitle);
        Assert.False(ui.Shell.Timeline.ShowClearFilters);

        var report = watched.CreateFile("report.txt", "first draft");
        await UiWait.UntilAsync(() => FindRow(ui, "report.txt", ActivityType.Created) is not null, "the new file appears in the timeline");
        var created = FindRow(ui, "report.txt", ActivityType.Created)!;
        Assert.Equal("Live", created.SourceText);
        Assert.IsType<DayHeaderRow>(ui.Shell.Timeline.Rows[0]);

        ui.Shell.Timeline.SelectedRow = created;
        var details = ui.Shell.Timeline.Details;
        await UiWait.UntilAsync(() => details.ItemExists == true, "the details panel confirms the file exists");
        Assert.Equal(report, details.FullPath);
        Assert.Equal(folder.DisplayPath, details.WatchedFolder);
        Assert.True(details.OpenCommand.CanExecute(null));
        details.ShowInExplorerCommand.Execute(null);
        Assert.Equal(report, Assert.Single(ui.ShellLauncher.Revealed));
        await details.CopyPathCommand.ExecuteAsync(null);
        Assert.Equal(report, ui.Clipboard.Text);

        File.Delete(report);
        await UiWait.UntilAsync(() => FindRow(ui, "report.txt", ActivityType.Deleted) is not null, "the deletion appears in the timeline");
        ui.Shell.Timeline.SelectedRow = FindRow(ui, "report.txt", ActivityType.Deleted);
        await UiWait.UntilAsync(() => details.ItemExists == false, "the details panel sees the file is gone");
        Assert.False(details.OpenCommand.CanExecute(null));
        Assert.False(details.ShowInExplorerCommand.CanExecute(null));
        Assert.Contains("no longer exists", details.AvailabilityNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Search_filters_and_csv_export_work_on_the_filtered_results()
    {
        using var watched = new TempDirectory();
        await using var ui = await UiHarness.StartAsync();
        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);
        var root = (await ui.Get<WatchedFolderService>().AddAsync(watched.Path, true, false, TestContext.Current.CancellationToken)).Root!;
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the folder is watched");

        watched.CreateFile("alpha.txt");
        watched.CreateFile("beta.txt");
        watched.CreateFile("Ünïcödé, report.txt");
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Count(r => r.IsCreated) == 3, "three creations are shown");

        ui.Shell.Timeline.SearchText = "ALPHA";
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().All(r => r.Name == "alpha.txt") && ui.Shell.Timeline.Rows.OfType<EventRow>().Any(), "search narrows the timeline");
        Assert.True(ui.Shell.Timeline.ShowClearFilters);

        ui.Shell.Timeline.SearchText = "ünïcödé";
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Any(r => r.Name.StartsWith("Ünïcödé", StringComparison.Ordinal)) && ui.Shell.Timeline.Rows.OfType<EventRow>().All(r => r.Name.StartsWith("Ünïcödé", StringComparison.Ordinal)), "unicode search is case-insensitive");

        var csv = Path.Combine(ui.Data.Path, "export.csv");
        ui.SavePicker.TargetPath = csv;
        await ui.Shell.Timeline.ExportCsvCommand.ExecuteAsync(null);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("csv", ui.SavePicker.LastRequest!.Extension);
        Assert.StartsWith("\"Detected (UTC, ISO 8601)\"", lines[0].TrimStart('﻿'), StringComparison.Ordinal);
        Assert.All(lines.Skip(1), l => Assert.Contains("\"Ünïcödé, report.txt\"", l, StringComparison.Ordinal));
        Assert.Contains(ui.Notifier.Notices, n => n.Title == "Export complete");

        ui.Shell.Timeline.ClearFiltersCommand.Execute(null);
        ui.Shell.Timeline.ShowDeleted = true;
        await UiWait.UntilAsync(() => !ui.Shell.Timeline.IsLoading && ui.Shell.Timeline.Rows.Count == 0, "no deletions match");
        Assert.Equal("No matching activity", ui.Shell.Timeline.EmptyTitle);

        ui.Shell.Timeline.ShowDeleted = false;
        ui.Shell.Timeline.SelectedSource = ui.Shell.Timeline.SourceOptions.Single(o => o.Value == ActivitySource.Live);
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Count(r => r.IsCreated) == 3, "live filter keeps the live events");
        Assert.Equal(root.Id, ui.Shell.Timeline.Rows.OfType<EventRow>().First().Entry.RootId);
    }

    [AvaloniaFact]
    public async Task Removing_a_folder_and_deleting_its_history_requires_confirmation()
    {
        using var watched = new TempDirectory();
        await using var ui = await UiHarness.StartAsync();
        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);
        await ui.Get<WatchedFolderService>().AddAsync(watched.Path, true, false, TestContext.Current.CancellationToken);
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the folder is watched");
        watched.CreateFile("history.txt");
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Any(), "there is history");

        var folder = ui.Shell.Folders.Folders.Single();
        var removing = folder.RemoveCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => ui.Shell.Dialogs.Current is RemoveFolderDialogViewModel, "the removal dialog opens");
        var dialog = (RemoveFolderDialogViewModel)ui.Shell.Dialogs.Current!;
        Assert.True(dialog.KeepHistory);
        Assert.Contains("1 activity record", dialog.HistoryText, StringComparison.Ordinal);
        dialog.DeleteHistory = true;
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        dialog.DeletionConfirmed = true;
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        dialog.ConfirmCommand.Execute(null);
        await removing;

        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.Count == 0, "the folder leaves the sidebar");
        await UiWait.UntilAsync(() => !ui.Shell.Timeline.IsLoading && ui.Shell.Timeline.Rows.Count == 0, "its history is gone");
        Assert.True(Directory.Exists(watched.Path));
        Assert.True(File.Exists(watched.Combine("history.txt")));
    }

    [AvaloniaFact]
    public async Task Keyboard_escape_cancels_a_dialog_and_ctrl_f_focuses_search()
    {
        using var watched = new TempDirectory();
        await using var ui = await UiHarness.StartAsync();
        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);
        ui.Picker.NextPath = watched.Path;

        var adding = ui.Shell.AddFolderCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => ui.Shell.Dialogs.IsOpen, "the dialog opens");
        Assert.False(ui.Shell.IsMainEnabled);
        Press(ui, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        await adding;

        Assert.False(ui.Shell.Dialogs.IsOpen);
        Assert.Empty(await ui.Get<WatchedFolderService>().GetActiveAsync(TestContext.Current.CancellationToken));

        Press(ui, Key.F, RawInputModifiers.Control, PhysicalKey.F);
        await UiWait.UntilAsync(() => ui.Window.FocusManager?.GetFocusedElement() is TextBox { Name: "SearchBox" }, "the search box has focus");

        Press(ui, Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma);
        await UiWait.UntilAsync(() => ui.Shell.IsSettingsPage, "Ctrl+, opens settings");
        Press(ui, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        await UiWait.UntilAsync(() => ui.Shell.IsActivityPage, "Escape returns to activity");
    }

    [AvaloniaFact]
    public async Task A_folder_that_disappears_is_shown_as_missing_and_recovers()
    {
        using var parent = new TempDirectory();
        var watched = parent.CreateDirectory("usb-backup");
        await using var ui = await UiHarness.StartAsync();
        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);
        await ui.Get<WatchedFolderService>().AddAsync(watched, true, false, TestContext.Current.CancellationToken);
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the folder is watched");

        Directory.Delete(watched);
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.Single().State == RootState.Missing, "the folder is reported missing");
        var folder = ui.Shell.Folders.Folders.Single();
        Assert.True(folder.NeedsRetry);
        Assert.Contains("moved, renamed, or deleted", folder.Message, StringComparison.Ordinal);
        Assert.Contains(ui.Notifier.Notices, n => n.Kind == App.Shared.Platform.NoticeKind.Warning && n.Title.Contains("missing", StringComparison.Ordinal));
        Assert.False(Directory.Exists(watched));

        Directory.CreateDirectory(watched);
        folder.RetryCommand.Execute(null);
        await UiWait.UntilAsync(() => folder.State == RootState.Watching, "the folder is watched again");
    }

    [AvaloniaFact]
    public async Task Every_page_and_dialog_renders_in_light_and_dark_without_binding_errors()
    {
        using var watched = new TempDirectory();
        watched.CreateFile("seed.txt");
        using var errors = new BindingErrorSink();
        await using var ui = await UiHarness.StartAsync();
        var screenshots = Environment.GetEnvironmentVariable("FOLDERTRAIL_SCREENSHOT_DIR");

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            ui.Shell.IsOnboardingVisible = true;
            await CaptureAsync(ui, "onboarding", theme, screenshots);
            await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);

            if (!ui.Shell.Folders.HasFolders)
            {
                await ui.Get<WatchedFolderService>().AddAsync(watched.Path, true, false, TestContext.Current.CancellationToken);
                await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the folder is watched");
                watched.CreateFile("draft.txt");
                await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Any(), "an event is shown");
            }

            ui.Shell.Folders.SelectedFolder = ui.Shell.Folders.Folders.Single();
            await UiWait.UntilAsync(() => !ui.Shell.Timeline.IsLoading && ui.Shell.Timeline.Rows.OfType<EventRow>().Any(), "the timeline has reloaded");
            ui.Shell.Timeline.SelectedRow = ui.Shell.Timeline.Rows.OfType<EventRow>().First();
            await UiWait.UntilAsync(() => ui.Shell.Timeline.Details.ItemExists is not null, "details are loaded");
            await CaptureAsync(ui, "activity", theme, screenshots);

            ui.Shell.Folders.SelectedFolder = null;
            await CaptureAsync(ui, "all-activity", theme, screenshots);

            ui.Shell.ShowSettingsCommand.Execute(null);
            await CaptureAsync(ui, "settings", theme, screenshots);

            await ui.Shell.ShowDiagnosticsCommand.ExecuteAsync(null);
            await CaptureAsync(ui, "diagnostics", theme, screenshots);
            ui.Shell.ShowActivityCommand.Execute(null);

            var folder = ui.Shell.Folders.Folders.Single();
            await OpenDialogAndCaptureAsync(ui, () => folder.EditExclusionsCommand.ExecuteAsync(null), "exclusions", theme, screenshots);
            await OpenDialogAndCaptureAsync(ui, () => folder.RemoveCommand.ExecuteAsync(null), "remove", theme, screenshots);
            await OpenDialogAndCaptureAsync(ui, () => ui.Shell.SettingsPage.EditGlobalExclusionsCommand.ExecuteAsync(null), "global-exclusions", theme, screenshots);
            await OpenDialogAndCaptureAsync(ui, () => ui.Shell.SettingsPage.DeleteAllDataCommand.ExecuteAsync(null), "delete-all", theme, screenshots);
            await OpenDialogAndCaptureAsync(ui, () => ui.Shell.Diagnostics.ExportCommand.ExecuteAsync(null), "export-diagnostics", theme, screenshots);
            ui.Picker.NextPath = watched.Path;
            await OpenDialogAndCaptureAsync(ui, () => ui.Shell.Folders.AddFolderInteractiveAsync(), "add-duplicate", theme, screenshots);
        }

        Assert.True(errors.Errors.IsEmpty, "Binding errors:\n" + string.Join("\n", errors.Errors.Distinct()));
    }

    /// <summary>Generates the README screenshots from real activity. Runs only when FOLDERTRAIL_DEMO_DIR is set.</summary>
    [AvaloniaFact]
    public async Task Readme_screenshots_from_real_activity()
    {
        var demoRoot = Environment.GetEnvironmentVariable("FOLDERTRAIL_DEMO_DIR");
        if (string.IsNullOrEmpty(demoRoot))
        {
            Assert.Skip("Set FOLDERTRAIL_DEMO_DIR to generate README screenshots.");
        }

        var projects = Path.Combine(demoRoot, "Projects");
        if (Directory.Exists(projects))
        {
            Directory.Delete(projects, recursive: true);
        }

        void Write(string relative, string content)
        {
            var path = Path.Combine(projects, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Write("Quarterly report.docx", new string('r', 48_000));
        Write("Budget 2026.xlsx", new string('b', 21_500));
        Write("notes.md", "# Notes");
        Write(@"Design\old-logo.png", new string('p', 9_800));
        Write(@"Design\palette.ase", new string('c', 1_200));

        await using var ui = await UiHarness.StartAsync();
        await ui.Shell.Onboarding.SkipCommand.ExecuteAsync(null);
        await ui.Get<WatchedFolderService>().AddAsync(projects, true, false, TestContext.Current.CancellationToken);
        await UiWait.UntilAsync(() => ui.Shell.Folders.Folders.SingleOrDefault()?.State == RootState.Watching, "the demo folder is watched");
        var folder = ui.Shell.Folders.Folders.Single();

        await folder.PauseCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => folder.State == RootState.Paused, "the folder is paused");
        File.AppendAllText(Path.Combine(projects, "Quarterly report.docx"), new string('x', 3_000));
        await folder.ResumeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Any(r => r.IsInferred), "the resume scan finds the change made while paused");

        async Task Step(Action action, Func<EventRow, bool> expected, string because)
        {
            action();
            await UiWait.UntilAsync(() => ui.Shell.Timeline.Rows.OfType<EventRow>().Any(expected), because);
            await Task.Delay(1_200);
        }

        await Step(() => Write("Proposal draft.docx", new string('d', 14_200)), r => r.Name == "Proposal draft.docx" && r.IsCreated, "the proposal is created");
        await Step(() => File.AppendAllText(Path.Combine(projects, "Budget 2026.xlsx"), new string('b', 2_048)), r => r.Name == "Budget 2026.xlsx" && r.IsModified, "the budget is modified");
        await Step(() => File.Move(Path.Combine(projects, "notes.md"), Path.Combine(projects, "Meeting notes.md")), r => r.Name == "Meeting notes.md" && r.IsRenamed, "the notes are renamed");
        await Step(() => File.Delete(Path.Combine(projects, @"Design\old-logo.png")), r => r.Name == "old-logo.png" && r.IsDeleted, "the old logo is deleted");
        await Step(() => Write(@"Invoices\INV-2026-014.pdf", new string('i', 31_000)), r => r.Name == "INV-2026-014.pdf", "the invoice is created");
        await Step(() => File.Move(Path.Combine(projects, "Proposal draft.docx"), Path.Combine(projects, "Proposal v2.docx")), r => r.Name == "Proposal v2.docx" && r.IsRenamed, "the proposal is renamed");

        ui.Shell.Timeline.SelectedRow = ui.Shell.Timeline.Rows.OfType<EventRow>().First(r => r.Name == "Proposal v2.docx");
        await UiWait.UntilAsync(() => ui.Shell.Timeline.Details.ItemExists == true, "details are loaded");

        var output = Path.Combine(demoRoot, "screenshots");
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            await CaptureAsync(ui, "foldertrail", theme, output);
        }

        await folder.PauseCommand.ExecuteAsync(null);
    }

    private static EventRow? FindRow(UiHarness ui, string name, ActivityType type) =>
        ui.Shell.Timeline.Rows.OfType<EventRow>().FirstOrDefault(r => r.Name == name && r.Entry.Type == type);

    private static void Press(UiHarness ui, Key key, RawInputModifiers modifiers, PhysicalKey physical)
    {
        ui.Window.KeyPress(key, modifiers, physical, null);
        ui.Window.KeyRelease(key, modifiers, physical, null);
    }

    private static async Task CaptureAsync(UiHarness ui, string name, ThemeVariant theme, string? directory)
    {
        await UiWait.SettleAsync();
        var frame = ui.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, $"{name}-{theme.Key.ToString()!.ToLowerInvariant()}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    private static async Task OpenDialogAndCaptureAsync(UiHarness ui, Func<Task> open, string name, ThemeVariant theme, string? directory)
    {
        var task = open();
        await UiWait.UntilAsync(() => ui.Shell.Dialogs.IsOpen, $"the {name} dialog opens");
        await UiWait.SettleAsync(400);
        await CaptureAsync(ui, "dialog-" + name, theme, directory);
        ui.Shell.Dialogs.CancelCurrentCommand.Execute(null);
        await task;
        Assert.False(ui.Shell.Dialogs.IsOpen);
    }
}
