using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>
/// 0.21.1: перцентили OpenDota по герою — сколько игроков на этом герое имеют GPM, XPM, добивания и смерти в минуту
/// не выше данного значения. Это все публичные матчи на герое, а не только ваш ранг.
/// Источник: GET https://api.opendota.com/api/benchmarks?hero_id={id}
/// </summary>
public sealed class HeroBenchmarks
{
    public int HeroId { get; set; }
    public DateTime FetchedUtc { get; set; }
    /// <summary>Метрика (gold_per_min, xp_per_min, last_hits_per_min, deaths_per_min…) → точки (перцентиль 0…1, значение) по возрастанию.</summary>
    public Dictionary<string, List<double[]>> Curves { get; set; } = new();

    /// <summary>Доля игроков (0…100) со значением не выше value; null — метрики нет или кривая пустая.</summary>
    public double? Percentile(string metric, double value)
    {
        if (!Curves.TryGetValue(metric, out var pts) || pts.Count < 2) return null;
        var p = pts.OrderBy(x => x[0]).ToList();
        if (value <= p[0][1]) return Math.Max(0, p[0][0] * 100 * (p[0][1] <= 0 ? 1 : value / p[0][1]));
        for (int i = 1; i < p.Count; i++)
        {
            if (value > p[i][1]) continue;
            double v0 = p[i - 1][1], v1 = p[i][1], q0 = p[i - 1][0], q1 = p[i][0];
            double t = v1 > v0 ? (value - v0) / (v1 - v0) : 1;
            return (q0 + (q1 - q0) * t) * 100;
        }
        return p[^1][0] * 100;
    }

    /// <summary>Разбор ответа OpenDota: {"hero_id":1,"result":{"gold_per_min":[{"percentile":0.1,"value":418},…]}}.</summary>
    public static HeroBenchmarks? Parse(JsonElement root, DateTime fetchedUtc)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
        var b = new HeroBenchmarks { FetchedUtc = fetchedUtc };
        if (root.TryGetProperty("hero_id", out var id) && id.TryGetInt32(out var hid)) b.HeroId = hid;
        foreach (var metric in result.EnumerateObject())
        {
            if (metric.Value.ValueKind != JsonValueKind.Array) continue;
            var pts = new List<double[]>();
            foreach (var e in metric.Value.EnumerateArray())
                if (e.TryGetProperty("percentile", out var pc) && e.TryGetProperty("value", out var v)
                    && pc.TryGetDouble(out var pd) && v.TryGetDouble(out var vd))
                    pts.Add(new[] { pd, vd });
            if (pts.Count >= 2) b.Curves[metric.Name] = pts.OrderBy(x => x[0]).ToList();
        }
        return b.Curves.Count > 0 ? b : null;
    }
}

/// <summary>Кэш перцентилей по героям на диске: неделю данные считаются свежими.</summary>
public sealed class HeroBenchmarksCache
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<int, HeroBenchmarks> _data = new();

    public HeroBenchmarksCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _data = JsonSerializer.Deserialize<Dictionary<int, HeroBenchmarks>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { Log.Warn("Кэш сравнения с игроками повреждён и будет проигнорирован: " + ex.Message); }
    }

    public HeroBenchmarks? Get(int heroId) { lock (_sync) return _data.TryGetValue(heroId, out var b) ? b : null; }

    public bool IsFresh(int heroId, DateTime nowUtc) => Get(heroId) is { } b && nowUtc - b.FetchedUtc < FreshFor;

    public void Put(HeroBenchmarks b)
    {
        string json;
        lock (_sync) { _data[b.HeroId] = b; json = JsonSerializer.Serialize(_data); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить кэш сравнения с игроками: " + ex.Message); }
    }
}
