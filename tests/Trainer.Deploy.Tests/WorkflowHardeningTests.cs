using System.Globalization;
using System.Text.RegularExpressions;
using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

/// <summary>
/// Structural checks over every workflow and composite action: what a pull request or a stray expression can reach,
/// who may write packages, and how the deploy workflows reach the server. The YAML is parsed, so a comment or a
/// string literal cannot satisfy or break a rule by accident.
/// </summary>
public sealed partial class WorkflowHardeningTests
{
    private const string SshSetup = "./.github/actions/ssh-setup";

    private static readonly string Root = ContentWorkspace.RepositoryRoot();

    private static readonly string[] DeployWorkflowFiles = ["deploy-app.yml", "ops-check.yml", "deploy-content.yml", "rotate-db-password.yml"];

    // The smoke workflow is the only one that may name the test hooks of the server scripts; they must never reach production.
    private static readonly string[] TestHookWorkflowFiles = ["deploy-smoke.yml"];

    private static readonly string[] ProvisionHookNames = ["DIT_TEST_AUTHORIZED_KEYS", "DIT_TEST_SSH_DIR", "DIT_TEST_APP_DIR"];

    private static readonly string[] TestHookNames = ["DIT_TEST_HOOKS", "DIT_TEST_KILL_AT", "DIT_TEST_IMPORT_URL", .. ProvisionHookNames];

    [GeneratedRegex(@"@[0-9a-f]{40}$")]
    private static partial Regex FullShaPin();

    [GeneratedRegex(@"\$\{\{[^}]*\b(?:inputs\.|github\.event|github\.head_ref|github\.ref_name|secrets\.|needs\.|steps\.)")]
    private static partial Regex UntrustedExpression();

    [GeneratedRegex(@"\b(?:ssh|scp|rsync)\b")]
    private static partial Regex RemoteCommand();

    [GeneratedRegex(@"(?:""\$\{ssh_cmd\[@\]\}""|ssh -F ""[^""]*"" dit)\s+""(?<cmd>(?:[^""\\]|\\.)*)""")]
    private static partial Regex RemoteArgument();

    [GeneratedRegex(@"(?:>|-C)\s*'(?<path>[^']*)'")]
    private static partial Regex WriteTarget();

    [GeneratedRegex(@"\b(?<verb>mkdir|rm|mv|ln|chmod)\b(?<args>[^&;|]*)")]
    private static partial Regex DirectoryCommand();

    [GeneratedRegex(@"'(?<path>[^']*)'")]
    private static partial Regex QuotedPath();

    [GeneratedRegex(@"bash -c '[^']*'")]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\\\r?\n\s*")]
    private static partial Regex LineContinuation();

    [GeneratedRegex(@"\b(?:cp|install|dd|tee|rsync|scp|sftp)\b")]
    private static partial Regex CopyCommand();

    private sealed record YamlFile(string Name, string Text, Dictionary<string, object?> Root, bool IsAction);

    private sealed record YamlNode(string[] Path, object? Value);

    private static List<YamlFile> AllFiles()
    {
        List<YamlFile> files = [];
        foreach (string path in Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml").Order(StringComparer.Ordinal))
        {
            files.Add(Load(path, Path.GetFileName(path), isAction: false));
        }

        string actions = Path.Combine(Root, ".github", "actions");
        if (Directory.Exists(actions))
        {
            foreach (string path in Directory.GetFiles(actions, "action.yml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                files.Add(Load(path, Path.GetRelativePath(Root, path).Replace('\\', '/'), isAction: true));
            }
        }

        return files;
    }

    private static List<YamlFile> Workflows() => [.. AllFiles().Where(f => !f.IsAction)];

    private static YamlFile Workflow(string name) => Workflows().Single(f => f.Name == name);

    private static YamlFile Load(string path, string name, bool isAction) => Parse(name, File.ReadAllText(path), isAction);

    private static YamlFile Parse(string name, string text, bool isAction)
    {
        Dictionary<object, object?> raw = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(text);
        return new YamlFile(name, text, raw.ToDictionary(p => (string)p.Key, p => p.Value), isAction);
    }

    /// <summary>A copy of a real workflow with one violation introduced; the real file is never touched.</summary>
    private static YamlFile Mutated(string name, string from, string to)
    {
        string original = Workflow(name).Text;
        Assert.Contains(from, original, StringComparison.Ordinal);
        return Parse(name, original.Replace(from, to, StringComparison.Ordinal), isAction: false);
    }

    private static YamlFile WithoutLine(string name, string marker)
    {
        string[] lines = Workflow(name).Text.Split('\n');
        Assert.Single(lines, line => line.Contains(marker, StringComparison.Ordinal));
        return Parse(name, string.Join('\n', lines.Where(line => !line.Contains(marker, StringComparison.Ordinal))), isAction: false);
    }

    private static Dictionary<string, object?> Map(object? node) =>
        node as Dictionary<string, object?> ?? ((Dictionary<object, object?>)node!).ToDictionary(p => (string)p.Key, p => p.Value);

    private static bool IsMap(object? node) => node is Dictionary<object, object?>;

    private static string Text(object? node) => Convert.ToString(node, CultureInfo.InvariantCulture) ?? string.Empty;

    private static IEnumerable<YamlNode> Nodes(object? node, string[] path)
    {
        yield return new YamlNode(path, node);
        if (node is Dictionary<object, object?> map)
        {
            foreach (KeyValuePair<object, object?> pair in map)
            {
                foreach (YamlNode child in Nodes(pair.Value, [.. path, (string)pair.Key]))
                {
                    yield return child;
                }
            }
        }
        else if (node is List<object?> list)
        {
            for (int index = 0; index < list.Count; index++)
            {
                foreach (YamlNode child in Nodes(list[index], [.. path, index.ToString(CultureInfo.InvariantCulture)]))
                {
                    yield return child;
                }
            }
        }
    }

    // The root is already keyed by string; Nodes walks the deserializer's object-keyed maps, so it must be handed one.
    private static IEnumerable<YamlNode> AllNodes(YamlFile file) =>
        Nodes(file.Root.ToDictionary(p => (object)p.Key, p => p.Value), []);

    private static object? NodeAt(YamlFile file, string[] path)
    {
        object? current = file.Root;
        foreach (string segment in path)
        {
            current = current is List<object?> list
                ? list[int.Parse(segment, CultureInfo.InvariantCulture)]
                : Map(current)[segment];
        }

        return current;
    }

    private static Dictionary<string, object?> Jobs(YamlFile file) => Map(file.Root["jobs"]);

    private static List<object?> Steps(object? job) => (List<object?>)Map(job)["steps"]!;

    private static string StepUses(object? step) => Text(Map(step).GetValueOrDefault("uses"));

    private static string StepRun(object? step) => Text(Map(step).GetValueOrDefault("run"));

    private static bool UsesSshSetup(object? job) =>
        IsMap(job) && Map(job).ContainsKey("steps") && Steps(job).Any(step => StepUses(step) == SshSetup);

    private static IEnumerable<(string Job, object? Value)> JobsUsingSshSetup(YamlFile file) =>
        Jobs(file).Where(p => UsesSshSetup(p.Value)).Select(p => (p.Key, p.Value));

    private static string RunText(YamlFile file) =>
        string.Join('\n', AllNodes(file).Where(n => n.Path.Length > 0 && n.Path[^1] == "run" && n.Value is string).Select(n => (string)n.Value!));

    private static string AllStrings(YamlFile file) =>
        string.Join('\n', AllNodes(file).Where(n => n.Value is string).Select(n => (string)n.Value!));

    private static IEnumerable<string> Triggers(YamlFile file)
    {
        object? on = file.Root["on"];
        return on switch
        {
            string single => [single],
            List<object?> list => list.Select(Text),
            Dictionary<object, object?> map => map.Keys.Select(Text),
            _ => [],
        };
    }

    private static int Rank(string level) => level switch
    {
        "write" => 2,
        "read" => 1,
        _ => 0,
    };

    [Fact]
    public void EveryRemoteAction_IsPinnedByFullSha()
    {
        foreach (YamlFile file in AllFiles())
        {
            AssertPinned(file);
        }
    }

    [Fact]
    public void PinCheck_FailsOnATagReference()
    {
        YamlFile mutated = Mutated("deploy-app.yml", "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v7");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertPinned(mutated));
    }

    private static void AssertPinned(YamlFile file)
    {
        foreach (YamlNode node in AllNodes(file).Where(n => n.Path.Length > 0 && n.Path[^1] == "uses" && n.Value is string))
        {
            string uses = (string)node.Value!;
            if (uses.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(FullShaPin().IsMatch(uses), $"{file.Name}: '{uses}' must be pinned by a full 40-character commit SHA.");
        }
    }

    [Fact]
    public void EveryWorkflow_DeclaresTopLevelPermissions_AndNeverWriteAll()
    {
        foreach (YamlFile file in Workflows())
        {
            Assert.True(file.Root.ContainsKey("permissions"), $"{file.Name} must declare top-level permissions.");
            Assert.True(IsMap(file.Root["permissions"]), $"{file.Name}: top-level permissions must be a mapping (use {{}} for none).");
            Assert.DoesNotContain("write-all", file.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PackagesWrite_OnlyInTheImageBuildJob()
    {
        List<string> writers = [];
        foreach (YamlFile file in Workflows())
        {
            if (IsMap(file.Root["permissions"]) && Text(Map(file.Root["permissions"]).GetValueOrDefault("packages")) == "write")
            {
                writers.Add($"{file.Name}:<top level>");
            }

            foreach (KeyValuePair<string, object?> job in Jobs(file))
            {
                Dictionary<string, object?> body = Map(job.Value);
                if (body.TryGetValue("permissions", out object? permissions) && IsMap(permissions)
                    && Text(Map(permissions).GetValueOrDefault("packages")) == "write")
                {
                    writers.Add($"{file.Name}:{job.Key}");
                }
            }
        }

        Assert.Equal(["deploy-app.yml:build"], writers);
    }

    [Fact]
    public void ReusableWorkflowCallers_GrantWhatTheCalledWorkflowDeclares()
    {
        const string prefix = "./.github/workflows/";
        int callers = 0;
        foreach (YamlFile file in Workflows())
        {
            foreach (KeyValuePair<string, object?> job in Jobs(file))
            {
                Dictionary<string, object?> body = Map(job.Value);
                if (!body.TryGetValue("uses", out object? uses) || !Text(uses).StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                callers++;
                YamlFile callee = Workflow(Text(uses)[prefix.Length..]);
                Dictionary<string, int> needed = [];
                void Collect(object? permissions)
                {
                    if (!IsMap(permissions))
                    {
                        return;
                    }

                    foreach (KeyValuePair<string, object?> scope in Map(permissions))
                    {
                        needed[scope.Key] = Math.Max(needed.GetValueOrDefault(scope.Key), Rank(Text(scope.Value)));
                    }
                }

                Collect(callee.Root["permissions"]);
                foreach (KeyValuePair<string, object?> calleeJob in Jobs(callee))
                {
                    Collect(Map(calleeJob.Value).GetValueOrDefault("permissions"));
                }

                Assert.True(body.ContainsKey("permissions") && IsMap(body["permissions"]), $"{file.Name}:{job.Key} must grant permissions explicitly to {Text(uses)}.");
                Dictionary<string, object?> granted = Map(body["permissions"]);
                foreach (KeyValuePair<string, int> scope in needed.Where(p => p.Value > 0))
                {
                    Assert.True(
                        Rank(Text(granted.GetValueOrDefault(scope.Key))) >= scope.Value,
                        $"{file.Name}:{job.Key} must grant {scope.Key} to {Text(uses)}, which declares it.");
                }
            }
        }

        Assert.True(callers >= 3, "ci.yml calls schema-compat.yml; deploy-app.yml calls schema-compat.yml and deploy-smoke.yml.");
    }

    [Fact]
    public void NoWorkflow_UsesPullRequestTarget()
    {
        foreach (YamlFile file in Workflows())
        {
            Assert.DoesNotContain("pull_request_target", Triggers(file));
        }
    }

    [Fact]
    public void PullRequestWorkflows_ReferenceNoSecrets()
    {
        int checkedFiles = 0;
        foreach (YamlFile file in Workflows().Where(f => Triggers(f).Contains("pull_request")))
        {
            checkedFiles++;
            AssertNoSecretsInPullRequestWorkflow(file, Workflow);
        }

        Assert.True(checkedFiles >= 2);
    }

    [Fact]
    public void PullRequestSecretCheck_FailsWhenAPullRequestWorkflowReadsASecret()
    {
        YamlFile mutated = Mutated("deploy-smoke.yml", "      IMAGE_INPUT: ${{ inputs.image }}", "      IMAGE_INPUT: ${{ inputs.image }}\n      LEAK: ${{ secrets.DEPLOY_SSH_KEY }}");

        Assert.Contains("pull_request", Triggers(mutated));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretsInPullRequestWorkflow(mutated, Workflow));
    }

    // A reusable workflow runs in the context of its caller, so a secret read by schema-compat.yml or content-checks.yml is
    // as exposed to a pull request as one read by ci.yml itself.
    [Theory]
    [InlineData("schema-compat.yml", "      PREVIOUS: ${{ inputs.previous }}", "      PREVIOUS: ${{ inputs.previous }}\n      LEAK: ${{ secrets.DEPLOY_SSH_KEY }}")]
    [InlineData("content-checks.yml", "      SANDBOX_IMAGE: mcr", "      LEAK: ${{ secrets.DEPLOY_SSH_KEY }}\n      SANDBOX_IMAGE: mcr")]
    public void PullRequestSecretCheck_FollowsCalledWorkflows(string callee, string from, string to)
    {
        YamlFile ci = Workflow("ci.yml");
        YamlFile mutatedCallee = Mutated(callee, from, to);
        AssertNoSecretsInPullRequestWorkflow(ci, Workflow);

        YamlFile Resolve(string name) => name == callee ? mutatedCallee : Workflow(name);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretsInPullRequestWorkflow(ci, Resolve));
    }

    private static void AssertNoSecretsInPullRequestWorkflow(YamlFile file, Func<string, YamlFile> resolve)
    {
        const string prefix = "./.github/workflows/";
        Assert.DoesNotContain("secrets.", AllStrings(file), StringComparison.Ordinal);
        Assert.False(AllNodes(file).Any(n => n.Path.Length > 0 && n.Path[^1] == "secrets"), $"{file.Name} runs on pull requests and must not pass secrets.");
        foreach (KeyValuePair<string, object?> job in Jobs(file))
        {
            string uses = Text(Map(job.Value).GetValueOrDefault("uses"));
            if (uses.StartsWith(prefix, StringComparison.Ordinal))
            {
                AssertNoSecretsInPullRequestWorkflow(resolve(uses[prefix.Length..]), resolve);
            }
        }
    }

    [Fact]
    public void SecretsAppearOnlyInEnvBlocks()
    {
        foreach (YamlFile file in AllFiles())
        {
            AssertSecretsOnlyInEnv(file);
        }
    }

    [Theory]
    [InlineData("        run: docker logout ghcr.io", "        run: echo ${{ secrets.DB_PASSWORD }}")]
    [InlineData("          ref: ${{ needs.resolve.outputs.sha }}", "          ref: ${{ secrets.DB_PASSWORD }}")]
    public void SecretsCheck_FailsOutsideEnvAndTheSshSetupInputs(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-app.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSecretsOnlyInEnv(mutated));
    }

    private static void AssertSecretsOnlyInEnv(YamlFile file)
    {
        foreach (YamlNode node in AllNodes(file).Where(n => n.Value is string text && text.Contains("secrets.", StringComparison.Ordinal)))
        {
            bool inEnv = node.Path.Contains("env");
            bool inSshSetupInput = false;
            int with = Array.LastIndexOf(node.Path, "with");
            if (with > 0 && node.Path.Length == with + 2)
            {
                inSshSetupInput = StepUses(NodeAt(file, node.Path[..with])) == SshSetup;
            }

            Assert.True(
                inEnv || inSshSetupInput,
                $"{file.Name}: secrets may be referenced only under env or as an input of the ssh-setup action ({string.Join('/', node.Path)}).");
        }
    }

    [Fact]
    public void InputsAppearOnlyInEnvBlocks()
    {
        foreach (YamlFile file in AllFiles())
        {
            AssertNoUntrustedExpressionInRun(file);
        }
    }

    [Theory]
    [InlineData("${{ inputs.ref }}")]
    [InlineData("${{ github.event.workflow_run.head_branch }}")]
    [InlineData("${{ github.ref_name }}")]
    [InlineData("${{ github.head_ref }}")]
    [InlineData("${{ secrets.DB_PASSWORD }}")]
    public void RunCheck_FailsOnAnInterpolatedExpression(string expression)
    {
        YamlFile mutated = Mutated("deploy-app.yml", "        run: docker logout ghcr.io", "        run: echo " + expression);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoUntrustedExpressionInRun(mutated));
    }

    private static void AssertNoUntrustedExpressionInRun(YamlFile file)
    {
        foreach (YamlNode node in AllNodes(file).Where(n => n.Path.Length > 0 && n.Path[^1] == "run" && n.Value is string))
        {
            Assert.False(
                UntrustedExpression().IsMatch((string)node.Value!),
                $"{file.Name}: a run script must read inputs, event data, outputs and secrets from env, not interpolate them ({string.Join('/', node.Path)}).");
        }
    }

    [Fact]
    public void Walker_ReachesScalarsNestedInSequencesAndMappings()
    {
        YamlFile sample = Parse("sample.yml", "jobs:\n  a:\n    steps:\n      - name: x\n        with:\n          list:\n            - - deep: needle\n", isAction: false);
        Assert.Contains(AllNodes(sample), n => Equals(n.Value, "needle") && string.Join('/', n.Path) == "jobs/a/steps/0/with/list/0/0/deep");

        // The node-based checks above are only worth anything while the walker sees real workflows.
        YamlFile real = Workflow("deploy-app.yml");
        Assert.True(AllNodes(real).Count(n => n.Path.Length > 0 && n.Path[^1] == "uses" && n.Value is string) >= 8);
        Assert.Contains(AllNodes(real), n => n.Path.Length == 5 && n.Path[0] == "jobs" && n.Path[1] == "deploy" && n.Path[2] == "steps" && n.Path[4] == "run");
        Assert.Contains(AllNodes(real), n => n.Path.Length == 4 && n.Path[^2] == "outputs" && n.Path[^1] == "image");
    }

    [Fact]
    public void RunScripts_NeverInterpolateSecretsIntoSshArguments()
    {
        foreach (YamlFile file in AllFiles())
        {
            AssertNoSecretsAfterSsh(file);
        }
    }

    [Theory]
    [InlineData("-u '$OWNER' -p $GHCR_TOKEN\"")]
    [InlineData("-u '$OWNER' \\\n            -p $GHCR_TOKEN\"")]
    public void RunScripts_SecretCheckFailsWhenATokenReachesTheSshArguments(string replacement)
    {
        YamlFile file = Mutated("deploy-app.yml", "-u '$OWNER' --password-stdin\"", replacement);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretsAfterSsh(file));
    }

    // Names bound to a secret or to github.token must not appear in what follows an ssh invocation: the remote command
    // line is visible in the server's process list. The value may still be piped in on stdin before the ssh call.
    private static void AssertNoSecretsAfterSsh(YamlFile file)
    {
        List<string> names = [];
        foreach (YamlNode node in AllNodes(file).Where(n => n.Path.Contains("env") && n.Value is string text
            && (text.Contains("secrets.", StringComparison.Ordinal) || text.Contains("github.token", StringComparison.Ordinal))))
        {
            names.Add(node.Path[^1]);
        }

        foreach (YamlNode node in AllNodes(file).Where(n => n.Path.Length > 0 && n.Path[^1] == "run" && n.Value is string))
        {
            // A backslash continuation would otherwise move an argument onto a line that does not mention ssh.
            string script = LineContinuation().Replace((string)node.Value!, " ");
            foreach (string line in script.Split('\n'))
            {
                int start = line.IndexOf("ssh -F", StringComparison.Ordinal);
                if (start < 0)
                {
                    start = line.IndexOf("${ssh_cmd[@]}", StringComparison.Ordinal);
                }

                if (start < 0)
                {
                    continue;
                }

                string arguments = line[start..];
                Assert.DoesNotContain("${{", arguments, StringComparison.Ordinal);
                foreach (string name in names)
                {
                    Assert.False(
                        arguments.Contains("$" + name, StringComparison.Ordinal) || arguments.Contains("${" + name, StringComparison.Ordinal),
                        $"{file.Name}: {name} holds a secret or token and must not be an ssh argument: {line.Trim()}");
                }
            }
        }
    }

    [Fact]
    public void NoJobPassesSecretsToACalledWorkflow()
    {
        foreach (YamlFile file in Workflows())
        {
            AssertNoPassedSecrets(file);
        }
    }

    [Fact]
    public void SecretsInheritCheck_FailsOnTheSmokeCaller()
    {
        YamlFile file = Mutated("deploy-app.yml", "    uses: ./.github/workflows/deploy-smoke.yml", "    uses: ./.github/workflows/deploy-smoke.yml\n    secrets: inherit");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoPassedSecrets(file));
    }

