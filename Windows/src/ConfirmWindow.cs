using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GigaTTS;

sealed class ConfirmWindow : Window
{
    ConfirmWindow(string title, string message, string confirm)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        var cancel = new Button { Content = "Отмена", MinWidth = 100 };
        var ok = new Button { Content = confirm, MinWidth = 100 };
        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 16 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
    }

    public static Task<bool> Ask(Window owner, string title, string message, string confirm)
        => new ConfirmWindow(title, message, confirm).ShowDialog<bool>(owner);
}
