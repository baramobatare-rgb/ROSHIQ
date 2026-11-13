using System.Text.Json;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.22.0: ход матча по записи OpenDota — размены, здания, Рошан, повороты по золоту.</summary>
public class MatchTimelineTests
{
    // Вы — Anti-Mage за Dire (слот 130). Ключи и поля — как в реальном ответе /matches/{id}.
    private const string Json = """
    {
      "match_id": 9086076880, "duration": 1500, "radiant_win": true, "version": 22,
      "radiant_gold_adv": [0, 100, 200, 1500, 4800, 5000, 5200, 1000, 900],
      "radiant_xp_adv":   [0,  50, 100,  800, 2000, 2500, 2600,  600, 500],
      "objectives": [
        { "time": 366, "type": "CHAT_MESSAGE_FIRSTBLOOD", "slot": 1 },
        { "time": 622, "type": "building_kill", "key": "npc_dota_badguys_tower1_top", "unit": "npc_dota_hero_largo" },
        { "time": 958, "type": "building_kill", "key": "npc_dota_goodguys_tower2_mid", "unit": "npc_dota_badguys_siege" },
        { "time": 1200, "type": "building_kill", "key": "npc_dota_badguys_melee_rax_bot" },
        { "time": 1400, "type": "building_kill", "key": "npc_dota_badguys_fort" },
        { "time": 1654, "type": "CHAT_MESSAGE_ROSHAN_KILL", "team": 3 }
      ],
      "players": [
        { "account_id": 42, "player_slot": 130, "hero_id": 1,
          "kills_log": [ { "time": 505, "key": "npc_dota_hero_lion" } ] },
        { "account_id": 7, "player_slot": 0, "hero_id": 2,
          "kills_log": [
            { "time": 500, "key": "npc_dota_hero_antimage" },
            { "time": 940, "key": "npc_dota_hero_antimage" },
            { "time": 950, "key": "npc_dota_goodguys_ranged" }
          ] },
        { "account_id": 8, "player_slot": 1, "hero_id": 26, "kills_log": [] }
      ]
    }
    """;

    private static MatchDetails Details()
    {
        using var doc = JsonDocument.Parse(Json);
        return MatchDetails.Parse(doc.RootElement, 42, id => id == 1 ? "npc_dota_hero_antimage" : null)!;
    }

    private static (string, string) Name(int id) => id == 2 ? ("Топорщик", "Axe") : ("?", "?");

    [Fact]
    public void GoldAdvantage_IsFromYourTeamsSide()
    {
        var d = Details();
        Assert.False(d.IsRadiant);
        Assert.Equal(-4800, d.TeamGoldAdv[4]);
        Assert.Equal(-2000, d.TeamXpAdv[4]);
    }

    [Fact]
    public void Deaths_TradeIsDetectedWithinTenSeconds()
    {
        var events = MatchTimeline.Build(Details(), Name);
        var deaths = events.Where(e => e.Kind == "death").ToList();
        Assert.Equal(2, deaths.Count);
        Assert.Equal(0, deaths[0].Severity);              // 500: в 505 вы убили Lion — размен
        Assert.Equal("Смерть от Топорщик — размен: 1 убийство", deaths[0].Ru);
        Assert.Equal(0, deaths[1].Severity);              // 940: убийств нет, но в 958 снесли вышку T2 — размен на сплите
        Assert.Contains("размен: взяли вышку T2 (мид)", deaths[1].Ru);
        Assert.Contains("Axe", deaths[1].En);
        Assert.Equal(0, MatchTimeline.UntradedDeaths(events));
    }

    [Fact]
    public void Buildings_AndRoshan_FromYourSide()
    {
        var events = MatchTimeline.Build(Details(), Name);
        Assert.Contains(events, e => e.Time == 622 && e.Severity == -1 && e.Ru == "Потеряли вышку T1 (верх)");
        Assert.Contains(events, e => e.Time == 958 && e.Severity == 1 && e.Ru == "Снесли вышку T2 (мид)");
        Assert.Contains(events, e => e.Time == 1200 && e.Ru == "Потеряли барак ближнего боя (низ)");
        Assert.Contains(events, e => e.Time == 1400 && e.Ru == "Потеряли трон");
        Assert.Contains(events, e => e.Kind == "roshan" && e.Severity == 1);   // team 3 = Dire = вы
        Assert.DoesNotContain(events, e => e.Time == 366);                      // первая кровь — не здание
    }

    [Fact]
    public void GoldSwings_AtMostOncePerThreeMinutes()
    {
        var swings = MatchTimeline.Build(Details(), Name).Where(e => e.Kind == "swing").ToList();
        // минуты 3–4: −4 600 за 2 минуты (с вашей стороны), минута 7: +4 000 обратно
        Assert.Equal(2, swings.Count);
        Assert.Equal(120, swings[0].Time);                // отрезок 2:00–4:00 — время начала
        Assert.Equal(-1, swings[0].Severity);
        Assert.StartsWith("−4", swings[0].Ru);
        Assert.Contains("за 2 минуты (до 4:00)", swings[0].Ru);
        Assert.Equal(300, swings[1].Time);                // 5:00–7:00
        Assert.Equal(1, swings[1].Severity);
    }

    [Fact]
    public void UntradedDeath_AndSwingCauses()
    {
        // смерть в 200 без убийств и объектов рядом; отрезок 2:00–4:00 — минус золото, причина — эта смерть
        var d = Details();
        var d2 = new MatchDetails
        {
            IsRadiant = d.IsRadiant, TeamGoldAdv = d.TeamGoldAdv, TeamXpAdv = d.TeamXpAdv,
            DeathsBy = new[] { new DeathBy(200, 2) }, HeroKills = Array.Empty<HeroKill>(), Objectives = d.Objectives
        };
        var events = MatchTimeline.Build(d2, Name);
        Assert.Contains(events, e => e.Kind == "death" && e.Severity == -1 && e.Ru == "Смерть от Топорщик — без размена");
        Assert.Contains(events, e => e.Kind == "swing" && e.Ru.Contains("ваших смертей без размена: 1"));
        Assert.Equal(1, MatchTimeline.UntradedDeaths(events));
    }

    [Fact]
    public void Key_ShowsAtMostTwelve_InTimeOrder()
    {
        var many = Enumerable.Range(0, 30).Select(i => new TimelineEvent(i * 60, "death", i % 2 == 0 ? -1 : 0, "x", "x")).ToList();
        var key = MatchTimeline.Key(many);
        Assert.Equal(MatchTimeline.KeyEvents, key.Count);
        Assert.All(key, e => Assert.Equal(-1, e.Severity));   // размененные смерти — только в «Показать всё»
        Assert.Equal(key.OrderBy(e => e.Time).Select(e => e.Time), key.Select(e => e.Time));
    }
}