    // deploy-smoke.yml also runs on pull requests, and the called workflows need no secret, so none may be handed over,
    // "inherit" included.
    private static void AssertNoPassedSecrets(YamlFile file)
    {
        Assert.False(
            AllNodes(file).Any(n => n.Path.Length == 3 && n.Path[0] == "jobs" && n.Path[2] == "secrets"),
            $"{file.Name}: a job passes secrets to a called workflow.");
    }

    [Fact]
    public void DeployWorkflows_UseWorkflowLevelConcurrencyAndProductionEnvironment()
    {
        Dictionary<string, string> groups = new()
        {
            ["deploy-app.yml"] = "production-app",
            ["ops-check.yml"] = "production-ops",
            ["deploy-content.yml"] = "production-content",
            ["rotate-db-password.yml"] = "production-rotate",
        };
        foreach (string name in DeployWorkflowFiles)
        {
            YamlFile file = Workflow(name);
            Assert.True(groups.ContainsKey(name), $"{name} needs a production concurrency group.");
            Dictionary<string, object?> concurrency = Map(file.Root["concurrency"]);
            Assert.Equal(groups[name], Text(concurrency["group"]));
            Assert.Equal("false", Text(concurrency["cancel-in-progress"]).ToLowerInvariant());
            Assert.Empty(Map(file.Root["permissions"]));

            foreach (KeyValuePair<string, object?> job in Jobs(file))
            {
                Assert.False(Map(job.Value).ContainsKey("concurrency"), $"{name}:{job.Key} must not set its own concurrency; the group is workflow-level.");
            }

            List<(string Job, object? Value)> ssh = [.. JobsUsingSshSetup(file)];
            Assert.NotEmpty(ssh);
            foreach ((string job, object? value) in ssh)
            {
                Assert.Equal("production", Text(Map(value).GetValueOrDefault("environment")));
                Assert.True(IsMap(Map(value)["permissions"]), $"{name}:{job} must declare its permissions.");
            }
        }
    }

    private static bool UsesProduction(YamlFile file) =>
        JobsUsingSshSetup(file).Any() || Jobs(file).Values.Any(job => IsMap(job) && Map(job).ContainsKey("environment"));

    // The list above is what the deploy checks run over; a workflow that touches the server or reads the production
    // environment without being on it would escape the concurrency, staging and test-hook rules.
    [Fact]
    public void DeploySet_IsExactlyTheWorkflowsThatUseSshSetupOrAnEnvironment()
    {
        AssertDeploySet(Workflows());
    }

    [Fact]
    public void DeploySetCheck_FailsOnAnUnlistedProductionWorkflow()
    {
        YamlFile sshOnly = Parse("extra-ssh.yml", "name: extra\non: workflow_dispatch\npermissions: {}\njobs:\n  go:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: ./.github/actions/ssh-setup\n", isAction: false);
        YamlFile environmentOnly = Parse("extra-env.yml", "name: extra\non: workflow_dispatch\npermissions: {}\njobs:\n  go:\n    runs-on: ubuntu-latest\n    environment: production\n    steps:\n      - run: echo hi\n", isAction: false);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertDeploySet([.. Workflows(), sshOnly]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertDeploySet([.. Workflows(), environmentOnly]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertDeploySet([.. Workflows().Where(f => f.Name != "ops-check.yml")]));
    }

    private static void AssertDeploySet(IEnumerable<YamlFile> workflows)
    {
        List<string> found = [.. workflows.Where(UsesProduction).Select(f => f.Name).Order(StringComparer.Ordinal)];
        Assert.Equal([.. DeployWorkflowFiles.Order(StringComparer.Ordinal)], found);
    }

    // Only a job that reaches the server may carry the environment (and so its secrets): a build, test or lint job that
    // declared production would hand every environment secret to code that has no use for it.
    [Fact]
    public void ProductionEnvironment_IsDeclaredOnlyByJobsThatUseSshSetup()
    {
        foreach (YamlFile file in Workflows())
        {
            AssertEnvironmentOnlyOnSshJobs(file);
        }
    }

    [Theory]
    [InlineData("deploy-app.yml", "  build:\n    needs: [resolve, prepare]\n", "  build:\n    environment: production\n    needs: [resolve, prepare]\n")]
    [InlineData("deploy-app.yml", "  resolve:\n    runs-on: ubuntu-latest\n", "  resolve:\n    environment: production\n    runs-on: ubuntu-latest\n")]
    [InlineData("deploy-content.yml", "  checks:\n    needs: resolve\n", "  checks:\n    environment: production\n    needs: resolve\n")]
    [InlineData("ci.yml", "  build-test:\n    runs-on: ubuntu-latest\n", "  build-test:\n    environment: production\n    runs-on: ubuntu-latest\n")]
    [InlineData("ops-check.yml", "    environment: production\n", "")]
    public void EnvironmentCheck_FailsWhenACodeOnlyJobDeclaresAnEnvironment(string name, string from, string to)
    {
        YamlFile mutated = Mutated(name, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEnvironmentOnlyOnSshJobs(mutated));
    }

    private static void AssertEnvironmentOnlyOnSshJobs(YamlFile file)
    {
        foreach (KeyValuePair<string, object?> job in Jobs(file))
        {
            bool declares = Map(job.Value).TryGetValue("environment", out object? environment);
            bool sshJob = UsesSshSetup(job.Value);
            Assert.True(declares == sshJob, $"{file.Name}:{job.Key}: a job has the production environment exactly when it uses ssh-setup.");
            if (declares)
            {
                Assert.Equal("production", Text(environment));
            }
        }
    }

    [Fact]
    public void DeployApp_ChecksWorkflowRunProvenance()
    {
        YamlFile file = Workflow("deploy-app.yml");

        AssertProvenance(file);
        Dictionary<string, object?> trigger = Map(Map(file.Root["on"])["workflow_run"]);
        Assert.Equal(["ci"], ((List<object?>)trigger["workflows"]!).Select(Text));
        Assert.Equal(["completed"], ((List<object?>)trigger["types"]!).Select(Text));
        Assert.Equal(["main"], ((List<object?>)trigger["branches"]!).Select(Text));
        Assert.Equal("ci", Text(Workflow("ci.yml").Root["name"]));
    }

    [Theory]
    [InlineData("\"$RUN_CONCLUSION\" = \"success\"")]
    [InlineData("\"$RUN_EVENT\" = \"push\"")]
    [InlineData("\"$RUN_BRANCH\" = \"main\"")]
    [InlineData("\"$RUN_REPOSITORY\" = \"$REPOSITORY\"")]
    [InlineData("needs.resolve.outputs.sha != ''")]
    [InlineData("--is-ancestor \"$sha\" origin/main")]
    public void DeployApp_ProvenanceCheckFailsWhenAGuardIsRemoved(string marker)
    {
        AssertProvenance(Workflow("deploy-app.yml"));
        YamlFile mutated = WithoutLine("deploy-app.yml", marker);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertProvenance(mutated));
    }

