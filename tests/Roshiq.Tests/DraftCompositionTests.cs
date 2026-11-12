using Roshiq.Core.Models;
using Roshiq.Draft;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21.3: оценка составов целиком по признакам героев.</summary>
public class DraftCompositionTests
{
    private static List<Hero> T(params string[] keys) => keys.Select(TestData.Hero).ToList();

    [Fact]
    public void AllMagicTeam_IsFlaggedAsRisk()
    {
        var lines = DraftComposition.Assess(T("crystal_maiden", "zuus", "lion", "skywrath_mage", "leshrac"), new List<Hero>());
        Assert.Equal("our_magic", lines[0].Key);
        Assert.Equal(-1, lines[0].Severity);
    }

    [Fact]
    public void PhysicalEnemiesWithoutInitiator_GiveItemAdviceAndAnOpening()
    {
        var lines = DraftComposition.Assess(new List<Hero>(), T("juggernaut", "phantom_assassin", "sven", "troll_warlord", "drow_ranger"));
        // герои, из-за которых вывод, названы прямо в строке
        Assert.Contains(lines, l => l.Key == "enemy_physical" && l.Ru.Contains("Crimson Guard") && l.Ru.Contains("Juggernaut"));
        Assert.Contains(lines, l => l.Key == "enemy_no_init" && l.Severity == 0);
        // враги сильнее поздно → один план «давить рано», без дубля
        Assert.Single(lines, l => l.Key.StartsWith("plan_"));
        Assert.Contains(lines, l => l.Key == "plan_early");
    }

    [Fact]
    public void IllusionEnemies_AskForAreaDamage_RisksFirst()
    {
        var lines = DraftComposition.Assess(T("axe", "tidehunter", "enigma", "lina"), T("phantom_lancer", "naga_siren", "chaos_knight", "spectre"));
        Assert.Contains(lines, l => l.Key == "enemy_units" && l.En.Contains("Phantom Lancer"));
        Assert.Contains(lines, l => l.Key == "our_teamfight");
        // риски раньше планов, планы раньше сильных сторон
        var order = lines.Select(l => l.Severity < 0 ? 0 : l.Severity == 0 ? 1 : 2).ToList();
        Assert.Equal(order.OrderBy(x => x), order);
        Assert.True(lines.Count <= DraftComposition.MaxLines);
    }

    [Fact]
    public void ContradictoryTempo_GivesNoPlan()
    {
        // свои — на раннюю игру (Axe, Huskar), враги — тоже сильны рано: сигналы противоречат друг другу
        var lines = DraftComposition.Assess(T("axe", "huskar", "lion", "tidehunter"), T("axe", "huskar", "lina", "lion"));
        Assert.DoesNotContain(lines, l => l.Key.StartsWith("plan_"));
    }

    [Fact]
    public void TooFewHeroes_NoConclusions()
    {
        Assert.Empty(DraftComposition.Assess(T("axe", "lina", "lion"), T("juggernaut", "sven")));
    }
}
