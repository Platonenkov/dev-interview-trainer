using System.Diagnostics;
using System.Text;

namespace Trainer.Snippets;

/// <summary>Outcome of one <c>docker</c> client invocation.</summary>
internal sealed record DockerProcessResult(int ExitCode, string Stdout, string Stderr, bool KilledByDeadline, bool Truncated);

/// <summary>Runs one <c>docker</c> command whose output is irrelevant; failures are swallowed.</summary>
internal interface IDockerCommandRunner
{
    Task RunQuietAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Runs the <c>docker</c> client without a shell. The host, not the container, enforces the deadline: when it expires, the
/// output cap is hit or the caller cancels, the container is killed by name and force-removed, because killing only the client
/// process would leave the container running.
/// </summary>
/// <param name="executable">Client executable; replaced in tests so that no daemon is needed.</param>
/// <param name="cleanupTimeout">Own budget of each cleanup command. Cleanup never reuses the caller's token or the snippet deadline, both of which may already be spent.</param>
/// <param name="cleanupRunner">Runs the kill and rm commands; a real client process when null.</param>
/// <param name="clientStarted">Receives the process id of the main client right after it started; lets tests observe its fate.</param>
internal sealed class DockerProcess(
    string executable, TimeSpan cleanupTimeout, IDockerCommandRunner? cleanupRunner = null, Action<int>? clientStarted = null)
{
    private readonly IDockerCommandRunner _cleanup = cleanupRunner ?? new ProcessRunner(executable);
    private readonly TimeSpan _cleanupTimeout = cleanupTimeout > TimeSpan.Zero
        ? cleanupTimeout
        : throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), cleanupTimeout, "The cleanup timeout must be positive.");

    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the container, if any, is already gone.</exception>
    public async Task<DockerProcessResult> RunContainerAsync(
        IReadOnlyList<string> arguments, string? containerName, TimeSpan deadline, int outputCapBytes, CancellationToken ct)
    {
        using Process process = Start(executable, arguments);
        clientStarted?.Invoke(process.Id);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CappedBuffer stdout = new(outputCapBytes, stop);
        CappedBuffer stderr = new(outputCapBytes, stop);
        Task stdoutTask = stdout.FillAsync(process.StandardOutput.BaseStream);
        Task stderrTask = stderr.FillAsync(process.StandardError.BaseStream);

        bool stopped = false;
        try
        {
            stop.CancelAfter(deadline);
            try
            {
                await process.WaitForExitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
        }
        finally
        {
            // Runs even when the client process exited on its own: the container may still be alive after a client crash.
            if (containerName is not null)
            {
                await RemoveContainerAsync(containerName);
            }

            await ReapAsync(process, stdoutTask, stderrTask);

            // The client may have asked the daemon to create the container just before it was killed, after the first
            // removal already ran; without this second pass that container would be created and never removed.
            if (containerName is not null)
            {
                await RemoveContainerAsync(containerName);
            }
        }

        ct.ThrowIfCancellationRequested();
        bool truncated = stdout.Truncated || stderr.Truncated;
        bool killedByDeadline = stopped && !truncated;
        int exitCode = process.HasExited ? process.ExitCode : -1;
        return new DockerProcessResult(exitCode, stdout.Text, stderr.Text, killedByDeadline, truncated);
    }

    /// <summary>Runs a management command (pull, inspect, ps) with a hard deadline and capped output; no container is involved.</summary>
    public Task<DockerProcessResult> RunToolAsync(
        IReadOnlyList<string> arguments, TimeSpan deadline, int outputCapBytes, CancellationToken ct) =>
        RunContainerAsync(arguments, containerName: null, deadline, outputCapBytes, ct);

    /// <summary>Kills and removes a container by name; failures are ignored because the container may never have been created or may already be gone.</summary>
    public async Task RemoveContainerAsync(string containerName)
    {
        await RunCleanupAsync(["kill", containerName]);
        await RunCleanupAsync(["rm", "-f", containerName]);
    }

    private async Task RunCleanupAsync(IReadOnlyList<string> arguments)
    {
        // A fresh token per command: a hung "kill" must not leave "rm -f" without any time budget.
        using CancellationTokenSource timeout = new(_cleanupTimeout);
        await _cleanup.RunQuietAsync(arguments, timeout.Token);
    }

    internal sealed class ProcessRunner(string executable, Action<int>? started = null) : IDockerCommandRunner
    {
        public async Task RunQuietAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Process? process = null;
            Task? drain = null;
            try
            {
                process = Start(executable, arguments);
                started?.Invoke(process.Id);
                // Output is not needed, but must be drained or the client can block on a full pipe.
                drain = Task.WhenAll(
                    process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct),
                    process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct));
                await process.WaitForExitAsync(ct);
                await drain;
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // Best effort: the caller's own failure is the one worth reporting. A client that outlived its budget
                // (for example a hung connection to the daemon) must not be left running.
                KillTree(process);
                // A pipe error after the kill would otherwise surface as an unobserved task exception.
                _ = drain?.ContinueWith(static task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static void KillTree(Process? process)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return process;
    }

    private async Task ReapAsync(Process process, Task stdoutTask, Task stderrTask)
    {
        using CancellationTokenSource timeout = new(_cleanupTimeout);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // The client is gone or unkillable; what was captured so far is still reported.
        }
    }

    /// <summary>Keeps at most <c>cap</c> bytes and discards the rest while still draining the pipe; hitting the cap stops the run.</summary>
    private sealed class CappedBuffer(int cap, CancellationTokenSource stop)
    {
        private readonly byte[] _data = new byte[cap];
        private int _length;

        public bool Truncated { get; private set; }

        public string Text => Encoding.UTF8.GetString(_data, 0, _length);

        public async Task FillAsync(Stream source)
        {
            byte[] chunk = new byte[8192];
            try
            {
                int read;
                while ((read = await source.ReadAsync(chunk)) > 0)
                {
                    int room = cap - _length;
                    if (read > room)
                    {
                        Buffer.BlockCopy(chunk, 0, _data, _length, room);
                        _length += room;
                        if (!Truncated)
                        {
                            Truncated = true;
                            await stop.CancelAsync();
                        }
                    }
                    else
                    {
                        Buffer.BlockCopy(chunk, 0, _data, _length, read);
                        _length += read;
                    }
                }
            }
            catch (IOException)
            {
                // The pipe closed because the client was killed.
            }
            catch (ObjectDisposedException)
            {
                // Same: the process was disposed while a read was pending.
            }
        }
    }
}
