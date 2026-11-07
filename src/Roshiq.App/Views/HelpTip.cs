using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Roshiq.App.Views;

/// <summary>Маленький значок «?» с подсказкой при наведении — для сложных настроек и терминов.</summary>
public sealed class HelpTip : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HelpTip), new PropertyMetadata("", (d, _) => ((HelpTip)d).UpdateTip()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public HelpTip()
    {
        Width = 16;
        Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        SetResourceReference(BorderBrushProperty, "LineStrong");
        SetResourceReference(BackgroundProperty, "Panel3");
        var q = new TextBlock
        {
            Text = "?", FontSize = 10.5, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        q.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Child = q;
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTipService.SetShowDuration(this, 60000);
    }

    private void UpdateTip() =>
        ToolTip = string.IsNullOrWhiteSpace(Text) ? null : new ToolTip
        {
            Content = new TextBlock { Text = Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }
        };
}
