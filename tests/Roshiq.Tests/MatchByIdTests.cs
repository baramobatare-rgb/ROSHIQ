using System.Text.Json;
using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.22.1: разбор своего матча по ID или ссылке.</summary>
public class MatchByIdTests
{
    [Theory]
    [InlineData("9086076880", 9086076880L)]
    [InlineData("  9086076880 ", 9086076880L)]
    [InlineData("https://www.dotabuff.com/matches/9086076880", 9086076880L)]
    [InlineData("https://www.opendota.com/matches/9086076880/overview", 9086076880L)]
    [InlineData("https://stratz.com/matches/9086076880?tab=performance", 9086076880L)]
    [InlineData("stratz.com/match/9086076880", 9086076880L)]
    public void Parse_AcceptsIdsAndLinks(string text, long expected) => Assert.Equal(expected, MatchIds.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]                                         // слишком короткий
    [InlineData("https://www.dotabuff.com/players/123456789")]   // это профиль, не матч
    public void Parse_RejectsOtherText(string text) => Assert.Null(MatchIds.Parse(text));

    [Fact]
    public void MatchRow_ForYourAccount()
    {
        const string json = """
        { "match_id": 9086076880, "duration": 2649, "radiant_win": true, "start_time": 1794423431,
          "players": [
            { "account_id": 1092267175, "player_slot": 2, "hero_id": 1, "kills": 8, "deaths": 1, "assists": 8,
              "gold_per_min": 735, "xp_per_min": 890, "last_hits": 420 },
            { "account_id": 5, "player_slot": 128, "hero_id": 2 } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var row = OpenDotaClient.MatchRowFor(doc.RootElement, 1092267175)!;
        Assert.Equal(9086076880, row.MatchId);
        Assert.True(row.Won);
        Assert.Equal(1, row.HeroId);
        Assert.Equal(735, row.GoldPerMin);
        Assert.Null(OpenDotaClient.MatchRowFor(doc.RootElement, 77));
    }
}
