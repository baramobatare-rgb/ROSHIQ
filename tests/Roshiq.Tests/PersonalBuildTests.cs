using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class PersonalBuildTests
{
    private static PersonalBuild Sample() => new()
    {
        HeroKey = "antimage", Position = Position.Carry, Name = "Линия",
        Notes = "План на матч", Entries = new() {
            new() { ItemKey = "item_tango", Phase = BuildPhase.Start, Note = "Моя заметка", AlternativeKey = "item_branches" },
            new() { ItemKey = "item_branches", Phase = BuildPhase.Start },
            new() { ItemKey = "item_blink", Phase = BuildPhase.Mid }
        }
    };

    [Fact]
    public void PersonalData_RoundTripsAndExportsWithExistingSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dp_personal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var path = Path.Combine(dir, "settings.json");
            var service = new SettingsService(path);
            var build = Sample();
            service.Update(s => { s.FavoriteHeroKeys.Add("antimage"); s.PersonalBuilds.Add(build);
                s.ActivePersonalBuilds[PersonalBuild.ContextKey(build.HeroKey, build.Position)] = build.Id; });
            var loaded = new SettingsService(path).Current;
            Assert.Contains("antimage", loaded.FavoriteHeroKeys);
            Assert.Equal("План на матч", Assert.Single(loaded.PersonalBuilds).Notes);
            Assert.Equal("Моя заметка", loaded.PersonalBuilds[0].Entries[0].Note);
            Assert.Equal("item_branches", loaded.PersonalBuilds[0].Entries[0].AlternativeKey);
            Assert.Equal(build.Id, PersonalBuildResolver.Active(loaded, TestData.Hero("antimage"), Position.Carry)?.Id);
            var export = Path.Combine(dir, "export.json"); service.Export(export);
            var imported = new SettingsService(Path.Combine(dir, "other.json"));
            Assert.True(imported.Import(export, out _));
            Assert.Equal(build.Id, Assert.Single(imported.Current.PersonalBuilds).Id);
        } finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OldSettings_KeepDefaultsForNewFields()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"violet\",\"UiScale\":1.2}")!.Normalize();
        Assert.Empty(settings.FavoriteHeroKeys); Assert.Empty(settings.PersonalBuilds); Assert.Empty(settings.ActivePersonalBuilds);
        Assert.Equal("violet", settings.Theme); Assert.Equal(1.2, settings.UiScale);
        Assert.Equal("standard", settings.Background);
    }

    [Fact]
    public void Normalize_KeepsNewThemesAndBackgrounds_FixesUnknown()
    {
        var ok = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"gold\",\"Background\":\"black\"}")!.Normalize();
        Assert.Equal("gold", ok.Theme); Assert.Equal("black", ok.Background);
        var bad = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"pink\",\"Background\":null}")!.Normalize();
        Assert.Equal("lime", bad.Theme); Assert.Equal("standard", bad.Background);
    }

    [Fact]
    public void Resolve_PreservesOrderNotesAndSkipsUnknownItems()
    {
        var build = Sample();
        build.Entries.Insert(0, new() { ItemKey = "not_in_database", Phase = BuildPhase.Start });
        var resolved = PersonalBuildResolver.Resolve(build, TestData.Hero("antimage"), Position.Carry, TestData.Db)!;
        Assert.Equal(new[] { "item_tango", "item_branches" }, resolved[BuildPhase.Start].Select(x => x.Item.Key));
        Assert.Contains("Моя заметка", resolved[BuildPhase.Start][0].Explain(Language.Russian));
        Assert.Contains("alternative:", resolved[BuildPhase.Start][0].Explain(Language.English), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("item_branches", resolved.NextItem(new[] { "item_tango" })?.Item.Key);
        Assert.False(resolved.EnemyAware);
    }

    [Fact]
    public void ActiveBuild_IsIsolatedByHeroAndRole()
    {
        var build = Sample();
        var settings = new AppSettings { PersonalBuilds = new() { build } };
        settings.ActivePersonalBuilds[PersonalBuild.ContextKey(build.HeroKey, build.Position)] = build.Id;
        Assert.Null(PersonalBuildResolver.Active(settings, TestData.Hero("axe"), Position.Carry));
        Assert.Null(PersonalBuildResolver.Active(settings, TestData.Hero("antimage"), Position.Mid));
        Assert.Null(PersonalBuildResolver.Resolve(build, TestData.Hero("axe"), Position.Carry, TestData.Db));
        Assert.Null(PersonalBuildResolver.Resolve(build, TestData.Hero("antimage"), Position.Mid, TestData.Db));
    }

    [Fact]
    public void RemovingBuild_ClearsActiveSelectionAndLeavesOtherData()
    {
        var build = Sample(); var other = Sample(); other.Position = Position.Mid;
        var settings = new AppSettings { FavoriteHeroKeys = new() { "antimage" }, PersonalBuilds = new() { build, other } };
        settings.ActivePersonalBuilds[PersonalBuild.ContextKey(build.HeroKey, build.Position)] = build.Id;
        settings.ActivePersonalBuilds[PersonalBuild.ContextKey(other.HeroKey, other.Position)] = other.Id;
        settings.PersonalBuilds.Remove(build); settings.Normalize();
        Assert.Single(settings.ActivePersonalBuilds); Assert.Contains("antimage", settings.FavoriteHeroKeys);
        Assert.Equal(other.Id, Assert.Single(settings.PersonalBuilds).Id);
    }

    [Fact]
    public void EditingCopy_DoesNotMutateSavedBuild()
    {
        var build = Sample(); var copy = build.Copy();
        copy.Entries[0].Note = "Changed"; copy.Entries.RemoveAt(1);
        Assert.Equal("Моя заметка", build.Entries[0].Note); Assert.Equal(3, build.Entries.Count);
        Assert.Equal(build.Id, copy.Id); Assert.NotEqual(build.Id, build.Copy(true).Id);
    }

    [Fact]
    public void MissingOrSituationalOnlyBuild_FallsBackToReference()
    {
        var build = Sample(); build.Entries.Clear();
        build.Entries.Add(new() { ItemKey = "unknown", Phase = BuildPhase.Start });
        build.Entries.Add(new() { ItemKey = "item_tango", Phase = BuildPhase.Situational });
        Assert.Null(PersonalBuildResolver.Resolve(build, TestData.Hero("antimage"), Position.Carry, TestData.Db));
        var settings = new AppSettings { FavoriteHeroKeys = null!, PersonalBuilds = null!, ActivePersonalBuilds = null! }.Normalize();
        Assert.Empty(settings.PersonalBuilds);
    }
}
