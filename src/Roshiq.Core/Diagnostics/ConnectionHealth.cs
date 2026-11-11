namespace Roshiq.Core.Diagnostics;

public enum ConnectionState { Disabled, ServerError, Offline, Waiting, NoData, BadToken, Connected }

/// <summary>Почему подсказки сейчас молчат, хотя связь с игрой есть (или её нет).</summary>
public enum TipSilence
{
    /// <summary>Идёт матч, подсказки показываются.</summary>
    None,
    /// <summary>Данных от игры нет — причину называет <see cref="ConnectionState"/>.</summary>
    NoConnection,
    /// <summary>Игрок в меню: матча нет.</summary>
    Menu,
    /// <summary>Выбор героев: работают подсказки драфта, игровые начнутся с матчем.</summary>
    Draft,
    /// <summary>Матч на паузе.</summary>
    Paused,
    /// <summary>Панель скрыта клавишей — уведомления поверх игры не показываются.</summary>
    OverlayHidden,
    /// <summary>Матч закончился.</summary>
    PostGame
}

/// <summary>What the player can conclude from the game window, local GSI server and recent packets.</summary>
public static class ConnectionHealth
{
    /// <summary>Пакет считается свежим: heartbeat GSI — 30 с, с запасом.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(45);
    /// <summary>Окно игры не найдено, но пакеты идут: верим пакетам, пока они совсем свежие.</summary>
    public static readonly TimeSpan FreshWithoutWindow = TimeSpan.FromSeconds(15);

    public static ConnectionState Evaluate(bool enabled, bool serverRunning, bool gameFound,
        DateTime? lastPacketUtc, DateTime? gameSinceUtc, GsiProblem problem, DateTime nowUtc)
    {
        if (!enabled) return ConnectionState.Disabled;
        if (!serverRunning) return ConnectionState.ServerError;
        if (problem == GsiProblem.BadToken && gameFound) return ConnectionState.BadToken;
        if (gameFound && lastPacketUtc is DateTime packet &&
            nowUtc - packet < Fresh && (gameSinceUtc == null || packet >= gameSinceUtc.Value - TimeSpan.FromSeconds(5)))
            return ConnectionState.Connected;
        if (!gameFound)
            // окно могло не найтись (другое имя процесса, запуск через лаунчер) — но данные идут, значит связь есть
            return lastPacketUtc is DateTime p && nowUtc - p < FreshWithoutWindow ? ConnectionState.Connected : ConnectionState.Offline;
        // «данных нет» решает сторож: он знает, настроена ли связь. Настроена — значит, Dota просто в меню и ждёт матча
        return problem == GsiProblem.NoData ? ConnectionState.NoData : ConnectionState.Waiting;
    }

    /// <summary>
    /// Почему сейчас нет подсказок. <paramref name="gameState"/> — DOTA_GAMERULES_STATE_* из последнего пакета (null — меню).
    /// </summary>
    public static TipSilence Silence(ConnectionState connection, string? gameState, bool paused, bool overlayHidden)
    {
        if (connection != ConnectionState.Connected) return TipSilence.NoConnection;
        switch (gameState)
        {
            case "DOTA_GAMERULES_STATE_HERO_SELECTION":
            case "DOTA_GAMERULES_STATE_STRATEGY_TIME":
            case "DOTA_GAMERULES_STATE_TEAM_SHOWCASE":
            case "DOTA_GAMERULES_STATE_WAIT_FOR_MAP_TO_LOAD":
                return TipSilence.Draft;
            case "DOTA_GAMERULES_STATE_PRE_GAME":
            case "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS":
                if (overlayHidden) return TipSilence.OverlayHidden;
                return paused ? TipSilence.Paused : TipSilence.None;
            case "DOTA_GAMERULES_STATE_POST_GAME":
                return TipSilence.PostGame;
            default:
                return TipSilence.Menu;
        }
    }
}
