using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>Реальная статистика матчапа из внешнего источника (не вычисляется и не выдумывается).</summary>
public sealed record MatchupStat(int HeroId, int OpponentId, int Games, int Wins, string Source, DateTime FetchedUtc)
{
    public double WinRate => Games > 0 ? (double)Wins / Games : 0;
}

public interface IMatchupStatsProvider
{
    /// <summary>Статистика героя heroId против opponentId, если она есть в локальном кэше.</summary>
    MatchupStat? Get(int heroId, int opponentId);
}

/// <summary>Провайдер "нет данных" — используется по умолчанию (офлайн).</summary>
public sealed class NoStatsProvider : IMatchupStatsProvider
{
    public static readonly NoStatsProvider Instance = new();
    public MatchupStat? Get(int heroId, int opponentId) => null;
}

/// <summary>
/// Локальный кэш матчапов (JSON). Заполняется только по явной команде пользователя
/// из публичного API OpenDota: GET https://api.opendota.com/api/heroes/{id}/matchups
/// </summary>
public sealed class MatchupStatsCache : IMatchupStatsProvider
{
    private readonly string _path;
    private readonly object _sync = new();
    /// <summary>Запись файла по одной: несколько загрузок, закончившихся одновременно, не дерутся за matchups.json.</summary>
    private readonly object _saveSync = new();
    private Dictionary<int, HeroMatchups> _data = new();

    public MatchupStatsCache(string path)
    {
        _path = path;
        LoadFromDisk();
    }

    public int HeroesCached { get { lock (_sync) return _data.Count; } }
    public DateTime? LastUpdatedUtc { get { lock (_sync) return _data.Count == 0 ? null : _data.Values.Max(v => v.FetchedUtc); } }

    public MatchupStat? Get(int heroId, int opponentId)
    {
        lock (_sync)
        {
            if (!_data.TryGetValue(heroId, out var m)) return null;
            var row = m.Rows.FirstOrDefault(r => r.HeroId == opponentId);
            return row == null ? null : new MatchupStat(heroId, opponentId, row.Games, row.Wins, m.Source, m.FetchedUtc);
        }
    }

    public void Put(int heroId, IEnumerable<OpponentRow> rows, string source, DateTime fetchedUtc)
    {
        lock (_sync)
            _data[heroId] = new HeroMatchups { Source = source, FetchedUtc = fetchedUtc, Rows = rows.ToList() };
        SaveToDisk();
    }

    public void Clear()
    {
        lock (_sync) _data.Clear();
        SaveToDisk();
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Dictionary<int, HeroMatchups>>(File.ReadAllText(_path));
            if (d != null) lock (_sync) _data = d;
        }
        catch (Exception ex) { Log.Warn("Кэш статистики повреждён и будет проигнорирован: " + ex.Message); }
    }

    private void SaveToDisk()
    {
        lock (_saveSync)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    string json;
                    lock (_sync) json = JsonSerializer.Serialize(_data);
                    Roshiq.Core.Settings.AtomicFile.WriteAllText(_path, json);   // целиком или никак: обрыв не оставит половину файла
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(150);   // файл держит кто-то снаружи (антивирус, второй экземпляр) — короткая повторная попытка
                }
                catch (Exception ex) { Log.Warn("Не удалось сохранить кэш статистики: " + ex.Message); return; }
            }
        }
    }

    public sealed class HeroMatchups
    {
        public string Source { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<OpponentRow> Rows { get; set; } = new();
    }

    public sealed class OpponentRow
    {
        [JsonPropertyName("hero_id")] public int HeroId { get; set; }
        [JsonPropertyName("games_played")] public int Games { get; set; }
        [JsonPropertyName("wins")] public int Wins { get; set; }
    }
}

/// <summary>Клиент публичного API OpenDota. Вызывается только вручную из раздела Updates.</summary>
public sealed class OpenDotaClient : IDisposable
{
    public const string BaseUrl = "https://api.opendota.com/api";
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public OpenDotaClient(string? apiKey = null, HttpClient? http = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Roshiq/0.1");
    }

    public async Task<IReadOnlyList<MatchupStatsCache.OpponentRow>> GetMatchupsAsync(int heroId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/heroes/{heroId}/matchups";
        var logged = url;
        if (_apiKey != null) url += "?api_key=" + Uri.EscapeDataString(_apiKey);
        try
        {
            var rows = await _http.GetFromJsonAsync<List<MatchupStatsCache.OpponentRow>>(url, ct).ConfigureAwait(false);
            NetworkLog.Record("GET", logged, $"OK ({rows?.Count ?? 0} строк)");
            return rows ?? new List<MatchupStatsCache.OpponentRow>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("GET", logged, "ошибка: " + ex.Message);
            throw;
        }
    }

