using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;

namespace Roshiq.Data;

/// <summary>Аккаунт, который когда-либо входил в Steam на этом компьютере (из config\loginusers.vdf).</summary>
public sealed record LocalSteamUser(string SteamId64, string? AccountName, string? PersonaName, bool MostRecent, long Timestamp)
{
    public long? AccountId => SteamIds.ToAccountId(SteamId64);
    public string Display => !string.IsNullOrWhiteSpace(PersonaName) ? PersonaName! : AccountName ?? SteamId64;
}

/// <summary>Разбор loginusers.vdf — обычного текстового файла Steam (формат KeyValues). Пароли в нём не хранятся.</summary>
public static class SteamLoginUsers
{
    public static IReadOnlyList<LocalSteamUser> Parse(string vdf)
    {
        var result = new List<LocalSteamUser>();
        var tokens = Tokenize(vdf);
        int i = 0;
        // "users" { "7656…" { "Key" "Value" … } … }
        while (i < tokens.Count && tokens[i] != "{") i++;
        i++;
        while (i < tokens.Count && tokens[i] != "}")
        {
            var id = tokens[i++];
            if (i >= tokens.Count || tokens[i] != "{") break;
            i++;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (i + 1 < tokens.Count && tokens[i] != "}")
            {
                var key = tokens[i++];
                if (tokens[i] == "{") { i = SkipBlock(tokens, i); continue; }
                fields[key] = tokens[i++];
            }
            i++; // }
            if (!Regex.IsMatch(id, @"^\d{17}$")) continue;
            fields.TryGetValue("AccountName", out var account);
            fields.TryGetValue("PersonaName", out var persona);
            bool recent = fields.TryGetValue("MostRecent", out var mr) && mr == "1";
            long ts = fields.TryGetValue("Timestamp", out var t) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result.Add(new LocalSteamUser(id, account, persona, recent, ts));
        }
        return result.OrderByDescending(u => u.MostRecent).ThenByDescending(u => u.Timestamp).ToList();
    }

    private static int SkipBlock(List<string> tokens, int i)
    {
        int depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i] == "{") depth++;
            else if (tokens[i] == "}" && --depth == 0) return i + 1;
        }
        return i;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    sb.Append(text[i++]);
                }
                i++;
                tokens.Add(sb.ToString());
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }
}

/// <summary>
/// Вход через Steam (OpenID 2.0) — официальная страница steamcommunity.com. Программа получает только Steam ID:
/// ни пароля, ни доступа к инвентарю или покупкам.
/// </summary>
public static class SteamOpenId
{
    public const string Endpoint = "https://steamcommunity.com/openid/login";
    private const string Ns = "http://specs.openid.net/auth/2.0";
    private const string Select = "http://specs.openid.net/auth/2.0/identifier_select";
    private static readonly Regex ClaimedId = new(@"^https://steamcommunity\.com/openid/id/(\d{17})$", RegexOptions.CultureInvariant);

    public static string LoginUrl(string returnTo, string realm) =>
        Endpoint + "?" + string.Join("&", new[]
        {
            ("openid.ns", Ns), ("openid.mode", "checkid_setup"), ("openid.return_to", returnTo),
            ("openid.realm", realm), ("openid.identity", Select), ("openid.claimed_id", Select)
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));

    /// <summary>Разобрать строку запроса ответа Steam.</summary>
    public static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    /// <summary>Steam ID64 из ответа (ещё не проверенного) или null.</summary>
    public static string? SteamIdFrom(IReadOnlyDictionary<string, string> response)
    {
        if (!response.TryGetValue("openid.mode", out var mode) || mode != "id_res") return null;
        if (!response.TryGetValue("openid.claimed_id", out var claimed)) return null;
        var m = ClaimedId.Match(claimed);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Проверить ответ у самого Steam (check_authentication): подделанный адрес возврата не пройдёт.
    /// Дополнительно сверяет, что ответ пришёл на наш адрес возврата.
    /// </summary>
    public static async Task<string?> VerifyAsync(IReadOnlyDictionary<string, string> response, string expectedReturnTo, HttpClient http, CancellationToken ct)
    {
        var id = SteamIdFrom(response);
        if (id == null) return null;
        if (!response.TryGetValue("openid.return_to", out var rt) || !rt.StartsWith(expectedReturnTo, StringComparison.Ordinal)) return null;
        if (!response.TryGetValue("openid.op_endpoint", out var op) || op != Endpoint) return null;
        var form = response.Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal))
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Key == "openid.mode" ? "check_authentication" : kv.Value)).ToList();
        using var content = new FormUrlEncodedContent(form);
        using var reply = await http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        var text = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        NetworkLog.Record("POST", Endpoint, "проверка входа Steam: " + (text.Contains("is_valid:true") ? "OK" : "отказ"));
        return text.Contains("is_valid:true", StringComparison.Ordinal) ? id : null;
    }
}

