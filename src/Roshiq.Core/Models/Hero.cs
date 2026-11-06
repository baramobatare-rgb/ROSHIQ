namespace Roshiq.Core.Models;

/// <summary>Герой Dota 2 с набором качественных признаков (traits).</summary>
public sealed class Hero
{
    public Hero(int id, string key, string name, string nameRu, PrimaryAttribute attribute,
        IEnumerable<Position> positions, IEnumerable<string> traits)
    {
        Id = id;
        Key = key;
        Name = name;
        NameRu = nameRu;
        Attribute = attribute;
        Positions = positions.ToArray();
        Traits = new HashSet<string>(traits, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Числовой id героя Dota 2 (как в GSI и OpenDota).</summary>
    public int Id { get; }
    /// <summary>Внутреннее имя без префикса npc_dota_hero_ (например, "antimage").</summary>
    public string Key { get; }
    public string Name { get; }
    public string NameRu { get; }
    public PrimaryAttribute Attribute { get; }
    /// <summary>Позиции по убыванию типичности.</summary>
    public IReadOnlyList<Position> Positions { get; }
    public IReadOnlySet<string> Traits { get; }

    public string InternalName => "npc_dota_hero_" + Key;
    public bool Has(string trait) => Traits.Contains(trait);
    public bool PlaysPosition(Position p) => Positions.Contains(p);
    // Имена игровых сущностей всегда английские; NameRu сохраняется для поиска.
    public string DisplayName(Language lang) => Name;
    public override string ToString() => Name;
}

/// <summary>Предмет с тегами "что даёт", "против чего" и требованиями к герою.</summary>
public sealed class Item
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string NameRu { get; init; }
    public required BuildPhase Phase { get; init; }
    /// <summary>Группа взаимоисключения (например, boots) или пусто.</summary>
    public string? Group { get; init; }
    /// <summary>Пустой список = подходит всем позициям.</summary>
    public required IReadOnlyList<Position> Positions { get; init; }
    public required IReadOnlySet<string> Provides { get; init; }
    public required IReadOnlySet<string> Counters { get; init; }
    /// <summary>Признаки, которые обязаны быть у героя.</summary>
    public required IReadOnlyList<string> Requires { get; init; }
    /// <summary>Признаки, которых у героя быть не должно.</summary>
    public required IReadOnlyList<string> Excludes { get; init; }
    public required string PurposeRu { get; init; }
    public required string PurposeEn { get; init; }

    public string DisplayName(Language lang) => Name;
    public string Purpose(Language lang) => lang == Language.Russian ? PurposeRu : PurposeEn;

    public bool SuitsPosition(Position p) => Positions.Count == 0 || Positions.Contains(p);

    public bool SuitsHero(Hero hero) =>
        Requires.All(hero.Has) && !Excludes.Any(hero.Has);

    public override string ToString() => Name;
}

/// <summary>Объяснение рекомендации на двух языках.</summary>
public sealed record Reason(ReasonKind Kind, double Weight, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.Russian ? Ru : En;
}
