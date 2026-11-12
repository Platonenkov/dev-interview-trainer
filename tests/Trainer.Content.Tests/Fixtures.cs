namespace Trainer.Content.Tests;

internal static class Fixtures
{
    public static string Valid => Path.Combine(AppContext.BaseDirectory, "TestContent", "valid");

    public static string Broken => Path.Combine(AppContext.BaseDirectory, "TestContent", "broken");

    /// <summary>The repository root, found by walking up to the solution file.</summary>
    public static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
            {
                directory = directory.Parent;
            }

            return directory is null
                ? throw new InvalidOperationException("Repository root (DevInterviewTrainer.slnx) was not found above the test output directory.")
                : directory.FullName;
        }
    }

    /// <summary>The committed content directory.</summary>
    public static string RepositoryContent => Path.Combine(RepositoryRoot, "content");
}
