using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>
/// Цены предметов. Во встроенной базе цен нет (они меняются от патча к патчу) —
/// они загружаются из OpenDota (constants/items) и хранятся в локальном кэше.
/// Если кэш пуст, приложение просто не показывает, сколько золота не хватает.
/// </summary>
public sealed class ItemCostCache
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, int> _costs = new(StringComparer.OrdinalIgnoreCase);

    public ItemCostCache(string path)
    {
        _path = path;
        Load();
    }

    public DateTime? UpdatedUtc { get; private set; }
    public int Count { get { lock (_sync) return _costs.Count; } }

    /// <summary>Цена предмета (ключ вида item_blink) или null, если неизвестна.</summary>
    public int? Cost(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return null;
        lock (_sync) return _costs.TryGetValue(itemKey, out var c) && c > 0 ? c : null;
    }

    public void Replace(IReadOnlyDictionary<string, int> costs, DateTime fetchedUtc)
    {
        lock (_sync) _costs = new Dictionary<string, int>(costs, StringComparer.OrdinalIgnoreCase);
        UpdatedUtc = fetchedUtc;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<CostFile>(File.ReadAllText(_path));
            if (file?.Costs == null) return;
            lock (_sync) _costs = new Dictionary<string, int>(file.Costs, StringComparer.OrdinalIgnoreCase);
            UpdatedUtc = file.UpdatedUtc;
        }
        catch (Exception ex) { Log.Warn("Кэш цен предметов повреждён и будет проигнорирован: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            CostFile file;
            lock (_sync) file = new CostFile { UpdatedUtc = UpdatedUtc, Costs = new Dictionary<string, int>(_costs) };
            File.WriteAllText(_path, JsonSerializer.Serialize(file));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить цены предметов: " + ex.Message); }
    }

    private sealed class CostFile
    {
        public DateTime? UpdatedUtc { get; set; }
        public Dictionary<string, int>? Costs { get; set; }
    }
}

/// <summary>Последний известный патч Dota 2 (из OpenDota constants/patch).</summary>
public sealed class PatchInfoCache
{
    private readonly string _path;

    public PatchInfoCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<PatchFile>(File.ReadAllText(path));
                Latest = file?.Latest;
                LatestDateUtc = file?.LatestDateUtc;
                CheckedUtc = file?.CheckedUtc;
            }
        }
        catch (Exception ex) { Log.Warn("Кэш патча повреждён: " + ex.Message); }
    }

    public string? Latest { get; private set; }
    public DateTime? LatestDateUtc { get; private set; }
    public DateTime? CheckedUtc { get; private set; }

    public void Set(string? latest, DateTime? dateUtc, DateTime checkedUtc)
    {
        Latest = latest;
        LatestDateUtc = dateUtc;
        CheckedUtc = checkedUtc;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new PatchFile { Latest = latest, LatestDateUtc = dateUtc, CheckedUtc = checkedUtc }));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить сведения о патче: " + ex.Message); }
    }

    private sealed class PatchFile
    {
        public string? Latest { get; set; }
        public DateTime? LatestDateUtc { get; set; }
        public DateTime? CheckedUtc { get; set; }
    }
}

/// <summary>Сравнение номеров патчей вида 7.41, 7.41d, «7.41d (пояснение)». Буквенные подверсии не учитываются.</summary>
public static class PatchVersion
{
    private static readonly Regex Pattern = new(@"(\d+)\.(\d+)", RegexOptions.CultureInvariant);

    public static (int Major, int Minor)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>true, если latest новее baseline (по номеру major.minor).</summary>
    public static bool IsNewer(string? latest, string? baseline)
    {
        var a = Parse(latest);
        var b = Parse(baseline);
        if (a == null || b == null) return false;
        return a.Value.Major != b.Value.Major ? a.Value.Major > b.Value.Major : a.Value.Minor > b.Value.Minor;
    }

    /// <summary>«7.41» из «7.41d (…)».</summary>
    public static string? Short(string? text)
    {
        var v = Parse(text);
        return v == null ? null : $"{v.Value.Major}.{v.Value.Minor:00}";
    }
}

/// <summary>Изменения героев в последнем патче: ключ героя → заметки (английский текст OpenDota).</summary>
public sealed record PatchDigest(string Patch, Dictionary<string, List<string>> Heroes, DateTime FetchedUtc)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static PatchDigest? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var d = JsonSerializer.Deserialize<PatchDigest>(File.ReadAllText(path), Json);
            return d == null ? null : d with { Heroes = new Dictionary<string, List<string>>(d.Heroes ?? new(), StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) { Log.Warn("Кэш изменений патча повреждён: " + ex.Message); return null; }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить изменения патча: " + ex.Message); }
    }
}

/// <summary>Строка из OpenDota players/{id}/recentMatches.</summary>
public sealed class RecentMatchRow
{
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("player_slot")] public int PlayerSlot { get; set; }
    [JsonPropertyName("radiant_win")] public bool? RadiantWin { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    [JsonPropertyName("start_time")] public long StartTime { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("xp_per_min")] public int XpPerMin { get; set; }
    [JsonPropertyName("gold_per_min")] public int GoldPerMin { get; set; }
    [JsonPropertyName("last_hits")] public int LastHits { get; set; }

    /// <summary>Слоты 0–127 — Radiant, 128+ — Dire.</summary>
    public bool IsRadiant => PlayerSlot < 128;
    public bool? Won => RadiantWin == null ? null : RadiantWin.Value == IsRadiant;
}

public static class SteamIds
{
    private const long Steam64Base = 76561197960265728L;

    /// <summary>Привести Steam ID (32 или 64 бит) к account id для OpenDota.</summary>
    public static long? ToAccountId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new string(text.Trim().Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        if (id >= Steam64Base) id -= Steam64Base;
        return id is > 0 and < uint.MaxValue ? id : null;
    }
}
