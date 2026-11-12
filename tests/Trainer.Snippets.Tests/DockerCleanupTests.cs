using System.Diagnostics;

namespace Trainer.Snippets.Tests;

/// <summary>Unit tests of the sandbox arguments and of container cleanup; no Docker daemon is needed.</summary>
public sealed class DockerCleanupTests
{
    private sealed class RecordingRunner(TimeSpan? hangFirstCommand = null) : IDockerCommandRunner
    {
        private readonly List<string> _commands = [];
        private readonly List<bool> _tokenWasCancelledOnEntry = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_gate)
                {
                    return [.. _commands];
                }
            }
        }

        public IReadOnlyList<bool> TokenWasCancelledOnEntry
        {
            get
            {
                lock (_gate)
                {
                    return [.. _tokenWasCancelledOnEntry];
                }
            }
        }

        public async Task RunQuietAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            bool first;
            lock (_gate)
            {
                first = _commands.Count == 0;
                _commands.Add(string.Join(' ', arguments));
                _tokenWasCancelledOnEntry.Add(ct.IsCancellationRequested);
            }

            if (first && hangFirstCommand is not null)
            {
                try
                {
                    await Task.Delay(hangFirstCommand.Value, ct);
                }
                catch (OperationCanceledException)
                {
                    // A hung docker client is abandoned when its own budget runs out.
                }
            }
        }
    }

    [Fact]
    public void BuildArguments_CapSwapAtTheMemoryLimit()
    {
        SandboxOptions options = new() { BuildMemoryMb = 1536 };
        DockerSnippetRunner runner = new(options);

        List<string> args = runner.BuildArguments("sb-x", "/src", "/out");

        int memory = args.IndexOf("--memory");
        int swap = args.IndexOf("--memory-swap");
        Assert.True(memory >= 0 && swap >= 0, "Both --memory and --memory-swap must be present.");
        Assert.Equal("1536m", args[memory + 1]);
        Assert.Equal(args[memory + 1], args[swap + 1]);
    }

    [Fact]
    public void RunArguments_CapSwapAtTheMemoryLimit()
    {
        DockerSnippetRunner runner = new(new SandboxOptions { RunMemoryMb = 128 });

        List<string> args = runner.RunArguments("sr-x", "/out");

        Assert.Equal("128m", args[args.IndexOf("--memory") + 1]);
        Assert.Equal("128m", args[args.IndexOf("--memory-swap") + 1]);
    }

    [Fact]
    public async Task RunContainer_WithAlreadyCancelledCaller_StillKillsAndRemovesTwice()
    {
        RecordingRunner cleanup = new();
        DockerProcess docker = new("dotnet", TimeSpan.FromSeconds(5), cleanup);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            docker.RunContainerAsync(["--list-sdks"], "sb-test", TimeSpan.FromSeconds(30), 4096, cts.Token));

        Assert.Equal(["kill sb-test", "rm -f sb-test", "kill sb-test", "rm -f sb-test"], cleanup.Commands);
        Assert.All(cleanup.TokenWasCancelledOnEntry, cancelled => Assert.False(cancelled));
    }

    private static (string Executable, string[] Arguments) HangingClient() =>
        OperatingSystem.IsWindows() ? ("ping", ["-n", "60", "127.0.0.1"]) : ("sleep", ["60"]);

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilGoneAsync(int pid)
    {
        for (int i = 0; i < 100 && IsAlive(pid); i++)
        {
            await Task.Delay(50);
        }
    }

    private sealed class AliveProbingRunner(Func<int> clientPid) : IDockerCommandRunner
    {
        public List<(string Command, bool ClientAlive)> Calls { get; } = [];

        public Task RunQuietAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Calls.Add((string.Join(' ', arguments), IsAlive(clientPid())));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunContainer_DeadlineKillsTheClientBeforeTheSecondRemoval()
    {
        (string executable, string[] arguments) = HangingClient();
        int pid = 0;
        AliveProbingRunner cleanup = new(() => pid);
        DockerProcess docker = new(executable, TimeSpan.FromSeconds(5), cleanup, started => pid = started);

        DockerProcessResult result = await docker.RunContainerAsync(
            arguments, "sb-test", TimeSpan.FromMilliseconds(300), 4096, TestContext.Current.CancellationToken);

        Assert.True(result.KilledByDeadline);
        Assert.Equal(["kill sb-test", "rm -f sb-test", "kill sb-test", "rm -f sb-test"], cleanup.Calls.Select(c => c.Command));
        Assert.Equal([true, true, false, false], cleanup.Calls.Select(c => c.ClientAlive));
    }

    [Fact]
    public async Task CleanupClient_ThatOutlivesItsBudget_IsKilled()
    {
        (string executable, string[] arguments) = HangingClient();
        int pid = 0;
        DockerProcess.ProcessRunner runner = new(executable, started => pid = started);
        using CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(300));

        await runner.RunQuietAsync(arguments, budget.Token);
        await WaitUntilGoneAsync(pid);

        Assert.NotEqual(0, pid);
        Assert.False(IsAlive(pid), "The hung cleanup client must be killed when its budget runs out.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveCleanupTimeout_IsRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerProcess("docker", TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void InfiniteCleanupTimeout_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerProcess("docker", Timeout.InfiniteTimeSpan));

    [Fact]
    public async Task RemoveContainer_HungKill_DoesNotStarveRm()
    {
        RecordingRunner cleanup = new(hangFirstCommand: TimeSpan.FromSeconds(30));
        DockerProcess docker = new("dotnet", TimeSpan.FromMilliseconds(200), cleanup);

        await docker.RemoveContainerAsync("sb-test").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["kill sb-test", "rm -f sb-test"], cleanup.Commands);
    }

    [Fact]
    public async Task RunContainer_WithoutContainerName_IssuesNoCleanup()
    {
        RecordingRunner cleanup = new();
        DockerProcess docker = new("dotnet", TimeSpan.FromSeconds(5), cleanup);

        await docker.RunToolAsync(["--list-sdks"], TimeSpan.FromSeconds(30), 4096, TestContext.Current.CancellationToken);

        Assert.Empty(cleanup.Commands);
    }
}
