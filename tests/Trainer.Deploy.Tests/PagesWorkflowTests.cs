using System.Globalization;
using System.Text.RegularExpressions;
using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

/// <summary>
/// Structure of the three workflows that build and publish the static site: <c>static-site.yml</c> (the reusable build),
/// <c>pages.yml</c> (gate, build, deploy with its in-lock recheck, live check) and the <c>static-site</c> job of <c>ci.yml</c>. The YAML is
/// parsed, so a comment or a string literal cannot satisfy or break a rule by accident. The branching of <c>pages.yml</c> lives
/// in <c>deploy/pages/pages-gate.sh</c> and is tested by <c>deploy/tests/pages-gate.test.sh</c>; the <c>run:</c> steps here
/// only call it, and a test below keeps it that way.
/// </summary>
public sealed class PagesWorkflowTests
{
    private const string PagesFile = "pages.yml";
    private const string StaticSiteFile = "static-site.yml";
    private const string CiFile = "ci.yml";
    private const string PagesEnvironment = "github-pages";
    private const string GateScript = "deploy/pages/pages-gate.sh";

    private static readonly string Root = ContentWorkspace.RepositoryRoot();

    private static readonly string[] RequiredInputs = ["ref", "site_url", "build_id"];

    private static readonly string[] OptionalStringInputs = ["goatcounter_code", "google_verification", "yandex_verification", "live_build_id"];

    private static readonly string[] OptionalBooleanInputs = ["retain", "upload"];

