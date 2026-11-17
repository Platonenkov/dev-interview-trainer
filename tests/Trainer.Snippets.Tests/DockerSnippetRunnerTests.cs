using System.Diagnostics;
using System.Text.Json;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Validation;

namespace Trainer.Snippets.Tests;

/// <summary>
/// Integration tests against a real Docker daemon. Every test removes the sb-/sr- containers it may have left behind,
/// including when an assertion fails, so a red run never leaks a busy-looping container.
/// </summary>
[Trait("Category", "Docker")]
public sealed class DockerSnippetRunnerTests : IAsyncLifetime
{
    private static readonly SandboxOptions FastOptions = new() { RunDeadline = TimeSpan.FromSeconds(4) };

    // Tests that expect output or an exception must not race the deadline: a cold container start under a parallel
    // full-solution run has taken longer than FastOptions allows. Only the tests that expect a timeout use FastOptions.
    private static readonly SandboxOptions CompletingOptions = new() { RunDeadline = TimeSpan.FromSeconds(30) };

    public async ValueTask InitializeAsync() => await new DockerSnippetRunner(FastOptions).PrepareAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await RemoveSandboxContainersAsync();

    private static CodeBlock Statements(string source) => new() { Language = "csharp", Harness = "statements", Source = source };

    private static async Task<SnippetResult> BuildAndRunAsync(string source, SandboxOptions? options = null)
    {
        DockerSnippetRunner runner = new(options ?? CompletingOptions);
        await using SnippetBuild build = await runner.BuildAsync(Statements(source), CancellationToken.None);
        return await runner.RunAsync(build, CancellationToken.None);
    }

