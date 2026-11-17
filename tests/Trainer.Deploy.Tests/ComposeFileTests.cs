using System.Globalization;
using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

public sealed class ComposeFileTests
{
    private const string LibraryScript = "lib.sh";
    private static readonly string[] SourcedLibraries = [LibraryScript, "deploy-lib.sh", "backup-lib.sh"];
    private const string Subnet = "172.30.0.0/24";
    private const string PostgresDigest = "sha256:d3244e8ce3f165ee3c93d9c4a4b1d0fe48b3a89e2287b717fdcbbde92d405065";

    private static readonly string DeployRoot = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy");

    private static Dictionary<string, object?> Load() =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(File.ReadAllText(Path.Combine(DeployRoot, "compose", "compose.yaml")));

    private static Dictionary<string, Dictionary<string, object?>> Services() =>
        ((Dictionary<object, object?>)Load()["services"]!)
            .ToDictionary(p => (string)p.Key, p => ToStringKeyed(p.Value));

    private static Dictionary<string, object?> Service(string name) => Services()[name];

    private static Dictionary<string, object?> ToStringKeyed(object? node) =>
        ((Dictionary<object, object?>)node!).ToDictionary(p => (string)p.Key, p => p.Value);

    private static List<string> Strings(object? node) => [.. ((List<object?>)node!).Select(item => (string)item!)];

    private static Dictionary<string, string?> AppEnvironment() =>
        ToStringKeyed(Service("app")["environment"]).ToDictionary(p => p.Key, p => (string?)p.Value);

    private static string[] ScriptFiles() => [.. Directory.GetFiles(DeployRoot, "*.sh", SearchOption.AllDirectories)];

    [Fact]
    public void EveryPublishedPort_IsBoundToLoopback()
    {
        foreach ((string name, Dictionary<string, object?> service) in Services())
        {
            if (!service.TryGetValue("ports", out object? ports))
            {
                continue;
            }

            foreach (string port in Strings(ports))
            {
                Assert.True(port.StartsWith("127.0.0.1:", StringComparison.Ordinal), $"{name} publishes {port} beyond loopback.");
            }
        }
    }

    [Fact]
    public void Postgres_PublishesNoPort() => Assert.False(Service("postgres").ContainsKey("ports"));