    /// <summary>Обновить кэш для набора героев. Пауза между запросами — бережём лимиты API.</summary>
    public async Task<int> RefreshAsync(MatchupStatsCache cache, IEnumerable<int> heroIds, IProgress<string>? progress, CancellationToken ct)
    {
        int ok = 0;
        foreach (var id in heroIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rows = await GetMatchupsAsync(id, ct).ConfigureAwait(false);
                cache.Put(id, rows, "OpenDota", DateTime.UtcNow);
                ok++;
                progress?.Report($"hero {id}: {rows.Count}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report($"hero {id}: {ex.Message}");
            }
            await Task.Delay(1100, ct).ConfigureAwait(false);
        }
        return ok;
    }

    private string WithKey(string url) =>
        _apiKey == null ? url : url + (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(_apiKey);

    private async Task<JsonDocument> GetDocumentAsync(string path, CancellationToken ct)
    {
        var logged = BaseUrl + path;
        try
        {
            using var response = await _http.GetAsync(WithKey(logged), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            NetworkLog.Record("GET", logged, "OK");
            return doc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("GET", logged, "ошибка: " + ex.Message);
            throw;
        }
    }

    /// <summary>Цены всех предметов текущего патча: item_xxx → стоимость.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetItemCostsAsync(CancellationToken ct)
    {
        using var doc = await GetDocumentAsync("/constants/items", ct).ConfigureAwait(false);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("cost", out var cost)) continue;
            if (cost.ValueKind == JsonValueKind.Number && cost.TryGetInt32(out var c) && c >= 0)
                result["item_" + p.Name] = c;
        }
        return result;
    }

