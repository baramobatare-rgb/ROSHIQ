using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Roshiq.GSI;
using Xunit;

namespace Roshiq.Tests;

public class GsiTests
{
    // Пример реального по структуре пакета GSI (поля как в документации Valve / сообществе)
    private const string Sample = """
    {
      "provider": { "name": "Dota 2", "appid": 570, "version": 47, "timestamp": 1730000000 },
      "map": { "name": "start", "matchid": "7999999999", "game_time": 615, "clock_time": 540,
               "daytime": true, "nightstalker_night": false, "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS",
               "paused": false, "win_team": "none", "customgamename": "" },
      "player": { "steamid": "7656119", "name": "player", "activity": "playing", "kills": 1, "deaths": 0,
                  "assists": 3, "gold": 812, "team_name": "dire" },
      "hero": { "id": 2, "name": "npc_dota_hero_axe", "level": 9, "alive": true },
      "items": {
        "slot0": { "name": "item_blink", "purchaser": 0 },
        "slot1": { "name": "empty" },
        "slot2": { "name": "item_phase_boots" },
        "stash0": { "name": "item_tango" },
        "teleport0": { "name": "item_tpscroll" }
      },
      "auth": { "token": "secret" }
    }
    """;

    [Fact]
    public void Parse_ReadsOfficialFields()
    {
        var s = GsiParser.Parse(Sample, "secret");
        Assert.True(s.AuthValid);
        Assert.Equal(2, s.HeroId);
        Assert.Equal("npc_dota_hero_axe", s.HeroName);
        Assert.Equal(9, s.HeroLevel);
        Assert.Equal(540, s.ClockTime);
        Assert.Equal("dire", s.TeamName);
        Assert.True(s.IsInProgress);
        Assert.False(s.IsHeroSelection);
        Assert.Contains("item_blink", s.InventoryItems);
        Assert.Contains("item_tpscroll", s.InventoryItems);
        Assert.DoesNotContain("item_tango", s.InventoryItems); // тайник не считается инвентарём
        Assert.DoesNotContain("empty", s.InventoryItems);
        Assert.Equal(2, TestData.Db.FindHero(s.HeroName)?.Id);
    }

    [Fact]
    public void Parse_MissingSections_AreNull()
    {
        var s = GsiParser.Parse("""{ "provider": { "name": "Dota 2" }, "map": { "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION" } }""", null);
        Assert.True(s.AuthValid);
        Assert.True(s.IsHeroSelection);
        Assert.Null(s.HeroId);
        Assert.Null(s.ClockTime);
        Assert.Empty(s.InventoryItems);
        Assert.False(s.HasHero);
    }

    [Fact]
    public void Parse_WrongToken_IsNotValid()
    {
        Assert.False(GsiParser.Parse(Sample, "other").AuthValid);
    }

    [Fact]
    public void Parse_WrongTypes_DoNotThrow()
    {
        var s = GsiParser.Parse("""{ "map": { "clock_time": "12", "paused": "yes" }, "hero": "oops", "items": [] }""", null);
        Assert.Equal(12, s.ClockTime);
        Assert.Null(s.Paused);
        Assert.Null(s.HeroId);
    }

    [Fact]
    public void Server_RejectsBadJsonAndToken()
    {
        var server = new GsiServer(0, "secret");
        Assert.Null(server.ProcessBody("not json"));
        Assert.Null(server.ProcessBody(Sample.Replace("\"secret\"", "\"bad\"")));
        Assert.Equal(2, server.PacketsRejected);
        Assert.NotNull(server.ProcessBody(Sample));
        Assert.Equal(1, server.PacketsReceived);
    }

    [Fact]
    public async Task Server_AcceptsHttpPostOnLoopback()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        using var server = new GsiServer(port, "secret");
        var tcs = new TaskCompletionSource<GameStateSnapshot>();
        server.SnapshotReceived += (_, s) => tcs.TrySetResult(s);
        server.Start();
        Assert.True(server.IsRunning);

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var resp = await http.PostAsync($"http://127.0.0.1:{port}/", new StringContent(Sample, Encoding.UTF8, "application/json"));
        Assert.True(resp.IsSuccessStatusCode);
        var snap = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, snap.HeroId);
    }

    [Fact]
    public void Config_ContainsPortTokenAndOnlyNeededSections()
    {
        var cfg = GsiConfigWriter.BuildContent(53742, "abc123");
        Assert.Contains("http://127.0.0.1:53742/", cfg);
        Assert.Contains("\"token\"         \"abc123\"", cfg);
        Assert.Contains("\"abilities\"     \"0\"", cfg);
        Assert.EndsWith(Path.Combine("cfg", "gamestate_integration", GsiConfigWriter.FileName), GsiConfigWriter.TargetPath("X"));
    }

    [Fact]
    public void LibraryFolders_AreParsed()
    {
        var vdf = "\"libraryfolders\"\n{\n \"0\"\n {\n  \"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n }\n \"1\"\n {\n  \"path\"\t\t\"D:\\\\SteamLibrary\"\n }\n}";
        var libs = SteamLocator.ParseLibraryFolders(vdf);
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }
}
