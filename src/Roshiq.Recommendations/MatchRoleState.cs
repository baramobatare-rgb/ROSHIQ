using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

public enum MatchRoleSource { Default, Lane, Queue, Screen, Manual }

/// <summary>Separates the saved usual role from the role for one live match.</summary>
public sealed class MatchRoleState
{
    private Position _matchPosition;

    public MatchRoleState(Position defaultPosition) => DefaultPosition = defaultPosition;

    public Position DefaultPosition { get; private set; }
    public Position CurrentPosition => Active ? _matchPosition : DefaultPosition;
    public MatchRoleSource Source { get; private set; } = MatchRoleSource.Default;
    public string? Reason { get; private set; }
    public string? MatchId { get; private set; }
    public bool Active { get; private set; }

    public void SetDefault(Position position)
    {
        DefaultPosition = position;
        if (Active && Source == MatchRoleSource.Default) _matchPosition = position;
    }

    /// <summary>Returns true when a new session started. A late-arriving match ID belongs to the current session.</summary>
    public bool Enter(string? matchId)
    {
        if (Active && (matchId == null || MatchId == null || MatchId == matchId))
        {
            MatchId ??= matchId;
            return false;
        }
        Active = true;
        MatchId = matchId;
        _matchPosition = DefaultPosition;
        Source = MatchRoleSource.Default;
        Reason = null;
        return true;
    }

    public bool Leave()
    {
        if (!Active) return false;
        Active = false;
        MatchId = null;
        Source = MatchRoleSource.Default;
        Reason = null;
        return true;
    }

    /// <summary>Manual wins; screen beats queue, queue beats lane. A lower-priority guess cannot replace a known role.</summary>
    public bool ApplyAutomatic(Position position, MatchRoleSource source, string? reason = null)
    {
        if (!Active || source is MatchRoleSource.Default or MatchRoleSource.Manual || source < Source) return false;
        if (source == Source && _matchPosition == position) return false;
        _matchPosition = position;
        Source = source;
        Reason = reason;
        return true;
    }

    public void SelectManual(Position position)
    {
        if (!Active) { SetDefault(position); return; }
        _matchPosition = position;
        Source = MatchRoleSource.Manual;
        Reason = null;
    }
}
