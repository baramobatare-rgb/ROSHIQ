using System.Globalization;
using System.Reflection;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;

namespace Roshiq.Data;

public sealed record TraitInfo(string Key, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.Russian ? Ru : En;
}

/// <summary>Правило: признак у героя-источника + признак у кандидата = бонус с объяснением.</summary>
public sealed record PairRule(string SourceTrait, string CandidateTrait, double Weight, string Ru, string En);

public sealed record GapRule(string Trait, double Weight, string Ru, string En);

/// <summary>Периодическое событие матча по игровым часам (руны, лотосы и т.п.).</summary>
/// <summary>Совет на линию против героя с признаком Trait.</summary>
public sealed record LaneTip(string Trait, string Ru, string En);

public sealed record TimerRule(string Key, string Ru, string En, int FirstSec, int IntervalSec, int? LastSec, int LeadSec,
    IReadOnlyList<Position>? Positions = null)
{
    public string Name(Language lang) => lang == Language.Russian ? Ru : En;
    /// <summary>Событие актуально для позиции (пустой список — для всех).</summary>
    public bool AppliesTo(Position position) => Positions == null || Positions.Count == 0 || Positions.Contains(position);
}

public sealed record RoleTip(Position Position, string Category, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.Russian ? Ru : En;
}

/// <summary>
/// Локальная офлайн-база: герои, предметы, правила драфта, веса ролей и справочные сборки.
/// Источник — встроенные текстовые файлы; любой файл можно переопределить,
/// положив одноимённый в %AppData%\Roshiq\data.
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<string, Hero> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Hero> _byId = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TraitInfo> _traits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Position, Dictionary<string, double>> _roleWeights = new();
    private readonly Dictionary<string, Dictionary<string, double>> _affinity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Hero, Position Pos, BuildPhase Phase), IReadOnlyList<string>> _curated = new();

    public IReadOnlyList<Hero> Heroes { get; private set; } = Array.Empty<Hero>();
    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    public IReadOnlyList<PairRule> CounterRules { get; private set; } = Array.Empty<PairRule>();
    public IReadOnlyList<PairRule> SynergyRules { get; private set; } = Array.Empty<PairRule>();
    public IReadOnlyList<GapRule> GapRules { get; private set; } = Array.Empty<GapRule>();
    public IReadOnlyList<RoleTip> RoleTips { get; private set; } = Array.Empty<RoleTip>();
    public IReadOnlyList<TimerRule> Timers { get; private set; } = Array.Empty<TimerRule>();
    /// <summary>Советы на линию по признакам соперника (lane_tips.txt), в порядке важности.</summary>
    public IReadOnlyList<LaneTip> LaneTips { get; private set; } = Array.Empty<LaneTip>();
    public IReadOnlyDictionary<string, string> VersionInfo { get; private set; } = new Dictionary<string, string>();
    /// <summary>Предупреждения валидации (неизвестные признаки, предметы и т.п.).</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
    /// <summary>Какие файлы взяты из пользовательской папки переопределений.</summary>
    public IReadOnlyList<string> OverriddenFiles { get; private set; } = Array.Empty<string>();

    public string DataVersion => VersionInfo.TryGetValue("dataVersion", out var v) ? v : "?";
    public string PatchBaseline => VersionInfo.TryGetValue("patchBaseline", out var v) ? v : "?";

    private GameDatabase() { }

    /// <summary>
    /// Загрузить данные. Порядок: ваши правки (overrideDirectory) → обновление данных с GitHub (remoteDirectory) → встроенные.
    /// </summary>
    public static GameDatabase Load(string? overrideDirectory = null, string? remoteDirectory = null)
    {
        var db = new GameDatabase();
        var source = new DataSource(overrideDirectory, remoteDirectory);
        db.LoadAll(source);
        db.OverriddenFiles = source.Overridden;
        db.RemoteFiles = source.Remote;
        return db;
    }

    /// <summary>Файлы данных, взятые из обновления с GitHub.</summary>
    public IReadOnlyList<string> RemoteFiles { get; private set; } = Array.Empty<string>();


    /// <summary>Имена всех встроенных файлов данных (heroes.txt, items.txt…).</summary>
    public static IReadOnlyList<string> ResourceFiles =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("Roshiq.Data.", StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal))
            .Select(n => n["Roshiq.Data.".Length..]).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public Hero? FindHero(string? keyOrName)
    {
        if (string.IsNullOrWhiteSpace(keyOrName)) return null;
        var k = keyOrName.Trim();
        if (k.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase)) k = k["npc_dota_hero_".Length..];
        if (_byKey.TryGetValue(k, out var h)) return h;
        return Heroes.FirstOrDefault(x =>
            string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.NameRu, k, StringComparison.OrdinalIgnoreCase));
    }

    public Hero? FindHero(int id) => _byId.TryGetValue(id, out var h) ? h : null;

    public Item? FindItem(string? key) =>
        key != null && _items.TryGetValue(key, out var i) ? i : null;

    public TraitInfo? Trait(string key) => _traits.TryGetValue(key, out var t) ? t : null;

    public string TraitText(string key, Language lang) => Trait(key)?.Text(lang) ?? key;

    public IEnumerable<TraitInfo> AllTraits => _traits.Values;

    /// <summary>Поиск по англ./рус. имени и признакам, фильтр по позиции и атрибуту.</summary>
    public IEnumerable<Hero> Search(string? text, Position? position = null, PrimaryAttribute? attribute = null)
    {
        var q = text?.Trim() ?? "";
        return Heroes.Where(h =>
                (position == null || h.PlaysPosition(position.Value)) &&
                (attribute == null || h.Attribute == attribute.Value) &&
                (q.Length == 0 ||
                 h.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 h.NameRu.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 h.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 h.Traits.Any(t => (Trait(t)?.Ru ?? t).Contains(q, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase);
    }

    public double RoleWeight(Position position, string tag) =>
        _roleWeights.TryGetValue(position, out var m) && m.TryGetValue(tag, out var w) ? w : 0;

    public double Affinity(string heroTrait, string tag) =>
        _affinity.TryGetValue(heroTrait, out var m) && m.TryGetValue(tag, out var w) ? w : 0;

    public IReadOnlyList<string>? CuratedBuild(Hero hero, Position position, BuildPhase phase) =>
        _curated.TryGetValue((hero.Key.ToLowerInvariant(), position, phase), out var list) ? list : null;

    public bool HasCuratedBuild(Hero hero, Position position) =>
        _curated.Keys.Any(k => k.Hero == hero.Key.ToLowerInvariant() && k.Pos == position);

    /// <summary>
    /// Позиция, чью базовую сборку показывать: сама позиция, если сборка для неё есть; иначе ближайшая из той же группы
    /// (керри/мид/оффлейн или саппорты). Сборку кора саппорту и наоборот не подставляем — тогда null.
    /// </summary>
    public Position? CuratedPosition(Hero hero, Position position)
    {
        if (HasCuratedBuild(hero, position)) return position;
        static bool Core(Position p) => (int)p <= 3;
        var key = hero.Key.ToLowerInvariant();
        var same = _curated.Keys.Where(k => k.Hero == key && Core(k.Pos) == Core(position)).Select(k => k.Pos).Distinct()
            .OrderBy(p => Math.Abs((int)p - (int)position)).ThenBy(p => (int)p).ToList();
        return same.Count > 0 ? same[0] : null;
    }

    // ---------------- загрузка ----------------

    private void LoadAll(DataSource src)
    {
        var warnings = new List<string>();

        foreach (var f in src.Rows("traits.txt", 3))
            _traits[f[0]] = new TraitInfo(f[0], f[1], f[2]);

        var heroes = new List<Hero>();
        foreach (var f in src.Rows("heroes.txt", 7))
        {
            if (!int.TryParse(f[0], out var id)) { warnings.Add($"heroes.txt: неверный id '{f[0]}'"); continue; }
            var positions = ParsePositions(f[5]);
            var traits = SplitList(f[6]);
            foreach (var t in traits.Where(t => !_traits.ContainsKey(t)))
                warnings.Add($"heroes.txt: {f[1]} — неизвестный признак '{t}'");
            var hero = new Hero(id, f[1], f[2], f[3], ParseAttribute(f[4]), positions, traits);
            if (_byKey.ContainsKey(hero.Key) || _byId.ContainsKey(id)) { warnings.Add($"heroes.txt: дубликат {hero.Key}"); continue; }
            heroes.Add(hero);
            _byKey[hero.Key] = hero;
            _byId[id] = hero;
        }
        Heroes = heroes;

        var items = new List<Item>();
        foreach (var f in src.Rows("items.txt", 11))
        {
            if (!TryParsePhase(f[3], out var phase)) { warnings.Add($"items.txt: {f[0]} — неизвестная фаза '{f[3]}'"); continue; }
            var req = SplitList(f[8]);
            var item = new Item
            {
                Key = f[0],
                Name = f[1],
                NameRu = f[2],
                Phase = phase,
                Group = f[4] == "-" ? null : f[4],
                Positions = f[5] == "*" ? Array.Empty<Position>() : ParsePositions(f[5]),
                Provides = new HashSet<string>(SplitList(f[6]), StringComparer.OrdinalIgnoreCase),
                Counters = new HashSet<string>(SplitList(f[7]), StringComparer.OrdinalIgnoreCase),
                Requires = req.Where(r => !r.StartsWith('!')).ToArray(),
                Excludes = req.Where(r => r.StartsWith('!')).Select(r => r[1..]).ToArray(),
                PurposeRu = f[9],
                PurposeEn = f[10]
            };
            foreach (var t in item.Counters.Concat(item.Requires).Concat(item.Excludes).Where(t => !_traits.ContainsKey(t)))
                warnings.Add($"items.txt: {item.Key} — неизвестный признак '{t}'");
            if (_items.ContainsKey(item.Key)) { warnings.Add($"items.txt: дубликат {item.Key}"); continue; }
            items.Add(item);
            _items[item.Key] = item;
        }
        Items = items;

        CounterRules = LoadPairRules(src, "counter_rules.txt", warnings);
        SynergyRules = LoadPairRules(src, "synergy_rules.txt", warnings);

        GapRules = src.Rows("gap_rules.txt", 4)
            .Select(f => new GapRule(f[0], ParseDouble(f[1]), f[2], f[3]))
            .ToArray();

        foreach (var f in src.Rows("role_item_weights.txt", 3))
        {
            if (!TryParsePosition(f[0], out var p)) continue;
            if (!_roleWeights.TryGetValue(p, out var m)) _roleWeights[p] = m = new(StringComparer.OrdinalIgnoreCase);
            m[f[1]] = ParseDouble(f[2]);
        }

        foreach (var f in src.Rows("hero_item_affinity.txt", 3))
        {
            if (!_affinity.TryGetValue(f[0], out var m)) _affinity[f[0]] = m = new(StringComparer.OrdinalIgnoreCase);
            m[f[1]] = ParseDouble(f[2]);
        }

        RoleTips = src.Rows("role_tips.txt", 4)
            .Where(f => TryParsePosition(f[0], out _))
            .Select(f => { TryParsePosition(f[0], out var p); return new RoleTip(p, f[1], f[2], f[3]); })
            .ToArray();

        foreach (var f in src.Rows("hero_builds.txt", 4))
        {
            if (!_byKey.ContainsKey(f[0])) { warnings.Add($"hero_builds.txt: неизвестный герой '{f[0]}'"); continue; }
            if (!TryParsePosition(f[1], out var p) || !TryParsePhase(f[2], out var phase)) { warnings.Add($"hero_builds.txt: неверная строка для {f[0]}"); continue; }
            var list = SplitList(f[3]);
            foreach (var key in list.Where(k => !_items.ContainsKey(k)))
                warnings.Add($"hero_builds.txt: {f[0]} — неизвестный предмет '{key}'");
            _curated[(f[0].ToLowerInvariant(), p, phase)] = list.Where(_items.ContainsKey).ToArray();
        }

        var timers = new List<TimerRule>();
        foreach (var f in src.Rows("timers.txt", 7))
        {
            if (!int.TryParse(f[3], out var first) || !int.TryParse(f[4], out var interval) || !int.TryParse(f[6], out var lead)
                || first < -90 || interval < 0 || lead < 0)
            { warnings.Add($"timers.txt: неверная строка для {f[0]}"); continue; }
            int? last = f[5] == "-" ? null : int.TryParse(f[5], out var l) ? l : null;
            if (timers.Any(t => string.Equals(t.Key, f[0], StringComparison.OrdinalIgnoreCase))) { warnings.Add($"timers.txt: дубликат {f[0]}"); continue; }
            var timerPositions = f.Length > 7 && f[7].Length > 0 && f[7] != "*" ? ParsePositions(f[7]) : null;
            timers.Add(new TimerRule(f[0], f[1], f[2], first, interval, last, Math.Min(lead, 120), timerPositions));
        }
        Timers = timers;

        var laneTips = new List<LaneTip>();
        foreach (var f in src.Rows("lane_tips.txt", 3))
        {
            if (!_traits.ContainsKey(f[0])) { warnings.Add($"lane_tips.txt: неизвестный признак '{f[0]}'"); continue; }
            laneTips.Add(new LaneTip(f[0], f[1], f[2]));
        }
        LaneTips = laneTips;

        var version = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in src.Lines("data_version.txt"))
        {
            var idx = line.IndexOf('=');
            if (idx > 0) version[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        VersionInfo = version;

        Warnings = warnings;
        foreach (var w in warnings) Log.Warn("DATA " + w);
        Log.Info($"База загружена: героев {Heroes.Count}, предметов {Items.Count}, правил контрпиков {CounterRules.Count}");
    }

    private IReadOnlyList<PairRule> LoadPairRules(DataSource src, string file, List<string> warnings)
    {
        var list = new List<PairRule>();
        foreach (var f in src.Rows(file, 5))
        {
            if (!_traits.ContainsKey(f[0]) || !_traits.ContainsKey(f[1]))
                warnings.Add($"{file}: неизвестный признак в правиле {f[0]}→{f[1]}");
            list.Add(new PairRule(f[0], f[1], ParseDouble(f[2]), f[3], f[4]));
        }
        return list;
    }

    private static string[] SplitList(string s) =>
        s == "-" || string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Position[] ParsePositions(string s) =>
        SplitList(s).Select(x => TryParsePosition(x, out var p) ? (Position?)p : null)
            .Where(p => p != null).Select(p => p!.Value).ToArray();

    private static bool TryParsePosition(string s, out Position p)
    {
        p = Position.Carry;
        if (!int.TryParse(s.Trim(), out var n) || n < 1 || n > 5) return false;
        p = (Position)n;
        return true;
    }

    private static bool TryParsePhase(string s, out BuildPhase phase) =>
        Enum.TryParse(s.Trim(), ignoreCase: true, out phase);

    private static PrimaryAttribute ParseAttribute(string s) => s.Trim().ToUpperInvariant() switch
    {
        "S" => PrimaryAttribute.Strength,
        "A" => PrimaryAttribute.Agility,
        "I" => PrimaryAttribute.Intelligence,
        _ => PrimaryAttribute.Universal
    };

    private static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Чтение строк из встроенного ресурса или файла-переопределения.</summary>
    private sealed class DataSource
    {
        private readonly string? _dir;
        private readonly string? _remote;
        public List<string> Overridden { get; } = new();
        public List<string> Remote { get; } = new();

        public DataSource(string? dir, string? remote = null) { _dir = dir; _remote = remote; }

        public IEnumerable<string> Lines(string file)
        {
            string text;
            var overridePath = _dir == null ? null : Path.Combine(_dir, file);
            var remotePath = _remote == null ? null : Path.Combine(_remote, file);
            if (overridePath != null && File.Exists(overridePath))
            {
                text = File.ReadAllText(overridePath);
                Overridden.Add(file);
            }
            else if (remotePath != null && File.Exists(remotePath))
            {
                text = File.ReadAllText(remotePath);
                Remote.Add(file);
            }
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("Roshiq.Data." + file)
                    ?? throw new InvalidOperationException("Встроенный ресурс не найден: " + file);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                yield return line;
            }
        }

        public IEnumerable<string[]> Rows(string file, int columns)
        {
            foreach (var line in Lines(file))
            {
                var parts = line.Split('|');
                if (parts.Length < columns)
                {
                    Log.Warn($"DATA {file}: пропущена строка (ожидалось {columns} полей): {line}");
                    continue;
                }
                for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
                yield return parts;
            }
        }
    }
}
