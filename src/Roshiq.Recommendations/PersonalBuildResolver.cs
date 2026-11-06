using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Resolve only the explicitly selected hero/role build; never invent missing items.</summary>
public static class PersonalBuildResolver
{
    public static PersonalBuild? Active(AppSettings settings, Hero hero, Position position)
    {
        settings.ActivePersonalBuilds.TryGetValue(PersonalBuild.ContextKey(hero.Key, position), out var id);
        return settings.PersonalBuilds.FirstOrDefault(x => x.Id == id && x.HeroKey == hero.Key && x.Position == position);
    }

    public static BuildRecommendation? Resolve(PersonalBuild? build, Hero hero, Position position, GameDatabase db)
    {
        if (build == null || build.HeroKey != hero.Key || build.Position != position) return null;
        var phases = new Dictionary<BuildPhase, IReadOnlyList<ItemPick>>();
        foreach (var phase in Enum.GetValues<BuildPhase>())
        {
            var picks = new List<ItemPick>();
            foreach (var entry in build.Entries.Where(x => x.Phase == phase))
            {
                var item = db.FindItem(entry.ItemKey);
                if (item == null) continue;
                var reasons = new List<Reason>();
                if (!string.IsNullOrWhiteSpace(entry.Note)) reasons.Add(new(ReasonKind.Item, 0, entry.Note, entry.Note));
                if (db.FindItem(entry.AlternativeKey) is Item alt)
                    reasons.Add(new(ReasonKind.Item, 0, "альтернатива: " + alt.Name, "alternative: " + alt.Name));
                picks.Add(new(item, 0, reasons, false));
            }
            phases[phase] = picks;
        }
        // An imported build containing only unknown items must not blank the live overlay.
        if (!new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late }.Any(x => phases[x].Count > 0)) return null;
        return new BuildRecommendation { Hero = hero, Position = position, Phases = phases, EnemyAware = false, HasCuratedBase = false };
    }
}