    /// <summary>Самый новый патч из списка OpenDota (имя вида «7.41» и дата выхода).</summary>
    public async Task<(string? Name, DateTime? DateUtc)> GetLatestPatchAsync(CancellationToken ct)
    {
        using var doc = await GetDocumentAsync("/constants/patch", ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, null);
        string? name = null;
        DateTime? date = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            var candidate = n.GetString();
            if (name != null && !PatchVersion.IsNewer(candidate, name)) continue;
            name = candidate;
            date = e.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                ? dt : null;
        }
        return (name, date);
    }

    /// <summary>
    /// Изменения героев в самом новом патче (OpenDota constants/patchnotes, на английском).
    /// Формат источника меняется, поэтому разбор терпимый: собираются все строки из записи героя.
    /// </summary>
    public async Task<PatchDigest?> GetPatchNotesAsync(CancellationToken ct)
    {
        using var doc = await GetDocumentAsync("/constants/patchnotes", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? latestKey = null;
        foreach (var p in root.EnumerateObject())
            if (PatchVersion.Parse(p.Name.Replace('_', '.')) != null &&
                (latestKey == null || PatchVersion.IsNewer(p.Name.Replace('_', '.'), latestKey.Replace('_', '.'))))
                latestKey = p.Name;
        if (latestKey == null || !root.TryGetProperty(latestKey, out var patch) || patch.ValueKind != JsonValueKind.Object) return null;
        var heroes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (patch.TryGetProperty("heroes", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var hero in h.EnumerateObject())
            {
                var key = hero.Name.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase) ? hero.Name[14..] : hero.Name;
                var notes = new List<string>();
                CollectStrings(hero.Value, notes, 10);
                if (notes.Count > 0) heroes[key] = notes;
            }
        }
        return new PatchDigest(latestKey.Replace('_', '.'), heroes, DateTime.UtcNow);
    }

    private static void CollectStrings(JsonElement e, List<string> into, int max)
    {
        if (into.Count >= max) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var t = e.GetString()?.Trim();
                if (!string.IsNullOrEmpty(t) && t.Length > 3 && !into.Contains(t)) into.Add(t);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) CollectStrings(x, into, max);
                break;
            case JsonValueKind.Object:
                foreach (var x in e.EnumerateObject()) CollectStrings(x.Value, into, max);
                break;
        }
    }

    /// <summary>Последние матчи игрока (до 20) — только если профиль открыт для OpenDota.</summary>
    public async Task<IReadOnlyList<RecentMatchRow>> GetRecentMatchesAsync(long accountId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/players/{accountId}/recentMatches";
        try
        {
            var rows = await _http.GetFromJsonAsync<List<RecentMatchRow>>(WithKey(url), ct).ConfigureAwait(false);
            NetworkLog.Record("GET", url, $"OK ({rows?.Count ?? 0} матчей)");
            return rows ?? new List<RecentMatchRow>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("GET", url, "ошибка: " + ex.Message);
            throw;
        }
    }

    /// <summary>Профиль игрока: ник, аватар, медаль. null — профиль не найден.</summary>
    public async Task<PlayerProfile?> GetPlayerProfileAsync(long accountId, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync($"/players/{accountId}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? name = null, avatar = null;
        if (root.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            name = p.TryGetProperty("personaname", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            avatar = p.TryGetProperty("avatarfull", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        }
        else return null;
        int? rank = root.TryGetProperty("rank_tier", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null;
        int? lb = root.TryGetProperty("leaderboard_rank", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
        return new PlayerProfile(accountId, name, avatar, rank, lb);
    }

    /// <summary>Ваша статистика по всем героям (игры, победы).</summary>
    public async Task<IReadOnlyList<PlayerHeroStat>> GetPlayerHeroesAsync(long accountId, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync($"/players/{accountId}/heroes", ct).ConfigureAwait(false);
        var list = new List<PlayerHeroStat>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            int Int(string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String when int.TryParse(v.GetString(), out var x) => x,
                _ => 0
            } : 0;
            long last = e.TryGetProperty("last_played", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt64() : 0;
            int hero = Int("hero_id");
            if (hero > 0) list.Add(new PlayerHeroStat(hero, Int("games"), Int("win"), last, Int("against_games"), Int("against_win")));
        }
        return list;
    }

    /// <summary>Попросить OpenDota разобрать запись матча (обычно готово через 2–15 минут).</summary>
    public async Task<bool> RequestParseAsync(long matchId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/request/{matchId}";
        try
        {
            using var response = await _http.PostAsync(WithKey(url), content: null, ct).ConfigureAwait(false);
            NetworkLog.Record("POST", url, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("POST", url, "ошибка: " + ex.Message);
            return false;
        }
    }

    /// <summary>Матч с данными вашего игрока. null — матч не найден или вас в нём нет.</summary>
    public async Task<MatchDetails?> GetMatchAsync(long matchId, long accountId, Func<int, string?> heroNpc, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync($"/matches/{matchId}", ct).ConfigureAwait(false);
        return MatchDetails.Parse(doc.RootElement, accountId, heroNpc);
    }

    /// <summary>Медианное время покупки предмета (item_xxx) на герое по всем матчам OpenDota; null — данных мало.</summary>
    public async Task<int?> GetItemTimingNormAsync(int heroId, string itemKey, CancellationToken ct)
    {
        var item = itemKey.StartsWith("item_", StringComparison.Ordinal) ? itemKey[5..] : itemKey;
        using var doc = await GetDocumentAsync($"/scenarios/itemTimings?item={Uri.EscapeDataString(item)}&hero_id={heroId}", ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var buckets = new List<(int, int)>();
        foreach (var e in doc.RootElement.EnumerateArray())
            if (MatchDetails.Int(e, "time") is int t && MatchDetails.Int(e, "games") is int g) buckets.Add((t, g));
        return ItemTimingNormCache.Median(buckets);
    }

    /// <summary>Соответствие числового id предмета ключу item_xxx (constants/item_ids).</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetItemIdMapAsync(CancellationToken ct)
    {
        using var doc = await GetDocumentAsync("/constants/item_ids", ct).ConfigureAwait(false);
        var map = new Dictionary<int, string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (int.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is string key && key.Length > 0)
                map[id] = "item_" + key;
        return map;
    }

    /// <summary>Популярность предметов на герое по фазам игры: фаза (start/early/mid/late) → id предмета → число покупок.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>> GetItemPopularityAsync(int heroId, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync($"/heroes/{heroId}/itemPopularity", ct).ConfigureAwait(false);
        var result = new Dictionary<string, IReadOnlyDictionary<int, int>>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var phase in doc.RootElement.EnumerateObject())
        {
            if (phase.Value.ValueKind != JsonValueKind.Object) continue;
            var counts = new Dictionary<int, int>();
            foreach (var it in phase.Value.EnumerateObject())
                if (int.TryParse(it.Name, out var id) && it.Value.ValueKind == JsonValueKind.Number && it.Value.TryGetInt32(out var n))
                    counts[id] = n;
            result[phase.Name] = counts;
        }
        return result;
    }

    /// <summary>Все герои OpenDota: id → английское имя (constants/heroes).</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetHeroListAsync(CancellationToken ct)
    {
        using var doc = await GetDocumentAsync("/constants/heroes", ct).ConfigureAwait(false);
        var map = new Dictionary<int, string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object || MatchDetails.Int(p.Value, "id") is not int id) continue;
            map[id] = MatchDetails.Str(p.Value, "localized_name") ?? MatchDetails.Str(p.Value, "name") ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return map;
    }

    public void Dispose() => _http.Dispose();
}
