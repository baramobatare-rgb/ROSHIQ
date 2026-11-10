using Roshiq.Core.Diagnostics;
using Xunit;

namespace Roshiq.Tests;

public class ConnectionHealthTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DistinguishesWaitingConnectedAndMissingData()
    {
        Assert.Equal(ConnectionState.Waiting, ConnectionHealth.Evaluate(true, true, true, null, Now.AddSeconds(-20), GsiProblem.None, Now));
        Assert.Equal(ConnectionState.Connected, ConnectionHealth.Evaluate(true, true, true, Now.AddSeconds(-5), Now.AddMinutes(-3), GsiProblem.None, Now));
        Assert.Equal(ConnectionState.NoData, ConnectionHealth.Evaluate(true, true, true, Now.AddMinutes(-2), Now.AddMinutes(-3), GsiProblem.None, Now));
    }

    [Fact]
    public void ReportsSpecificFailures()
    {
        Assert.Equal(ConnectionState.Disabled, ConnectionHealth.Evaluate(false, false, true, null, null, GsiProblem.None, Now));
        Assert.Equal(ConnectionState.ServerError, ConnectionHealth.Evaluate(true, false, true, null, null, GsiProblem.None, Now));
        Assert.Equal(ConnectionState.BadToken, ConnectionHealth.Evaluate(true, true, true, null, Now.AddMinutes(-2), GsiProblem.BadToken, Now));
        Assert.Equal(ConnectionState.Offline, ConnectionHealth.Evaluate(true, true, false, null, null, GsiProblem.None, Now));
    }

    [Fact]
    public void FreshPacketsMeanConnectedEvenWhenWindowIsNotFound()
    {
        // окно не нашлось (другой процесс, лаунчер), но данные идут — не пишем «Dota 2 не запущена»
        Assert.Equal(ConnectionState.Connected, ConnectionHealth.Evaluate(true, true, false, Now.AddSeconds(-3), null, GsiProblem.None, Now));
        // игру закрыли — через 15 секунд без пакетов статус честно «не запущена»
        Assert.Equal(ConnectionState.Offline, ConnectionHealth.Evaluate(true, true, false, Now.AddSeconds(-20), null, GsiProblem.None, Now));
    }

    [Fact]
    public void HeartbeatInMenuKeepsConnectionAlive()
    {
        // в меню Dota шлёт пакет раз в 30 секунд — это не «данных нет»
        Assert.Equal(ConnectionState.Connected, ConnectionHealth.Evaluate(true, true, true, Now.AddSeconds(-31), Now.AddMinutes(-10), GsiProblem.None, Now));
    }

    [Theory]
    [InlineData(null, false, false, TipSilence.Menu)]
    [InlineData("DOTA_GAMERULES_STATE_HERO_SELECTION", false, false, TipSilence.Draft)]
    [InlineData("DOTA_GAMERULES_STATE_STRATEGY_TIME", false, false, TipSilence.Draft)]
    [InlineData("DOTA_GAMERULES_STATE_PRE_GAME", false, false, TipSilence.None)]
    [InlineData("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, false, TipSilence.None)]
    [InlineData("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", true, false, TipSilence.Paused)]
    [InlineData("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, true, TipSilence.OverlayHidden)]
    [InlineData("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", true, true, TipSilence.OverlayHidden)]
    [InlineData("DOTA_GAMERULES_STATE_POST_GAME", false, false, TipSilence.PostGame)]
    public void ExplainsWhyTipsAreSilent(string? gameState, bool paused, bool hidden, TipSilence expected)
    {
        Assert.Equal(expected, ConnectionHealth.Silence(ConnectionState.Connected, gameState, paused, hidden));
    }

    [Theory]
    [InlineData(ConnectionState.Disabled)]
    [InlineData(ConnectionState.ServerError)]
    [InlineData(ConnectionState.Offline)]
    [InlineData(ConnectionState.Waiting)]
    [InlineData(ConnectionState.NoData)]
    [InlineData(ConnectionState.BadToken)]
    public void WithoutConnectionTheReasonIsTheConnection(ConnectionState state)
    {
        Assert.Equal(TipSilence.NoConnection, ConnectionHealth.Silence(state, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, false));
    }

    [Fact]
    public void EveryStateHasTexts()
    {
        var L = new Roshiq.Core.Localization.Loc();
        foreach (var lang in new[] { Roshiq.Core.Models.Language.Russian, Roshiq.Core.Models.Language.English })
        {
            L.Language = lang;
            foreach (var st in Enum.GetValues<ConnectionState>())
            {
                var k = "conn_" + st.ToString().ToLowerInvariant();
                Assert.NotEqual(k + "_title", L.Get(k + "_title"));
                Assert.NotEqual(k + "_detail", L.Get(k + "_detail"));
            }
            foreach (var sl in Enum.GetValues<TipSilence>())
            {
                var k = "conn_silence_" + sl.ToString().ToLowerInvariant();
                Assert.NotEqual(k, L.Get(k));
            }
        }
    }
}
