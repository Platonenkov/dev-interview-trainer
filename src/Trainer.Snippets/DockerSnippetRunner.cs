using System.Runtime.InteropServices;
using Trainer.Content.Model;

namespace Trainer.Snippets;

/// <summary>
/// Builds and runs untrusted C# snippets in two separate throw-away containers: a build container (network off, non-root,
/// source mounted read-only) and a run container (network off, read-only root, read-only output, no capabilities).
/// </summary>
public sealed class DockerSnippetRunner : ISnippetRunner
{
    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>disable</Nullable>
            <InvariantGlobalization>true</InvariantGlobalization>
            <UseAppHost>false</UseAppHost>
          </PropertyGroup>
        </Project>
        """;

    private static readonly TimeSpan PullDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InspectDeadline = TimeSpan.FromSeconds(30);

    private readonly SandboxOptions _options;
    private readonly DockerProcess _docker;

    public DockerSnippetRunner()
        : this(new SandboxOptions())
    {
    }

    public DockerSnippetRunner(SandboxOptions options)
        : this(options, new DockerProcess("docker", options.CleanupTimeout))
    {
    }

    internal DockerSnippetRunner(SandboxOptions options, DockerProcess docker)
    {
        _options = options;
        _docker = docker;
    }

    /// <summary>Pulls the pinned image when it is not present locally. Call it once before any build so that the download never counts against a build deadline.</summary>
    public async Task PrepareAsync(CancellationToken ct)
    {
        DockerProcessResult inspect = await _docker.RunToolAsync(
            ["image", "inspect", "--format", "{{.Id}}", _options.Image], InspectDeadline, _options.OutputCapBytes, ct);
        if (inspect.ExitCode == 0)
        {
            return;
        }

        DockerProcessResult pull = await _docker.RunToolAsync(["pull", _options.Image], PullDeadline, _options.OutputCapBytes, ct);
        if (pull.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker pull {_options.Image} failed (exit {pull.ExitCode}): {pull.Stderr.Trim()}");
        }
    }

    public async Task<SnippetBuild> BuildAsync(CodeBlock code, CancellationToken ct)
    {
        string root = Path.Combine(Path.GetTempPath(), "snippet-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "src");
        string outputDirectory = Path.Combine(root, "out");
        bool handedOver = false;
        try
        {
            CreateWorldReadableDirectory(root);
            CreateWorldReadableDirectory(sourceDirectory);
            CreateWorldReadableDirectory(outputDirectory);
            await WriteWorldReadableFileAsync(Path.Combine(sourceDirectory, "Snippet.csproj"), ProjectFile, ct);
            await WriteWorldReadableFileAsync(Path.Combine(sourceDirectory, "Program.cs"), code.Source, ct);

            string name = "sb-" + Guid.NewGuid().ToString("N");
            DockerProcessResult result = await _docker.RunContainerAsync(
                BuildArguments(name, sourceDirectory, outputDirectory), name, _options.BuildDeadline, _options.OutputCapBytes, ct);

            SnippetResult? failure = null;
            if (result.KilledByDeadline)
            {
                failure = new SnippetResult(SnippetResult.KindSandboxError, "build exceeded the build deadline");
            }
            else if (result.Truncated)
            {
                failure = new SnippetResult(SnippetResult.KindSandboxError, "build output exceeded the capture limit");
            }
            else if (result.ExitCode != 0)
            {
                failure = SnippetResultParser.ParseBuild(result.ExitCode, result.Stdout + "\n" + result.Stderr);
            }

            if (failure is not null)
            {
                return SnippetBuild.Failed(failure);
            }

            MakeTreeWorldReadable(outputDirectory);
            handedOver = true;
            return SnippetBuild.Succeeded(outputDirectory, () => DeleteDirectoryAsync(root));
        }
        finally
        {
            if (!handedOver)
            {
                await DeleteDirectoryAsync(root);
            }
        }
    }

    public async Task<SnippetResult> RunAsync(SnippetBuild build, CancellationToken ct)
    {
        if (build.Failure is not null)
        {
            return build.Failure;
        }

        string name = "sr-" + Guid.NewGuid().ToString("N");
        DockerProcessResult result = await _docker.RunContainerAsync(
            RunArguments(name, build.OutputDirectory!), name, _options.RunDeadline, _options.OutputCapBytes, ct);
        return SnippetResultParser.ParseRun(result.ExitCode, result.Stdout, result.Stderr, result.KilledByDeadline, result.Truncated);
    }

    internal List<string> BuildArguments(string name, string sourceDirectory, string outputDirectory)
    {
        List<string> args = ["run", "--name", name, "--rm", "--network", "none"];

        // On Linux the bind-mounted output must be writable by the container, so it runs as the host user. Docker Desktop
        // (Windows, macOS) maps ownership of shared folders itself and rejects the host uid as meaningless.
        if (OperatingSystem.IsLinux())
        {
            args.AddRange(["--user", $"{NativeIds.GetUserId()}:{NativeIds.GetGroupId()}"]);
        }

        args.AddRange([
            "--memory", $"{_options.BuildMemoryMb}m", "--memory-swap", $"{_options.BuildMemoryMb}m", "--cpus", "2", "--pids-limit", "512",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--log-driver", "none",
            "-e", "HOME=/tmp", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1", "-e", "NUGET_PACKAGES=/tmp/nuget",
            "--tmpfs", "/tmp:rw,exec,size=1g",
            "-v", $"{sourceDirectory}:/src:ro",
            "-v", $"{outputDirectory}:/out:rw",
            _options.Image,
            "dotnet", "build", "/src/Snippet.csproj", "-c", "Release", "-o", "/out", "-p:BaseIntermediateOutputPath=/tmp/obj/", "-v", "q",
        ]);
        return args;
    }

    internal List<string> RunArguments(string name, string outputDirectory) =>
    [
        "run", "--name", name, "--rm", "--network", "none", "--read-only", "--user", "65534:65534",
        "--memory", $"{_options.RunMemoryMb}m", "--memory-swap", $"{_options.RunMemoryMb}m", "--cpus", "1", "--pids-limit", "128",
        "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--log-driver", "none",
        "--tmpfs", "/tmp:rw,noexec,size=16m",
        "-e", "TZ=UTC", "-e", "DOTNET_gcServer=0", "-e", "DOTNET_EnableDiagnostics=0",
        "-v", $"{outputDirectory}:/out:ro",
        _options.Image,
        "dotnet", "/out/Snippet.dll",
    ];

    // The run container is uid 65534, not the host user that owns these files. Directories need 0755 and files 0644 (the
    // permission bits for "others") for it to traverse and read the compiled output; the host umask cannot be trusted to give that.
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode FileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static void CreateWorldReadableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    private static async Task WriteWorldReadableFileAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }

    private static void MakeTreeWorldReadable(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(directory, DirectoryMode);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(entry, Directory.Exists(entry) ? DirectoryMode : FileMode);
        }
    }

    private static ValueTask DeleteDirectoryAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory: leaving it behind must not fail verification.
        }

        return ValueTask.CompletedTask;
    }

    private static class NativeIds
    {
        [DllImport("libc", EntryPoint = "getuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern uint GetUid();

        [DllImport("libc", EntryPoint = "getgid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern uint GetGid();

        public static uint GetUserId() => GetUid();

        public static uint GetGroupId() => GetGid();
    }
}
