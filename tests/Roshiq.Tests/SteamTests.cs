using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Draft;
using Roshiq.GSI;
using Xunit;

namespace Roshiq.Tests;

public class SteamTests
{
    private const string Vdf = """
    "users"
    {
    	"76561198000000001"
    	{
    		"AccountName"		"old_login"
    		"PersonaName"		"Old Nick"
    		"RememberPassword"		"1"
    		"MostRecent"		"0"
    		"Timestamp"		"1600000000"
    	}
    	"76561198000000002"
    	{
    		"AccountName"		"main_login"
    		"PersonaName"		"Ник \"в кавычках\""
    		"MostRecent"		"1"
    		"Timestamp"		"1700000000"
    		"Nested"
    		{
    			"x"		"y"
    		}
    	}
    	"not_an_id"
    	{
    		"AccountName"		"junk"
    	}
    }
    """;

    [Fact]
    public void LoginUsers_ParsesAccounts_MostRecentFirst()
    {
        var users = SteamLoginUsers.Parse(Vdf);
        Assert.Equal(2, users.Count);
        Assert.Equal("76561198000000002", users[0].SteamId64);
        Assert.True(users[0].MostRecent);
        Assert.Equal("Ник \"в кавычках\"", users[0].PersonaName);
        Assert.Equal(39734274L, users[0].AccountId); // 76561198000000002 − 76561197960265728
        Assert.Equal("Old Nick", users[1].Display);
        Assert.Empty(SteamLoginUsers.Parse(""));
    }

    [Fact]
    public void OpenId_BuildsLoginUrl_AndReadsClaimedId()
    {
        var url = SteamOpenId.LoginUrl("http://127.0.0.1:5000/steam", "http://127.0.0.1:5000/");
        Assert.StartsWith("https://steamcommunity.com/openid/login?", url);
        Assert.Contains("openid.mode=checkid_setup", url);
        Assert.Contains("openid.return_to=http%3A%2F%2F127.0.0.1%3A5000%2Fsteam", url);

        var q = SteamOpenId.ParseQuery("openid.mode=id_res&openid.claimed_id=https%3A%2F%2Fsteamcommunity.com%2Fopenid%2Fid%2F76561198000000002");
        Assert.Equal("76561198000000002", SteamOpenId.SteamIdFrom(q));
        // чужой провайдер или отмена — не принимается
        Assert.Null(SteamOpenId.SteamIdFrom(SteamOpenId.ParseQuery("openid.mode=id_res&openid.claimed_id=https%3A%2F%2Fevil.example%2Fopenid%2Fid%2F76561198000000002")));
        Assert.Null(SteamOpenId.SteamIdFrom(SteamOpenId.ParseQuery("openid.mode=cancel")));
    }

    [Theory]
    [InlineData(54, null, "Легенда 4")]
    [InlineData(10, null, "Рекрут")]
    [InlineData(80, 512, "Титан #512")]
    [InlineData(null, null, null)]
    [InlineData(5, null, null)]
    public void Ranks_FromTier(int? tier, int? leaderboard, string? expected) =>
        Assert.Equal(expected, Ranks.Name(tier, leaderboard, Language.Russian));

    [Fact]
    public void HeroStats_SuggestPool_PrefersFrequentWinners()
    {
        var path = Path.Combine(Path.GetTempPath(), "roshiq-heroes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cache = new PlayerHeroStatsCache(path);
            cache.Replace(42, new[]
            {
                new PlayerHeroStat(1, 100, 60, 0),
                new PlayerHeroStat(2, 100, 40, 0),
                new PlayerHeroStat(3, 5, 5, 0),   // мало игр
                new PlayerHeroStat(4, 0, 0, 0)    // не играл — не хранится
            }, DateTime.UtcNow);
            Assert.Equal(new[] { 1, 2 }, cache.SuggestPool(count: 5));
            Assert.Equal(3, new PlayerHeroStatsCache(path).Count);
            Assert.Equal(42, new PlayerHeroStatsCache(path).AccountId);
            cache.Clear();
            Assert.Equal(0, new PlayerHeroStatsCache(path).Count);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Draft_UsesPersonalStats()
    {
        var hero = TestData.Hero("axe");
        var state = DraftState.Empty(Position.Offlane);
        var analyzer = new DraftAnalyzer(TestData.Db);
        double before = analyzer.Evaluate(hero, state).Score;
        analyzer.Personal = new Dictionary<int, (int, int)> { [hero.Id] = (40, 24) };
        var withStats = analyzer.Evaluate(hero, state);
        Assert.True(withStats.Score > before);
        Assert.Contains(withStats.Reasons, r => r.En.Contains("Your hero: 40 games"));

        analyzer.Personal = new Dictionary<int, (int, int)> { [hero.Id] = (3, 3) }; // мало игр — не учитывается
        Assert.Equal(before, analyzer.Evaluate(hero, state).Score);

        analyzer.Personal = new Dictionary<int, (int, int)> { [hero.Id] = (30, 9) };
        Assert.Contains(analyzer.Evaluate(hero, state).Risks, r => r.En.Contains("You win"));
    }

    [Fact]
    public void Gsi_ParsesSteamId()
    {
        const string json = """{ "player": { "steamid": "76561198000000002", "name": "Me" } }""";
        var s = GsiParser.Parse(json, null);
        Assert.Equal("76561198000000002", s.SteamId);
        Assert.Equal(39734274L, SteamIds.ToAccountId(s.SteamId));
    }
}
