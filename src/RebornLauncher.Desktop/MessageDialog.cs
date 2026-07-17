using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RebornLauncher.Desktop;

/// <summary>Avalonia ships no message box, so this is the launcher's minimal stand-in.</summary>
internal static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
        };
        ok.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children = { text, ok },
        };

        await dialog.ShowDialog(owner);
    }
}
