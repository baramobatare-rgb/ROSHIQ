using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>Выборы и победы героя по рангам OpenDota (/heroStats): индексы 0…7 — Рекрут…Титан (1_pick…8_pick), плюс про-сцена.</summary>
public sealed class HeroTierRow
{
    public int HeroId { get; set; }
    public int[] Picks { get; set; } = new int[8];
    public int[] Wins { get; set; } = new int[8];
    public int ProPick { get; set; }
    public int ProWin { get; set; }
}

/// <summary>0.23.0: снимок /heroStats для тир-листа.</summary>
public sealed class HeroTierStats
{
    public DateTime FetchedUtc { get; set; }
    public List<HeroTierRow> Rows { get; set; } = new();

    public static HeroTierStats Parse(JsonElement root, DateTime nowUtc)
    {
        var s = new HeroTierStats { FetchedUtc = nowUtc };
        if (root.ValueKind != JsonValueKind.Array) return s;
        foreach (var h in root.EnumerateArray())
        {
            if (MatchDetails.Int(h, "id") is not int id) continue;
            var row = new HeroTierRow { HeroId = id, ProPick = MatchDetails.Int(h, "pro_pick") ?? 0, ProWin = MatchDetails.Int(h, "pro_win") ?? 0 };
            for (int i = 0; i < 8; i++)
            {
                row.Picks[i] = MatchDetails.Int(h, $"{i + 1}_pick") ?? 0;
                row.Wins[i] = MatchDetails.Int(h, $"{i + 1}_win") ?? 0;
            }
            s.Rows.Add(row);
        }
        return s;
    }
}

/// <summary>Кэш /heroStats (cache\herostats.json): OpenDota пересчитывает его раз в сутки — чаще и не запрашиваем.</summary>
public sealed class HeroTierStatsCache
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);
    private readonly string _path;
    private HeroTierStats? _data;

    public HeroTierStatsCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _data = JsonSerializer.Deserialize<HeroTierStats>(File.ReadAllText(path));
        }
        catch (Exception ex) { Log.Warn("Кэш тир-листа повреждён и будет проигнорирован: " + ex.Message); }
    }

    public HeroTierStats? Get() => _data;
    public bool IsFresh(DateTime nowUtc) => _data != null && nowUtc - _data.FetchedUtc < FreshFor;

    public void Put(HeroTierStats s)
    {
        _data = s;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить кэш тир-листа: " + ex.Message); }
    }
}
