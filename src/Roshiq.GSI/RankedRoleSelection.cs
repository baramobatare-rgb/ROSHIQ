using System.Text.RegularExpressions;
using Roshiq.Core.Models;

namespace Roshiq.GSI;

/// <summary>
/// Roles checked in Dota's ranked role search. These are queue preferences, not the
/// assigned role when multiple boxes are checked. Read only the current Steam user's
/// local config; never guess an assignment from several selected roles.
/// </summary>
public static class RankedRoleSelection
{
    private static readonly (string Key, Position Position)[] RoleKeys =
    {
        ("dota_lane_selection_safelane", Position.Carry),
        ("dota_lane_selection_midlane", Position.Mid),
        ("dota_lane_selection_offlane", Position.Offlane),
        ("dota_lane_selection_soft_support", Position.SoftSupport),
        ("dota_lane_selection_hard_support", Position.HardSupport)
    };

    public static IReadOnlyList<Position> ParseChecked(string config)
    {
        if (!Enabled(config, "dota_ranked_role_queue")) return Array.Empty<Position>();
        if (Enabled(config, "dota_ranked_role_queue_automatically_select_all"))
            return RoleKeys.Select(x => x.Position).ToArray();
        return RoleKeys.Where(x => Enabled(config, x.Key)).Select(x => x.Position).ToArray();
    }

    public static Position? ParseSingle(string config) => ParseChecked(config) is { Count: 1 } selected ? selected[0] : null;

    public static IReadOnlyList<Position> ReadChecked(string steamRoot, long accountId)
    {
        if (accountId <= 0) return Array.Empty<Position>();
        var path = Path.Combine(steamRoot, "userdata", accountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "570", "local", "cfg", "machine_convars.vcfg");
        try { return File.Exists(path) ? ParseChecked(File.ReadAllText(path)) : Array.Empty<Position>(); }
        catch (IOException) { return Array.Empty<Position>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<Position>(); }
    }

    public static Position? ReadSingle(string steamRoot, long accountId) =>
        ReadChecked(steamRoot, accountId) is { Count: 1 } selected ? selected[0] : null;

    private static bool Enabled(string config, string key) =>
        Regex.IsMatch(config, "\"" + Regex.Escape(key) + "\"\\s+\"(?:true|1)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