    [Fact]
    public void Images_ArePinnedByDigest_ExceptAppImage()
    {
        foreach ((string name, Dictionary<string, object?> service) in Services())
        {
            string image = (string)service["image"]!;
            if (name == "app")
            {
                Assert.Equal("${APP_IMAGE}", image);
                continue;
            }

            Assert.Contains("@sha256:", image, StringComparison.Ordinal);
        }

        Assert.EndsWith(PostgresDigest, (string)Service("postgres")["image"]!, StringComparison.Ordinal);
        // The workflows read this pin through .github/actions/postgres-from-compose; none repeats the digest.
        string ci = File.ReadAllText(Path.Combine(ContentWorkspace.RepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain(PostgresDigest, ci, StringComparison.Ordinal);
        Assert.Contains("./.github/actions/postgres-from-compose", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void App_IsReadOnlyDropsCapabilitiesAndForbidsNewPrivileges()
    {
        Dictionary<string, object?> app = Service("app");

        Assert.Equal("true", Convert.ToString(app["read_only"], CultureInfo.InvariantCulture)?.ToLowerInvariant());
        Assert.Contains("/tmp", Strings(app["tmpfs"]));
        Assert.Equal(["ALL"], Strings(app["cap_drop"]));
        Assert.Contains("no-new-privileges:true", Strings(app["security_opt"]));
    }

    [Fact]
    public void App_MountsContentParentReadOnly() =>
        Assert.Contains("${APP_DIR}/content:/content:ro", Strings(Service("app")["volumes"]));

    [Fact]
    public void App_HealthCheckUsesReadiness()
    {
        Dictionary<string, object?> health = ToStringKeyed(Service("app")["healthcheck"]);

        Assert.Equal(["CMD", "curl", "-fsS", "http://127.0.0.1:8080/health/ready"], Strings(health["test"]));
        Assert.Equal("120s", health["start_period"]);
    }

    [Fact]
    public void App_TrustsOnlyTheNetworkGateway()
    {
        // Docker gives the first address of the subnet to the gateway.
        string[] octets = Subnet.Split('/')[0].Split('.');
        string gateway = $"{octets[0]}.{octets[1]}.{octets[2]}.1";
        Dictionary<string, string?> environment = AppEnvironment();

        Assert.Equal(gateway, environment["App__ForwardedHeaders__KnownProxies__0"]);
        Assert.DoesNotContain("App__ForwardedHeaders__KnownProxies__1", environment.Keys);

        Dictionary<object, object?> network = (Dictionary<object, object?>)((Dictionary<object, object?>)Load()["networks"]!)["default"]!;
        string subnet = (string)((Dictionary<object, object?>)((List<object?>)((Dictionary<object, object?>)network["ipam"]!)["config"]!)[0]!)["subnet"]!;
        Assert.Equal(Subnet, subnet);
    }

    [Fact]
    public void EveryService_HasBoundedJsonLogs()
    {
        foreach ((string name, Dictionary<string, object?> service) in Services())
        {
            Dictionary<string, object?> logging = ToStringKeyed(service["logging"]);
            Assert.Equal("json-file", logging["driver"]);
            Dictionary<string, object?> options = ToStringKeyed(logging["options"]);
            Assert.Equal("10m", options["max-size"]);
            Assert.Equal("3", options["max-file"]);
            Assert.True(service["restart"] is "unless-stopped", $"{name} must restart unless stopped.");
        }
    }

    [Fact]
    public void Umami_IsHardenedAndBoundToLoopback()
    {
        Dictionary<string, object?> umami = Service("umami");

        Assert.Equal(["127.0.0.1:3001:3000"], Strings(umami["ports"]));
        Assert.Equal(["ALL"], Strings(umami["cap_drop"]));
        Assert.Contains("no-new-privileges:true", Strings(umami["security_opt"]));
        Assert.Equal("512m", umami["mem_limit"]);
        // Pins Umami to the one header nginx overwrites (verified against the 3.5.0 image); otherwise it trusts the first of a list.
        Assert.Equal("x-forwarded-for", ToStringKeyed(umami["environment"])["CLIENT_IP_HEADER"]);
    }

    [Fact]
    public void RequiredSecretsAndUrls_FailWhenMissing()
    {
        string text = File.ReadAllText(Path.Combine(DeployRoot, "compose", "compose.yaml"));
        string[] required =
        [
            "DB_PASSWORD", "PUBLIC_BASE_URL", "ADMIN_TELEGRAM_IDS", "TELEGRAM_OIDC_CLIENT_ID", "TELEGRAM_OIDC_CLIENT_SECRET",
            "CONTENT_IMPORT_TOKEN", "UMAMI_DB_PASSWORD", "UMAMI_APP_SECRET",
        ];

        foreach (string name in required)
        {
            Assert.DoesNotContain("${" + name + "}", text, StringComparison.Ordinal);
            Assert.Contains("${" + name + ":?", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PgHba_AllowsOnlyAppRolesOnTheComposeSubnet()
    {
        string[] rules = [.. File.ReadAllLines(Path.Combine(DeployRoot, "compose", "postgres", "pg_hba.conf"))
            .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

        Assert.Equal(
            [
                "local all postgres peer",
                $"host trainer trainer {Subnet} scram-sha-256",
                $"host umami umami {Subnet} scram-sha-256",
            ],
            rules);
    }

    [Fact]
    public void PostgresAdminCommands_RunAsPostgresUser()
    {
        foreach (string file in ScriptFiles())
        {
            foreach (string command in LogicalLines(file))
            {
                bool execsPostgres = command.Contains(" exec ", StringComparison.Ordinal) && command.Contains("postgres", StringComparison.Ordinal);
                if (execsPostgres)
                {
                    Assert.True(
                        command.Contains("--user postgres", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} runs an exec against postgres without --user postgres: {command}");
                }
            }
        }
    }

    [Fact]
    public void LogicalLines_JoinContinuations()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# exec postgres\ndit_compose exec \\\n  postgres psql\nprintf x |\n  dit_compose exec postgres psql\n");

            Assert.Equal(["dit_compose exec   postgres psql", "printf x |   dit_compose exec postgres psql"], LogicalLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Non-comment script lines with backslash and pipe continuations joined, so a command split over lines is one command.</summary>
    private static List<string> LogicalLines(string file)
    {
        List<string> result = [];
        string current = string.Empty;
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.TrimEnd();
            if (current.Length == 0 && line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            bool continues = line.EndsWith('\\') || line.EndsWith('|');
            current += (current.Length == 0 ? string.Empty : " ") + (line.EndsWith('\\') ? line[..^1].TrimEnd() : line);
            if (!continues)
            {
                result.Add(current);
                current = string.Empty;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current);
        }

        return result;
    }

    [Fact]
    public void NoScriptEnablesXtrace()
    {
        foreach (string file in ScriptFiles())
        {
            foreach (string line in File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith('#')))
            {
                Assert.DoesNotMatch(@"\bset\s+(-[a-zA-Z]*x|-o\s+xtrace)", line);
            }
        }
    }

    [Fact]
    public void EveryScript_UsesStrictMode()
    {
        string[] files = [.. ScriptFiles().Where(f => !SourcedLibraries.Contains(Path.GetFileName(f)))];

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Assert.True(
                File.ReadAllLines(file).Any(l => l.Trim() == "set -euo pipefail"),
                $"{Path.GetFileName(file)} must contain 'set -euo pipefail'.");
        }
    }

    [Fact]
    public void Library_SetsNoShellOptionsForItsCallers()
    {
        foreach (string name in SourcedLibraries)
        {
            string library = Path.Combine(DeployRoot, "remote", name);

            foreach (string line in File.ReadAllLines(library).Where(l => !l.TrimStart().StartsWith('#')))
            {
                Assert.DoesNotMatch(@"^\s*(set\s+[-+]|shopt\b|trap\b)", line);
            }
        }
    }
}