    // The comparisons themselves are asserted, each tied through its env name to the event property it reads, so a
    // renamed variable cannot detach a check from its source and a deleted check cannot hide behind an unused env entry.
    private static void AssertProvenance(YamlFile file)
    {
        Dictionary<string, object?> jobs = Jobs(file);
        object? step = Steps(jobs["resolve"]).Single(s => Map(s).ContainsKey("env")
            && Map(Map(s)["env"]).Values.Any(v => Text(v).Contains("github.event.workflow_run.conclusion", StringComparison.Ordinal)));
        Dictionary<string, object?> env = Map(Map(step)["env"]);
        string run = StepRun(step);

        string NameOf(string expression) => env.Single(p => Text(p.Value) == "${{ " + expression + " }}").Key;
        void RequireSkip(string expression, string expected)
        {
            string pattern = @"\[ ""\$" + NameOf(expression) + @""" = " + expected + @" \] \|\| skip";
            Assert.True(Regex.IsMatch(run, pattern), $"resolve must compare {expression} ({pattern}).");
        }

        RequireSkip("github.event.workflow_run.conclusion", @"""success""");
        RequireSkip("github.event.workflow_run.event", @"""push""");
        RequireSkip("github.event.workflow_run.head_branch", @"""main""");
        RequireSkip("github.event.workflow_run.head_repository.full_name", @"""\$" + NameOf("github.repository") + @"""");
        // The triggering commit is not what ships (see AssertNewestGreen), so its SHA is not copied into the output.
        Assert.DoesNotMatch(@"sha=""\$" + NameOf("github.event.workflow_run.head_sha") + @"""", run);
        Assert.Contains("git merge-base --is-ancestor \"$sha\" origin/main", run, StringComparison.Ordinal);

        Dictionary<string, object?> prepare = Map(jobs["prepare"]);
        Assert.Equal("resolve", Text(prepare["needs"]));
        Assert.Contains("needs.resolve.outputs.sha != ''", Text(prepare.GetValueOrDefault("if")), StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_ReadsDeployedCommitFromServer()
    {
        YamlFile file = Workflow("deploy-app.yml");
        string prepare = string.Join('\n', Steps(Jobs(file)["prepare"]).Select(StepRun));

        // Which release runs is the server's record; the API is asked only which commit passed ci (resolve).
        Assert.DoesNotContain("gh run", file.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("gh api", prepare, StringComparison.Ordinal);
        Assert.Contains("last-good.env", prepare, StringComparison.Ordinal);
        Assert.Contains("DIT_COMMIT", prepare, StringComparison.Ordinal);
        Assert.Contains("APP_IMAGE", prepare, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_DeploysTheNewestCommitWithASuccessfulCiPushRun()
    {
        AssertNewestGreen(Workflow("deploy-app.yml"));
    }

    [Theory]
    [InlineData("status=success", "status=completed")]
    [InlineData("event=push&", "")]
    [InlineData("branch=main&", "")]
    [InlineData("workflows/ci.yml/runs", "runs")]
    [InlineData("      actions: read\n      contents: read\n    outputs:\n      sha:", "      contents: read\n    outputs:\n      sha:")]
    [InlineData("          GH_TOKEN: ${{ github.token }}\n        run: |\n          set -euo pipefail\n          skip()", "          GH_TOKEN: ${{ secrets.DEPLOY_SSH_KEY }}\n        run: |\n          set -euo pipefail\n          skip()")]
    [InlineData("            sha=\"$(newest_green)\"\n            [ -n \"$sha\" ] || skip", "            sha=\"$RUN_SHA\"\n            [ -n \"$sha\" ] || skip")]
    [InlineData("              sha=\"$(newest_green)\"\n              if [ -z", "              sha=\"$WORKFLOW_SHA\"\n              if [ -z")]
    [InlineData("git merge-base --is-ancestor \"$candidate\" origin/main", "true")]
    [InlineData("git merge-base --is-ancestor \"$best\" \"$candidate\"", "true")]
    [InlineData("successful ci runs of main.\" >&2", "successful ci runs of main.\"")]
    public void NewestGreenCheck_FailsOnAViolation(string from, string to)
    {
        AssertNewestGreen(Workflow("deploy-app.yml"));
        YamlFile mutated = Mutated("deploy-app.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNewestGreen(mutated));
    }

    // An annotation echoed inside a command substitution (inline, or in a function that is called as $(name)) is captured
    // into the variable instead of reaching the log, and the step then dies silently on set -e.
    [Fact]
    public void Workflows_SendAnnotationsInsideCommandSubstitutionsToStderr()
    {
        foreach (YamlFile file in AllFiles())
        {
            AssertAnnotationsToStderr(file);
        }
    }

    [Fact]
    public void AnnotationCheck_FailsWhenASubstitutionCapturesAnAnnotation()
    {
        YamlFile mutated = Mutated("deploy-app.yml", "successful ci runs of main.\" >&2", "successful ci runs of main.\"");
        YamlFile inline = Parse("inline.yml", "name: x\njobs:\n  a:\n    steps:\n      - run: |\n          v=\"$(echo \"::notice::hi\"; date)\"\n", isAction: false);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAnnotationsToStderr(mutated));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAnnotationsToStderr(inline));
    }

    private static void AssertAnnotationsToStderr(YamlFile file)
    {
        Regex annotation = new(@"echo\s+""::(?:error|notice|warning)::[^""\n]*""(?!\s*>&2)");
        foreach (string run in AllNodes(file).Where(n => n.Path.Length > 0 && n.Path[^1] == "run" && n.Value is string).Select(n => (string)n.Value!))
        {
            List<string> bodies = [];
            for (int at = run.IndexOf("$(", StringComparison.Ordinal); at >= 0; at = run.IndexOf("$(", at + 2, StringComparison.Ordinal))
            {
                int depth = 1;
                int end = at + 2;
                while (end < run.Length && depth > 0)
                {
                    depth += run[end] == '(' ? 1 : run[end] == ')' ? -1 : 0;
                    end++;
                }

                bodies.Add(run[(at + 2)..Math.Max(at + 2, end - 1)]);
            }

            foreach (Match call in Regex.Matches(run, @"\$\((?<name>[A-Za-z_][A-Za-z0-9_]*)\)"))
            {
                Match definition = Regex.Match(run, @"(?ms)^\s*" + call.Groups["name"].Value + @"\(\) \{\n(?<body>.*?)^\s*\}\n");
                if (definition.Success)
                {
                    bodies.Add(definition.Groups["body"].Value);
                }
            }

            foreach (string body in bodies)
            {
                Assert.False(annotation.IsMatch(body), $"{file.Name}: an annotation inside a command substitution must go to stderr (>&2): {body.Trim()[..Math.Min(80, body.Trim().Length)]}");
            }
        }
    }

    [Fact]
    public void DeployApp_AutomaticRunsSkipTheCommitThatAlreadyRuns()
    {
        AssertSkipsCommitThatRuns(Workflow("deploy-app.yml"));
    }

    [Theory]
    [InlineData("[ \"$deploy\" = \"true\" ] && [ \"$deployed\" = \"$SHA\" ]", "[ \"$deploy\" = \"true\" ] && [ \"$deployed\" = \"$SHA\" ] && [ \"$ROLLBACK\" = \"true\" ]")]
    [InlineData("already runs; no deployment", "already runs")]
    public void SkipCheck_FailsOnAViolation(string from, string to)
    {
        AssertSkipsCommitThatRuns(Workflow("deploy-app.yml"));
        YamlFile mutated = Mutated("deploy-app.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSkipsCommitThatRuns(mutated));
    }

    // Out-of-order ci runs resolve the commit that already runs; only an automatic run skips it, and a manual run of the
    // deployed commit still redeploys (the settings re-render depends on that).
    private static void AssertSkipsCommitThatRuns(YamlFile file)
    {
        string prepare = string.Join('\n', Steps(Jobs(file)["prepare"]).Select(StepRun));
        int automatic = prepare.IndexOf("[ \"$EVENT_NAME\" = \"workflow_run\" ]; then\n    # A push that changed only content/", StringComparison.Ordinal);
        int skip = prepare.IndexOf("[ \"$deploy\" = \"true\" ] && [ \"$deployed\" = \"$SHA\" ]", StringComparison.Ordinal);
        int stale = prepare.IndexOf("[ \"$ROLLBACK\" != \"true\" ] && [ \"$SHA\" != \"$deployed\" ]", StringComparison.Ordinal);
        Assert.True(automatic >= 0 && skip > automatic && stale > skip, "The already-runs skip must sit inside the workflow_run block.");
        Assert.DoesNotContain("[ \"$deploy\" = \"true\" ] && [ \"$deployed\" = \"$SHA\" ] &&", prepare, StringComparison.Ordinal);
        Assert.Contains("already runs; no deployment", prepare, StringComparison.Ordinal);
        int between = prepare.IndexOf("fi\n  fi\n", skip, StringComparison.Ordinal);
        Assert.True(between > skip && between < stale, "The skip must be closed inside the workflow_run block.");
    }

    // A late ci run of an older commit replaces the pending deployment of a newer one (one pending run per group), so the
    // commit is chosen by what passed ci, never by which run triggered this one. The empty-ref manual run uses the same
    // choice; a rollback names its commit.
    private static void AssertNewestGreen(YamlFile file)
    {
        Dictionary<string, object?> resolve = Map(Jobs(file)["resolve"]);
        Dictionary<string, object?> permissions = Map(resolve["permissions"]);
        Assert.Equal("read", Text(permissions.GetValueOrDefault("actions")));
        Assert.Equal("read", Text(permissions["contents"]));

        object? step = Steps(resolve).Single(s => Text(Map(s).GetValueOrDefault("id")) == "resolve");
        Assert.Equal("${{ github.token }}", Text(Map(Map(step)["env"])["GH_TOKEN"]));
        string run = StepRun(step);
        Assert.Contains("gh api \"repos/$REPOSITORY/actions/workflows/ci.yml/runs?branch=main&event=push&status=success&per_page=30\"", run, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor \"$candidate\" origin/main", run, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor \"$best\" \"$candidate\"", run, StringComparison.Ordinal);
        string call = "sha=\"$(newest_green)\"";
        Assert.Equal(2, Regex.Count(run, Regex.Escape(call)));
        int workflowRun = run.IndexOf("if [ \"$EVENT_NAME\" = \"workflow_run\" ]", StringComparison.Ordinal);
        int firstUse = run.IndexOf(call, StringComparison.Ordinal);
        int dispatch = run.IndexOf("# The trusted deploy/", StringComparison.Ordinal);
        Assert.True(workflowRun >= 0 && firstUse > workflowRun && dispatch > firstUse, "The workflow_run path must resolve the newest green commit.");
        Assert.True(run.IndexOf(call, dispatch, StringComparison.Ordinal) > dispatch, "The empty-ref manual path must resolve the newest green commit.");
        Assert.Contains("A rollback needs the ref input.", run, StringComparison.Ordinal);
        Assert.Contains("successful ci runs of main.\" >&2", run, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKFLOW_SHA", run, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_AutomaticRunsRespectTheRollbackHold()
    {
        AssertRollbackHold(Workflow("deploy-app.yml"), Workflow("rotate-db-password.yml"));
    }

    [Theory]
    [InlineData("deploy-app.yml", "if [ -f '$APP_DIR/flags/deploy-hold' ]; then echo hold; fi; ", "")]
    [InlineData("deploy-app.yml", "[ \"$EVENT_NAME\" = \"workflow_run\" ] && grep -qx hold <<<\"$record\"", "grep -qx hold <<<\"$record\"")]
    [InlineData("deploy-app.yml", "        if: ${{ inputs.rollback }}\n        run: |", "        run: |")]
    [InlineData("deploy-app.yml", "NO HOLD EXISTS", "hold failed")]
    [InlineData("deploy-app.yml", "[ \"$KEEP_HOLD\" = \"true\" ] && [ \"$SHA\" != \"$deployed\" ]", "[ \"$KEEP_HOLD\" = \"never\" ]")]
    [InlineData("deploy-app.yml", "[ \"$KEEP_HOLD\" = \"true\" ] && [ -z \"$DISPATCH_REF\" ]", "[ \"$KEEP_HOLD\" = \"never\" ]")]
    [InlineData("deploy-app.yml", "        if: ${{ github.event_name == 'workflow_dispatch' && !inputs.rollback && !inputs.keep_hold }}", "        if: ${{ github.event_name == 'workflow_dispatch' && !inputs.keep_hold }}")]
    [InlineData("deploy-app.yml", "        if: ${{ github.event_name == 'workflow_dispatch' && !inputs.rollback && !inputs.keep_hold }}", "        if: ${{ !inputs.rollback && !inputs.keep_hold }}")]
    [InlineData("deploy-app.yml", "        if: ${{ github.event_name == 'workflow_dispatch' && !inputs.rollback && !inputs.keep_hold }}", "        if: ${{ github.event_name == 'workflow_dispatch' && !inputs.rollback }}")]
    [InlineData("deploy-app.yml", "      keep_hold:\n", "      keep_hold_x:\n")]
    [InlineData("deploy-app.yml", "      - name: Hold automatic deployments after a rollback\n", "      - name: Hold automatic deployments after a rollback\n        continue-on-error: true\n")]
    [InlineData("deploy-app.yml", "      - name: Check out the tip of main\n", "      - name: Hold marker\n        run: gh variable set DEPLOY_HOLD\n      - name: Check out the tip of main\n")]
    [InlineData("rotate-db-password.yml", " -f keep_hold=true", "")]
    public void RollbackHoldCheck_FailsOnAViolation(string name, string from, string to)
    {
        AssertRollbackHold(Workflow("deploy-app.yml"), Workflow("rotate-db-password.yml"));
        YamlFile mutated = Mutated(name, from, to);
        YamlFile app = name == "deploy-app.yml" ? mutated : Workflow("deploy-app.yml");
        YamlFile rotate = name == "rotate-db-password.yml" ? mutated : Workflow("rotate-db-password.yml");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRollbackHold(app, rotate));
    }

    // GITHUB_TOKEN has no permission that writes environment variables, so the hold is a file on the server that the
    // workflow writes and reads over ssh. It is set only after a successful rollback and cleared only by a manual run
    // without rollback; rotate-db-password redeploys the commit that runs and must not clear it.
    private static void AssertRollbackHold(YamlFile app, YamlFile rotate)
    {
        Assert.DoesNotContain("gh variable", app.Text, StringComparison.Ordinal);
        Dictionary<string, object?> inputs = Map(Map(Map(app.Root["on"])["workflow_dispatch"])["inputs"]);
        Assert.True(inputs.ContainsKey("keep_hold"), "deploy-app must declare keep_hold.");
        Assert.Equal("boolean", Text(Map(inputs["keep_hold"]).GetValueOrDefault("type")));
        Assert.Equal("false", Text(Map(inputs["keep_hold"]).GetValueOrDefault("default")).ToLowerInvariant());

        string prepare = string.Join('\n', Steps(Jobs(app)["prepare"]).Select(StepRun));
        Assert.Contains("if [ -f '$APP_DIR/flags/deploy-hold' ]; then echo hold; fi; ", prepare, StringComparison.Ordinal);
        Assert.Contains("[ \"$EVENT_NAME\" = \"workflow_run\" ] && grep -qx hold <<<\"$record\"", prepare, StringComparison.Ordinal);

        List<object?> steps = Steps(Jobs(app)["deploy"]);
        int wait = StepIndex(steps, "Wait for the outcome");
        int hold = StepIndex(steps, "Hold automatic deployments after a rollback");
        int release = StepIndex(steps, "Release the hold after a manual deployment");
        Assert.True(wait >= 0 && hold == wait + 1 && release == hold + 1, "The hold steps must directly follow the wait step, which fails the job unless the deployment succeeded.");
        Assert.False(Map(steps[hold]).ContainsKey("continue-on-error"));
        Assert.False(Map(steps[release]).ContainsKey("continue-on-error"));
        Assert.Equal("${{ inputs.rollback }}", Text(Map(steps[hold]).GetValueOrDefault("if")));
        Assert.Contains("> '$APP_DIR/flags/deploy-hold'", StepRun(steps[hold]), StringComparison.Ordinal);
        // A failed write must say plainly that no hold exists and how to set it.
        Assert.Contains("NO HOLD EXISTS", StepRun(steps[hold]), StringComparison.Ordinal);
        Assert.Contains("exit 1", StepRun(steps[hold]), StringComparison.Ordinal);
        // keep_hold is for redeploying what runs: it needs a ref (resolve) equal to the deployed commit (prepare).
        Assert.Contains("[ \"$KEEP_HOLD\" = \"true\" ] && [ -z \"$DISPATCH_REF\" ]", string.Join("\n", Steps(Jobs(app)["resolve"]).Select(StepRun)), StringComparison.Ordinal);
        Assert.Contains("[ \"$KEEP_HOLD\" = \"true\" ] && [ \"$SHA\" != \"$deployed\" ]", prepare, StringComparison.Ordinal);
        string releaseCondition = Text(Map(steps[release]).GetValueOrDefault("if"));
        Assert.Contains("github.event_name == 'workflow_dispatch'", releaseCondition, StringComparison.Ordinal);
        Assert.Contains("!inputs.rollback", releaseCondition, StringComparison.Ordinal);
        Assert.Contains("!inputs.keep_hold", releaseCondition, StringComparison.Ordinal);
        Assert.Contains("rm -f '$APP_DIR/flags/deploy-hold'", StepRun(steps[release]), StringComparison.Ordinal);

        Assert.Contains("-f keep_hold=true", string.Join('\n', RotateSteps(rotate).Select(StepRun)), StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_DeployJobRunsWhenCompatSkipped()
    {
        Dictionary<string, object?> deploy = Map(Jobs(Workflow("deploy-app.yml"))["deploy"]);
        string condition = Text(deploy["if"]);
        List<string> needs = [.. ((List<object?>)deploy["needs"]!).Select(Text)];

        Assert.Contains("!cancelled()", condition, StringComparison.Ordinal);
        Assert.Contains("needs.prepare.outputs.deploy == 'true'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.smoke.result == 'success'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.compat.result == 'success'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.compat.result == 'skipped'", condition, StringComparison.Ordinal);
        Assert.Contains("prepare", needs);
        Assert.Contains("smoke", needs);
        Assert.Contains("compat", needs);
        Assert.Contains("build", needs);
    }

    [Fact]
    public void DeployApp_ManualSameCommitRedeploys()
    {
        string prepare = string.Join('\n', Steps(Jobs(Workflow("deploy-app.yml"))["prepare"]).Select(StepRun));

        // The content-only rule belongs to the automatic path and needs a non-empty diff.
        Assert.Matches(@"\[ ""\$EVENT_NAME"" = ""workflow_run"" \]", prepare);
        Assert.Contains("[ -n \"$files\" ]", prepare, StringComparison.Ordinal);
        Assert.Contains("'^content/'", prepare, StringComparison.Ordinal);
        // The stale guard skips only a strict ancestor and never when the commit equals the deployed one.
        Assert.Contains("[ \"$SHA\" != \"$deployed\" ]", prepare, StringComparison.Ordinal);
        Assert.Contains("[ \"$ROLLBACK\" != \"true\" ]", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("[ \"$SHA\" = \"$deployed\" ]", prepare, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployJobs_UploadOnlyIntoStaging()
    {
        foreach (string name in DeployWorkflowFiles)
        {
            AssertUploadsOnlyIntoStaging(Workflow(name));
        }
    }

    [Theory]
    [InlineData("cat > '$STAGE/.env.new'", "cat > '$APP_DIR/.env'")]
    [InlineData("tar -xzpf - -C '$STAGE/bundle'", "tar -xzpf - -C '$APP_DIR'")]
    [InlineData("mkdir -m 700 '$STAGE' '$STAGE/bundle'", "mkdir -m 700 '$STAGE' '$APP_DIR/bundles'")]
    [InlineData("cat > '$STAGE/.env.new'", "cp '$STAGE/.env.new' '$STAGE/.env' ; cat > '$APP_DIR/.env'")]
    [InlineData("cat > '$STAGE/.env.new'", "cat > '$STAGE/.env.new'; mv '$STAGE/.env.new' '$APP_DIR/.env'")]
    [InlineData("cat > '$STAGE/.env.new'", "cat > '$STAGE/.env.new'; ln -sfn '$STAGE' '$APP_DIR/current-bundle'")]
    [InlineData("cat > '$STAGE/.env.new'", "cat > '$STAGE/.env.new'; chmod 777 '$APP_DIR'")]
    [InlineData("rm -rf '$STAGE'; fi", "rm -rf '$APP_DIR/staging'; fi")]
    [InlineData("rm -rf '$STAGE'; fi", "rm -rf '$APP_DIR'; fi")]
    [InlineData("rm -rf '$STAGE'; fi", "rm -rf $APP_DIR/bundles; fi")]
    [InlineData("echo '$SHA' > '$APP_DIR/flags/deploy-hold'", "echo '$SHA' > '$APP_DIR/flags/maintenance'")]
    [InlineData("rm -f '$APP_DIR/flags/deploy-hold'", "rm -f '$APP_DIR/flags'")]
    public void DeployJobs_UploadCheckFailsOnAWriteOutsideStaging(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-app.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertUploadsOnlyIntoStaging(mutated));
    }

    // Only the remote command (the quoted argument of ssh) is judged: $RUNNER_TEMP is a legal source on the runner but a
    // write target on the server must be the run's staging directory, whatever else the line mentions.
    private static void AssertUploadsOnlyIntoStaging(YamlFile file)
    {
        int targets = 0;
        foreach ((string job, object? value) in JobsUsingSshSetup(file))
        {
            string script = string.Join('\n', Steps(value).Select(StepRun));
            Assert.DoesNotMatch(@"\b(?:scp|rsync|sftp)\b", script);
            foreach (Match remote in RemoteArgument().Matches(script))
            {
                // The inline wrapper of the detached start is fixed text whose paths are variables; it is judged by its own test.
                string command = InlineScript().Replace(remote.Groups["cmd"].Value, "bash -c SCRIPT");
                Assert.False(CopyCommand().IsMatch(command), $"{file.Name}:{job}: a remote command copies files: {command}");
                List<string> paths = [.. WriteTarget().Matches(command).Select(m => m.Groups["path"].Value)];
                foreach (Match directoryCommand in DirectoryCommand().Matches(command))
                {
                    string verb = directoryCommand.Groups["verb"].Value;
                    string args = directoryCommand.Groups["args"].Value;
                    List<string> quoted = [.. QuotedPath().Matches(args).Select(m => m.Groups["path"].Value)];
                    paths.AddRange(quoted);

                    // A target that is not quoted (a bare variable or path) could not be checked, so it is refused.
                    foreach (string token in QuotedPath().Replace(args, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Assert.True(
                            token.StartsWith('-') || Regex.IsMatch(token, "^[0-7]{3,4}$"),
                            $"{file.Name}:{job}: '{verb}' must name its targets in single quotes: {command}");
                    }

                    if (verb == "rm")
                    {
                        Assert.False(quoted.Any(p => p.TrimEnd('/') == "$APP_DIR/staging"), $"{file.Name}:{job}: rm must not remove the staging root.");
                    }
                }

                foreach (string path in paths)
                {
                    targets++;
                    // The one other file the workflow layer owns on the server: the hold that a rollback sets.
                    Assert.True(
                        path == "$APP_DIR/flags/deploy-hold"
                        || ((path.StartsWith("$STAGE", StringComparison.Ordinal) || path.StartsWith("$APP_DIR/staging", StringComparison.Ordinal))
                            && !path.Contains("..", StringComparison.Ordinal)),
                        $"{file.Name}:{job}: a remote write must target the staging directory, not '{path}'.");
                }
            }

            if (script.Contains("STAGE=", StringComparison.Ordinal))
            {
                Assert.Matches(@"STAGE=""?\$APP_DIR/staging/", script);
            }
        }

        Assert.True(targets >= 3, $"{file.Name}: the remote write targets were not found; the check would pass on anything.");
    }

    [Fact]
    public void DeployJobs_CleanUpSshMaterialAlways()
    {
        foreach (string name in DeployWorkflowFiles)
        {
            YamlFile file = Workflow(name);
            foreach ((string job, object? value) in JobsUsingSshSetup(file))
            {
                object? last = Steps(value)[^1];
                Assert.Equal("always()", Text(Map(last).GetValueOrDefault("if")).Trim());
                Assert.Contains("rm -rf \"$RUNNER_TEMP/ssh\" \"$RUNNER_TEMP/env\"", StepRun(last), StringComparison.Ordinal);
                Assert.True(Steps(value).Count(s => StepUses(s) == SshSetup) == 1, $"{name}:{job} sets up ssh once.");
            }
        }
    }

    [Fact]
    public void DeployApp_StartsTheServerScriptDetachedAndWaitsForItsExitStatus()
    {
        YamlFile file = Workflow("deploy-app.yml");
        List<object?> steps = Steps(Jobs(file)["deploy"]);
        string start = StepRun(steps.Single(s => Text(Map(s).GetValueOrDefault("name")).StartsWith("Start the deployment", StringComparison.Ordinal)));
        string wait = StepRun(steps.Single(s => Text(Map(s).GetValueOrDefault("name")) == "Wait for the outcome"));

        Assert.Contains("setsid bash -c", start, StringComparison.Ordinal);
        Assert.Contains("</dev/null >/dev/null 2>&1 &", start, StringComparison.Ordinal);
        Assert.Contains("deploy.exit", wait, StringComparison.Ordinal);
        // Success is the exit status 0 and nothing else: the only assignments of code are its start value and the status read.
        Assert.Contains("[ \"$code\" = \"0\" ]", wait, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(wait, @"^\s*code=", RegexOptions.Multiline));
        Assert.Contains("code=\"${state#exit:}\"", wait, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(wait, @"\bexit 0\b"));
        Assert.EndsWith("exit 1", wait.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_CompatTestsTheReleaseThatWillRunOnTheSchemaThatWillBeInTheDatabase()
    {
        AssertCompatArguments(Workflow("deploy-app.yml"));
    }

    [Fact]
    public void DeployApp_CompatCheckFailsWhenTheRollbackArgumentsAreNotSwapped()
    {
        YamlFile mutated = Mutated("deploy-app.yml", "compat_previous=\"$SHA\"", "compat_previous=\"$deployed\"");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCompatArguments(mutated));
    }

    // Default: the running release (deployed) must work on the schema of the new one (SHA). Rollback to an ancestor: the
    // database keeps the newer schema, so the older release (SHA) is tested on the schema of the deployed commit.
    private static void AssertCompatArguments(YamlFile file)
    {
        Dictionary<string, object?> with = Map(Map(Jobs(file)["compat"])["with"]);
        Assert.Equal("${{ needs.prepare.outputs.compat_previous }}", Text(with["previous"]));
        Assert.Equal("${{ needs.prepare.outputs.compat_current }}", Text(with["current"]));

        string prepare = string.Join('\n', Steps(Jobs(file)["prepare"]).Select(StepRun));
        Assert.Matches(
            @"compat_previous=""\$deployed""\s+compat_current=""\$SHA""\s+if \[ ""\$ROLLBACK"" = ""true"" \][^\n]*git merge-base --is-ancestor ""\$SHA"" ""\$deployed""[^\n]*; then\s+compat_previous=""\$SHA""\s+compat_current=""\$deployed""\s+fi",
            prepare);
        Assert.Contains("echo \"compat_previous=$compat_previous\"", prepare, StringComparison.Ordinal);
        Assert.Contains("echo \"compat_current=$compat_current\"", prepare, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresImageDigest_IsWrittenOnlyInComposeAndTheWorkflowsReadItFromThere()
    {
        string compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        Assert.Matches(@"image:\s*postgres:[^@\s]+@sha256:[0-9a-f]{64}", compose);

        string action = AllFiles().Single(f => f.Name == ActionPostgres).Text;
        Assert.Contains("deploy/compose/compose.yaml", action, StringComparison.Ordinal);
        Assert.DoesNotMatch("sha256:[0-9a-f]{64}", action);

        foreach (string name in new[] { "ci.yml", "schema-compat.yml" })
        {
            AssertPostgresFromCompose(Workflow(name));
        }
    }

    [Theory]
    [InlineData("ci.yml", "      - name: Start PostgreSQL\n        uses: ./.github/actions/postgres-from-compose\n", "")]
    [InlineData("ci.yml", "    permissions:\n      contents: read\n    env:\n      DIT_RELEASE_PUBLISH_DIR", "    permissions:\n      contents: read\n    services:\n      postgres:\n        image: postgres@sha256:d3244e8ce3f165ee3c93d9c4a4b1d0fe48b3a89e2287b717fdcbbde92d405065\n    env:\n      DIT_RELEASE_PUBLISH_DIR")]
    [InlineData("schema-compat.yml", "        uses: ./.github/actions/postgres-from-compose\n", "        run: echo none\n")]
    [InlineData("schema-compat.yml", "    env:\n      PREVIOUS", "    services:\n      postgres:\n        image: postgres:17-alpine\n    env:\n      PREVIOUS")]
    public void PostgresPinCheck_FailsWhenAWorkflowStopsReadingTheComposePin(string name, string from, string to)
    {
        AssertPostgresFromCompose(Workflow(name));
        YamlFile mutated = Mutated(name, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertPostgresFromCompose(mutated));
    }

    private const string ActionPostgres = ".github/actions/postgres-from-compose/action.yml";

    private static void AssertPostgresFromCompose(YamlFile file)
    {
        Assert.Contains("uses: ./.github/actions/postgres-from-compose", file.Text, StringComparison.Ordinal);
        Assert.DoesNotMatch("postgres[:@]", string.Join('\n', file.Text.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && !l.Contains("postgres-from-compose", StringComparison.Ordinal))));
        Assert.False(Jobs(file).Values.Any(job => IsMap(job) && Map(job).ContainsKey("services")), $"{file.Name}: PostgreSQL starts from the compose pin, not from a services block.");
    }

    [Fact]
    public void DeployApp_PassesOneImageRefAndOneAppDir()
    {
        YamlFile file = Workflow("deploy-app.yml");
        Dictionary<string, object?> deploy = Map(Jobs(file)["deploy"]);
        Dictionary<string, object?> jobEnv = Map(deploy["env"]);

        Assert.Equal("${{ needs.build.outputs.image }}", Text(jobEnv["IMAGE"]));
        Assert.Equal("${{ vars.APP_DIR }}", Text(jobEnv["APP_DIR"]));
        Dictionary<string, object?> render = Map(Steps(deploy).Single(s => StepRun(s).Contains("render-env.sh", StringComparison.Ordinal)));
        Assert.Equal("${{ needs.build.outputs.image }}", Text(Map(render["env"])["APP_IMAGE"]));
        Assert.Equal("${{ vars.APP_DIR }}", Text(Map(render["env"])["APP_DIR"]));
        Assert.DoesNotContain("/srv/dev-interview-trainer", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionWorkflows_NeverMentionTheTestHooks()
    {
        foreach (YamlFile file in AllFiles().Where(f => !TestHookWorkflowFiles.Contains(f.Name)))
        {
            AssertNoTestHooks(file);
        }
    }

    [Theory]
    [InlineData("DIT_TEST_HOOKS")]
    [InlineData("DIT_TEST_KILL_AT")]
    [InlineData("DIT_TEST_IMPORT_URL")]
    public void TestHookCheck_FailsWhenAProductionWorkflowNamesAHook(string hook)
    {
        YamlFile mutated = Mutated("deploy-content.yml", "      STAGE_ID: ${{ github.run_id }}-${{ github.run_attempt }}\n", "      STAGE_ID: ${{ github.run_id }}-${{ github.run_attempt }}\n      " + hook + ": 1\n");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoTestHooks(mutated));
    }

    private static void AssertNoTestHooks(YamlFile file)
    {
        foreach (string hook in TestHookNames)
        {
            Assert.DoesNotContain(hook, file.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SshSetup_EveryUsePassesTheDeployPort()
    {
        foreach (string name in DeployWorkflowFiles)
        {
            AssertSshSetupPassesThePort(Workflow(name));
        }

        string action = File.ReadAllText(Path.Combine(Root, ".github", "actions", "ssh-setup", "action.yml"));
        Assert.Contains("SSH_PORT: ${{ inputs.port }}", action, StringComparison.Ordinal);
        Assert.Contains("[[ \"$SSH_PORT\" =~ ^[0-9]{1,5}$ ]]", action, StringComparison.Ordinal);
        Assert.Contains("echo \"  Port $SSH_PORT\"", action, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deploy-content.yml")]
    [InlineData("ops-check.yml")]
    [InlineData("rotate-db-password.yml")]
    public void SshSetup_FailsWhenAUseDropsThePort(string name)
    {
        YamlFile mutated = WithoutLine(name, "port: ${{ vars.DEPLOY_SSH_PORT || '22' }}");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSshSetupPassesThePort(mutated));
    }

    [Fact]
    public void SshSetup_FailsWhenAUseHardCodesThePort()
    {
        YamlFile mutated = Mutated("ops-check.yml", "port: ${{ vars.DEPLOY_SSH_PORT || '22' }}", "port: 22");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSshSetupPassesThePort(mutated));
    }

    // The port lives in a variable the bootstrap writes; a use that forgets it connects to 22 and fails on a moved sshd.
    private static void AssertSshSetupPassesThePort(YamlFile file)
    {
        List<(string Job, object? Value)> uses = [.. JobsUsingSshSetup(file)];
        Assert.NotEmpty(uses);
        foreach ((string job, object? value) in uses)
        {
            foreach (object? step in Steps(value).Where(s => StepUses(s) == SshSetup))
            {
                Assert.Equal("${{ vars.DEPLOY_SSH_PORT || '22' }}", Text(Map(Map(step)["with"]).GetValueOrDefault("port")));
            }
        }
    }

    // provision.sh runs as root over ssh; its test hooks move key and sshd paths and switch off the ownership steps, so
    // they are honoured only under DIT_TEST_HOOKS=1 and never named anywhere else.
    [Fact]
    public void ProvisionScript_HonoursItsTestHooksOnlyUnderTheHookGuard()
    {
        string text = File.ReadAllText(Path.Combine(Root, "deploy", "bootstrap", "remote", "provision.sh"));
        Match guard = System.Text.RegularExpressions.Regex.Match(text, @"if \[ ""\$\{DIT_TEST_HOOKS:-\}"" = ""1"" \]; then\n(?<body>(?:  .*\n)+)fi\n");
        Assert.True(guard.Success, "provision.sh must read its test hooks inside an if DIT_TEST_HOOKS=1 block.");
        string outside = text.Replace(guard.Value, string.Empty, StringComparison.Ordinal);
        foreach (string hook in ProvisionHookNames)
        {
            Assert.Contains(hook, guard.Groups["body"].Value, StringComparison.Ordinal);
            Assert.DoesNotContain(hook, outside, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OpsCheck_WaitsForARunningDeploymentButNotThroughTheDeployGroup()
    {
        AssertOpsCheckWait(Workflow("ops-check.yml"));
    }

    [Theory]
    [InlineData("    timeout-minutes: 30\n", "    timeout-minutes: 15\n")]
    [InlineData("    timeout-minutes: 30\n", "    timeout-minutes: 20\n")]
    [InlineData("SECONDS + 1200", "SECONDS + 3600")]
    [InlineData("([d]eploy-app|[r]eceive-content|[r]otate-db-password)", "[d]eploy-app")]
    [InlineData("([d]eploy-app|[r]eceive-content|[r]otate-db-password)", "([d]eploy-app|[r]otate-db-password)")]
    [InlineData("([d]eploy-app|[r]eceive-content|[r]otate-db-password)", "([d]eploy-app|[r]eceive-content)")]
    [InlineData("group: production-ops", "group: production-app")]
    public void OpsCheckWait_FailsOnAViolation(string from, string to)
    {
        AssertOpsCheckWait(Workflow("ops-check.yml"));
        YamlFile mutated = Mutated("ops-check.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertOpsCheckWait(mutated));
    }

    // The job timeout must outlast the wait plus the upload and the checks, or the "running the checks anyway" branch can
    // never be reached. Content deliveries and rotations hold .import-pending and .rotation-pending while they run, which
    // the guards check would report, so the wait covers all three server scripts.
    private static void AssertOpsCheckWait(YamlFile file)
    {
        Dictionary<string, object?> job = Map(Jobs(file)["check"]);
        List<object?> steps = Steps(job);
        int wait = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Wait for a running deployment");
        int run = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Run the checks");

        Assert.True(wait >= 0 && run > wait, "The wait step must come before the checks.");
        string script = StepRun(steps[wait]);
        Assert.Contains("pgrep -f '/bundle/remote/([d]eploy-app|[r]eceive-content|[r]otate-db-password).sh'", script, StringComparison.Ordinal);
        Match seconds = Regex.Match(script, @"SECONDS \+ (?<n>\d+)");
        Assert.True(seconds.Success, "The wait deadline was not found.");
        int waitSeconds = int.Parse(seconds.Groups["n"].Value, CultureInfo.InvariantCulture);
        int timeoutMinutes = int.Parse(Text(job["timeout-minutes"]), CultureInfo.InvariantCulture);
        Assert.True(timeoutMinutes * 60 >= waitSeconds + 600, $"timeout-minutes ({timeoutMinutes}) must exceed the {waitSeconds} s wait by the time of the checks.");
        Assert.Contains("[ \"$SECONDS\" -ge \"$deadline\" ]", script, StringComparison.Ordinal);
        Assert.Contains("sleep", script, StringComparison.Ordinal);
        // One pending run per group: sharing production-app could drop a queued deployment.
        Assert.DoesNotContain("production-app", AllStrings(file), StringComparison.Ordinal);
        Assert.Equal("production-ops", Text(Map(file.Root["concurrency"])["group"]));
    }

    [Fact]
    public void DeployApp_BuildDecidesOnTheManifestLookupThroughTheClassifierScript()
    {
        string script = string.Join('\n', Steps(Jobs(Workflow("deploy-app.yml"))["build"]).Select(StepRun));

        Assert.Contains("inspect_output=\"$(docker manifest inspect", script, StringComparison.Ordinal);
        Assert.Contains("elif bash deploy/scripts/manifest-missing.sh <<<\"$inspect_output\"; then", script, StringComparison.Ordinal);
        Assert.Matches(@"else\s+echo ""::error::Could not look up[^\n]*\s+exit 1", script);
    }

    [Fact]
    public void Ci_ContentJobCallsContentChecksWithReadOnlyPermissions()
    {
        AssertCiContentCall(Workflow("ci.yml"));
    }

    [Theory]
    [InlineData("    uses: ./.github/workflows/content-checks.yml", "    uses: ./.github/workflows/schema-compat.yml")]
    [InlineData("    permissions:\n      contents: read\n    uses: ./.github/workflows/content-checks.yml", "    permissions:\n      contents: write\n    uses: ./.github/workflows/content-checks.yml")]
    [InlineData("    uses: ./.github/workflows/content-checks.yml", "    uses: ./.github/workflows/content-checks.yml\n    secrets: inherit")]
    public void CiContentCheck_FailsWhenTheCallChanges(string from, string to)
    {
        YamlFile mutated = Mutated("ci.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCiContentCall(mutated));
    }

    // The job keeps its name, so branch protection that requires "content" keeps working through the reusable workflow.
    private static void AssertCiContentCall(YamlFile file)
    {
        Dictionary<string, object?> job = Map(Jobs(file)["content"]);

        Assert.Equal("./.github/workflows/content-checks.yml", Text(job["uses"]));
        Assert.Equal("build-test", Text(job["needs"]));
        Assert.Equal("read", Text(Map(job["permissions"])["contents"]));
        Assert.False(job.ContainsKey("secrets"));
    }

    [Fact]
    public void ContentChecks_IsAReusableReadOnlyWorkflowWithoutSecrets()
    {
        AssertContentChecks(Workflow("content-checks.yml"));
    }

    [Theory]
    [InlineData("permissions:\n  contents: read\n\njobs:", "permissions:\n  contents: write\n\njobs:")]
    [InlineData("    permissions:\n      contents: read\n    env:", "    permissions:\n      contents: write\n    env:")]
    [InlineData("        default: \"\"\n", "        default: main\n")]
    [InlineData("          ref: ${{ inputs.ref }}\n          persist-credentials: false", "          persist-credentials: false")]
    [InlineData("    env:\n      # Must match", "    env:\n      TOKEN: ${{ secrets.DB_PASSWORD }}\n      # Must match")]
    [InlineData("    runs-on: ubuntu-latest\n    timeout-minutes: 30\n", "    runs-on: ubuntu-latest\n    timeout-minutes: 30\n    environment: production\n")]
    [InlineData("  workflow_call:\n", "  push:\n  workflow_call:\n")]
    public void ContentChecks_CheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("content-checks.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentChecks(mutated));
    }

    // It runs for pull requests through ci and for production through deploy-content, so it may read the repository and
    // nothing else: no secrets, no environment, and the checkout is the commit its caller names.
    private static void AssertContentChecks(YamlFile file)
    {
        Assert.Equal(["workflow_call"], Triggers(file));
        Dictionary<string, object?> reference = Map(Map(Map(Map(file.Root["on"])["workflow_call"])["inputs"])["ref"]);
        Assert.Equal("string", Text(reference["type"]));
        Assert.Equal(string.Empty, Text(reference["default"]));
        Assert.Equal("false", Text(reference["required"]).ToLowerInvariant());

        Dictionary<string, object?> permissions = Map(file.Root["permissions"]);
        Assert.Equal(["contents"], permissions.Keys);
        Assert.Equal("read", Text(permissions["contents"]));

        KeyValuePair<string, object?> job = Assert.Single(Jobs(file));
        Assert.Equal("content", job.Key);
        Dictionary<string, object?> body = Map(job.Value);
        Assert.False(body.ContainsKey("environment"));
        Assert.Equal("read", Text(Map(body["permissions"])["contents"]));
        Assert.Equal(["contents"], Map(body["permissions"]).Keys);
        Assert.DoesNotContain("secrets", AllStrings(file), StringComparison.Ordinal);
        Assert.DoesNotContain(AllNodes(file), n => n.Path.Length > 0 && n.Path[^1] == "secrets");

        Dictionary<string, object?> checkout = Map(Steps(job.Value).Single(s => StepUses(s).StartsWith("actions/checkout@", StringComparison.Ordinal)));
        Assert.Equal("${{ inputs.ref }}", Text(Map(checkout["with"]).GetValueOrDefault("ref")));
        Assert.Contains("dotnet run --no-build --project tools/Trainer.ContentCli -- validate", RunText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void DeployContent_RunsOnPushesToMainThatTouchContentAndOnDispatch()
    {
        AssertContentTriggers(Workflow("deploy-content.yml"));
    }

    [Theory]
    [InlineData("    branches: [main]\n", "    branches: [main, dev]\n")]
    [InlineData("      - content/**\n", "      - docs/**\n")]
    [InlineData("  workflow_dispatch:\n", "  pull_request:\n  workflow_dispatch:\n")]
    [InlineData("        default: false\n", "        default: true\n")]
    [InlineData("        type: boolean\n", "        type: string\n")]
    public void DeployContent_TriggerCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-content.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentTriggers(mutated));
    }

    private static void AssertContentTriggers(YamlFile file)
    {
        Dictionary<string, object?> on = Map(file.Root["on"]);
        Assert.Equal(["push", "workflow_dispatch"], on.Keys.Order(StringComparer.Ordinal));
        Dictionary<string, object?> push = Map(on["push"]);
        Assert.Equal(["main"], ((List<object?>)push["branches"]!).Select(Text));
        Assert.Equal(["content/**"], ((List<object?>)push["paths"]!).Select(Text));
        Dictionary<string, object?> inputs = Map(Map(on["workflow_dispatch"])["inputs"]);
        Assert.Equal(string.Empty, Text(Map(inputs["ref"])["default"]));
        Dictionary<string, object?> rollback = Map(inputs["rollback"]);
        Assert.Equal("boolean", Text(rollback["type"]));
        Assert.Equal("false", Text(rollback["default"]).ToLowerInvariant());
    }

    [Fact]
    public void DeployContent_ResolvesOneImmutableShaOnMain()
    {
        AssertContentResolve(Workflow("deploy-content.yml"));
    }

    [Theory]
    [InlineData("[[ \"$sha\" =~ ^[0-9a-f]{40}$ ]]")]
    [InlineData("--is-ancestor \"$sha\" origin/main")]
    [InlineData("[ \"$REF_NAME\" != \"refs/heads/main\" ]")]
    [InlineData("--is-ancestor \"$DISPATCH_REF\" origin/main")]
    [InlineData("sha=\"$(git rev-parse origin/main)\"")]
    [InlineData("sha=\"$DISPATCH_REF\"")]
    public void DeployContent_ResolveCheckFailsWhenAGuardIsRemoved(string marker)
    {
        YamlFile mutated = WithoutLine("deploy-content.yml", marker);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentResolve(mutated));
    }

    [Theory]
    [InlineData("          ref: main\n          fetch-depth: 0", "          fetch-depth: 0")]
    [InlineData("sha=\"$(git rev-parse origin/main)\"", "sha=\"$GITHUB_SHA\"")]
    [InlineData("          ROLLBACK: ${{ inputs.rollback }}\n        run: |\n          set -euo pipefail\n          # The trusted", "          ROLLBACK: \"false\"\n        run: |\n          set -euo pipefail\n          # The trusted")]
    public void DeployContent_ResolveCheckFailsWhenTheTipOfMainIsNotWhatANonRollbackRunDelivers(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-content.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentResolve(mutated));
    }

    private static void AssertContentResolve(YamlFile file)
    {
        Dictionary<string, object?> resolve = Map(Jobs(file)["resolve"]);
        Dictionary<string, object?> step = Map(Steps(resolve).Single(s => Text(Map(s).GetValueOrDefault("id")) == "resolve"));
        Dictionary<string, object?> env = Map(step["env"]);
        string run = StepRun(step);

        // A run replaced in the concurrency group must lose nothing: without rollback every run delivers the tip of main as
        // fetched at the start of the run (the branch is checked out by name), and only a rollback delivers the named ref.
        Dictionary<string, object?> checkout = Map(Steps(resolve).Single(s => StepUses(s).StartsWith("actions/checkout@", StringComparison.Ordinal)));
        Assert.Equal("main", Text(Map(checkout["with"]).GetValueOrDefault("ref")));
        Assert.Equal("${{ inputs.ref }}", Text(env["DISPATCH_REF"]));
        Assert.Equal("${{ inputs.rollback }}", Text(env["ROLLBACK"]));
        Assert.Equal("${{ github.ref }}", Text(env["REF_NAME"]));
        Assert.Contains("--is-ancestor \"$DISPATCH_REF\" origin/main", run, StringComparison.Ordinal);
        Assert.Contains("if [ \"$ROLLBACK\" = \"true\" ]; then", run, StringComparison.Ordinal);
        Assert.Contains("sha=\"$DISPATCH_REF\"", run, StringComparison.Ordinal);
        Assert.Contains("sha=\"$(git rev-parse origin/main)\"", run, StringComparison.Ordinal);
        Assert.Equal("${{ steps.resolve.outputs.sha }}", Text(Map(resolve["outputs"])["sha"]));
        Assert.Contains("[ \"$REF_NAME\" != \"refs/heads/main\" ]", run, StringComparison.Ordinal);
        Assert.Contains("[[ \"$sha\" =~ ^[0-9a-f]{40}$ ]]", run, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor \"$sha\" origin/main", run, StringComparison.Ordinal);
        Assert.Contains("echo \"sha=$sha\" >> \"$GITHUB_OUTPUT\"", run, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployContent_ChecksTheResolvedCommitAndRunsTheWorkflowsOwnScripts()
    {
        AssertContentWiring(Workflow("deploy-content.yml"));
    }

    [Theory]
    [InlineData("    with:\n      ref: ${{ needs.resolve.outputs.sha }}", "    with:\n      ref: main")]
    [InlineData("    needs: [resolve, checks]\n", "    needs: [resolve]\n")]
    [InlineData("    uses: ./.github/workflows/content-checks.yml\n    with:", "    uses: ./.github/workflows/schema-compat.yml\n    with:")]
    [InlineData("          fetch-depth: 0\n          persist-credentials: false\n      - uses: ./.github/actions/ssh-setup", "          ref: ${{ needs.resolve.outputs.sha }}\n          fetch-depth: 0\n          persist-credentials: false\n      - uses: ./.github/actions/ssh-setup")]
    [InlineData("git archive --format=tar.gz \"$SHA\" content", "git archive --format=tar.gz HEAD content")]
    public void DeployContent_WiringCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-content.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentWiring(mutated));
    }

    // The scripts that run on the server must be the workflow's own (trusted) commit; only the archive is taken at the
    // resolved content commit, and only after content-checks passed for that commit.
    private static void AssertContentWiring(YamlFile file)
    {
        Dictionary<string, object?> checks = Map(Jobs(file)["checks"]);
        Assert.Equal("./.github/workflows/content-checks.yml", Text(checks["uses"]));
        Assert.Equal("resolve", Text(checks["needs"]));
        Assert.Equal("${{ needs.resolve.outputs.sha }}", Text(Map(checks["with"])["ref"]));
        Assert.Equal("read", Text(Map(checks["permissions"])["contents"]));

        Dictionary<string, object?> deploy = Map(Jobs(file)["deploy"]);
        List<string> needs = [.. ((List<object?>)deploy["needs"]!).Select(Text)];
        Assert.Equal(["checks", "resolve"], needs.Order(StringComparer.Ordinal));
        Assert.Equal("production", Text(deploy["environment"]));
        Assert.Equal("${{ needs.resolve.outputs.sha }}", Text(Map(deploy["env"])["SHA"]));
        Assert.Equal("${{ inputs.rollback }}", Text(Map(deploy["env"])["ROLLBACK"]));

        List<object?> checkouts = [.. Steps(deploy).Where(s => StepUses(s).StartsWith("actions/checkout@", StringComparison.Ordinal))];
        Assert.NotEmpty(checkouts);
        foreach (object? checkout in checkouts)
        {
            Assert.False(Map(Map(checkout)["with"]).ContainsKey("ref"), "deploy must check out the workflow's own commit.");
        }

        Assert.Contains("git archive --format=tar.gz \"$SHA\" content", string.Join('\n', Steps(deploy).Select(StepRun)), StringComparison.Ordinal);
    }

    [Fact]
    public void DeployContent_StaleGuardIsOverriddenOnlyByRollback()
    {
        AssertContentStaleGuard(Workflow("deploy-content.yml"));
    }

    [Theory]
    [InlineData("[ \"$ROLLBACK\" != \"true\" ] && ", "")]
    [InlineData("[ \"$SHA\" != \"$live\" ] && ", "")]
    [InlineData("git merge-base --is-ancestor \"$SHA\" \"$live\"", "git merge-base --is-ancestor \"$live\" \"$SHA\"")]
    [InlineData("Upload the bundle into the staging directory\n        if: ${{ env.STALE != 'true' }}", "Upload the bundle into the staging directory\n        if: always()")]
    [InlineData("Deliver the content\n        if: ${{ env.STALE != 'true' }}", "Deliver the content\n        if: always()")]
    public void DeployContent_StaleGuardCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-content.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentStaleGuard(mutated));
    }

    // The freshness check runs on the runner, before anything is uploaded: the group serialises these runs and nothing
    // else switches current. A commit equal to the live one is delivered again (reuse), a strict ancestor only with rollback.
    private static void AssertContentStaleGuard(YamlFile file)
    {
        List<object?> steps = Steps(Jobs(file)["deploy"]);
        int guard = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Skip content older than the live release");
        int upload = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Upload the bundle into the staging directory");
        int deliver = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Deliver the content");
        Assert.True(guard >= 0 && upload > guard && deliver > upload, "The stale check must precede the upload and the delivery.");

        string run = StepRun(steps[guard]);
        Assert.Contains("readlink '$APP_DIR/content/current'", run, StringComparison.Ordinal);
        Assert.Contains("[ \"$ROLLBACK\" != \"true\" ]", run, StringComparison.Ordinal);
        Assert.Contains("[ \"$SHA\" != \"$live\" ]", run, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor \"$SHA\" \"$live\"", run, StringComparison.Ordinal);
        Assert.DoesNotContain("[ \"$SHA\" = \"$live\" ]", run, StringComparison.Ordinal);
        Assert.Contains("echo \"STALE=true\" >> \"$GITHUB_ENV\"", run, StringComparison.Ordinal);
        foreach (int index in new[] { upload, deliver })
        {
            Assert.Equal("${{ env.STALE != 'true' }}", Text(Map(steps[index])["if"]).Trim());
        }
    }

    [Fact]
    public void DeployContent_DeliversThroughReceiveContentAndOnlyZeroAndFiveAreNotFailures()
    {
        AssertContentDelivery(Workflow("deploy-content.yml"));
    }

    [Theory]
    [InlineData("before relying on this release.\"; exit 1 ;;", "before relying on this release.\" ;;")]
    [InlineData("no later deployment can deliver it.\"; exit 1 ;;", "no later deployment can deliver it.\" ;;")]
    [InlineData("Run this workflow again.\"; exit 1 ;;", "Run this workflow again.\" ;;")]
    [InlineData("refuses deliveries).\"; exit 1 ;;", "refuses deliveries).\" ;;")]
    [InlineData("              if [ \"$ROLLBACK\" = \"true\" ]; then\n                echo \"::error::The content schema is newer", "              if [ \"$ROLLBACK\" = \"never\" ]; then\n                echo \"::error::The content schema is newer")]
    [InlineData("|| code=$?", "|| true")]
    [InlineData("bash '$STAGE/bundle/remote/receive-content.sh' '$STAGE' '$SHA'", "bash '$STAGE/bundle/remote/receive-content.sh' '$STAGE'")]
    [InlineData("if ! pgrep -f '$STAGE/bundle/remote/[r]eceive-content.sh' >/dev/null; then rm -rf '$STAGE'; fi", "rm -rf '$STAGE'")]
    public void DeployContent_DeliveryCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-content.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentDelivery(mutated));
    }

    // Only 0 succeeds quietly and only 5 (content newer than the application, not a rollback) succeeds with a warning;
    // every other status fails the job. The cleanup never removes the staging directory under a running script.
    private static void AssertContentDelivery(YamlFile file)
    {
        List<object?> steps = Steps(Jobs(file)["deploy"]);
        string run = StepRun(steps.Single(s => Text(Map(s).GetValueOrDefault("name")) == "Deliver the content"));

        Assert.Contains("git archive --format=tar.gz \"$SHA\" content | \"${ssh_cmd[@]}\"", run, StringComparison.Ordinal);
        Assert.Contains("bash '$STAGE/bundle/remote/receive-content.sh' '$STAGE' '$SHA'", run, StringComparison.Ordinal);
        Assert.Contains("|| code=$?", run, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s*0\) echo ""Content \$SHA is imported and current\."" ;;$", run);
        Assert.Matches(@"(?s)\n\s*5\)\s+if \[ ""\$ROLLBACK"" = ""true"" \]; then\s+echo ""::error::[^\n]*""\s+exit 1\s+fi\s+echo ""::warning::[^\n]*""\s+;;", run);
        foreach (string code in new[] { "7", "6", "4" })
        {
            Assert.Matches(@"(?m)^\s*" + code + @"\) echo ""::error::[^\n]*""; exit 1 ;;$", run);
        }

        Assert.Matches(@"(?m)^\s*\*\) echo ""::error::[^\n]*""; exit 1 ;;$", run);
        Assert.Equal(5, Regex.Count(run, @"\bexit 1\b"));

        string cleanup = StepRun(steps[^1]);
        Assert.Contains("if ! pgrep -f '$STAGE/bundle/remote/[r]eceive-content.sh' >/dev/null; then rm -rf '$STAGE'; fi", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_DispatchesContentFollowUpWithoutRollback()
    {
        AssertContentFollowUp(Workflow("deploy-app.yml"));
    }

    [Theory]
    [InlineData("gh workflow run deploy-content.yml -f ref=\"$tip\"", "gh workflow run deploy-content.yml -f ref=\"$tip\" -f rollback=true")]
    [InlineData("gh workflow run deploy-content.yml -f ref=\"$tip\"", "gh workflow run deploy-content.yml -f ref=\"$live\"")]
    [InlineData("git -C main-tip rev-parse \"HEAD:content\"", "git -C main-tip rev-parse \"$SHA:content\"")]
    [InlineData("          ref: main\n          path: main-tip\n", "          path: main-tip\n")]
    [InlineData("          path: main-tip\n          fetch-depth: 0\n", "          path: main-tip\n          fetch-depth: 1\n")]
    [InlineData("      actions: write\n", "")]
    [InlineData("The application is deployed; only dispatching deploy-content failed.", "dispatching failed.")]
    [InlineData("          GH_TOKEN: ${{ github.token }}\n          GH_REPO", "          GH_TOKEN: ${{ secrets.DB_PASSWORD }}\n          GH_REPO")]
    [InlineData("git -C main-tip rev-parse \"$live:content\"", "git -C main-tip rev-parse \"$live\"")]
    [InlineData("      - name: Publish the content of this commit when the live release lacks it\n", "      - name: Publish the content of this commit when the live release lacks it\n        if: always()\n")]
    public void DeployApp_ContentFollowUpCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated("deploy-app.yml", from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertContentFollowUp(mutated));
    }

    // The dispatch is the last step before the cleanup, so it runs only after the deployment succeeded, and it never sets
    // rollback: deploy-content then treats a commit older than the live release as stale. It compares the live content with
    // the tip of main as it is now (a fresh checkout), not with the deployed commit: deploy-content delivers the tip, and a
    // delivery that failed on the lock (exit 4) or was replaced is re-delivered by the next deployment. Only this job may
    // write actions.
    private static void AssertContentFollowUp(YamlFile file)
    {
        Dictionary<string, object?> jobs = Jobs(file);
        foreach (KeyValuePair<string, object?> job in jobs)
        {
            bool writesActions = Map(job.Value).TryGetValue("permissions", out object? permissions) && IsMap(permissions)
                && Text(Map(permissions).GetValueOrDefault("actions")) == "write";
            Assert.Equal(job.Key == "deploy", writesActions);
        }

        List<object?> steps = Steps(jobs["deploy"]);
        int wait = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Wait for the outcome");
        int follow = steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")) == "Publish the content of this commit when the live release lacks it");
        Assert.True(wait >= 0 && follow > wait && follow == steps.Count - 2, "The content dispatch must follow the wait step and precede the cleanup.");
        Dictionary<string, object?> tipStep = Map(steps[follow - 1]);
        Assert.Equal("actions/checkout", StepUses(tipStep).Split('@')[0]);
        Dictionary<string, object?> tipWith = Map(tipStep.GetValueOrDefault("with") ?? new Dictionary<string, object?>());
        Assert.Equal("main", Text(tipWith.GetValueOrDefault("ref")));
        Assert.Equal("main-tip", Text(tipWith.GetValueOrDefault("path")));
        Assert.Equal("0", Text(tipWith.GetValueOrDefault("fetch-depth")));
        Assert.False(Map(steps[follow]).ContainsKey("if"), "The dispatch runs only when every earlier step succeeded.");

        Dictionary<string, object?> env = Map(Map(steps[follow])["env"]);
        Assert.Equal("${{ github.token }}", Text(env["GH_TOKEN"]));
        string run = StepRun(steps[follow]);
        Assert.Contains("gh workflow run deploy-content.yml -f ref=\"$tip\"", run, StringComparison.Ordinal);
        Assert.DoesNotContain("rollback", run, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git -C main-tip rev-parse \"$live:content\"", run, StringComparison.Ordinal);
        Assert.Contains("git -C main-tip rev-parse \"HEAD:content\"", run, StringComparison.Ordinal);
        Assert.DoesNotContain("$SHA", run, StringComparison.Ordinal);
        Assert.Contains("The application is deployed; only dispatching deploy-content failed.", run, StringComparison.Ordinal);
    }

    private const string RotateFile = "rotate-db-password.yml";

    private const string RotateScript = "bash '$STAGE/bundle/remote/rotate-db-password.sh' '$STAGE'";

    private static int StepIndex(List<object?> steps, string namePrefix) =>
        steps.FindIndex(s => Text(Map(s).GetValueOrDefault("name")).StartsWith(namePrefix, StringComparison.Ordinal));

    private static List<object?> RotateSteps(YamlFile file) => Steps(Jobs(file)["rotate"]);

    [Fact]
    public void RotateDbPassword_IsManualOnlyUnderItsOwnGroupWithTheNarrowestPermissions()
    {
        AssertRotateShape(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("  workflow_dispatch:\n", "  push:\n  workflow_dispatch:\n")]
    [InlineData("group: production-rotate", "group: production-app")]
    [InlineData("      actions: write\n", "      actions: write\n      packages: write\n")]
    [InlineData("      contents: read\n      actions: write", "      contents: write\n      actions: write")]
    [InlineData("          - umami\n      mode:", "          - umami\n          - other\n      mode:")]
    [InlineData("          - finish\n", "          - finish\n          - revert\n")]
    [InlineData("    environment: production\n", "")]
    [InlineData("permissions: {}\n", "permissions:\n  contents: read\n")]
    public void RotateDbPassword_ShapeCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateShape(mutated));
    }

    private static void AssertRotateShape(YamlFile file)
    {
        Assert.Equal(["workflow_dispatch"], Triggers(file));
        Dictionary<string, object?> inputs = Map(Map(Map(file.Root["on"])["workflow_dispatch"])["inputs"]);
        Assert.Equal(["role", "mode"], inputs.Keys);
        Assert.Equal("choice", Text(Map(inputs["role"])["type"]));
        Assert.Equal(["trainer", "umami"], ((List<object?>)Map(inputs["role"])["options"]!).Select(Text));
        Assert.Equal("choice", Text(Map(inputs["mode"])["type"]));
        Assert.Equal(["rotate", "finish"], ((List<object?>)Map(inputs["mode"])["options"]!).Select(Text));

        Assert.Empty(Map(file.Root["permissions"]));
        Dictionary<string, object?> concurrency = Map(file.Root["concurrency"]);
        Assert.Equal("production-rotate", Text(concurrency["group"]));
        Assert.Equal("false", Text(concurrency["cancel-in-progress"]).ToLowerInvariant());

        KeyValuePair<string, object?> job = Assert.Single(Jobs(file));
        Assert.Equal("rotate", job.Key);
        Dictionary<string, object?> body = Map(job.Value);
        Assert.Equal("production", Text(body.GetValueOrDefault("environment")));
        Assert.False(body.ContainsKey("concurrency"));
        Dictionary<string, object?> permissions = Map(body["permissions"]);
        Assert.Equal(["contents", "actions"], permissions.Keys);
        Assert.Equal("read", Text(permissions["contents"]));
        Assert.Equal("write", Text(permissions["actions"]));
    }

    [Fact]
    public void RotateDbPassword_ChecksTheSecretsTokenBeforeAnythingChanges()
    {
        AssertRotateTokenCheck(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("          GH_TOKEN: ${{ secrets.SECRETS_ADMIN_TOKEN }}\n        run: |\n          set -euo pipefail\n          printf 'check'", "          GH_TOKEN: ${{ github.token }}\n        run: |\n          set -euo pipefail\n          printf 'check'")]
    [InlineData("--env production --repo \"$GITHUB_REPOSITORY\"\n          gh secret delete", "--env production --repo \"$GITHUB_REPOSITORY\" || true\n          gh secret delete")]
    [InlineData("      - name: Check that the secrets token can write environment secrets\n", "      - name: Early upload\n        run: ssh -F \"$RUNNER_TEMP/ssh/ssh_config\" dit \"mkdir -m 700 '$STAGE'\"\n      - name: Check that the secrets token can write environment secrets\n")]
    [InlineData("      - name: Check that the secrets token can write environment secrets\n", "      - uses: ./.github/actions/ssh-setup\n        with:\n          ssh-key: ${{ secrets.DEPLOY_SSH_KEY }}\n          known-hosts: ${{ vars.DEPLOY_KNOWN_HOSTS }}\n          host: ${{ vars.DEPLOY_HOST }}\n          user: ${{ vars.DEPLOY_USER }}\n      - name: Check that the secrets token can write environment secrets\n")]
    public void RotateDbPassword_TokenCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateTokenCheck(mutated));
    }

    [Fact]
    public void RotateDbPassword_TokenCheckFailsWhenTheDeleteIsRemoved()
    {
        YamlFile mutated = WithoutLine(RotateFile, "gh secret delete DIT_TOKEN_CHECK");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateTokenCheck(mutated));
    }

    // The probe writes and deletes a throw-away secret with the token that will write the real one; it runs before the
    // key is set up or anything is sent to the server, so a token without the Environments permission ends the run with
    // nothing changed.
    private static void AssertRotateTokenCheck(YamlFile file)
    {
        List<object?> steps = RotateSteps(file);
        int check = StepIndex(steps, "Check that the secrets token");
        Assert.True(check >= 0, "The token check step is missing.");
        for (int index = 0; index < check; index++)
        {
            Assert.DoesNotMatch(RemoteCommand(), StepRun(steps[index]));
            Assert.NotEqual(SshSetup, StepUses(steps[index]));
        }

        Assert.Equal("${{ secrets.SECRETS_ADMIN_TOKEN }}", Text(Map(Map(steps[check])["env"])["GH_TOKEN"]));
        Assert.False(Map(steps[check]).ContainsKey("if"));
        string run = StepRun(steps[check]);
        Assert.Contains("set -euo pipefail", run, StringComparison.Ordinal);
        int set = run.IndexOf("gh secret set DIT_TOKEN_CHECK --env production", StringComparison.Ordinal);
        int delete = run.IndexOf("gh secret delete DIT_TOKEN_CHECK --env production", StringComparison.Ordinal);
        Assert.True(set >= 0 && delete > set, "The probe secret must be set and then deleted.");
        Assert.DoesNotContain("|| true", run, StringComparison.Ordinal);
        Assert.True(steps.FindIndex(s => StepUses(s) == SshSetup) > check, "ssh-setup must come after the token check.");
        Assert.True(StepIndex(steps, "Upload the bundle") > check, "The upload must come after the token check.");
    }

    [Fact]
    public void RotateDbPassword_ChecksThePendingRoleBeforeFinishing()
    {
        AssertRotateModes(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("[ \"$pending\" = \"$ROLE\" ] ||", "true ||")]
    [InlineData("          pending=\"$(\"${ssh_cmd[@]}\"", "          \"${ssh_cmd[@]}\" \"APP_DIR='$APP_DIR' bash '$STAGE/bundle/remote/rotate-db-password.sh' '$STAGE' '$ROLE' finish\"\n          pending=\"$(\"${ssh_cmd[@]}\"")]
    [InlineData("        if: ${{ inputs.mode == 'finish' }}", "        if: always()")]
    [InlineData("        if: ${{ inputs.mode == 'rotate' }}", "        if: always()")]
    [InlineData("      MODE: ${{ inputs.mode }}", "      MODE: rotate")]
    [InlineData("          [[ \"$pw\" =~ ^[0-9a-f]{48}$ ]] || { echo \"::error::The password generator returned an unexpected value. Nothing was changed.\"; exit 1; }\n", "")]
    [InlineData("[ \"$journal_role\" = \"$ROLE\" ] ||", "true ||")]
    [InlineData("          echo \"::add-mask::$pw\"\n", "")]
    [InlineData("          journal_role=\"$(sed -n \"s/^ROTATION_ROLE='\\([a-z]*\\)'\\$/\\1/p\" <<<\"$journal\")\"\n", "          journal_role=\"$ROLE\"\n")]
    [InlineData("          echo \"::add-mask::$pw\"\n          [ \"$journal_role\" = \"$ROLE\" ]", "          [ \"$journal_role\" = \"$ROLE\" ]")]
    [InlineData("          printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n", "          printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n          \"${ssh_cmd[@]}\" \"cat '$APP_DIR/.rotation-pending'\"\n")]
    public void RotateDbPassword_ModeCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateModes(mutated));
    }

    // Mode finish compares the role the owner typed with the role in the server's journal before it runs finish, so the
    // secret that is written afterwards belongs to the rotation that is actually pending. Mode rotate generates the value
    // on the runner and sends it on stdin.
    private static void AssertRotateModes(YamlFile file)
    {
        List<object?> steps = RotateSteps(file);
        int finish = StepIndex(steps, "Verify the pending role");
        int begin = StepIndex(steps, "Generate the new password");
        Assert.True(finish >= 0 && begin >= 0, "Both mode steps must exist.");
        Assert.Equal("${{ inputs.mode == 'finish' }}", Text(Map(steps[finish])["if"]).Trim());
        Assert.Equal("${{ inputs.mode == 'rotate' }}", Text(Map(steps[begin])["if"]).Trim());
        Dictionary<string, object?> env = Map(Map(Jobs(file)["rotate"])["env"]);
        Assert.Equal("${{ inputs.mode }}", Text(env["MODE"]));
        Assert.Equal("${{ inputs.role }}", Text(env["ROLE"]));

        string run = StepRun(steps[finish]);
        int pending = run.IndexOf(RotateScript + " pending-role", StringComparison.Ordinal);
        int compare = run.IndexOf("[ \"$pending\" = \"$ROLE\" ] ||", StringComparison.Ordinal);
        int finishCall = run.IndexOf(RotateScript + " '$ROLE' finish", StringComparison.Ordinal);
        Assert.True(pending >= 0 && compare > pending && finishCall > compare, "pending-role must be compared with the role input before finish runs.");
        Assert.Equal(1, Regex.Count(run, @"'\$ROLE' finish"));
        Assert.Matches(@"\[ ""\$pending"" = ""\$ROLE"" \] \|\| \{[^\n]*exit 1; \}", run);

        // The read-back takes role and value from the same read, masks the value before any use and refuses a role that is
        // no longer the verified one.
        int readRole = run.IndexOf("ROTATION_ROLE='", StringComparison.Ordinal);
        int readPassword = run.IndexOf("ROTATION_PASSWORD='", StringComparison.Ordinal);
        int mask = run.IndexOf("echo \"::add-mask::$pw\"", StringComparison.Ordinal);
        int roleCheck = run.IndexOf("[ \"$journal_role\" = \"$ROLE\" ] ||", StringComparison.Ordinal);
        int write = run.IndexOf("printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"", StringComparison.Ordinal);
        Assert.True(readRole > finishCall && readPassword > finishCall, "both lines are read after finish ran.");
        Assert.True(mask > readPassword && mask > readRole && roleCheck > mask && write > roleCheck, "mask, then the role check, then the file.");
        Assert.Equal(1, Regex.Count(run, @"cat '\$APP_DIR/\.rotation-pending'"));
        Assert.Matches(@"\[ ""\$journal_role"" = ""\$ROLE"" \] \|\| \{[^\n]*exit 1; \}", run);

        string rotate = StepRun(steps[begin]);
        int generate = rotate.IndexOf("pw=\"$(openssl rand -hex 24)\" ||", StringComparison.Ordinal);
        int shape = rotate.IndexOf("[[ \"$pw\" =~ ^[0-9a-f]{48}$ ]] ||", StringComparison.Ordinal);
        int maskRotate = rotate.IndexOf("echo \"::add-mask::$pw\"", StringComparison.Ordinal);
        int writeRotate = rotate.IndexOf("printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"", StringComparison.Ordinal);
        Assert.True(generate >= 0 && shape > generate && maskRotate > shape && writeRotate > maskRotate, "generate, check, mask, then write.");
        Assert.Contains(RotateScript + " '$ROLE' begin\" < \"$RUNNER_TEMP/env/new-password\"", rotate, StringComparison.Ordinal);
        Assert.DoesNotContain("'$ROLE' finish", rotate, StringComparison.Ordinal);
    }

    [Fact]
    public void RotateDbPassword_ExplainsEveryExitOfBegin()
    {
        AssertRotateBeginExits(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("            255) echo", "            2 | 255) echo")]
    [InlineData("            2) echo", "            2 | 255) echo")]
    [InlineData("            3) echo", "            4) echo")]
    [InlineData("nothing changed. Fix the cause and run mode rotate again.", "run mode finish.")]
    [InlineData("            0) ;;\n", "")]
    [InlineData("exit 1 ;;\n            *)", ";;\n            *)")]
    public void RotateDbPassword_BeginExitCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateBeginExits(mutated));
    }

    // 2 and 255 (no script ran) mean nothing was journaled: telling the owner to run finish would send them to "no rotation is
    // pending". 3 is the guard. Anything else may have journaled, and every branch fails the run.
    private static void AssertRotateBeginExits(YamlFile file)
    {
        string run = StepRun(RotateSteps(file)[StepIndex(RotateSteps(file), "Generate the new password")]);
        Assert.Matches(@"case ""\$code"" in\s+0\) ;;\s+3\) echo ""::error::A guard file refuses the rotation\.[^\n]*mode finish[^\n]*""; exit 1 ;;", run);
        Assert.Matches(@"\n\s+2\) echo ""::error::[^\n]*nothing changed\. Fix the cause and run mode rotate again\.""; exit 1 ;;", run);
        Assert.Matches(@"\n\s+255\) echo ""::error::The connection to the server failed[^\n]*state is unknown[^\n]*mode finish[^\n]*""; exit 1 ;;", run);
        Assert.DoesNotContain("255) echo \"::error::The rotation was refused", run, StringComparison.Ordinal);
        Assert.Matches(@"\*\) echo ""::error::[^\n]*mode finish[^\n]*mode rotate can be run again\.""; exit 1 ;;\s+esac", run);
        Assert.Equal(4, Regex.Count(run, @"exit 1 ;;"));
    }

    [Fact]
    public void RotateDbPassword_DerivesTheSecretNameFromTheRoleOnly()
    {
        AssertRotateSecretName(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("trainer) name=DB_PASSWORD ;;", "trainer) name=UMAMI_DB_PASSWORD ;;")]
    [InlineData("umami) name=UMAMI_DB_PASSWORD ;;", "umami) name=DB_PASSWORD ;;")]
    [InlineData("          ok=0\n", "          name=\"${OVERRIDE:-$name}\"\n          ok=0\n")]
    [InlineData("gh secret set \"$name\" --env", "gh secret set DB_PASSWORD --env")]
    [InlineData("            *) echo \"::error::The role must be trainer or umami.\"; exit 1 ;;\n          esac\n          ok=0", "          esac\n          ok=0")]
    public void RotateDbPassword_SecretNameCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateSecretName(mutated));
    }

    private static void AssertRotateSecretName(YamlFile file)
    {
        List<object?> steps = RotateSteps(file);
        int update = StepIndex(steps, "Update the environment secret");
        Assert.True(update >= 0, "The secret update step is missing.");
        string run = StepRun(steps[update]);
        Assert.Matches(@"case ""\$ROLE"" in\s+trainer\) name=DB_PASSWORD ;;\s+umami\) name=UMAMI_DB_PASSWORD ;;\s+\*\) [^\n]*exit 1 ;;\s+esac", run);
        Assert.Equal(2, Regex.Count(run, @"\bname="));
        Assert.Contains("gh secret set \"$name\" --env production --repo \"$GITHUB_REPOSITORY\"", run, StringComparison.Ordinal);
        Assert.Equal("${{ secrets.SECRETS_ADMIN_TOKEN }}", Text(Map(Map(steps[update])["env"])["GH_TOKEN"]));
        Assert.Matches(@"for attempt in 1 2 3; do", run);
        // Only the probe and this step may write a secret.
        Assert.Equal(2, Regex.Count(string.Join('\n', steps.Select(StepRun)), @"gh secret set"));
    }

    [Fact]
    public void RotateDbPassword_CommitsOnlyAfterTheSecretWasConfirmed()
    {
        AssertRotateCommitOrder(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("      - name: Commit the rotation on the server\n", "      - name: Commit the rotation on the server\n        if: always()\n")]
    [InlineData("      - name: Update the environment secret\n", "      - name: Update the environment secret\n        continue-on-error: true\n")]
    [InlineData("      - name: Update the environment secret\n", "      - name: Early commit\n        run: ssh -F \"$RUNNER_TEMP/ssh/ssh_config\" dit \"APP_DIR='$APP_DIR' bash '$STAGE/bundle/remote/rotate-db-password.sh' '$STAGE' '$ROLE' commit\"\n      - name: Update the environment secret\n")]
    [InlineData("          if [ \"$ok\" != \"1\" ]; then", "          if false; then")]
    [InlineData("              ok=1\n              break\n", "              break\n")]
    [InlineData("< \"$RUNNER_TEMP/env/new-password\"; then\n              ok=1", "< \"$RUNNER_TEMP/env/new-password\" || true; then\n              ok=1")]
    public void RotateDbPassword_CommitOrderCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateCommitOrder(mutated));
    }

    // Committing deletes the journal, which is the only thing that keeps deployments away from a database whose password
    // GitHub does not know. It therefore runs only when every earlier step succeeded, and the update step fails the run
    // unless one of the three attempts was confirmed.
    private static void AssertRotateCommitOrder(YamlFile file)
    {
        List<object?> steps = RotateSteps(file);
        int update = StepIndex(steps, "Update the environment secret");
        int commit = StepIndex(steps, "Commit the rotation");
        int dispatch = StepIndex(steps, "Dispatch deploy-app");
        int verify = StepIndex(steps, "Verify the pending role");
        int begin = StepIndex(steps, "Generate the new password");
        Assert.True(begin >= 0 && verify >= 0 && update > Math.Max(begin, verify), "The secret is updated after the server step of either mode.");
        Assert.True(commit == update + 1 && dispatch == commit + 1 && dispatch == steps.Count - 2, "commit follows the secret update directly, then the dispatch, then the cleanup.");
        foreach (int index in new[] { update, commit, dispatch })
        {
            Assert.False(Map(steps[index]).ContainsKey("if"), "The step runs only when every earlier step succeeded.");
            Assert.False(Map(steps[index]).ContainsKey("continue-on-error"));
        }

        Assert.Equal(1, Regex.Count(string.Join('\n', steps.Select(StepRun)), @"rotate-db-password\.sh' '\$STAGE' '\$ROLE' commit"));
        Assert.Contains(RotateScript + " '$ROLE' commit", StepRun(steps[commit]), StringComparison.Ordinal);

        string run = StepRun(steps[update]);
        Assert.Matches(@"if gh secret set ""\$name""[^\n|]*; then\s+ok=1\s+break\s+fi", run);
        Assert.Matches(@"if \[ ""\$ok"" != ""1"" \]; then[^\n]*\n(?:[^\n]*\n)*?\s+exit 1\s+fi", run);
        Assert.DoesNotContain("|| true", run, StringComparison.Ordinal);
    }

    [Fact]
    public void RotateDbPassword_NeverPutsThePasswordInArgumentsOrScriptText()
    {
        AssertRotateSecretHandling(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("gh secret set \"$name\" --env production --repo \"$GITHUB_REPOSITORY\" < \"$RUNNER_TEMP/env/new-password\"", "gh secret set \"$name\" --env production --repo \"$GITHUB_REPOSITORY\" --body \"$(cat \"$RUNNER_TEMP/env/new-password\")\"")]
    [InlineData("          echo \"::add-mask::$pw\"\n", "          echo \"new password: $pw\"\n")]
    [InlineData("          umask 077\n          mkdir -p \"$RUNNER_TEMP/env\"\n          pw=\"$(openssl rand", "          mkdir -p \"$RUNNER_TEMP/env\"\n          pw=\"$(openssl rand")]
    [InlineData("          set -euo pipefail\n          # The name comes", "          set -euxo pipefail\n          # The name comes")]
    [InlineData("pw=\"$(openssl rand -hex 24)\" ||", "pw=\"$(openssl rand -hex 24 | tee /dev/stderr)\" ||")]
    [InlineData("          unset journal\n", "          echo \"$journal\"\n          unset journal\n")]
    [InlineData("          unset journal\n", "          unset journal\n          echo \"$journal_role $pw\"\n")]
    [InlineData("          printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n", "          echo \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n")]
    [InlineData("          printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n", "          printf '%s' \"$pw\" > \"$RUNNER_TEMP/env/new-password\"\n          echo \"$pw\"\n")]
    [InlineData("          case \"$code\" in\n            0) ;;", "          echo \"${{ steps.x.outputs.pw }}\"\n          case \"$code\" in\n            0) ;;")]
    public void RotateDbPassword_SecretHandlingCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateSecretHandling(mutated));
    }

    // The value lives in a mode 600 file and reaches the server and GitHub on stdin: no argument, no expression, no trace.
    private static void AssertRotateSecretHandling(YamlFile file)
    {
        string[] allowed =
        [
            @"^ssh -F ""\$RUNNER_TEMP/ssh/ssh_config"" dit ""[^""]*"" < ""\$RUNNER_TEMP/env/new-password"" \|\| code=\$\?$",
            @"^printf '%s' ""\$pw"" > ""\$RUNNER_TEMP/env/new-password""$",
            @"^if gh secret set ""\$name"" --env production --repo ""\$GITHUB_REPOSITORY"" < ""\$RUNNER_TEMP/env/new-password""; then$",
        ];
        // The read-back keeps the value in the shell variable pw: it may be parsed, checked, masked and written, never printed.
        string[] pwAllowed =
        [
            @"^pw=""\$\(openssl rand -hex 24\)"" \|\| \{[^\n]*exit 1; \}$",
            @"^\[\[ ""\$pw"" =~ \^\[0-9a-f\]\{48\}\$ \]\] \|\| \{[^\n]*exit 1; \}$",
            @"^journal=""\$\(""\$\{ssh_cmd\[@\]\}"" ""cat '\$APP_DIR/\.rotation-pending'""\)""$",
            @"^pw=""\$\(sed -n ""[^""]*"" <<<""\$journal""\)""$",
            @"^journal_role=""\$\(sed -n ""[^""]*"" <<<""\$journal""\)""$",
            @"^\[\[ ""\$pw"" =~ \^\[A-Za-z0-9\._~-\]\{32,128\}\$ \]\] \|\| \{[^\n]*exit 1; \}$",
            @"^echo ""::add-mask::\$pw""$",
            @"^printf '%s' ""\$pw"" > ""\$RUNNER_TEMP/env/new-password""$",
        ];
        int writers = 0;
        foreach (object? step in RotateSteps(file))
        {
            string run = StepRun(step);
            // Both variables carry the plaintext ($journal holds the whole journal): "$journal_role" is a different name.
            foreach (string raw in run.Split('\n').Where(l => Regex.IsMatch(l, @"\$\{?(?:pw|journal)\b")))
            {
                string line = raw.Trim();
                Assert.True(pwAllowed.Any(pattern => Regex.IsMatch(line, pattern)), $"{file.Name}: the password variables may be used only in the known forms: {line}");
            }

            Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"--body|--body-file|\s-b\s|\btee\b", run);
            Assert.DoesNotMatch(@"\bset\s+-[a-zA-Z]*x|\bbash\s+-[a-zA-Z]*x|xtrace", run);
            foreach (string raw in run.Split('\n').Where(l => l.Contains("new-password", StringComparison.Ordinal)))
            {
                string line = raw.Trim();
                Assert.True(allowed.Any(pattern => Regex.IsMatch(line, pattern)), $"{file.Name}: the password file may be used only in the known forms: {line}");
            }

            if (run.Contains("> \"$RUNNER_TEMP/env/new-password\"", StringComparison.Ordinal))
            {
                writers++;
                int umask = run.IndexOf("umask 077", StringComparison.Ordinal);
                Assert.True(umask >= 0 && umask < run.IndexOf("new-password", StringComparison.Ordinal), $"{file.Name}: umask 077 must precede the file that holds the password.");
            }
        }

        Assert.Equal(2, writers);
        Assert.DoesNotContain("--body", AllStrings(file), StringComparison.Ordinal);
    }

    [Fact]
    public void RotateDbPassword_DispatchesDeployAppWithTheWorkflowToken()
    {
        AssertRotateDispatch(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("          GH_TOKEN: ${{ github.token }}\n          GH_REPO", "          GH_TOKEN: ${{ secrets.SECRETS_ADMIN_TOKEN }}\n          GH_REPO")]
    [InlineData("gh workflow run deploy-app.yml --ref main -f ref=\"$sha\" -f keep_hold=true", "gh workflow run deploy-app.yml --ref main -f ref=\"$sha\" -f keep_hold=true -f rollback=true")]
    [InlineData("gh workflow run deploy-app.yml --ref main -f ref=\"$sha\" -f keep_hold=true", "gh workflow run deploy-app.yml --ref main -f ref=\"$sha\"")]
    [InlineData("gh workflow run deploy-app.yml --ref main -f ref=\"$sha\" -f keep_hold=true", "gh workflow run deploy-app.yml -f ref=\"$sha\" -f keep_hold=true")]
    [InlineData("! [[ \"$sha\" =~ ^[0-9a-f]{40}$ ]]", "false")]
    [InlineData("grep -E '^DIT_COMMIT=' '$APP_DIR/last-good.env'", "grep -E '^APP_IMAGE=' '$APP_DIR/last-good.env'")]
    [InlineData("only dispatching deploy-app failed.", "dispatching failed.")]
    [InlineData("      - name: Dispatch deploy-app for the deployed commit\n", "      - name: Dispatch deploy-app for the deployed commit\n        if: always()\n")]
    public void RotateDbPassword_DispatchCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRotateDispatch(mutated));
    }

    // GITHUB_TOKEN may start workflows through workflow_dispatch, which is why actions: write is granted to the job; the
    // secrets token has no business near it and a rotation never rolls back. The commit is the one that runs (DIT_COMMIT of
    // last-good.env, validated as a full SHA), never the tip of main, whose ci may have failed and which deploy-app does
    // not check for an explicit ref; keep_hold leaves a rollback's hold in place.
    private static void AssertRotateDispatch(YamlFile file)
    {
        List<object?> steps = RotateSteps(file);
        int dispatch = StepIndex(steps, "Dispatch deploy-app");
        Assert.True(dispatch >= 0, "The dispatch step is missing.");
        Assert.False(Map(steps[dispatch]).ContainsKey("if"));
        Assert.Equal("${{ github.token }}", Text(Map(Map(steps[dispatch])["env"])["GH_TOKEN"]));
        string run = StepRun(steps[dispatch]);
        Assert.Contains("gh workflow run deploy-app.yml --ref main -f ref=\"$sha\" -f keep_hold=true", run, StringComparison.Ordinal);
        Assert.DoesNotContain("rollback", run, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commits/main", run, StringComparison.Ordinal);
        Assert.Contains("grep -E '^DIT_COMMIT=' '$APP_DIR/last-good.env'", run, StringComparison.Ordinal);
        Assert.Contains("! [[ \"$sha\" =~ ^[0-9a-f]{40}$ ]]", run, StringComparison.Ordinal);
        Assert.Contains("only dispatching deploy-app failed.", run, StringComparison.Ordinal);
        Assert.Single(steps, s => Map(s).TryGetValue("env", out object? env) && Text(Map(env).GetValueOrDefault("GH_TOKEN")) == "${{ github.token }}");
    }

    [Theory]
    [InlineData("tar -xzpf - -C '$STAGE/bundle'", "tar -xzpf - -C '$APP_DIR'")]
    [InlineData("mkdir -m 700 '$STAGE' '$STAGE/bundle'", "mkdir -m 700 '$STAGE' '$APP_DIR/bundles'")]
    [InlineData("rm -rf '$STAGE'\" || true", "rm -rf '$APP_DIR/staging'\" || true")]
    public void RotateDbPassword_UploadCheckFailsOnAWriteOutsideStaging(string from, string to)
    {
        AssertUploadsOnlyIntoStaging(Workflow(RotateFile));
        YamlFile mutated = Mutated(RotateFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertUploadsOnlyIntoStaging(mutated));
    }

    [Fact]
    public void RotateDbPassword_IsCoveredByTheTestHookCheck()
    {
        Assert.DoesNotContain(RotateFile, TestHookWorkflowFiles);
        Assert.Contains(AllFiles(), f => f.Name == RotateFile);
        AssertNoTestHooks(Workflow(RotateFile));
    }

    [Theory]
    [InlineData("DIT_TEST_HOOKS")]
    [InlineData("DIT_TEST_KILL_AT")]
    [InlineData("DIT_TEST_IMPORT_URL")]
    public void TestHookCheck_FailsWhenTheRotationWorkflowNamesAHook(string hook)
    {
        YamlFile mutated = Mutated(RotateFile, "      MODE: ${{ inputs.mode }}\n", "      MODE: ${{ inputs.mode }}\n      " + hook + ": 1\n");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoTestHooks(mutated));
    }

    private const string LinkCheckFile = "link-check.yml";

    [Fact]
    public void LinkCheck_FollowsTheIssueReportingRules()
    {
        AssertLinkCheck(Workflow(LinkCheckFile));
    }

    [Theory]
    [InlineData("    permissions:\n      contents: read\n      issues: write\n", "    permissions:\n      contents: read\n")]
    [InlineData("      issues: write\n", "      issues: write\n      pull-requests: write\n")]
    [InlineData("      contents: read\n      issues: write\n", "      contents: write\n      issues: write\n")]
    [InlineData("permissions:\n  contents: read\n\n# The issue", "permissions:\n  contents: read\n  issues: write\n\n# The issue")]
    [InlineData("concurrency:\n  group: link-check\n  cancel-in-progress: false\n", "")]
    [InlineData("  cancel-in-progress: false\n", "  cancel-in-progress: true\n")]
    [InlineData("  group: link-check\n", "  group: link-check-${{ github.run_id }}\n")]
    [InlineData("          GH_TOKEN: ${{ github.token }}\n", "          GH_TOKEN: ${{ secrets.DEPLOY_PAT }}\n")]
    [InlineData("        id: check\n        run: |\n", "        id: check\n        env:\n          GH_TOKEN: ${{ github.token }}\n        run: |\n")]
    [InlineData("          GH_REPO: ${{ github.repository }}\n", "          GH_REPO: ${{ github.repository }}\n          EXTRA: ${{ github.token }}\n")]
    [InlineData("          report=\"$RUNNER_TEMP/links.md\"\n\n          # Only", "          report=\"$RUNNER_TEMP/links.md\"\n          export GH_TOKEN=\"${{ github.token }}\"\n\n          # Only")]
    [InlineData("          CHECK_EXIT: ${{ steps.check.outputs.code }}\n", "          CHECK_EXIT: 1\n")]
    [InlineData("          echo \"code=$code\" >> \"$GITHUB_OUTPUT\"\n", "")]
    [InlineData("          set -euo pipefail\n          report=\"$RUNNER_TEMP/links.md\"\n          rm", "          set -uo pipefail\n          report=\"$RUNNER_TEMP/links.md\"\n          rm")]
    [InlineData("          set -euo pipefail\n          report=\"$RUNNER_TEMP/links.md\"\n\n          # Only", "          set -uo pipefail\n          report=\"$RUNNER_TEMP/links.md\"\n\n          # Only")]
    [InlineData("--body-file \"$report\"\n              for", "--body \"$(cat \"$report\")\"\n              for")]
    [InlineData("gh issue create --title \"Dead source links\" --label dead-links --body-file \"$report\"", "gh issue create --title \"Dead source links\" --label dead-links --body-file \"$report\" --body x")]
    [InlineData("gh issue edit \"$primary\" --body-file \"$report\"", "gh issue edit \"$primary\" --body \"${{ github.event.head_commit.message }}\"")]
    [InlineData("            exit 1\n          fi\n\n          for number", "          fi\n\n          for number")]
    [InlineData("            # Fail the job as well, so the owner gets the workflow notification besides the issue.\n            exit 1\n", "            exit 0\n")]
    [InlineData(" || [ ! -s \"$report\" ]", "")]
    [InlineData("          rm -f \"$report\"\n", "")]
    [InlineData("{ [ \"$CHECK_EXIT\" != \"0\" ] && [ \"$CHECK_EXIT\" != \"1\" ]; }", "[ \"$CHECK_EXIT\" != \"0\" ]")]
    [InlineData("gh label create dead-links --force", "gh label create dead-links")]
    [InlineData("--state open --limit 100", "--state open --limit 1")]
    [InlineData("gh issue list --label dead-links --state open", "gh issue list --state open")]
    [InlineData("--title \"Dead source links\"", "--title \"Links\"")]
    [InlineData("gh issue close \"$extra\"", "gh issue reopen \"$extra\"")]
    [InlineData("All source links resolve as of", "Done on")]
    [InlineData("check-links --report \"$report\" || code=$?", "check-links --report \"$report\"")]
    public void LinkCheck_CheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(LinkCheckFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertLinkCheck(mutated));
    }

    // The issue body is built from content URLs, which are untrusted data; a crashed checker must never read as "all links resolve".
    // The behaviour itself is exercised by LinkCheckScriptTests; this pins the structure that test cannot see.
    private static void AssertLinkCheck(YamlFile file)
    {
        Dictionary<string, object?> top = Map(file.Root["permissions"]);
        Assert.Equal(["contents"], top.Keys);
        Assert.Equal("read", Text(top["contents"]));

        Assert.True(file.Root.ContainsKey("concurrency"), "the workflow must serialise its runs.");
        Dictionary<string, object?> concurrency = Map(file.Root["concurrency"]);
        Assert.Equal("link-check", Text(concurrency["group"]));
        Assert.Equal("false", Text(concurrency["cancel-in-progress"]).ToLowerInvariant());

        KeyValuePair<string, object?> job = Assert.Single(Jobs(file));
        Assert.Equal("check-links", job.Key);
        Assert.False(Map(job.Value).ContainsKey("concurrency"));
        Dictionary<string, object?> granted = Map(Map(job.Value)["permissions"]);
        Assert.Equal(["contents", "issues"], granted.Keys);
        Assert.Equal("read", Text(granted["contents"]));
        Assert.Equal("write", Text(granted["issues"]));

        // The write token belongs to the issue step alone; the step that fetches URLs from the content never sees it.
        object? checker = Steps(job.Value).Single(step => Text(Map(step).GetValueOrDefault("id")) == "check");
        object? updater = Steps(job.Value).Single(step => StepRun(step).Contains("gh issue", StringComparison.Ordinal));
        Assert.NotSame(checker, updater);
        Assert.False(Map(checker).ContainsKey("env"), "the checker step must have no environment of its own.");
        Assert.Contains("check-links", StepRun(checker), StringComparison.Ordinal);
        Assert.DoesNotContain("gh ", StepRun(checker), StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", StepRun(updater), StringComparison.Ordinal);

        List<YamlNode> tokenNodes = [.. AllNodes(file).Where(n => n.Value is string text && (text.Contains("github.token", StringComparison.Ordinal) || text.Contains("secrets.", StringComparison.Ordinal)))];
        YamlNode token = Assert.Single(tokenNodes);
        Assert.Equal(["env", "GH_TOKEN"], token.Path[^2..]);
        Assert.Equal("${{ github.token }}", Text(token.Value));
        Assert.Same(Map(updater)["env"], NodeAt(file, token.Path[..^1]));
        Dictionary<string, object?> updaterEnv = Map(Map(updater)["env"]);
        Assert.Equal("${{ steps.check.outputs.code }}", Text(updaterEnv["CHECK_EXIT"]));

        string checkRun = StepRun(checker);
        string run = StepRun(updater);
        Assert.StartsWith("set -euo pipefail\n", checkRun, StringComparison.Ordinal);
        Assert.StartsWith("set -euo pipefail\n", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", checkRun + run, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", checkRun + run, StringComparison.Ordinal);
        Assert.DoesNotContain("--body ", run, StringComparison.Ordinal);
        Assert.DoesNotContain("-b ", run, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(run, @"gh issue (?:create|edit)\b[^\n]*--body-file ""\$report"""));
        Assert.Equal(2, Regex.Count(run, @"gh issue (?:create|edit)\b"));

        // The checker records its exit code instead of failing, so the issue step decides; the report is removed first.
        Assert.Contains("check-links --report \"$report\" || code=$?", checkRun, StringComparison.Ordinal);
        Assert.Contains("echo \"code=$code\" >> \"$GITHUB_OUTPUT\"", checkRun, StringComparison.Ordinal);
        Assert.InRange(checkRun.IndexOf("rm -f \"$report\"", StringComparison.Ordinal), 0, checkRun.IndexOf("check-links --report", StringComparison.Ordinal));

        Assert.Contains("gh label create dead-links --force", run, StringComparison.Ordinal);
        Assert.Contains("gh issue list --label dead-links --state open --limit 100", run, StringComparison.Ordinal);
        Assert.Contains("--title \"Dead source links\" --label dead-links", run, StringComparison.Ordinal);
        Assert.Contains("--comment \"All source links resolve as of", run, StringComparison.Ordinal);
        Assert.Contains("gh issue close \"$extra\" --comment \"Duplicate of #$primary", run, StringComparison.Ordinal);

        // Verdict guard: only exit 0 or 1 with a non-empty report may reach the issue commands.
        int guard = run.IndexOf("{ [ \"$CHECK_EXIT\" != \"0\" ] && [ \"$CHECK_EXIT\" != \"1\" ]; }", StringComparison.Ordinal);
        Assert.True(guard >= 0, "missing the exit-code guard");
        Assert.Contains("[ ! -s \"$report\" ]", run[guard..], StringComparison.Ordinal);
        int guardExit = run.IndexOf("exit 1\n", guard, StringComparison.Ordinal);
        Assert.InRange(guardExit, guard, run.IndexOf("gh label create", StringComparison.Ordinal) - 1);

        // Dead links: update or open the issue first, then fail the job so the owner is notified as well.
        int create = run.IndexOf("gh issue create", StringComparison.Ordinal);
        int exitOne = run.IndexOf("exit 1\n", create, StringComparison.Ordinal);
        int close = run.IndexOf("gh issue close \"$number\"", StringComparison.Ordinal);
        Assert.True(exitOne > create && close > exitOne, "exit 1 must follow the issue update and precede the close");
    }

    private const string CiFile = "ci.yml";

    private const string LintJob = "workflow-lint";

    private const string LintChecksum = "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8";

    private const string LintHeader = "  workflow-lint:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    permissions:\n      contents: read\n";

    private const string LintVerify = "          echo \"$ACTIONLINT_SHA256  $RUNNER_TEMP/$archive\" | sha256sum --check --strict -\n";

    private const string LintUnpack = "          mkdir \"$RUNNER_TEMP/actionlint\"\n          tar -xzf \"$RUNNER_TEMP/$archive\" -C \"$RUNNER_TEMP/actionlint\" actionlint\n";

    [Fact]
    public void Ci_WorkflowLintJobRunsAPinnedActionlintOverEveryWorkflow()
    {
        AssertWorkflowLint(Workflow(CiFile));
    }

    [Theory]
    [InlineData("  workflow-lint:\n", "  lint-workflows:\n")]
    [InlineData(LintHeader, "  workflow-lint:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    permissions:\n      contents: write\n")]
    [InlineData(LintHeader, LintHeader + "      packages: write\n")]
    [InlineData(LintHeader, "  workflow-lint:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n")]
    [InlineData("  workflow-lint:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n", "  workflow-lint:\n    runs-on: ubuntu-latest\n    timeout-minutes: 30\n")]
    [InlineData("          persist-credentials: false\n      - name: Lint the workflows", "          persist-credentials: true\n      - name: Lint the workflows")]
    [InlineData("ACTIONLINT_VERSION: 1.7.12", "ACTIONLINT_VERSION: latest")]
    [InlineData("ACTIONLINT_VERSION: 1.7.12", "ACTIONLINT_VERSION: \"1.7\"")]
    [InlineData("          ACTIONLINT_VERSION: 1.7.12\n", "")]
    [InlineData("ACTIONLINT_SHA256: " + LintChecksum, "ACTIONLINT_SHA256: " + "8aca8db96f1b9477")]
    [InlineData("          ACTIONLINT_SHA256: " + LintChecksum + "\n", "")]
    [InlineData("releases/download/v${ACTIONLINT_VERSION}/$archive", "releases/latest/download/$archive")]
    [InlineData(LintVerify, "")]
    [InlineData(LintVerify, "          echo \"$ACTIONLINT_SHA256  $RUNNER_TEMP/$archive\" | sha256sum --check --strict - || true\n")]
    [InlineData(LintVerify + LintUnpack, LintUnpack + LintVerify)]
    [InlineData("\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes=", "\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes= .github/workflows/ci.yml")]
    [InlineData("\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes=", "\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes= .github/workflows/*.yml")]
    [InlineData("\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes=", "\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes= -ignore 'SC2153'")]
    [InlineData("\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes=", "echo skipped")]
    [InlineData("\"$RUNNER_TEMP/actionlint/actionlint\" -color -pyflakes=", "\"${{ runner.temp }}/actionlint/actionlint\" -color -pyflakes=")]
    [InlineData("          set -euo pipefail\n          archive=", "          set -eu\n          archive=")]
    public void Ci_WorkflowLintCheckFailsOnAViolation(string from, string to)
    {
        YamlFile mutated = Mutated(CiFile, from, to);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertWorkflowLint(mutated));
    }

    // The checksum is the only thing that stops a swapped release asset from running on the runner, so the check must come
    // before the first byte of the archive is extracted, and the job must lint every workflow, never a hand-picked list.
    private static void AssertWorkflowLint(YamlFile file)
    {
        Assert.True(Jobs(file).TryGetValue(LintJob, out object? job), $"{file.Name} must have the {LintJob} job.");
        Dictionary<string, object?> body = Map(job);

        Assert.True(body.ContainsKey("permissions"), "the job must declare its own permissions.");
        Dictionary<string, object?> permissions = Map(body["permissions"]);
        Assert.Equal(["contents"], permissions.Keys);
        Assert.Equal("read", Text(permissions["contents"]));
        Assert.Equal("5", Text(body.GetValueOrDefault("timeout-minutes")));

        List<object?> steps = Steps(job);
        object? checkout = steps[0];
        Assert.StartsWith("actions/checkout@", StepUses(checkout), StringComparison.Ordinal);
        Assert.Equal("false", Text(Map(Map(checkout)["with"])["persist-credentials"]).ToLowerInvariant());

        object? lint = Assert.Single(steps, step => StepRun(step).Contains("actionlint", StringComparison.Ordinal));
        Dictionary<string, object?> env = Map(Map(lint)["env"]);
        Assert.True(env.ContainsKey("ACTIONLINT_VERSION") && env.ContainsKey("ACTIONLINT_SHA256"), "the version and the checksum must be pinned in env.");
        string version = Text(env["ACTIONLINT_VERSION"]);
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Matches("^[0-9a-f]{64}$", Text(env["ACTIONLINT_SHA256"]));

        string run = StepRun(lint);
        Assert.StartsWith("set -euo pipefail\n", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", run, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/releases/download/v${ACTIONLINT_VERSION}/", run, StringComparison.Ordinal);

        int verify = run.IndexOf("\"$ACTIONLINT_SHA256  ", StringComparison.Ordinal);
        Assert.True(verify >= 0, "the archive must be checked against ACTIONLINT_SHA256.");
        int verifyEnd = run.IndexOf('\n', verify);
        string verifyLine = run[verify..verifyEnd];
        Assert.Contains("| sha256sum --check", verifyLine, StringComparison.Ordinal);
        Assert.DoesNotContain("||", verifyLine, StringComparison.Ordinal);

        int unpack = run.IndexOf("tar -x", StringComparison.Ordinal);
        int execute = run.IndexOf("/actionlint/actionlint\"", StringComparison.Ordinal);
        Assert.True(unpack >= 0 && execute >= 0, "the job must extract and run actionlint.");
        Assert.True(verify < unpack && unpack < execute, "the checksum must be verified before extraction, extraction before execution.");

        // No file argument means actionlint finds every workflow under .github/workflows itself.
        string invocation = run[execute..];
        Assert.Equal(invocation.TrimEnd('\n'), invocation.Split('\n')[0]);
        Assert.DoesNotMatch(@"\.ya?ml|-ignore|-config-file|\*", invocation);
    }
}
