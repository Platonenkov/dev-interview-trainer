using System.Diagnostics;
using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

/// <summary>
/// Runs the two <c>run:</c> scripts of link-check.yml under bash with a recording <c>gh</c> and a scripted <c>dotnet</c>
/// on PATH, because the exit-code decision (which verdicts may open or close the issue) cannot be read off the YAML.
/// </summary>
public sealed class LinkCheckScriptTests : IDisposable
{
    private const string CheckStep = "Check source links";
    private const string IssueStep = "Update the tracking issue";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "link-check-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [Theory]
    // Verdict 0 closes every open dead-links issue and nothing else.
    [InlineData("0", "text", "", 0, "label create dead-links --force|issue list --label dead-links --state open --limit 100")]
    [InlineData("0", "text", "12", 0, "label create|issue list|issue close 12 --comment All source links resolve as of ")]
    [InlineData("0", "text", "12 15", 0, "label create|issue list|issue close 12 --comment All source links resolve as of |issue close 15 --comment All source links resolve as of ")]
    // Verdict 1 creates or edits the oldest issue, closes duplicates, and still fails the job.
    [InlineData("1", "text", "", 1, "label create|issue list|issue create --title Dead source links --label dead-links --body-file ")]
    [InlineData("1", "text", "12", 1, "label create|issue list|issue edit 12 --body-file ")]
    [InlineData("1", "text", "12 15", 1, "label create|issue list|issue edit 12 --body-file |issue close 15 --comment Duplicate of #12, which tracks the dead links.")]
    // Anything else, or no report to show for it, is a crash: the issue is never touched.
    [InlineData("1", "empty", "12", 1, "")]
    [InlineData("1", "none", "12", 1, "")]
    [InlineData("2", "text", "12", 1, "")]
    [InlineData("2", "none", "12", 1, "")]
    [InlineData("3", "text", "12", 1, "")]
    [InlineData("130", "none", "12", 1, "")]
    [InlineData("134", "text", "12", 1, "")]
    [InlineData("139", "none", "12", 1, "")]
    [InlineData("0", "empty", "12", 1, "")]
    [InlineData("0", "none", "12", 1, "")]
    public async Task IssueStep_ActsOnlyOnAVerdict(string checkerExit, string report, string openIssues, int expectedExit, string expectedCalls)
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        Setup setup = Prepare();
        Dictionary<string, string> environment = new()
        {
            ["STUB_DOTNET_EXIT"] = checkerExit,
            ["STUB_GH_LIST"] = openIssues,
        };
        if (report != "none")
        {
            environment["STUB_REPORT"] = report == "text" ? "## Link check\\n\\nChecked 1 URL(s).\\n" : string.Empty;
        }

        (int checkExit, string checkLog) = await RunAsync(bash!, setup, setup.Scripts[CheckStep], environment);
        Assert.True(checkExit == 0, checkLog);
        string output = (await File.ReadAllTextAsync(setup.GithubOutput, TestContext.Current.CancellationToken)).Trim();
        Assert.Equal($"code={checkerExit}", output);

        environment["CHECK_EXIT"] = output["code=".Length..];
        (int issueExit, string log) = await RunAsync(bash!, setup, setup.Scripts[IssueStep], environment);

