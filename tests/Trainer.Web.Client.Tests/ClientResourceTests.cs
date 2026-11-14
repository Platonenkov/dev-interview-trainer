using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trainer.Web.Client.Localization;

namespace Trainer.Web.Client.Tests;

public sealed class ClientResourceTests
{
    private static readonly string Folder = Path.Combine(RepositoryRoot(), "src", "Trainer.Web.Client", "Localization");

    [Fact]
    public void ClientResx_RuAndEn_HaveSameKeys()
    {
        string[] ru = KeysOf(Path.Combine(Folder, "ClientResource.ru.resx"));
        string[] en = KeysOf(Path.Combine(Folder, "ClientResource.en.resx"));

        Assert.NotEmpty(en);
        Assert.Equal(en, ru);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void ClientResource_ResolvesEveryKeyInItsCulture(string culture)
    {
        ResourceManagerStringLocalizerFactory factory = new(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        IStringLocalizer localizer = factory.Create(typeof(ClientResource));
        Dictionary<string, string> expected = ValuesOf(Path.Combine(Folder, $"ClientResource.{culture}.resx"));
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach ((string key, string value) in expected)
            {
                LocalizedString found = localizer[key];
                Assert.False(found.ResourceNotFound, $"{key} is missing in {culture}.");
                Assert.Equal(value, found.Value);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("en", 1, "One")]
    [InlineData("en", 2, "Many")]
    [InlineData("en", 21, "Many")]
    [InlineData("ru", 1, "One")]
    [InlineData("ru", 21, "One")]
    [InlineData("ru", 11, "Many")]
    [InlineData("ru", 3, "Few")]
    [InlineData("ru", 24, "Few")]
    [InlineData("ru", 13, "Many")]
    [InlineData("ru", 5, "Many")]
    [InlineData("ru", 0, "Many")]
    public void PluralForms_PickTheCulturesForm(string culture, int count, string form) =>
        Assert.Equal("Storage.Full." + form, PluralForms.Key("Storage.Full", count, CultureInfo.GetCultureInfo(culture)));

    // The client tests reference only the client, so they find the sources themselves instead of using TestSupport.
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string[] KeysOf(string resxPath) => [.. ValuesOf(resxPath).Keys.Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> ValuesOf(string resxPath) =>
        XDocument.Load(resxPath).Root!.Elements("data").ToDictionary(
            e => (string)e.Attribute("name")!,
            e => (string)e.Element("value")!,
            StringComparer.Ordinal);
}
