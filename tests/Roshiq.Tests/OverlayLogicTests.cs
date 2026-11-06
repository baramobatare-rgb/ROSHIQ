using Roshiq.Core.Compliance;
using Roshiq.Core.Layout;
using Roshiq.Core.Models;
using Xunit;

namespace Roshiq.Tests;

/// <summary>Логика оверлея проверяется без Windows и без запущенной Dota 2.</summary>
public class OverlayLogicTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly RelRect Panel = new(0.78, 0.22, 0.19, 0.30);

    [Fact]
    public void NoGame_OverlayHidden_UnlessTestMode()
    {
        var d = OverlayVisibility.Decide(GameWindowState.NotFound, OverlayMode.Expanded, Panel, false, false, false, Screen);
        Assert.False(d.Visible);
        Assert.Equal("overlay_game_not_found", d.ReasonKey);

        var t = OverlayVisibility.Decide(GameWindowState.NotFound, OverlayMode.Expanded, Panel, false, false, true, Screen);
        Assert.True(t.Visible);
        Assert.Equal("overlay_test_mode", t.ReasonKey);
    }

    [Fact]
    public void MinimizedGame_HidesOverlay()
    {
        var game = new GameWindowState(true, true, false, new PixelRect(0, 0, 1920, 1080));
        Assert.False(OverlayVisibility.Decide(game, OverlayMode.Minimal, Panel, false, false, false, Screen).Visible);
    }

    [Fact]
    public void HiddenMode_And_UserToggle_HideOverlay()
    {
        var game = new GameWindowState(true, false, true, Screen);
        Assert.False(OverlayVisibility.Decide(game, OverlayMode.Hidden, Panel, false, false, false, Screen).Visible);
        Assert.False(OverlayVisibility.Decide(game, OverlayMode.Expanded, Panel, true, false, false, Screen).Visible);
    }

    [Fact]
    public void Unfocused_HiddenOnlyWhenOptionEnabled()
    {
        var game = new GameWindowState(true, false, false, Screen);
        Assert.True(OverlayVisibility.Decide(game, OverlayMode.Expanded, Panel, false, false, false, Screen).Visible);
        Assert.False(OverlayVisibility.Decide(game, OverlayMode.Expanded, Panel, false, true, false, Screen).Visible);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Bounds_ScaleWithResolution(int w, int h)
    {
        var game = new GameWindowState(true, false, true, new PixelRect(100, 50, w, h));
        var d = OverlayVisibility.Decide(game, OverlayMode.Expanded, Panel, false, false, false, Screen);
        Assert.True(d.Visible);
        Assert.Equal(100 + (int)Math.Round(0.78 * w), d.Bounds.X);
        Assert.Equal(50 + (int)Math.Round(0.22 * h), d.Bounds.Y);
        Assert.Equal((int)Math.Round(0.19 * w), d.Bounds.Width);
        Assert.True(d.Bounds.X + d.Bounds.Width <= 100 + w);
    }

    [Fact]
    public void MinimalMode_IsCompact()
    {
        var game = new GameWindowState(true, false, true, Screen);
        var min = OverlayVisibility.Decide(game, OverlayMode.Minimal, Panel, false, false, false, Screen);
        var exp = OverlayVisibility.Decide(game, OverlayMode.Expanded, Panel, false, false, false, Screen);
        Assert.True(min.Bounds.Height < exp.Bounds.Height);
    }

    [Theory]
    [InlineData(0.0, 0.75, 0.2, 0.2)]   // на мини-карте
    [InlineData(0.4, 0.0, 0.2, 0.1)]    // на верхней панели
    [InlineData(0.4, 0.85, 0.2, 0.1)]   // на нижнем HUD
    [InlineData(2.0, -1.0, 9.0, 9.0)]   // мусорные значения
    public void SafeZone_KeepsPanelOffImportantHud(double x, double y, double w, double h)
    {
        var r = SafeZone.Clamp(new RelRect(x, y, w, h));
        foreach (var zone in SafeZone.Forbidden) Assert.False(r.Intersects(zone), $"{r} пересекает {zone}");
        Assert.InRange(r.X, 0, 1);
        Assert.InRange(r.Right, 0, 1.0000001);
        Assert.InRange(r.Bottom, 0, 1.0000001);
    }

    [Fact]
    public void ToastArea_DoesNotCoverMinimapOrTopBar()
    {
        Assert.False(SafeZone.ToastArea.Intersects(SafeZone.Minimap));
        Assert.False(SafeZone.ToastArea.Intersects(SafeZone.TopBar));
    }

    [Fact]
    public void RelRect_PixelRoundTrip()
    {
        var host = new PixelRect(0, 0, 2560, 1440);
        var px = Panel.ToPixels(host);
        var back = RelRect.FromPixels(px, host);
        Assert.Equal(Panel.X, back.X, 3);
        Assert.Equal(Panel.W, back.W, 3);
    }
}

public class ComplianceTests
{
    [Fact]
    public void StandardContext_AllowsVisibleDraftButNotEnemyItemAdvice()
    {
        Assert.True(ComplianceGate.IsAllowed(Feature.ReferenceBuilds, UsageContext.Reference));
        Assert.True(ComplianceGate.IsAllowed(Feature.ScheduledSupportReminders, UsageContext.Reference));
        Assert.True(ComplianceGate.IsAllowed(Feature.AutoDraftRecognition, UsageContext.Reference));
        Assert.False(ComplianceGate.IsAllowed(Feature.EnemyAwareItemAdvice, UsageContext.Reference));
    }

    [Fact]
    public void TrainingContext_AllowsDraftAndEnemyItemAdvice()
    {
        Assert.True(ComplianceGate.IsAllowed(Feature.AutoDraftRecognition, UsageContext.Training));
        Assert.True(ComplianceGate.IsAllowed(Feature.EnemyAwareItemAdvice, UsageContext.Training));
        Assert.True(ComplianceGate.IsAllowed(Feature.LiveDraftAdviceInPublicMatch, UsageContext.Training));
        Assert.True(ComplianceGate.IsAllowed(Feature.LiveDraftAdviceInPublicMatch, UsageContext.Reference));
    }
}
