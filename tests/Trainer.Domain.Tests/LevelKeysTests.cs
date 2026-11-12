using Trainer.Domain.Levels;

namespace Trainer.Domain.Tests;

public sealed class LevelKeysTests
{
    [Theory]
    [InlineData("junior", Level.Junior)]
    [InlineData("ARCHITECT", Level.Architect)]
    public void TryParse_AcceptsKnownKeysCaseInsensitive(string key, Level expected)
    {
        Assert.True(LevelKeys.TryParse(key, out Level level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("expert")]
    public void TryParse_RejectsUnknownKeys(string? key) => Assert.False(LevelKeys.TryParse(key, out _));

    [Fact]
    public void Levels_AreOrdered() => Assert.True(Level.Senior > Level.Middle);

    [Fact]
    public void ToKey_IsLowercaseAndRoundTripsForAllLevels()
    {
        Assert.Equal(5, LevelKeys.All.Count);
        foreach (Level level in LevelKeys.All)
        {
            string key = LevelKeys.ToKey(level);
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.True(LevelKeys.TryParse(key, out Level parsed));
            Assert.Equal(level, parsed);
        }
    }

    [Fact]
    public void All_IsOrderedAscending() =>
        Assert.Equal(
            new[] { Level.Junior, Level.Middle, Level.Senior, Level.Lead, Level.Architect },
            LevelKeys.All);
}