    private static async Task<(int ExitCode, string Output)> DockerAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    private static async Task<string[]> SandboxContainerNamesAsync()
    {
        (_, string output) = await DockerAsync("ps", "-a", "--filter", "name=^/s[br]-", "--format", "{{.Names}}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task RemoveSandboxContainersAsync()
    {
        foreach (string name in await SandboxContainerNamesAsync())
        {
            await DockerAsync("rm", "-f", name);
        }
    }

    private static async Task<string> WaitForRunningContainerAsync(string prefix)
    {
        for (int i = 0; i < 100; i++)
        {
            (_, string output) = await DockerAsync("ps", "--filter", $"name=^/{prefix}", "--format", "{{.Names}}");
            string? name = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (name is not null)
            {
                return name;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No running container named {prefix}* appeared.");
    }

    [Fact]
    public async Task ClosureForLoop_Prints333()
    {
        SnippetResult result = await BuildAndRunAsync(
            "var actions = new List<Action>();\nfor (int i = 0; i < 3; i++) { actions.Add(() => Console.Write(i)); }\nforeach (Action a in actions) { a(); }");
        Assert.Equal(new SnippetResult("output", "333"), result);
    }

    [Fact]
    public async Task CompileError_IsReportedFromBuild()
    {
        DockerSnippetRunner runner = new(FastOptions);
        await using SnippetBuild build = await runner.BuildAsync(Statements("Console.Write(undefinedName);"), CancellationToken.None);
        Assert.Equal(new SnippetResult("compile-error", "CS0103"), build.Failure);
    }

    [Fact]
    public async Task UnhandledException_IsReportedWithTypeName()
    {
        SnippetResult result = await BuildAndRunAsync("throw new InvalidOperationException(\"boom\");");
        Assert.Equal(new SnippetResult("exception", "System.InvalidOperationException"), result);
    }

    [Fact]
    public async Task CultureIsInvariant()
    {
        SnippetResult result = await BuildAndRunAsync("Console.Write(1.5.ToString());");
        Assert.Equal(new SnippetResult("output", "1.5"), result);
    }

    [Fact]
    public async Task RunContainer_HasNoNetwork()
    {
        DockerSnippetRunner runner = new(FastOptions with { RunDeadline = TimeSpan.FromSeconds(20) });
        await using SnippetBuild build = await runner.BuildAsync(Statements("Thread.Sleep(30000);"), CancellationToken.None);
        using CancellationTokenSource cts = new();
        Task<SnippetResult> run = runner.RunAsync(build, cts.Token);

        string name = await WaitForRunningContainerAsync("sr-");
        (int exitCode, string inspect) = await DockerAsync("inspect", name);
        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(inspect);
        JsonElement hostConfig = json.RootElement[0].GetProperty("HostConfig");
        Assert.Equal("none", hostConfig.GetProperty("NetworkMode").GetString());
        Assert.True(hostConfig.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.Equal(128, hostConfig.GetProperty("PidsLimit").GetInt64());

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task NameResolution_FailsInsideRunContainer()
    {
        SnippetResult result = await BuildAndRunAsync("System.Net.Dns.GetHostEntry(\"example.com\");");
        Assert.Equal("exception", result.Kind);
    }

    [Fact]
    public async Task InfiniteLoop_IsKilledAtRunDeadline()
    {
        DockerSnippetRunner runner = new(FastOptions);
        await using SnippetBuild build = await runner.BuildAsync(Statements("while (true) { }"), CancellationToken.None);

        Stopwatch stopwatch = Stopwatch.StartNew();
        SnippetResult result = await runner.RunAsync(build, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("timeout", result.Kind);
        Assert.True(stopwatch.Elapsed < FastOptions.RunDeadline + TimeSpan.FromSeconds(5), $"Run phase took {stopwatch.Elapsed}.");
        Assert.Empty(await SandboxContainerNamesAsync());
    }

    [Fact]
    public async Task BuildDeadline_LeavesNoBuildContainer()
    {
        DockerSnippetRunner runner = new(FastOptions with { BuildDeadline = TimeSpan.FromSeconds(1) });
        await using SnippetBuild build = await runner.BuildAsync(Statements("Console.Write(1);"), CancellationToken.None);

        Assert.Equal("sandbox-error", build.Failure?.Kind);
        (_, string output) = await DockerAsync("ps", "-a", "--filter", "name=^/sb-", "--format", "{{.Names}}");
        Assert.Empty(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task IgnoredSigterm_StillKilled()
    {
        SnippetResult result = await BuildAndRunAsync(
            """
            using System.Runtime.InteropServices;
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => ctx.Cancel = true);
            using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => ctx.Cancel = true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { while (true) { } };
            while (true) { }
            """,
            FastOptions);
        Assert.Equal("timeout", result.Kind);
        Assert.Empty(await SandboxContainerNamesAsync());
    }

    [Fact]
    public async Task OutputFlood_IsCappedAndReported()
    {
        SandboxOptions options = FastOptions with { OutputCapBytes = 4096 };
        SnippetResult result = await BuildAndRunAsync(
            "var chunk = new string('x', 1024);\nwhile (true) { Console.Write(chunk); }", options);
        Assert.True(result.Kind is "sandbox-error" or "timeout", $"Unexpected kind {result.Kind}.");
        Assert.Empty(await SandboxContainerNamesAsync());
    }

    [Fact]
    public async Task Cancellation_RemovesContainer()
    {
        DockerSnippetRunner runner = new(FastOptions with { RunDeadline = TimeSpan.FromSeconds(30) });
        await using SnippetBuild build = await runner.BuildAsync(Statements("while (true) { }"), CancellationToken.None);
        using CancellationTokenSource cts = new();
        Task<SnippetResult> run = runner.RunAsync(build, cts.Token);

        await WaitForRunningContainerAsync("sr-");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(await SandboxContainerNamesAsync());
    }

    [Fact]
    public async Task BuildContainer_HasNoNetworkAndIsRemoved()
    {
        DockerSnippetRunner runner = new(FastOptions);
        using CancellationTokenSource cts = new();
        Task<SnippetBuild> build = runner.BuildAsync(Statements("Console.Write(1);"), cts.Token);

        string name = await WaitForRunningContainerAsync("sb-");
        (_, string inspect) = await DockerAsync("inspect", name);
        using JsonDocument json = JsonDocument.Parse(inspect);
        Assert.Equal("none", json.RootElement[0].GetProperty("HostConfig").GetProperty("NetworkMode").GetString());

        await using SnippetBuild finished = await build;
        Assert.Null(finished.Failure);
        Assert.Empty(await SandboxContainerNamesAsync());
    }

    [Fact]
    public async Task VerifierOverRealRunner_AcceptsValidFixtureSet()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "TestContent", "valid");
        ContentSet set = await new ContentReader().ReadAsync(root, CancellationToken.None);

        IReadOnlyList<ContentIssue> issues = await new SnippetVerifier(new DockerSnippetRunner(new SandboxOptions()))
            .VerifyAsync(set, CancellationToken.None);

        Assert.Empty(issues);
    }
}
