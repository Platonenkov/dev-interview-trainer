using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class StylesheetTests
{
    [Fact]
    public void Stylesheets_ContainNoControlCharacters()
    {
        // A CSS escape such as \25B8 loses its backslash when a tool treats it as a C-style escape, leaving a control character.
        string css = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "wwwroot", "css");
        string[] files = Directory.GetFiles(css, "*.css");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            int bad = text.IndexOfAny([.. Enumerable.Range(0, 32).Where(c => c is not ('\t' or '\n' or '\r')).Select(c => (char)c)]);
            Assert.True(bad < 0, $"{Path.GetFileName(file)} has control character U+{(bad < 0 ? 0 : text[bad]):X4} at index {bad}.");
        }
    }
}