/// <summary>Профиль игрока в OpenDota.</summary>
public sealed record PlayerProfile(long AccountId, string? Name, string? AvatarUrl, int? RankTier, int? LeaderboardRank);

public static class Ranks
{
    private static readonly string[] Ru = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    private static readonly string[] En = { "", "Herald", "Guardian", "Crusader", "Archon", "Legend", "Ancient", "Divine", "Immortal" };

    /// <summary>Медаль из rank_tier OpenDota (десятки — медаль, единицы — звёзды): 54 → «Легенда 4».</summary>
    public static string? Name(int? rankTier, int? leaderboard, Language lang)
    {
        if (rankTier is not int t || t < 10) return null;
        int medal = Math.Clamp(t / 10, 1, 8), stars = t % 10;
        var name = (lang == Language.English ? En : Ru)[medal];
        if (medal == 8) return leaderboard is int lb && lb > 0 ? $"{name} #{lb}" : name;
        return stars > 0 ? $"{name} {stars}" : name;
    }
}

/// <summary>Ваша статистика на герое (все матчи из OpenDota).</summary>
public sealed record PlayerHeroStat(int HeroId, int Games, int Wins, long LastPlayed, int AgainstGames = 0, int AgainstWins = 0)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    /// <summary>Ваш винрейт, когда этот герой был у противника.</summary>
    public double AgainstWinRate => AgainstGames == 0 ? 0 : (double)AgainstWins / AgainstGames;
}

/// <summary>Кэш вашей статистики по героям (cache\player_heroes.json).</summary>
public sealed class PlayerHeroStatsCache
{
    private readonly string _path;
    private Dictionary<int, PlayerHeroStat> _stats = new();

    public PlayerHeroStatsCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(path));
                if (file?.Heroes != null) { _stats = file.Heroes.ToDictionary(h => h.HeroId); AccountId = file.AccountId; UpdatedUtc = file.UpdatedUtc; }
            }
        }
        catch (Exception ex) { Log.Warn("Кэш статистики героев повреждён: " + ex.Message); }
    }

    public long? AccountId { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }
    public int Count => _stats.Count;
    public IReadOnlyDictionary<int, PlayerHeroStat> All => _stats;

    public PlayerHeroStat? Get(int heroId) => _stats.TryGetValue(heroId, out var s) ? s : null;

    public void Replace(long accountId, IEnumerable<PlayerHeroStat> stats, DateTime utc)
    {
        _stats = stats.Where(s => s.Games > 0 || s.AgainstGames > 0).ToDictionary(s => s.HeroId);
        AccountId = accountId;
        UpdatedUtc = utc;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new StatsFile { AccountId = accountId, UpdatedUtc = utc, Heroes = _stats.Values.ToList() }));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить статистику героев: " + ex.Message); }
    }

    public void Clear()
    {
        _stats = new();
        AccountId = null;
        UpdatedUtc = null;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    /// <summary>Герои противника, против которых вы чаще проигрываете (не меньше minGames игр), худшие первыми.</summary>
    public IReadOnlyList<PlayerHeroStat> WorstMatchups(int count = 6, int minGames = 10, double below = 0.45) =>
        _stats.Values.Where(s => s.AgainstGames >= minGames && s.AgainstWinRate < below)
            .OrderBy(s => s.AgainstWinRate).ThenByDescending(s => s.AgainstGames).Take(count).ToList();

    /// <summary>Герои для пула: сыграно не меньше minGames, отсортированы по «играю часто и выигрываю».</summary>
    public IReadOnlyList<int> SuggestPool(int count = 12, int minGames = 10) =>
        _stats.Values.Where(s => s.Games >= minGames)
            .OrderByDescending(s => s.Games * (0.5 + s.WinRate))
            .Take(count).Select(s => s.HeroId).ToList();

    private sealed class StatsFile
    {
        public long? AccountId { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public List<PlayerHeroStat>? Heroes { get; set; }
    }
}