    [Fact]
    public void Pages_OnlyDeployHasAnEnvironmentAndBuildRunsOnlyOnTheGateDecision()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);

        Assert.Equal(["gate", "build", "deploy", "live-check"], jobs.Keys);
        Assert.Equal("${{ needs.gate.outputs.decision == 'deploy' }}", Text(Map(jobs["build"])["if"]));
        foreach (string job in new[] { "gate", "build", "live-check" })
        {
            Assert.False(Map(jobs[job]).ContainsKey("environment"), $"{job} must have no environment: a run the gate skips must not create a deployment record.");
        }
    }

    [Fact]
    public void Pages_DeployRechecksInsideItsLockRightBeforeDeployPages()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);
        Dictionary<string, object?> deploy = Map(jobs["deploy"]);
        Dictionary<string, object?> step = StepWithId(deploy, "recheck");
        Dictionary<string, object?> env = Map(step["env"]);

        // The decision is repeated under the lock: a run that waited for another deployment sees what it made live.
        Assert.Equal(["gate", "build"], Needs(deploy));
        Assert.False(deploy.ContainsKey("if"), "deploy must always start, so that its recheck runs inside the lock.");
        Assert.Equal("${{ steps.recheck.outputs.deploy }}", Text(Map(deploy["outputs"])["deployed"]));

        int checkout = StepIndex(deploy, s => Text(s.GetValueOrDefault("uses")).StartsWith("actions/checkout@", StringComparison.Ordinal));
        int recheck = StepIndex(deploy, s => Text(s.GetValueOrDefault("id")) == "recheck");
        int publish = StepIndex(deploy, s => Text(s.GetValueOrDefault("uses")).StartsWith("actions/deploy-pages@", StringComparison.Ordinal));
        Assert.True(0 <= checkout && checkout < recheck && recheck < publish, "checkout, then the recheck, then deploy-pages.");
        Dictionary<string, object?> with = Map(Map(Steps(deploy)[checkout])["with"]);
        Assert.Equal("main", Text(with["ref"]));
        Assert.Equal("0", Text(with["fetch-depth"]));
        Assert.Equal("false", Text(with["persist-credentials"]).ToLowerInvariant());

        // deploy-pages runs only on a positive answer, and every step that follows the recheck is conditional on it, so the
        // job ends green and without publishing when the recheck declines.
        Dictionary<string, object?> deployPages = Map(Steps(deploy)[publish]);
        Assert.Equal("${{ steps.recheck.outputs.deploy == 'true' }}", Text(deployPages["if"]));
        Assert.Equal("deployment", Text(deployPages["id"]));
        Assert.Equal(publish, Steps(deploy).Count - 2);

        // The job ends only when the live site carries this build, so the lock is held until the next deploy job can see it.
        Dictionary<string, object?> wait = Map(Steps(deploy)[publish + 1]);
        Assert.Equal("Wait until the build is live", Text(wait["name"]));
        Assert.Equal("${{ steps.recheck.outputs.deploy == 'true' }}", Text(wait["if"]));
        Assert.Equal("set -euo pipefail\nbash deploy/pages/pages-gate.sh wait-live \"$SITE_URL\" \"$BUILD_ID\"\n", Text(wait["run"]));
        Assert.Equal("${{ needs.gate.outputs.site_url }}", Text(Map(wait["env"])["SITE_URL"]));
        Assert.Equal("${{ needs.gate.outputs.build_id }}", Text(Map(wait["env"])["BUILD_ID"]));
        Assert.Equal(2, Map(wait["env"]).Count);
        Dictionary<string, object?> decline = Steps(deploy).Select(Map).Single(s => Text(s.GetValueOrDefault("name")) == "Say that nothing is published");
        Assert.Equal("${{ steps.recheck.outputs.deploy != 'true' }}", Text(decline["if"]));
        Assert.Contains("GITHUB_STEP_SUMMARY", Text(decline["run"]), StringComparison.Ordinal);
        Assert.Contains("already live or older", Text(decline["run"]), StringComparison.Ordinal);

        // The comparison itself (still deploy, same live build id, or still empty) is tested in pages-gate.test.sh; here the
        // step must hand the script exactly what the gate saw and write only its output.
        Assert.Equal(
            "set -euo pipefail\nbash deploy/pages/pages-gate.sh recheck \"$SITE_URL\" \"$CANDIDATE\" \"$EVENT_NAME\" \"$SEEN_FIRST\" \"$SEEN_LIVE_BUILD_ID\" >> \"$GITHUB_OUTPUT\"\n",
            Text(step["run"]));
        Assert.Equal("${{ needs.gate.outputs.first }}", Text(env["SEEN_FIRST"]));
        Assert.Equal("${{ needs.gate.outputs.live_build_id }}", Text(env["SEEN_LIVE_BUILD_ID"]));
        Assert.Equal("${{ needs.gate.outputs.candidate }}", Text(env["CANDIDATE"]));
        Assert.Equal("${{ inputs.allow_unknown_live }}", Text(env["PAGES_GATE_ALLOW_UNKNOWN_LIVE"]));
        Assert.False(jobs.ContainsKey("recheck"), "A separate recheck job would not be inside the deploy lock.");
    }

    [Fact]
    public void Pages_LiveCheckRunsOnlyAfterARealDeploy()
    {
        Dictionary<string, object?> liveCheck = Map(Jobs(PagesFile)["live-check"]);

        Assert.Contains("deploy", Needs(liveCheck));
        Assert.Equal("${{ needs.deploy.result == 'success' && needs.deploy.outputs.deployed == 'true' }}", Text(liveCheck["if"]));
        Assert.Equal("${{ needs.gate.outputs.build_id }}", Text(Map(Map(Steps(liveCheck).Single())["env"])["BUILD_ID"]));
    }

    [Fact]
    public void Pages_BuildsTheTriggeringCommitAndChecksProvenance()
    {
        Dictionary<string, object?> gate = Map(Jobs(PagesFile)["gate"]);
        Dictionary<string, object?> provenance = StepWithId(gate, "candidate");
        Dictionary<string, object?> env = Map(provenance["env"]);

        // The checks are shell logic in the script (pages-gate.test.sh runs them with a fake gh); the step passes the event
        // values through env and writes the script's output.
        Assert.Equal("set -euo pipefail\nbash deploy/pages/pages-gate.sh provenance >> \"$GITHUB_OUTPUT\"\n", Text(provenance["run"]));
        Assert.Equal("${{ github.event.workflow_run.head_sha }}", Text(env["RUN_SHA"]));
        Assert.Equal("${{ github.event.workflow_run.conclusion }}", Text(env["RUN_CONCLUSION"]));
        Assert.Equal("${{ github.event.workflow_run.event }}", Text(env["RUN_EVENT"]));
        Assert.Equal("${{ github.event.workflow_run.head_branch }}", Text(env["RUN_BRANCH"]));
        Assert.Equal("${{ github.event.workflow_run.head_repository.full_name }}", Text(env["RUN_REPOSITORY"]));
        Assert.Equal("${{ github.repository }}", Text(env["REPOSITORY"]));
        Assert.Equal("${{ github.event_name }}", Text(env["EVENT_NAME"]));
        Assert.Equal("${{ github.ref }}", Text(env["REF_NAME"]));
        Assert.Equal("${{ github.sha }}", Text(env["SHA"]));
        Assert.False(env.ContainsKey("SITE_URL"), "provenance does not need the site address; it is derived by the step before it.");

        // The script comes from main: the checkout precedes every decision step and names main, never the candidate. The
        // site address is the first decision, so that every later step and job uses one value.
        List<object?> steps = Steps(gate);
        Assert.StartsWith("actions/checkout@", Text(Map(steps[0])["uses"]), StringComparison.Ordinal);
        Assert.Equal("main", Text(Map(Map(steps[0])["with"])["ref"]));
        Dictionary<string, object?> site = Map(steps[1]);
        Assert.Equal("site", Text(site["id"]));
        Assert.Equal("set -euo pipefail\nbash deploy/pages/pages-gate.sh site-url >> \"$GITHUB_OUTPUT\"\n", Text(site["run"]));
        Assert.Equal("${{ vars.SITE_URL }}", Text(Map(site["env"])["SITE_URL"]));
        Assert.Equal("${{ github.repository }}", Text(Map(site["env"])["REPOSITORY"]));
        Assert.Equal(2, Map(site["env"]).Count);
        Assert.Equal("${{ steps.site.outputs.site_url }}", Text(Map(gate["outputs"])["site_url"]));
        Assert.Equal("candidate", Text(Map(steps[2])["id"]));

        Dictionary<string, object?> build = Map(Jobs(PagesFile)["build"]);
        Assert.Equal("${{ needs.gate.outputs.candidate }}", Text(Map(build["with"])["ref"]));
        Assert.Equal("${{ needs.gate.outputs.build_id }}", Text(Map(build["with"])["build_id"]));
        string decide = Text(StepWithId(gate, "gate")["run"]);
        Assert.Contains("build_id=$CANDIDATE.$RUN_ID.$RUN_ATTEMPT", decide, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_RunStepsHoldNoBranchingLogic()
    {
        // Every decision belongs in the script, where it is tested; a conditional or a short-circuit in a run step would be
        // logic that no test executes.
        foreach (string job in new[] { "gate", "deploy" })
        {
            foreach (Dictionary<string, object?> step in Steps(Map(Jobs(PagesFile)[job])).Select(Map).Where(s => s.ContainsKey("run")))
            {
                string run = Text(step["run"]);
                Assert.DoesNotMatch(@"(^|\s)(if|case|while|until|for)\s", run);
                Assert.DoesNotContain("&&", run, StringComparison.Ordinal);
                Assert.DoesNotContain("||", run, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Pages_ManualRunsCanAllowAnUnknownLiveCommitAndAutomaticRunsCannot()
    {
        Dictionary<string, object?> on = Map(Load(PagesFile)["on"]);
        Dictionary<string, object?> input = Map(Map(Map(on["workflow_dispatch"])["inputs"])["allow_unknown_live"]);

        Assert.Equal("boolean", Text(input["type"]));
        Assert.Equal("false", Text(input["default"]).ToLowerInvariant());
        Assert.Equal("false", Text(input["required"]).ToLowerInvariant());
        Assert.Single(Map(Map(on["workflow_dispatch"])["inputs"]));

        // Both decision steps get it through env; the script honours it for workflow_dispatch only (pages-gate.test.sh).
        foreach ((string job, string id) in new[] { ("gate", "gate"), ("deploy", "recheck") })
        {
            Assert.Equal("${{ inputs.allow_unknown_live }}", Text(Map(StepWithId(Map(Jobs(PagesFile)[job]), id)["env"])["PAGES_GATE_ALLOW_UNKNOWN_LIVE"]));
        }

        Assert.DoesNotContain("inputs.allow_unknown_live", string.Join('\n', AllNodes(Load(PagesFile)).Where(n => n.Path.Contains("run")).Select(n => Text(n.Value))), StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_RetainsExceptOnTheGateIdentifiedFirstDeployment()
    {
        Dictionary<string, object?> with = Map(Map(Jobs(PagesFile)["build"])["with"]);

        Assert.Equal("${{ needs.gate.outputs.first != 'true' }}", Text(with["retain"]));
        Assert.Equal("${{ needs.gate.outputs.live_build_id }}", Text(with["live_build_id"]));
        Assert.Equal("true", Text(with["upload"]).ToLowerInvariant());
        Assert.Equal("${{ vars.GOATCOUNTER_CODE }}", Text(with["goatcounter_code"]));
        Assert.Equal("${{ vars.GOOGLE_SITE_VERIFICATION }}", Text(with["google_verification"]));
        Assert.Equal("${{ vars.YANDEX_VERIFICATION }}", Text(with["yandex_verification"]));
    }

    [Fact]
    public void Pages_JobsDeclareEveryPermissionTheirStepsNeed()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);

        AssertPermissions(Map(Load(PagesFile)["permissions"]), ("contents", "read"));
        AssertPermissions(Map(Map(jobs["gate"])["permissions"]), ("contents", "read"), ("actions", "read"));
        AssertPermissions(Map(Map(jobs["build"])["permissions"]), ("contents", "read"));
        AssertPermissions(Map(Map(jobs["deploy"])["permissions"]), ("contents", "read"), ("pages", "write"), ("id-token", "write"));
        AssertPermissions(Map(Map(jobs["live-check"])["permissions"]), ("contents", "read"));

        // gate reads run lists with GITHUB_TOKEN (through gh, in the script), which is why it has actions: read and the others
        // do not.
        Assert.Contains("gh run list", File.ReadAllText(Path.Combine(Root, "deploy", "pages", "pages-gate.sh")), StringComparison.Ordinal);
        Assert.Equal("${{ github.token }}", Text(Map(StepWithId(Map(jobs["gate"]), "candidate")["env"])["GH_TOKEN"]));
        foreach (string job in new[] { "build", "deploy", "live-check" })
        {
            Assert.DoesNotContain(AllNodes(Map(jobs[job])), n => n.Path.Contains("GH_TOKEN"));
        }
        AssertPermissions(Map(Load(StaticSiteFile)["permissions"]), ("contents", "read"));
        AssertPermissions(Map(Map(Jobs(StaticSiteFile)["build"])["permissions"]), ("contents", "read"));
    }

    [Fact]
    public void Pages_OnlyTheDeployJobHasPagesWriteAndIdToken()
    {
        foreach (string file in new[] { PagesFile, StaticSiteFile, CiFile })
        {
            Dictionary<string, object?> top = Map(Load(file)["permissions"]);
            Assert.False(top.ContainsKey("pages") || top.ContainsKey("id-token"), $"{file}: no workflow-level pages or id-token.");
            foreach (KeyValuePair<string, object?> job in Jobs(file))
            {
                bool declares = Map(job.Value).TryGetValue("permissions", out object? permissions)
                    && (Map(permissions).ContainsKey("pages") || Map(permissions).ContainsKey("id-token"));
                Assert.Equal(file == PagesFile && job.Key == "deploy", declares);
            }
        }
    }

    [Fact]
    public void Pages_OnlyTheDeployJobHoldsTheConcurrencyGroup()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);

        // A run that will not deploy must not take a slot from, or replace, a pending run that will, so the group is not on
        // the workflow and not on gate. Builds may run in parallel across runs; the recheck inside the deploy job, under the
        // lock, is what keeps the site from going backwards.
        Assert.False(Load(PagesFile).ContainsKey("concurrency"));
        Assert.Equal(["deploy"], jobs.Keys.Where(job => Map(jobs[job]).ContainsKey("concurrency")));
        Dictionary<string, object?> concurrency = Map(Map(jobs["deploy"])["concurrency"]);
        Assert.Equal("pages", Text(concurrency["group"]));
        Assert.Equal("false", Text(concurrency["cancel-in-progress"]).ToLowerInvariant());
    }

    [Fact]
    public void SiteUrl_IsDerivedByTheGateAndOverriddenOnlyByTheRepositoryVariable()
    {
        // No workflow names a site address, and no file names this repository inside a URL; the host suffix of the derivation
        // lives only in the gate script.
        string[] workflows = Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml");
        Assert.True(workflows.Length > 3);
        foreach (string file in workflows)
        {
            Assert.DoesNotContain("github.io", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(@"https?://[^\s'""]*dev-interview-trainer", File.ReadAllText(Path.Combine(Root, GateScript)));

        // The variable is read by exactly one step of pages.yml (the gate's site-url step) and by ci.yml.
        Assert.Contains("vars.SITE_URL", Read(PagesFile), StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Read(PagesFile), @"vars\.SITE_URL"));
        Assert.Equal("${{ vars.SITE_URL }}", Text(Map(StepWithId(Map(Jobs(PagesFile)["gate"]), "site")["env"])["SITE_URL"]));
        foreach (string file in workflows.Where(f => !f.EndsWith("pages.yml", StringComparison.Ordinal) && !f.EndsWith("ci.yml", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("vars.SITE_URL", File.ReadAllText(file), StringComparison.Ordinal);
        }

        // Every later use in pages.yml reads the gate's output.
        List<string> siteUrls = [.. AllNodes(Load(PagesFile)).Where(n => n.Path.Length > 0 && n.Path[^1] == "site_url" && n.Path[0] == "jobs" && n.Path[1] != "gate").Select(n => Text(n.Value))];
        Assert.NotEmpty(siteUrls);
        Assert.All(siteUrls, value => Assert.Equal("${{ needs.gate.outputs.site_url }}", value));
        YamlNode[] environmentUses = [.. AllNodes(Load(PagesFile)).Where(n => n.Path.Length > 0 && n.Path[^1] == "SITE_URL" && !(n.Path[1] == "gate"))];
        Assert.NotEmpty(environmentUses);
        foreach (YamlNode node in environmentUses)
        {
            Assert.Equal("${{ needs.gate.outputs.site_url }}", Text(node.Value));
        }
        Assert.Equal("${{ steps.site.outputs.site_url }}", Text(Map(StepWithId(Map(Jobs(PagesFile)["gate"]), "gate")["env"])["SITE_URL"]));

        // ci.yml may only add a sentinel on a reserved domain whenever no custom domain is set.
        Dictionary<string, object?> with = Map(Map(Jobs(CiFile)["static-site"])["with"]);
        Assert.Equal("${{ vars.SITE_URL || 'https://ci.invalid/fork-build/' }}", Text(with["site_url"]));
        Assert.Equal("false", Text(with["upload"]).ToLowerInvariant());
    }

    [Fact]
    public void Ci_StaticSiteJobCallsTheReusableWorkflowWithoutUploadOrRetention()
    {
        Dictionary<string, object?> job = Map(Jobs(CiFile)["static-site"]);
        Dictionary<string, object?> with = Map(job["with"]);

        Assert.Equal("./.github/workflows/static-site.yml", Text(job["uses"]));
        Assert.Equal(["build-test"], Needs(job));
        AssertPermissions(Map(job["permissions"]), ("contents", "read"));
        Assert.Equal("false", Text(with["retain"]).ToLowerInvariant());
        Assert.Equal("false", Text(with["upload"]).ToLowerInvariant());
        Assert.Equal("${{ github.sha }}", Text(with["ref"]));
        Assert.Equal("${{ github.sha }}.${{ github.run_id }}.${{ github.run_attempt }}", Text(with["build_id"]));
        Assert.False(job.ContainsKey("secrets"));
    }

    [Fact]
    public void StaticSite_DerivesPrefixAndAppUrlFromTheSiteUrl()
    {
        Dictionary<string, object?> job = Map(Jobs(StaticSiteFile)["build"]);
        List<object?> steps = Steps(job);
        Dictionary<string, object?> derive = Map(steps[0]);
        string run = Text(derive["run"]);

        Assert.DoesNotContain("dev-interview-trainer", Read(StaticSiteFile), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("site", Text(derive["id"]));
        Assert.Equal("${{ inputs.site_url }}", Text(Map(derive["env"])["SITE_URL"]));

        // First of all, before the checkout and the SDK: an empty site address fails at once.
        int emptyCheck = run.IndexOf("[ -z \"$SITE_URL\" ]", StringComparison.Ordinal);
        Assert.True(emptyCheck >= 0 && run.IndexOf("exit 1", emptyCheck, StringComparison.Ordinal) > emptyCheck);
        Assert.Contains("prefix=\"/${rest#*/}\"", run, StringComparison.Ordinal);
        Assert.Contains("echo \"prefix=$prefix\" >> \"$GITHUB_OUTPUT\"", run, StringComparison.Ordinal);
        Assert.Contains("echo \"app_url=http://127.0.0.1:5080$prefix\" >> \"$GITHUB_OUTPUT\"", run, StringComparison.Ordinal);
        Assert.True(StepIndex(job, step => Text(step.GetValueOrDefault("uses")).StartsWith("actions/checkout@", StringComparison.Ordinal)) > 0);
    }

    [Fact]
    public void StaticSite_PublishUsesTheConstantVersion()
    {
        List<string> publishes = [.. Steps(Map(Jobs(StaticSiteFile)["build"])).Select(s => Text(Map(s).GetValueOrDefault("run")))
            .SelectMany(run => run.Split('\n'))
            .Where(line => line.Contains("dotnet publish src/Trainer.Web", StringComparison.Ordinal))];

        Assert.NotEmpty(publishes);
        foreach (string line in publishes)
        {
            Assert.Contains("-p:IncludeSourceRevisionInInformationalVersion=false", line, StringComparison.Ordinal);
            Assert.Contains("-p:InformationalVersion=static", line, StringComparison.Ordinal);
            Assert.Contains("-c Release", line, StringComparison.Ordinal);
        }

        Assert.Contains(publishes, line => line.Contains("-o \"$GITHUB_WORKSPACE/artifacts/web\"", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticSite_AppUrlHostIsAllowedAndAppRunsFromItsPublishDirectory()
    {
        Dictionary<string, object?> job = Map(Jobs(StaticSiteFile)["build"]);
        Dictionary<string, object?> start = Steps(job).Select(Map).Single(s => Text(s.GetValueOrDefault("name")) == "Start the published app");
        Dictionary<string, object?> env = Map(start["env"]);

        // The comment in the workflow may name the setting; no key or value may set it.
        Assert.DoesNotContain(AllNodes(Load(StaticSiteFile)), n => n.Path.Contains("AllowedHosts") || (n.Value is string text && text.Contains("AllowedHosts", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("artifacts/web", Text(start["working-directory"]));
        Assert.Equal("StaticExport", Text(env["ASPNETCORE_ENVIRONMENT"]));
        Assert.Equal("http://127.0.0.1:5080", Text(env["ASPNETCORE_URLS"]));
        Assert.Equal("Static", Text(env["App__Edition"]));
        Assert.Equal("${{ inputs.site_url }}", Text(env["App__PublicBaseUrl"]));
        Assert.Equal("${{ inputs.build_id }}", Text(env["App__StaticSite__BuildId"]));
        Assert.Equal("${{ github.workspace }}/content", Text(env["App__ContentRoot"]));
        Assert.Contains("Trainer.Web.dll", Text(start["run"]), StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSite_ExportsThenVerifiesAsSeparateStepsAndWaitsForTheApp()
    {
        Dictionary<string, object?> job = Map(Jobs(StaticSiteFile)["build"]);
        int wait = StepIndex(job, s => Text(s.GetValueOrDefault("name")) == "Wait for the app");
        int export = StepIndex(job, s => Text(s.GetValueOrDefault("name")) == "Export the site");
        int verify = StepIndex(job, s => Text(s.GetValueOrDefault("name")) == "Verify the export");
        int smoke = StepIndex(job, s => Text(s.GetValueOrDefault("name")) == "Static smoke tests");

        Assert.True(0 < wait && wait < export && export < verify && verify < smoke);
        string waitRun = Text(Map(Steps(job)[wait])["run"]);
        Assert.Contains("seq 1 120", waitRun, StringComparison.Ordinal);
        Assert.Contains("${APP_URL}health/ready", waitRun, StringComparison.Ordinal);

        string exportRun = Text(Map(Steps(job)[export])["run"]);
        Assert.Contains("--app \"$APP_URL\" --site-url \"$SITE_URL\" --out \"$GITHUB_WORKSPACE/artifacts/site\" --build-id \"$BUILD_ID\" --commit \"$REF\"", exportRun, StringComparison.Ordinal);
        Assert.Contains("--retain-from \"$SITE_URL\" --live-build-id \"$LIVE_BUILD_ID\" --previous-page-out \"$GITHUB_WORKSPACE/artifacts/previous-page.html\"", exportRun, StringComparison.Ordinal);
        Assert.Contains("[ \"$RETAIN\" = \"true\" ]", exportRun, StringComparison.Ordinal);

        string verifyRun = Text(Map(Steps(job)[verify])["run"]);
        Assert.Contains(" verify --dir \"$GITHUB_WORKSPACE/artifacts/site\" --site-url \"$SITE_URL\"", verifyRun, StringComparison.Ordinal);
        Assert.DoesNotContain("||", verifyRun, StringComparison.Ordinal);
        Assert.False(Map(Steps(job)[verify]).ContainsKey("continue-on-error"));
    }

    [Fact]
    public void StaticSite_SmokeStepRequiresItsTestsAndSetsAbsoluteInputs()
    {
        Dictionary<string, object?> job = Map(Jobs(StaticSiteFile)["build"]);
        Dictionary<string, object?> smoke = Steps(job).Select(Map).Single(s => Text(s.GetValueOrDefault("name")) == "Static smoke tests");
        Dictionary<string, object?> env = Map(smoke["env"]);
        string run = Text(smoke["run"]);

        Assert.Equal("1", Text(env["DIT_STATIC_SMOKE_REQUIRED"]));
        Assert.Equal("${{ github.workspace }}/artifacts/site", Text(env["DIT_STATIC_SITE_DIR"]));
        Assert.Equal("${{ inputs.site_url }}", Text(env["DIT_STATIC_SITE_URL"]));
        Assert.Equal("0", Text(env["DIT_PERF_ENFORCE"]));
        Assert.Contains("--minimum-expected-tests", run, StringComparison.Ordinal);
        Assert.Contains("--filter-trait \"Category=StaticSmoke\"", run, StringComparison.Ordinal);
        Assert.Contains("tests/Trainer.Web.Release", run, StringComparison.Ordinal);

        // Set only when a previous page was really captured (retention); a missing file makes the suite fail.
        Assert.Equal("${{ inputs.retain && format('{0}/artifacts/previous-page.html', github.workspace) || '' }}", Text(env["DIT_PREVIOUS_PAGE"]));
    }

    [Fact]
    public void StaticSite_UploadsThePagesArtifactOnlyWhenAskedTo()
    {
        Dictionary<string, object?> job = Map(Jobs(StaticSiteFile)["build"]);
        Dictionary<string, object?> upload = Steps(job).Select(Map).Single(s => Text(s.GetValueOrDefault("uses")).StartsWith("actions/upload-pages-artifact@", StringComparison.Ordinal));

        Assert.Equal("${{ inputs.upload }}", Text(upload["if"]));
        Assert.Equal("artifacts/site", Text(Map(upload["with"])["path"]));
        Assert.StartsWith("actions/upload-pages-artifact@", Text(Map(Steps(job)[^1])["uses"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_PassesTheEventToGateAndRecheck()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);
        foreach ((string job, string id, string command) in new[] { ("gate", "gate", "decide"), ("deploy", "recheck", "recheck") })
        {
            Dictionary<string, object?> step = StepWithId(Map(jobs[job]), id);
            Assert.Equal("${{ github.event_name }}", Text(Map(step["env"])["EVENT_NAME"]));
            Assert.Contains("bash deploy/pages/pages-gate.sh " + command + " \"$SITE_URL\" \"$CANDIDATE\" \"$EVENT_NAME\"", Text(step["run"]), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(Root, "deploy", "pages", "pages-gate.sh")));
        Assert.False(Directory.Exists(Path.Combine(Root, ".github", "scripts")), "The gate script lives under deploy/, where ShellCheck and the shell-convention tests scan.");
    }

    [Fact]
    public void Pages_GateAndDeployReadTheFullHistoryOfMain()
    {
        Dictionary<string, object?> jobs = Jobs(PagesFile);
        foreach (string name in new[] { "gate", "deploy" })
        {
            Dictionary<string, object?> checkout = Steps(Map(jobs[name])).Select(Map).Single(s => Text(s.GetValueOrDefault("uses")).StartsWith("actions/checkout@", StringComparison.Ordinal));
            Dictionary<string, object?> with = Map(checkout["with"]);
            Assert.Equal("main", Text(with["ref"]));
            Assert.Equal("0", Text(with["fetch-depth"]));
            Assert.Equal("false", Text(with["persist-credentials"]).ToLowerInvariant());
        }
    }

    [Fact]
    public void Pages_HasNoScheduleNoPushNoPullRequest()
    {
        Dictionary<string, object?> on = Map(Load(PagesFile)["on"]);

        Assert.Equal(["workflow_run", "workflow_dispatch"], on.Keys);
        Dictionary<string, object?> run = Map(on["workflow_run"]);
        Assert.Equal(["ci"], ((List<object?>)run["workflows"]!).Select(Text));
        Assert.Equal(["completed"], ((List<object?>)run["types"]!).Select(Text));
        Assert.Equal(["main"], ((List<object?>)run["branches"]!).Select(Text));
    }

    [Fact]
    public void Pages_UsesNoSecrets()
    {
        foreach (string file in new[] { PagesFile, StaticSiteFile })
        {
            YamlNode[] nodes = [.. AllNodes(Load(file))];
            Assert.DoesNotContain(nodes, n => n.Value is string text && text.Contains("secrets.", StringComparison.Ordinal));
            Assert.DoesNotContain(nodes, n => n.Path.Length > 0 && n.Path[^1] == "secrets");
            Assert.DoesNotContain(nodes, n => n.Value is string text && text == "inherit");
        }
    }

    [Fact]
    public void GithubPagesEnvironment_OnlyOnTheDeployJobOfPages()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml").Order(StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            foreach (KeyValuePair<string, object?> job in Jobs(file))
            {
                bool pagesDeploy = file == PagesFile && job.Key == "deploy";
                Assert.Equal(pagesDeploy, EnvironmentName(job.Value) == PagesEnvironment);
            }
        }

        Dictionary<string, object?> environment = Map(Map(Jobs(PagesFile)["deploy"])["environment"]);
        Assert.Equal(PagesEnvironment, Text(environment["name"]));
        Assert.Equal("${{ steps.deployment.outputs.page_url }}", Text(environment["url"]));
    }

    [Fact]
    public void StaticSite_OptionalInputsHaveDefaults()
    {
        Dictionary<string, object?> inputs = Map(Map(Load(StaticSiteFile)["on"])["workflow_call"]);
        inputs = Map(inputs["inputs"]);

        foreach (string name in RequiredInputs)
        {
            Assert.Equal("true", Text(Map(inputs[name])["required"]).ToLowerInvariant());
            Assert.Equal("string", Text(Map(inputs[name])["type"]));
        }

        foreach (string name in OptionalStringInputs)
        {
            Dictionary<string, object?> input = Map(inputs[name]);
            Assert.Equal("string", Text(input["type"]));
            Assert.Equal("false", Text(input["required"]).ToLowerInvariant());
            Assert.True(input.ContainsKey("default"), $"{name} needs a default.");
            Assert.Equal(string.Empty, Text(input["default"]));
        }

        foreach (string name in OptionalBooleanInputs)
        {
            Dictionary<string, object?> input = Map(inputs[name]);
            Assert.Equal("boolean", Text(input["type"]));
            Assert.Equal("false", Text(input["required"]).ToLowerInvariant());
            Assert.Equal("false", Text(input["default"]).ToLowerInvariant());
        }

        // The declared inputs are exactly the ones checked above; a new input must be added to a list with its default.
        Assert.Equal(
            [.. RequiredInputs.Concat(OptionalStringInputs).Concat(OptionalBooleanInputs).Order(StringComparer.Ordinal)],
            [.. inputs.Keys.Order(StringComparer.Ordinal)]);

        // ci.yml passes the three required inputs and the two switches, and nothing else.
        HashSet<string> passed = [.. Map(Map(Jobs(CiFile)["static-site"])["with"]).Keys];
        Assert.Subset(new HashSet<string> { "ref", "site_url", "build_id", "retain", "upload" }, passed);
        Assert.Superset(new HashSet<string> { "ref", "site_url", "build_id" }, passed);
    }

    private sealed record YamlNode(string[] Path, object? Value);

    private static string Read(string name) => File.ReadAllText(Path.Combine(Root, ".github", "workflows", name));

    private static Dictionary<string, object?> Load(string name)
    {
        Dictionary<object, object?> raw = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(Read(name));
        return raw.ToDictionary(p => (string)p.Key, p => p.Value);
    }

    private static Dictionary<string, object?> Jobs(string file) => Map(Load(file)["jobs"]);

    private static Dictionary<string, object?> Map(object? node) =>
        node as Dictionary<string, object?> ?? ((Dictionary<object, object?>)node!).ToDictionary(p => (string)p.Key, p => p.Value);

    private static bool IsMap(object? node) => node is Dictionary<object, object?> or Dictionary<string, object?>;

    private static string Text(object? node) => Convert.ToString(node, CultureInfo.InvariantCulture) ?? string.Empty;

    private static List<object?> Steps(Dictionary<string, object?> job) => (List<object?>)job["steps"]!;

    private static string RunText(Dictionary<string, object?> job) =>
        string.Join('\n', Steps(job).Select(step => Text(Map(step).GetValueOrDefault("run"))));

    private static Dictionary<string, object?> StepWithId(Dictionary<string, object?> job, string id) =>
        Steps(job).Select(Map).Single(step => Text(step.GetValueOrDefault("id")) == id);

    private static int StepIndex(Dictionary<string, object?> job, Func<Dictionary<string, object?>, bool> match)
    {
        List<object?> steps = Steps(job);
        for (int index = 0; index < steps.Count; index++)
        {
            if (match(Map(steps[index])))
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] Needs(Dictionary<string, object?> job) =>
        job["needs"] is List<object?> list ? [.. list.Select(Text)] : [Text(job["needs"])];

    private static string EnvironmentName(object? job)
    {
        if (!IsMap(job) || !Map(job).TryGetValue("environment", out object? environment))
        {
            return string.Empty;
        }

        return IsMap(environment) ? Text(Map(environment).GetValueOrDefault("name")) : Text(environment);
    }

    private static void AssertPermissions(Dictionary<string, object?> actual, params (string Scope, string Level)[] expected)
    {
        Assert.Equal([.. expected.Select(e => e.Scope).Order(StringComparer.Ordinal)], [.. actual.Keys.Order(StringComparer.Ordinal)]);
        foreach ((string scope, string level) in expected)
        {
            Assert.Equal(level, Text(actual[scope]));
        }
    }

    private static IEnumerable<YamlNode> AllNodes(Dictionary<string, object?> root) =>
        Nodes(root.ToDictionary(p => (object)p.Key, p => p.Value), []);

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
}
