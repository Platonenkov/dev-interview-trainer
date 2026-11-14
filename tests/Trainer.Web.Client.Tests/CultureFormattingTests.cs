using System.Globalization;
using Trainer.Web.Client.Localization;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// The client runs with invariant globalization (ADR 0013): a culture keeps its name, which picks the satellite
/// resources, but carries invariant data. Nothing may depend on that data; these tests stand in for it with a culture
/// whose language data is wrong.
/// </summary>
public sealed class CultureFormattingTests
{
    /// <summary>What invariant mode yields for a named culture: the name survives, the language data does not.</summary>
    private sealed class InvariantDataCulture(string name) : CultureInfo(name)
    {
        public override string TwoLetterISOLanguageName => "iv";

        public override string ThreeLetterISOLanguageName => "ivl";
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    public void UiLanguage_ComesFromTheCultureName(string name, string language) =>
        Assert.Equal(language, UiLanguage.Of(new InvariantDataCulture(name)));

    [Theory]
    [InlineData("ru", 1, "One")]
    [InlineData("ru", 3, "Few")]
    [InlineData("ru", 5, "Many")]
    [InlineData("en", 1, "One")]
    [InlineData("en", 3, "Many")]
    public void PluralForms_UseTheCultureName_NotItsLanguageData(string name, int count, string form) =>
        Assert.Equal("Profile.Streak." + form, PluralForms.Key("Profile.Streak", count, new InvariantDataCulture(name)));
}
