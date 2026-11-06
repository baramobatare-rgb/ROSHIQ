using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Советы по роли и герою на основе базы советов и признаков героя.</summary>
public sealed class RoleAdvisor
{
    private readonly GameDatabase _db;

    public RoleAdvisor(GameDatabase db) { _db = db; }

    public IReadOnlyList<RoleTip> TipsFor(Position position) =>
        _db.RoleTips.Where(t => t.Position == position).ToList();

    /// <summary>Советы, зависящие от сочетания героя и роли (выводятся из признаков, а не из шаблонного текста).</summary>
    public IReadOnlyList<Reason> HeroTips(Hero hero, Position position)
    {
        var tips = new List<Reason>();
        if (!hero.PlaysPosition(position))
            tips.Add(new Reason(ReasonKind.Risk, 0,
                $"{hero.Name} нетипичен для позиции {(int)position}; обычные позиции: {string.Join(", ", hero.Positions.Select(p => (int)p))}",
                $"{hero.Name} is unusual in position {(int)position}; usual positions: {string.Join(", ", hero.Positions.Select(p => (int)p))}"));

        void Add(bool cond, string ru, string en) { if (cond) tips.Add(new Reason(ReasonKind.Info, 0, ru, en)); }

        Add(hero.Has("late_game") && position == Position.Carry,
            "Герой раскрывается в поздней игре: приоритет — фарм и безопасное развитие",
            "This hero scales late: prioritize farm and safe development");
        Add(hero.Has("early_game"),
            "Сильная ранняя игра: используйте её для давления до того, как враги наберут предметы",
            "Strong early game: pressure before enemies get their items");
        Add(hero.Has("fragile"),
            "Герой хрупкий: держите дистанцию и продумывайте путь отхода",
            "Fragile hero: keep distance and plan your escape");
        Add(hero.Has("initiator"),
            "Инициатор: начинайте драку, когда союзники рядом и способности готовы",
            "Initiator: start fights when allies are near and spells are ready");
        Add(hero.Has("mana_dependent"),
            "Зависит от маны: следите за запасом маны перед дракой",
            "Mana dependent: check your mana before a fight");
        Add(hero.Has("channeling"),
            "Есть поддерживаемые заклинания: ищите безопасную позицию перед их применением",
            "Has channeled spells: find a safe position before channeling");
        Add(hero.Has("save") && position is Position.SoftSupport or Position.HardSupport,
            "Ваши спасающие способности — главный ресурс в драке: не тратьте их на атаку",
            "Your saves are your key fight resource: don't waste them offensively");
        Add(hero.Has("push"),
            "Хорошо давит строения: используйте моменты после выигранной драки",
            "Good at pushing: take buildings after won fights");
        Add(hero.Has("global"),
            "Глобальное присутствие: помогайте другим линиям, следя за картой",
            "Global presence: help other lanes by watching the map");
        return tips;
    }
}

/// <summary>Определение вероятной роли игрока по разрешённым данным (выбор пользователя, герой из GSI, предметы).</summary>
public static class RoleDetector
{
    public sealed record Result(Position Position, Confidence Confidence, string ReasonRu, string ReasonEn);

    public static Result Detect(Position userSelected, bool userConfirmed, Hero? hero, IReadOnlyCollection<string>? items)
    {
        if (userConfirmed)
            return new Result(userSelected, Confidence.High, "Позиция выбрана пользователем", "Position selected by the user");

        bool hasSupportItems = items != null && items.Any(i =>
            i.Equals("item_ward_observer", StringComparison.OrdinalIgnoreCase) ||
            i.Equals("item_ward_sentry", StringComparison.OrdinalIgnoreCase) ||
            i.Equals("item_ward_dispenser", StringComparison.OrdinalIgnoreCase) ||
            i.Equals("item_smoke_of_deceit", StringComparison.OrdinalIgnoreCase));

        if (hero == null)
            return new Result(userSelected, Confidence.Low, "Герой ещё не известен — используется выбранная позиция", "Hero unknown — using selected position");

        if (hasSupportItems)
        {
            var sup = hero.Positions.FirstOrDefault(p => p is Position.SoftSupport or Position.HardSupport);
            if (hero.PlaysPosition(Position.SoftSupport) || hero.PlaysPosition(Position.HardSupport))
                return new Result(sup, Confidence.Medium, "В инвентаре варды/смок — похоже на роль саппорта", "Wards/smoke in inventory — looks like a support role");
        }

        if (hero.PlaysPosition(userSelected))
            return new Result(userSelected, Confidence.Medium, "Герой подходит выбранной позиции", "Hero fits the selected position");

        return new Result(hero.Positions.Count > 0 ? hero.Positions[0] : userSelected, Confidence.Low,
            "Выбранная позиция нетипична для героя — предложена основная", "Selected position is unusual for this hero — suggesting the primary one");
    }
}
