namespace Roshiq.Core.Models;

public enum GamePhase { Lane, Mid, Late }

/// <summary>
/// Единые границы фаз игры для разбора матча, итогов, коучинга и карты смертей:
/// линия — до 10:00, середина — 10:00–25:00, поздняя игра — с 25:00.
/// (Отдельные правила вроде «выкуп после 30-й минуты» — не фазы и сюда не относятся.)
/// </summary>
public static class GamePhases
{
    public const int LaneEnd = 600;
    public const int MidEnd = 1500;

    public static GamePhase Of(int clock) => clock < LaneEnd ? GamePhase.Lane : clock < MidEnd ? GamePhase.Mid : GamePhase.Late;

    /// <summary>Интервал фазы в секундах игрового времени [From, To).</summary>
    public static (int From, int To) Range(GamePhase phase) => phase switch
    {
        GamePhase.Lane => (0, LaneEnd),
        GamePhase.Mid => (LaneEnd, MidEnd),
        _ => (MidEnd, int.MaxValue)
    };

    public static (string Ru, string En) Name(GamePhase phase) => phase switch
    {
        GamePhase.Lane => ("Линия (0–10)", "Laning (0–10)"),
        GamePhase.Mid => ("Середина (10–25)", "Mid game (10–25)"),
        _ => ("Поздняя игра (25+)", "Late game (25+)")
    };
}
