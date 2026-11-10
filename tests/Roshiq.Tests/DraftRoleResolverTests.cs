using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Vision;
using Xunit;

namespace Roshiq.Tests;

public class DraftRoleResolverTests
{
    private static readonly IReadOnlyList<SlotRegion> Slots = DraftLayout.Compute(new DraftLayoutSettings(), 2560, 187, 1440);

    [Theory]
    [InlineData("Полная", "поддержка", Position.HardSupport)]
    [InlineData("Лёгкая", "", Position.Carry)]
    [InlineData("Сложная", "", Position.Offlane)]
    [InlineData("Центр", "", Position.Mid)]
    [InlineData("Поддержка", "", Position.SoftSupport)]
    public void ResolvesRoleOnlyUnderOwnName(string first, string second, Position expected)
    {
        var slot = Slots.Single(s => s.Side == Side.Radiant && s.Index == 2);
        var words = new List<DraftOcrWord>
        {
            new("OtherPlayer", 470, 115, 565, 132),
            new("MyPlayer", slot.X + 5, 115, slot.X + 105, 132),
            new(first, slot.X + 7, 145, slot.X + 70, 163),
            new("Центр", 500, 145, 550, 163)
        };
        if (second.Length > 0) words.Add(new(second, slot.X + 75, 145, slot.X + 135, 163));
        Assert.Equal(expected, DraftRoleResolver.Resolve(words, "MyPlayer", Slots, 187));
    }

    [Fact]
    public void ReturnsUnknownWhenNameOrRoleIsUnclear()
    {
        var words = new List<DraftOcrWord> { new("MyPlayer", 630, 115, 730, 132) };
        Assert.Null(DraftRoleResolver.Resolve(words, "MyPlayer", Slots, 187));
        words.Add(new("Центр", 660, 145, 710, 163));
        Assert.Null(DraftRoleResolver.Resolve(words, "AnotherPlayer", Slots, 187));
    }
}
