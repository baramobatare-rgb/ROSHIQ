using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;

namespace Roshiq.App.Views;

/// <summary>Small, non-activating draft prompt when several ranked roles were queued.</summary>
public sealed class RolePromptWindow : Window
{
    private static RolePromptWindow? _open;

    public static void Open()
    {
        if (_open is { IsVisible: true }) return;
        _open = new RolePromptWindow();
        _open.Show();
    }

    public static void CloseIfOpen()
    {
        if (_open is { IsVisible: true } prompt) prompt.Close();
        _open = null;
    }

    private RolePromptWindow()
    {
        Title = Loc.T("role_prompt_title");
        Width = 490;
        Height = 186;
        MinWidth = 490;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Ui.Res("Bg");
        Foreground = Ui.Res("Text");
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 24);
        Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height - 24);

        var body = new StackPanel { Margin = new Thickness(18, 13, 18, 12) };
        body.Children.Add(new TextBlock { Text = Loc.T("role_prompt_title"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = Loc.T("role_prompt_desc"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 4, 0, 12) });
        var choices = new UniformGrid { Rows = 1, Columns = 5 };
        foreach (var position in Enum.GetValues<Position>())
        {
            var button = new Button { Content = $"{(int)position} · {Loc.T("role_short_" + (int)position)}",
                Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 5, 0),
                Padding = new Thickness(4, 9, 4, 9), FontSize = 11 };
            button.Click += (_, _) => App.Host.SelectPosition(position);
            choices.Children.Add(button);
        }
        body.Children.Add(choices);
        var later = new Button { Content = Loc.T("role_prompt_later"), Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        later.Click += (_, _) => Close();
        body.Children.Add(later);
        Content = body;
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }
}