        Assert.True(expectedExit == issueExit, $"exit code {issueExit}, expected {expectedExit}.\n{log}");
        string[] calls = File.Exists(setup.GhLog) ? await File.ReadAllLinesAsync(setup.GhLog, TestContext.Current.CancellationToken) : [];
        string[] expected = expectedCalls.Length == 0 ? [] : expectedCalls.Split('|');
        Assert.True(expected.Length == calls.Length, $"gh calls: {string.Join(" | ", calls)}");
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.StartsWith(expected[index], calls[index], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IssueStep_AMissingCheckerOutput_IsACrash()
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        Setup setup = Prepare();
        await File.WriteAllTextAsync(Path.Combine(setup.RunnerTemp, "links.md"), "## Link check\n", TestContext.Current.CancellationToken);

        (int exitCode, _) = await RunAsync(
            bash!, setup, setup.Scripts[IssueStep], new Dictionary<string, string> { ["CHECK_EXIT"] = string.Empty, ["STUB_GH_LIST"] = "12" });

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(setup.GhLog));
    }

    [Fact]
    public async Task CheckStep_RemovesAReportLeftByAnEarlierRun()
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        Setup setup = Prepare();
        string report = Path.Combine(setup.RunnerTemp, "links.md");
        await File.WriteAllTextAsync(report, "stale", TestContext.Current.CancellationToken);

        await RunAsync(bash!, setup, setup.Scripts[CheckStep], new Dictionary<string, string> { ["STUB_DOTNET_EXIT"] = "1" });

        Assert.False(File.Exists(report));
    }

    private sealed record Setup(Dictionary<string, string> Scripts, string Stubs, string RunnerTemp, string GithubOutput, string GhLog);

    private Setup Prepare()
    {
        string stubs = Path.Combine(_work, "bin");
        string runnerTemp = Path.Combine(_work, "runner-temp");
        Directory.CreateDirectory(stubs);
        Directory.CreateDirectory(runnerTemp);

        WriteExecutable(Path.Combine(stubs, "dotnet"), """
            #!/usr/bin/env bash
            report=""
            previous=""
            for argument in "$@"; do
              if [ "$previous" = "--report" ]; then report="$argument"; fi
              previous="$argument"
            done
            if [ -n "${STUB_REPORT+x}" ] && [ -n "$report" ]; then printf '%b' "$STUB_REPORT" > "$report"; fi
            exit "${STUB_DOTNET_EXIT:-0}"
            """);
        WriteExecutable(Path.Combine(stubs, "gh"), """
            #!/usr/bin/env bash
            echo "$*" >> "$GH_LOG"
            if [ "$1 $2" = "issue list" ]; then echo "${STUB_GH_LIST:-}"; fi
            exit 0
            """);

        return new Setup(LoadScripts(), stubs, runnerTemp, Path.Combine(_work, "github-output"), Path.Combine(_work, "gh.log"));
    }

    private static void WriteExecutable(string path, string text)
    {
        // The scripts run on Linux in CI and under Git Bash locally: keep LF endings whatever the checkout did.
        File.WriteAllText(path, text.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private Dictionary<string, string> LoadScripts()
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), ".github", "workflows", "link-check.yml");
        Dictionary<object, object?> root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(File.ReadAllText(path));
        Dictionary<object, object?> job = (Dictionary<object, object?>)((Dictionary<object, object?>)root["jobs"]!)["check-links"]!;
        Dictionary<string, string> scripts = [];
        foreach (Dictionary<object, object?> step in ((List<object?>)job["steps"]!).Cast<Dictionary<object, object?>>())
        {
            if (step.TryGetValue("name", out object? name) && step.TryGetValue("run", out object? run) && name is string stepName && run is string text)
            {
                string file = Path.Combine(_work, scripts.Count + ".sh");
                File.WriteAllText(file, text.ReplaceLineEndings("\n"));
                scripts[stepName] = file;
            }
        }

        Assert.Contains(CheckStep, scripts.Keys);
        Assert.Contains(IssueStep, scripts.Keys);
        return scripts;
    }

    private static async Task<(int ExitCode, string Log)> RunAsync(string bash, Setup setup, string script, Dictionary<string, string> environment)
    {
        ProcessStartInfo info = new(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = setup.RunnerTemp,
        };
        // The runner starts run: steps as "bash -e {0}"; the scripts set their own options as well.
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        info.Environment["PATH"] = setup.Stubs + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        info.Environment["RUNNER_TEMP"] = setup.RunnerTemp;
        info.Environment["GITHUB_OUTPUT"] = setup.GithubOutput;
        info.Environment["GH_LOG"] = setup.GhLog;
        foreach (KeyValuePair<string, string> pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string? FindBash()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
