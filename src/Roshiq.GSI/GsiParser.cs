using System.Text.Json;

namespace Roshiq.GSI;

/// <summary>
/// Данные, которые официально передаёт Dota 2 Game State Integration.
/// Любое поле может отсутствовать (зависит от режима, стадии матча и роли наблюдателя), поэтому всё nullable.
/// </summary>
public sealed record GameStateSnapshot
{
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
    public bool AuthValid { get; init; }
    public string? ProviderName { get; init; }
    public int? ProviderVersion { get; init; }

    // map
    public string? MatchId { get; init; }
    public string? GameState { get; init; }
    public int? ClockTime { get; init; }
    public int? GameTime { get; init; }
    public bool? Paused { get; init; }
    public bool? Daytime { get; init; }
    public string? CustomGameName { get; init; }
    /// <summary>Победившая сторона после игры: radiant / dire / none.</summary>
    public string? WinTeam { get; init; }

    // player
    public string? PlayerName { get; init; }
    /// <summary>Steam ID64 игрока (GSI player.steamid) — для автоматической привязки аккаунта.</summary>
    public string? SteamId { get; init; }
    public string? Activity { get; init; }
    public string? TeamName { get; init; }
    public int? Gold { get; init; }
    public int? GoldUnreliable { get; init; }
    public int? Kills { get; init; }
    public int? Deaths { get; init; }
    public int? Assists { get; init; }
    public int? LastHits { get; init; }
    public int? Denies { get; init; }
    public int? Gpm { get; init; }
    public int? Xpm { get; init; }

    // hero
    public int? HeroId { get; init; }
    public string? HeroName { get; init; }
    public int? HeroLevel { get; init; }
    public bool? HeroAlive { get; init; }
    public int? BuybackCost { get; init; }
    public int? BuybackCooldown { get; init; }
    /// <summary>Секунд до возрождения (когда герой мёртв).</summary>
    public int? RespawnSeconds { get; init; }
    /// <summary>Координаты героя на карте (мировые, примерно −8000…8000; центр — середина карты).</summary>
    public int? HeroX { get; init; }
    public int? HeroY { get; init; }

    // items: ключи слотов (slot0..slot8, stash0..5, teleport0, neutral0) → item_xxx
    public IReadOnlyDictionary<string, string> ItemSlots { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> InventoryItems =>
        ItemSlots.Where(kv => !kv.Key.StartsWith("stash", StringComparison.Ordinal))
            .Select(kv => kv.Value).Where(v => v != "empty").Distinct().ToList();

    /// <summary>
    /// Идёт выбор героев. Кроме классических стадий (выбор, стратегия) учитываем драфт игроков и любые будущие стадии
    /// с «DRAFT» / «BAN» / «HERO_SELECTION» в названии — Valve меняет их от режима к режиму.
    /// </summary>
    public bool IsHeroSelection => GameState is "DOTA_GAMERULES_STATE_HERO_SELECTION" or "DOTA_GAMERULES_STATE_STRATEGY_TIME"
        or "DOTA_GAMERULES_STATE_PLAYER_DRAFT"
        || (GameState is string g && (g.Contains("_DRAFT", StringComparison.Ordinal) || g.Contains("_BAN", StringComparison.Ordinal)
                                       || g.Contains("HERO_SELECTION", StringComparison.Ordinal)));
    public bool IsInProgress => GameState is "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" or "DOTA_GAMERULES_STATE_PRE_GAME";
    public bool IsPostGame => GameState is "DOTA_GAMERULES_STATE_POST_GAME";
    public bool HasHero => HeroId is > 0 || (HeroName?.StartsWith("npc_dota_hero_", StringComparison.Ordinal) ?? false);
}

/// <summary>Безопасный разбор JSON от GSI: неизвестные и отсутствующие поля не ломают обработку.</summary>
public static class GsiParser
{
    public static GameStateSnapshot Parse(string json, string? expectedToken)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("GSI: ожидался объект JSON");

        string? token = Str(Obj(root, "auth"), "token");
        bool auth = string.IsNullOrEmpty(expectedToken) || string.Equals(token, expectedToken, StringComparison.Ordinal);

        var provider = Obj(root, "provider");
        var map = Obj(root, "map");
        var player = Obj(root, "player");
        var hero = Obj(root, "hero");
        var items = Obj(root, "items");

        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (items is { } it)
        {
            foreach (var p in it.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var name = Str(p.Value, "name");
                if (!string.IsNullOrEmpty(name)) slots[p.Name] = name;
            }
        }

        return new GameStateSnapshot
        {
            AuthValid = auth,
            ProviderName = Str(provider, "name"),
            ProviderVersion = Int(provider, "version"),
            MatchId = Str(map, "matchid"),
            GameState = Str(map, "game_state"),
            ClockTime = Int(map, "clock_time"),
            GameTime = Int(map, "game_time"),
            Paused = Bool(map, "paused"),
            Daytime = Bool(map, "daytime"),
            CustomGameName = Str(map, "customgamename"),
            WinTeam = Str(map, "win_team")?.Trim().ToLowerInvariant(),
            PlayerName = Str(player, "name"),
            SteamId = Str(player, "steamid") ?? Str(player, "accountid"),
            Activity = Str(player, "activity"),
            TeamName = Str(player, "team_name")?.Trim().ToLowerInvariant(), // "radiant"/"dire" — сравнения ниже чувствительны к регистру
            Gold = Int(player, "gold"),
            GoldUnreliable = Int(player, "gold_unreliable"),
            Kills = Int(player, "kills"),
            Deaths = Int(player, "deaths"),
            Assists = Int(player, "assists"),
            LastHits = Int(player, "last_hits"),
            Denies = Int(player, "denies"),
            Gpm = Int(player, "gpm"),
            Xpm = Int(player, "xpm"),
            HeroId = Int(hero, "id"),
            HeroName = Str(hero, "name"),
            HeroLevel = Int(hero, "level"),
            HeroAlive = Bool(hero, "alive"),
            BuybackCost = Int(hero, "buyback_cost"),
            BuybackCooldown = Int(hero, "buyback_cooldown"),
            RespawnSeconds = Int(hero, "respawn_seconds"),
            HeroX = Int(hero, "xpos"),
            HeroY = Int(hero, "ypos"),
            ItemSlots = slots
        };
    }

    private static JsonElement? Obj(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p) return null;
        return p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    }

    private static string? Str(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static int? Int(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > int.MinValue && d < int.MaxValue) return (int)d;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool? Bool(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
