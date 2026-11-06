using Roshiq.Core.Models;

namespace Roshiq.Core.Settings;

public sealed class PersonalBuild
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string HeroKey { get; set; } = "";
    public Position Position { get; set; } = Position.Carry;
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<PersonalBuildEntry> Entries { get; set; } = new();
    public static string ContextKey(string heroKey, Position position) => $"{heroKey}:{(int)position}";
    public PersonalBuild Copy(bool newIdentity = false) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : Id, HeroKey = HeroKey, Position = Position,
        Name = Name, Notes = Notes,
        Entries = Entries.Select(x => new PersonalBuildEntry { ItemKey = x.ItemKey, Phase = x.Phase,
            Note = x.Note, AlternativeKey = x.AlternativeKey }).ToList()
    };
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        Notes ??= "";
        Entries = (Entries ?? new()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.ItemKey) && Enum.IsDefined(x.Phase)).ToList();
        foreach (var entry in Entries) entry.Note ??= "";
    }
}

public sealed class PersonalBuildEntry
{
    public string ItemKey { get; set; } = "";
    public BuildPhase Phase { get; set; }
    public string Note { get; set; } = "";
    public string? AlternativeKey { get; set; }
}
