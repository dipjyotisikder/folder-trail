using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FolderTrail.App.Shell;

/// <summary>Shown instead of the main window when the database can't be opened, so nothing is silently lost.</summary>
public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(string message, string dataDirectory)
    {
        Title = "FolderTrail can't start";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var openFolder = new Button { Content = "Open data folder" };
        openFolder.Click += (_, _) =>
        {
            if (Directory.Exists(dataDirectory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dataDirectory }, UseShellExecute = false })?.Dispose();
            }
        };
        var quit = new Button { Content = "Quit", IsDefault = true, Classes = { "accent" } };
        quit.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "FolderTrail couldn't open its data", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"Your data is stored in {dataDirectory}. Nothing was changed or deleted. If the database was created by a newer version of FolderTrail, install that version.",
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "secondary" },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { openFolder, quit },
                },
            },
        };
    }
}
