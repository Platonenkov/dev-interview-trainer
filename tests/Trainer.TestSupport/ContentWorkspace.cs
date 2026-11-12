namespace Trainer.TestSupport;

/// <summary>A throwaway copy of a content root that tests can edit to produce another version of the content.</summary>
public sealed class ContentWorkspace : IDisposable
{
    private ContentWorkspace(string root) => Root = root;

    public string Root { get; }

    /// <summary>The committed valid fixture set: 6 questions, 3 topics, one stack.</summary>
    public static string ValidFixtureRoot => Path.Combine(RepositoryRoot(), "tests", "Trainer.Content.Tests", "TestContent", "valid");

    /// <summary>Fixture files that fail to read or validate, for example <c>dotnet/csharp/bad-date.yaml</c>.</summary>
    public static string BrokenFixtureRoot => Path.Combine(RepositoryRoot(), "tests", "Trainer.Content.Tests", "TestContent", "broken");

    /// <summary>The valid set plus the extras web tests need: an English-only question, one with a closing script tag in its title, one to retire, and a redirect.</summary>
    public static string WebFixtureRoot => Path.Combine(RepositoryRoot(), "tests", "Trainer.Web.Tests", "TestContent", "web");

    public static ContentWorkspace CopyOfWeb() => CopyOf(WebFixtureRoot);

    public static ContentWorkspace CopyOfValid() => CopyOf(ValidFixtureRoot);

    public static ContentWorkspace CopyOf(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "dit-content-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source, root);
        return new ContentWorkspace(root);
    }

    public ContentWorkspace Clone() => CopyOf(Root);

    public string Read(string relativePath) => File.ReadAllText(PathOf(relativePath));

    /// <summary>Replaces the first occurrence of <paramref name="oldText"/>; fails loudly when the anchor is missing.</summary>
    public void Edit(string relativePath, string oldText, string newText)
    {
        string path = PathOf(relativePath);
        string text = File.ReadAllText(path);
        int index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"'{oldText}' was not found in {relativePath}.");
        }

        File.WriteAllText(path, text.Remove(index, oldText.Length).Insert(index, newText));
    }

    public void Write(string relativePath, string text)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Delete(string relativePath) => File.Delete(PathOf(relativePath));

    public void Move(string fromRelativePath, string toRelativePath) => File.Move(PathOf(fromRelativePath), PathOf(toRelativePath));

    public void CopyIn(string sourceFile, string relativePath) =>
        File.Copy(sourceFile, PathOf(relativePath), overwrite: true);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private string PathOf(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root (DevInterviewTrainer.slnx) was not found above the test output directory.");
    }
}
