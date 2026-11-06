using Roshiq.Core.Diagnostics;
using Roshiq.Data;

namespace Roshiq.Tests;

/// <summary>Общая база для тестов (загружается один раз, без пользовательских переопределений).</summary>
public static class TestData
{
    private static readonly Lazy<GameDatabase> Lazy = new(() =>
    {
        Log.FileEnabled = false;
        return GameDatabase.Load(overrideDirectory: null);
    });

    public static GameDatabase Db => Lazy.Value;

    public static Roshiq.Core.Models.Hero Hero(string key) =>
        Db.FindHero(key) ?? throw new InvalidOperationException("нет героя " + key);
}
