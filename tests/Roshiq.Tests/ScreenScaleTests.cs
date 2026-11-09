using Roshiq.Core.Layout;
using Roshiq.Core.Settings;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.18.3: оверлей и уведомления подстраиваются под разрешение игры (1440p, ultrawide, 4K).</summary>
public class ScreenScaleTests
{
    [Theory]
    [InlineData(1080, 1.0, 1.0)]    // 1920×1080 — как раньше
    [InlineData(1440, 1.0, 1.35)]   // 2560×1440 и 3440×1440 при масштабе Windows 100%
    [InlineData(1440, 1.25, 1.05)]  // 1440p при 125%: Windows уже увеличил, не удваиваем
    [InlineData(2160, 1.5, 1.35)]   // 4K при 150%
    [InlineData(2160, 1.0, 2.0)]    // 4K при 100%
    [InlineData(4320, 1.0, 2.0)]    // больше двух раз не увеличиваем
    [InlineData(768, 1.0, 1.0)]     // маленький экран не уменьшаем
    public void Auto_KeepsTheShareOfScreenLikeOn1080p(int height, double dpi, double expected) =>
        Assert.Equal(expected, ScreenScale.Auto(height, dpi), 3);

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1440, 0.0)]
    [InlineData(1440, double.NaN)]
    public void Auto_BadInput_IsNeutral(int height, double dpi) => Assert.Equal(1.0, ScreenScale.Auto(height, dpi));

    [Fact]
    public void Settings_AutoScaleOnByDefault_ScaleUpTo200Percent_MainWindowUpTo160()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"UiScale\":5,\"Overlay\":{\"Scale\":1.9}}")!.Normalize();
        Assert.True(s.Overlay.AutoScale);
        Assert.Equal(1.9, s.Overlay.Scale);
        Assert.Equal(1.6, s.UiScale);
        var off = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Overlay\":{\"AutoScale\":false,\"Scale\":9}}")!.Normalize();
        Assert.False(off.Overlay.AutoScale);
        Assert.Equal(2.0, off.Overlay.Scale);
    }
}
