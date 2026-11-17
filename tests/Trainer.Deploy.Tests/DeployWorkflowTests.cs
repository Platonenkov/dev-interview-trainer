using System.Text.RegularExpressions;
using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

public sealed partial class DeployWorkflowTests
{
    private static readonly string Root = ContentWorkspace.RepositoryRoot();

    private static readonly string[] RequiredSmokeChecks =
    [
        "first_deploy_without_content_is_ready",
        "failed_first_install_can_be_retried",
        "internal_endpoints_only_on_internal_port",
        "content_switch_is_seen_without_restart",
        "forwarded_proto_is_trusted_from_gateway",
        "anonymous_page_is_cookieless_and_cached",
        "og_image_renders_in_container",
        "failed_readiness_rolls_back_to_last_good",
        "killed_deploy_then_failed_deploy_rolls_back_to_last_good",
        "killed_between_record_steps_never_mixes_deployments",
        "umami_image_change_is_not_rolled_back_automatically",
        "failed_deploy_changing_hba_restores_previous_rules",
        "concurrent_uploads_and_deploys_serialise",
        "guard_files_block_deploys",
        "stale_password_config_is_refused",
        "image_runs_as_non_root_with_relinked_runtime",
        "flags_path_without_o_x_is_refused",
        "unparseable_umami_image_is_refused_before_activation",
        "old_images_and_bundles_are_pruned",
        "interrupted_run_then_umami_change_keeps_the_guard",
        "backup_is_encrypted_and_contains_key_ring",
        "both_dumps_have_database_specific_meta_and_restore",
        "backup_without_recipient_fails",
        "restore_with_wrong_identity_leaves_live_database_untouched",
        "restore_into_scratch_db_matches_row_counts",
        "restore_replace_keeps_key_ring_and_runs_migrations",
        "restore_admitted_slow_post_is_rejected_after_restart",
        "restore_replace_failure_keeps_maintenance_and_blocks_deploys",
        "rebuild_empty_server_from_older_backup",
        "offsite_outage_keeps_unuploaded_files_and_predeploy_succeeds",
        "retention_keeps_14_daily",
        "ops_check_flags_stale_backup_and_guards",
        "content_out_of_schema_range_is_refused_before_switch",
        "rejected_import_switches_back",
        "rejected_first_delivery_returns_to_fresh_state",
        "lost_response_is_retried_and_decided_by_second_call",
        "unresolved_import_blocks_switch_until_settled",
        "same_sha_with_different_content_is_refused",
        "same_sha_reupload_is_reused_while_current",
        "tar_with_parent_paths_is_refused",
        "keeps_five_releases_and_current",
        "truncated_release_is_not_reused",
        "settled_rejection_switches_back",
        "killed_delivery_is_settled_by_the_next_delivery",
        "import_url_hook_is_ignored_without_the_gate",
        "refused_import_switches_back",
        "missing_token_is_refused_before_switch",
        "current_outside_releases_is_refused",
        "lock_timeout_is_reported_distinctly",
        "rotate_begin_then_commit_keeps_app_ready",
        "rotate_new_password_never_in_process_list",
        "deploy_is_refused_while_rotation_pending",
        "finish_with_other_role_fails_without_change",
        "crash_before_alter_role_then_finish_converges",
        "crash_after_alter_role_then_finish_converges",
        "crash_after_env_update_then_finish_converges",
        "crash_after_last_good_then_finish_recreates_service",
        "failed_alter_role_exits_one_and_never_logs_the_password",
    ];

    // Changing any of these makes a pull request run the full smoke tier; everything else under the path filter gets the quick one.
    private static readonly string[] FullTierPaths =
        ["deploy/*", ".github/workflows/deploy-*.yml", ".github/actions/*", "Dockerfile", ".dockerignore"];

    [GeneratedRegex(@"^\s*(?<patterns>deploy/\*[^\n)]*)\)\s*$", RegexOptions.Multiline)]
    private static partial Regex FullTierCase();

    [GeneratedRegex(@"^pass (?<name>[a-z0-9_]+)$", RegexOptions.Multiline)]
    private static partial Regex SmokeCheck();

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s+(?<action>[^\s@]+)@(?<ref>\S+)", RegexOptions.Multiline)]
    private static partial Regex ActionReference();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static Dictionary<string, object?> Workflow(string file)
    {
        Dictionary<object, object?> raw = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<object, object?>>(Read(".github", "workflows", file));
        return raw.ToDictionary(p => (string)p.Key, p => p.Value);
    }

    private static Dictionary<string, object?> Map(object? node) =>
        ((Dictionary<object, object?>)node!).ToDictionary(p => (string)p.Key, p => p.Value);

    private static List<string> Strings(object? node) => [.. ((List<object?>)node!).Select(item => (string)item!)];

    private static string ModeStepScript()
    {
        Dictionary<string, object?> smoke = Map(Map(Workflow("deploy-smoke.yml")["jobs"])["smoke"]);
        Dictionary<string, object?> step = ((List<object?>)smoke["steps"]!).Select(Map).Single(s => (string?)s.GetValueOrDefault("name") == "Choose the smoke mode");
        return (string)step["run"]!;
    }

    private static string[] FullTierPathsOf(string script) =>
        [.. FullTierCase().Match(script).Groups["patterns"].Value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static string StepText(Dictionary<string, object?> job) =>
        string.Join('\n', ((List<object?>)job["steps"]!).Select(step => string.Join('\n', Map(step).Select(p => $"{p.Key}: {p.Value}"))));

    [Fact]
    public void DeploySmoke_DeclaresStaticReadOnlyPermissions()
    {
        Dictionary<string, object?> workflow = Workflow("deploy-smoke.yml");
        Dictionary<string, object?> permissions = Map(workflow["permissions"]);

        Assert.Equal(["contents", "packages"], permissions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("read", permissions["contents"]);
        Assert.Equal("read", permissions["packages"]);
        Assert.DoesNotContain("${{", Read(".github", "workflows", "deploy-smoke.yml").Split("permissions:")[1].Split("concurrency:")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void DeploySmoke_RunsOnPullRequestsCallsAndDispatch()
    {
        Dictionary<string, object?> on = Map(Workflow("deploy-smoke.yml")["on"]);

        Assert.Equal(["pull_request", "workflow_call", "workflow_dispatch"], on.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["deploy/**", "Dockerfile", ".dockerignore", "src/**", ".github/workflows/deploy-*.yml", ".github/workflows/deploy-smoke.yml", ".github/actions/**"],
            Strings(Map(on["pull_request"])["paths"]));
        Dictionary<string, object?> image = Map(Map(Map(on["workflow_call"])["inputs"])["image"]);
        Assert.Equal("string", image["type"]);
        Assert.Equal("false", Convert.ToString(image["required"], System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant());
    }

    [Fact]
    public void DeploySmoke_DeclaresTheModeInputForCallsAndManualRuns()
    {
        Dictionary<string, object?> on = Map(Workflow("deploy-smoke.yml")["on"]);

        Dictionary<string, object?> call = Map(Map(Map(on["workflow_call"])["inputs"])["mode"]);
        Assert.Equal("string", call["type"]);
        Assert.Equal("full", call["default"]);
        Dictionary<string, object?> dispatch = Map(Map(Map(on["workflow_dispatch"])["inputs"])["mode"]);
        Assert.Equal("choice", dispatch["type"]);
        Assert.Equal(["quick", "full"], Strings(dispatch["options"]));
        Assert.Equal("full", dispatch["default"]);
    }

    [Fact]
    public void DeploySmoke_ChoosesTheModeFromTheChangedFilesAndValidatesTheRequestedOne()
    {
        string script = ModeStepScript();

        Assert.Equal(FullTierPaths, FullTierPathsOf(script));
        Assert.Contains("git diff --name-only \"$BASE_SHA...$HEAD_SHA\"", script, StringComparison.Ordinal);
        Assert.Contains("quick | full) ;;", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("echo \"DIT_SMOKE_MODE=$mode\" >> \"$GITHUB_ENV\"", script, StringComparison.Ordinal);
        // Every event value reaches the shell through env, never through an expression inside the script text.
        Assert.DoesNotContain("${{", script, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", Read(".github", "workflows", "deploy-smoke.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeploySmoke_ModeRuleMissingADeploymentPathIsDetected()
    {
        string script = ModeStepScript();

        foreach (string path in FullTierPaths)
        {
            string weakened = script.Replace(" | " + path, string.Empty, StringComparison.Ordinal);
            if (weakened == script)
            {
                weakened = script.Replace(path + " | ", string.Empty, StringComparison.Ordinal);
            }

            Assert.NotEqual(script, weakened);
            Assert.NotEqual(FullTierPaths, FullTierPathsOf(weakened));
        }
    }

    [Fact]
    public void DeployApp_CallsTheSmokeInTheQuickTier()
    {
        Dictionary<string, object?> smoke = Map(Map(Workflow("deploy-app.yml")["jobs"])["smoke"]);

        Assert.Equal("./.github/workflows/deploy-smoke.yml", smoke["uses"]);
        Assert.Equal("quick", Map(smoke["with"])["mode"]);
    }

    [Fact]
    public void DeploySmoke_ChecksJobRunsEveryStaticCheck()
    {
        Dictionary<string, object?> checks = Map(Map(Workflow("deploy-smoke.yml")["jobs"])["checks"]);
        string text = StepText(checks);

        Assert.Contains("shellcheck", text, StringComparison.Ordinal);
        Assert.Contains("render-env.test.sh", text, StringComparison.Ordinal);
        Assert.Contains("lib.test.sh", text, StringComparison.Ordinal);
        Assert.Contains("nginx-check.sh", text, StringComparison.Ordinal);
        Assert.Contains("provision-check.sh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hashFiles", text, StringComparison.Ordinal);
        Assert.Contains("Invoke-Pester deploy/bootstrap/tests", text, StringComparison.Ordinal);
        Assert.Contains("--project tests/Trainer.Deploy.Tests", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploySmoke_SmokeJobTestsTheImageAndUsesNoRepositorySecret()
    {
        string workflow = Read(".github", "workflows", "deploy-smoke.yml");
        Dictionary<string, object?> smoke = Map(Map(Workflow("deploy-smoke.yml")["jobs"])["smoke"]);
        string text = StepText(smoke);

        Assert.Contains("DIT_MIGRATE_IMAGE=\"$IMAGE\"", text, StringComparison.Ordinal);
        Assert.Contains("tests/Trainer.Infrastructure.Tests", text, StringComparison.Ordinal);
        Assert.Contains("Trainer.Infrastructure.Tests.MigrationBundleTests", text, StringComparison.Ordinal);
        Assert.Contains("bash deploy/tests/smoke.sh", text, StringComparison.Ordinal);
        Assert.Contains("docker login ghcr.io", text, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", text, StringComparison.Ordinal);
        Assert.Contains("GHCR_TOKEN: ${{ github.token }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", workflow, StringComparison.Ordinal);
        // The image reference reaches the shell only through env, never through an expression inside the script text.
        Assert.DoesNotContain("${{ inputs.image }}", string.Join('\n', ((List<object?>)smoke["steps"]!).Select(step => Convert.ToString(Map(step).GetValueOrDefault("run"), System.Globalization.CultureInfo.InvariantCulture))), StringComparison.Ordinal);
    }

    [Fact]
    public void DeploySmoke_RunsFourKillIterationsWithinTheJobTimeout()
    {
        Dictionary<string, object?> smoke = Map(Map(Workflow("deploy-smoke.yml")["jobs"])["smoke"]);

        Assert.Equal("4", Map(smoke["env"])["DIT_SMOKE_KILL_ITERATIONS"]);
        Assert.Equal(45, Convert.ToInt32(smoke["timeout-minutes"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Ci_DispatchDefaultsPreviousToTheParentCommit()
    {
        Dictionary<string, object?> dispatch = Map(Map(Map(Workflow("ci.yml")["on"])["workflow_dispatch"])["inputs"]);
        Dictionary<string, object?> previous = Map(dispatch["previous"]);

        Assert.Equal("false", Convert.ToString(previous["required"], System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant());
        Assert.Contains("git rev-parse HEAD^", Read(".github", "workflows", "ci.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaCompat_IsAReusableReadOnlyWorkflowWithTwoCommitInputs()
    {
        Dictionary<string, object?> workflow = Workflow("schema-compat.yml");
        Dictionary<string, object?> call = Map(Map(workflow["on"])["workflow_call"]);
        Dictionary<string, object?> inputs = Map(call["inputs"]);

        Assert.Equal(["current", "previous"], inputs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("read", Map(workflow["permissions"])["contents"]);
        Assert.Equal(["contents"], Map(Map(Map(workflow["jobs"])["check"])["permissions"]).Keys);
    }

    [Fact]
    public void SchemaCompat_SkipsWithoutMigrationChangesAndLogsAnUnmarkedPreviousSuite()
    {
        string workflow = Read(".github", "workflows", "schema-compat.yml");

        Assert.Contains("git diff --quiet \"$PREVIOUS\" \"$CURRENT\" -- src/Trainer.Infrastructure/Persistence/Migrations", workflow, StringComparison.Ordinal);
        Assert.Contains("tests/Trainer.Web.Release/RERUNNABLE", workflow, StringComparison.Ordinal);
        Assert.Contains("previous Release suite is not re-runnable; second run skipped", workflow, StringComparison.Ordinal);
        Assert.Contains("10.0.303", workflow, StringComparison.Ordinal);
        Assert.Contains("wasm-tools", workflow, StringComparison.Ordinal);
        Assert.Contains("./.github/actions/postgres-from-compose", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres@sha256", workflow, StringComparison.Ordinal);
        // The commit under test is checked out by SHA; an empty ref (a falsy branch of an && || expression) cannot happen.
        Assert.Contains("ref: ${{ inputs.current }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("&& ''", workflow, StringComparison.Ordinal);
        // Commit inputs are validated and reach the shell through env only.
        Assert.DoesNotMatch(@"run:[^\n]*\$\{\{\s*inputs\.", workflow);
        Assert.Contains("^[0-9a-f]{40}$", workflow, StringComparison.Ordinal);
        // A run in which no test executed must fail, not pass.
        Assert.DoesNotContain("--ignore-exit-code", workflow, StringComparison.Ordinal);
        Assert.Contains("--minimum-expected-tests", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_CallsSchemaCompatWithReadOnlyPermissions()
    {
        Dictionary<string, object?> jobs = Map(Workflow("ci.yml")["jobs"]);
        Dictionary<string, object?> job = Map(jobs["schema-compat"]);

        Assert.Equal("./.github/workflows/schema-compat.yml", job["uses"]);
        Assert.Equal("read", Map(job["permissions"])["contents"]);
        Dictionary<string, object?> with = Map(job["with"]);
        Assert.Equal("${{ needs.schema-base.outputs.previous }}", with["previous"]);
        Assert.Equal("${{ needs.schema-base.outputs.current }}", with["current"]);
    }

    [Fact]
    public void ReleaseSuite_CarriesTheRerunnableMarker() =>
        Assert.True(File.Exists(Path.Combine(Root, "tests", "Trainer.Web.Release", "RERUNNABLE")));

    [Theory]
    [InlineData("deploy-smoke.yml")]
    [InlineData("schema-compat.yml")]
    public void NewWorkflows_PinExternalActionsByCommitSha(string file)
    {
        MatchCollection references = ActionReference().Matches(Read(".github", "workflows", file));

        Assert.NotEmpty(references);
        foreach (Match reference in references)
        {
            Assert.Matches("^[0-9a-f]{40}$", reference.Groups["ref"].Value);
        }
    }

    [Fact]
    public void SmokeContent_IsACopyOfTheWebTestContent()
    {
        string source = Path.Combine(Root, "tests", "Trainer.Web.Tests", "TestContent", "web");
        string copy = Path.Combine(Root, "deploy", "tests", "content");
        string[] expected = [.. Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f)).Order(StringComparer.Ordinal)];
        string[] actual = [.. Directory.GetFiles(copy, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(copy, f)).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, actual);
        foreach (string relative in expected)
        {
            Assert.True(
                File.ReadAllBytes(Path.Combine(source, relative)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(copy, relative))),
                $"deploy/tests/content/{relative} differs from the Web test content; copy the directory again.");
        }
    }

    [Fact]
    public void Smoke_PrintsEveryRequiredCheck()
    {
        string[] printed = [.. SmokeCheck().Matches(Read("deploy", "tests", "smoke.sh")).Select(m => m.Groups["name"].Value)];

        foreach (string name in RequiredSmokeChecks)
        {
            Assert.Contains(name, printed);
        }

        Assert.Equal(printed.Length, printed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Smoke_QuickTierStopsAfterTheRollbackSectionAndKeepsTheBackupStandIn()
    {
        string script = Read("deploy", "tests", "smoke.sh");
        int lastQuickCheck = script.IndexOf("\npass failed_deploy_changing_hba_restores_previous_rules\n", StringComparison.Ordinal);
        int cut = script.IndexOf("if [ \"$smoke_mode\" = \"quick\" ]; then", StringComparison.Ordinal);
        int firstFullCheck = script.IndexOf("\npass concurrent_uploads_and_deploys_serialise\n", StringComparison.Ordinal);
        int proxy = script.IndexOf("s3-no-read-proxy.py\" --port 9000", StringComparison.Ordinal);

        Assert.True(lastQuickCheck > 0 && cut > lastQuickCheck && firstFullCheck > cut, "the quick cut must sit between the rollback and the serialisation section.");
        // The rollback deployments take a pre-deploy backup, so the S3 stand-in must start before the cut in both tiers.
        Assert.True(proxy > 0 && proxy < cut);
        Assert.Contains("exit 0", script[cut..firstFullCheck], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Smoke_RefusesAnUnknownModeBeforeTouchingDocker()
    {
        string? bash = ShellScriptTests.FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        System.Diagnostics.ProcessStartInfo info = new(bash!)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Root,
        };
        info.ArgumentList.Add(Path.Combine(Root, "deploy", "tests", "smoke.sh"));
        info.Environment["DIT_SMOKE_MODE"] = "nightly";

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("DIT_SMOKE_MODE must be", await stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_DecidesTheUmamiChangeAfterTheRepairAndLogsSafely()
    {
        string script = Read("deploy", "remote", "deploy-app.sh");
        string steps = script[script.IndexOf("dit_lock || exit 1", StringComparison.Ordinal)..];

        // The repair restores the interrupted run's own Umami values, so this run's decision must come last.
        Assert.True(
            steps.IndexOf("\ndecide_umami_change\n", StringComparison.Ordinal) > steps.IndexOf("\nrecover_interrupted_run\n", StringComparison.Ordinal),
            "decide_umami_change must run after recover_interrupted_run.");
        string pump = script[script.IndexOf("pump_log() {", StringComparison.Ordinal)..];
        Assert.Contains("trap '' TERM INT HUP", pump[..pump.IndexOf("\n}\n", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_ChecksTheFlagsPathBeforeItChangesAnything()
    {
        string script = Read("deploy", "remote", "deploy-app.sh");
        int traversal = script.IndexOf("dit_check_traversable \"$APP_DIR/flags\"", StringComparison.Ordinal);

        Assert.True(traversal > 0, "deploy-app.sh must verify that nginx can traverse the flags directory.");
        Assert.True(traversal < script.IndexOf("phase=active", StringComparison.Ordinal), "the check must come before the activation.");
        Assert.True(traversal > script.IndexOf("dit_ensure_layout", StringComparison.Ordinal), "the directories must exist before they are checked.");
    }
}
