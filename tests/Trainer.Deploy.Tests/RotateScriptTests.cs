using System.Text.RegularExpressions;
using Trainer.TestSupport;

namespace Trainer.Deploy.Tests;

/// <summary>
/// Structural rules of <c>rotate-db-password.sh</c> that the smoke test cannot observe from outside: the order of the
/// journal and the first change, the guards each mode takes, and how the password is handed to psql.
/// </summary>
public sealed class RotateScriptTests
{
    private static readonly string ScriptPath = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy", "remote", "rotate-db-password.sh");

    private static string Script() => File.ReadAllText(ScriptPath);

    private static string Body(string text, string function)
    {
        Match match = Regex.Match(text, @"^" + function + @"\(\) \{\n(?<body>.*?)^\}$", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"{function}() is missing.");
        return match.Groups["body"].Value;
    }

    private static string Mutate(string from, string to)
    {
        string text = Script();
        Assert.Contains(from, text, StringComparison.Ordinal);
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_StartsWithABashShebangAndStrictModeAndNeverTraces()
    {
        AssertHeader(Script());
    }

    [Theory]
    [InlineData("{ set +x; } 2>/dev/null\n", "")]
    [InlineData("set -euo pipefail\n", "")]
    public void HeaderCheck_FailsOnAViolation(string from, string to)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertHeader(Mutate(from, to)));
    }

    // The trace is switched off before the library is sourced and before any variable holds the password, so even
    // "bash -x" shows nothing that matters.
    private static void AssertHeader(string text)
    {
        Assert.StartsWith("#!/usr/bin/env bash\n", text, StringComparison.Ordinal);
        int strict = text.IndexOf("\nset -euo pipefail\n", StringComparison.Ordinal);
        int quiet = text.IndexOf("\n{ set +x; } 2>/dev/null\n", StringComparison.Ordinal);
        int library = text.IndexOf(". \"$here/lib.sh\"", StringComparison.Ordinal);
        Assert.True(strict >= 0 && quiet > strict && library > quiet, "strict mode, then set +x, then the library.");
    }

    [Fact]
    public void Begin_RefusesOverEveryGuardAndJournalsBeforeItChangesAnything()
    {
        AssertBeginOrder(Script());
    }

    [Theory]
    [InlineData("  dit_refuse_guards\n  IFS= read -r password", "  dit_refuse_guards --allow rotation\n  IFS= read -r password")]
    [InlineData("  dit_refuse_guards\n  IFS= read -r password", "  IFS= read -r password")]
    [InlineData("  write_journal \"$password\" || exit 1\n", "")]
    [InlineData("  kill_point after-journal\n  run_finish", "  run_finish\n  kill_point after-journal")]
    public void BeginCheck_FailsOnAViolation(string from, string to)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertBeginOrder(Mutate(from, to)));
    }

    private static void AssertBeginOrder(string text)
    {
        string begin = Body(text, "do_begin");
        int guards = begin.IndexOf("dit_refuse_guards\n", StringComparison.Ordinal);
        int journal = begin.IndexOf("write_journal \"$password\" || exit 1", StringComparison.Ordinal);
        int kill = begin.IndexOf("kill_point after-journal", StringComparison.Ordinal);
        int finish = begin.IndexOf("run_finish", StringComparison.Ordinal);
        Assert.True(guards >= 0 && journal > guards && kill > journal && finish > kill, "guards, journal, kill point, finish.");
        Assert.Equal(1, Regex.Count(begin, @"dit_refuse_guards"));

        // finish and commit proceed over the rotation guard only, and commit changes nothing but the journal.
        Assert.Contains("dit_refuse_guards --allow rotation\n  require_pending_role", Body(text, "do_finish"), StringComparison.Ordinal);
        Assert.DoesNotContain("dit_refuse_guards", Body(text, "do_commit"), StringComparison.Ordinal);
        int lockAt = text.IndexOf("\ndit_lock || exit 1\n", StringComparison.Ordinal);
        Assert.True(lockAt >= 0 && lockAt < text.IndexOf("case \"$mode\" in\n  begin)", StringComparison.Ordinal), "every mode takes the lock before it runs.");
    }

    [Fact]
    public void FinishAndCommit_CheckThePendingRoleBeforeAnyChange()
    {
        AssertRoleCheck(Script());
    }

    [Theory]
    [InlineData("    dit_log \"pending rotation is for $journal_role\"\n    exit 2", "    dit_log \"pending rotation is for $journal_role\"")]
    [InlineData("  dit_refuse_guards --allow rotation\n  require_pending_role\n  run_finish", "  dit_refuse_guards --allow rotation\n  run_finish")]
    [InlineData("do_commit() {\n  require_pending_role\n", "do_commit() {\n")]
    public void RoleCheck_FailsOnAViolation(string from, string to)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRoleCheck(Mutate(from, to)));
    }

    private static void AssertRoleCheck(string text)
    {
        string check = Body(text, "require_pending_role");
        Assert.Matches(@"if \[ ""\$journal_role"" != ""\$role"" \]; then\s+dit_log ""pending rotation is for \$journal_role""\s+exit 2\s+fi", check);
        Assert.Contains("  dit_refuse_guards --allow rotation\n  require_pending_role\n  run_finish\n", Body(text, "do_finish"), StringComparison.Ordinal);
        Assert.StartsWith("  require_pending_role\n", Body(text, "do_commit"), StringComparison.Ordinal);
    }

    [Fact]
    public void Password_ReachesPsqlThroughTheEnvironmentOnly()
    {
        AssertPasswordHandling(Script());
    }

    [Theory]
    [InlineData("'\\getenv pw ROTATION_PW'", "'\\set pw secret'")]
    [InlineData("PASSWORD :'pw';", "PASSWORD '$journal_password';")]
    [InlineData("-e ROTATION_PW postgres", "postgres")]
    [InlineData("--user postgres -e ROTATION_PW", "-e ROTATION_PW")]
    [InlineData("\"SET log_min_error_statement = panic;\" ", "")]
    [InlineData("\"SET log_statement = 'none';\" ", "")]
    [InlineData("\"SET log_min_error_statement = panic;\" \"SET log_statement = 'none';\" '\\getenv pw ROTATION_PW' \"ALTER ROLE $role PASSWORD :'pw';\"", "'\\getenv pw ROTATION_PW' \"ALTER ROLE $role PASSWORD :'pw';\" \"SET log_min_error_statement = panic;\" \"SET log_statement = 'none';\"")]
    [InlineData("  dit_log \"rotation of role $role is journaled\"\n", "  dit_log \"rotation of role $role is journaled $journal_password\"\n")]
    [InlineData("  printf '%s\\n' \"$journal_password\" | dit_env_set \"$env_file\" \"$name\"", "  dit_env_set \"$env_file\" \"$name\" \"$journal_password\"")]
    public void PasswordCheck_FailsOnAViolation(string from, string to)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertPasswordHandling(Mutate(from, to)));
    }

    private static void AssertPasswordHandling(string text)
    {
        string alter = Body(text, "alter_role");
        Assert.Contains("'\\getenv pw ROTATION_PW'", alter, StringComparison.Ordinal);
        Assert.Contains("\"ALTER ROLE $role PASSWORD :'pw';\"", alter, StringComparison.Ordinal);
        Assert.Contains("ROTATION_PW=\"$journal_password\" dit_compose exec -T --user postgres -e ROTATION_PW postgres psql", alter, StringComparison.Ordinal);

        // The server logs the failing statement, plaintext included, unless both settings are off in the same session first.
        int panic = alter.IndexOf("\"SET log_min_error_statement = panic;\"", StringComparison.Ordinal);
        int none = alter.IndexOf("\"SET log_statement = 'none';\"", StringComparison.Ordinal);
        int statement = alter.IndexOf("\"ALTER ROLE $role PASSWORD :'pw';\"", StringComparison.Ordinal);
        Assert.True(panic >= 0 && none >= 0 && statement > panic && statement > none, "both log settings must be sent before ALTER ROLE.");

        // The value reaches dit_env_set on stdin, and no command line anywhere names the password variables.
        Assert.Equal(2, Regex.Count(text, @"printf '%s\\n' ""\$journal_password"" \| dit_env_set "));
        foreach (string line in text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
        {
            if (line.Contains("$journal_password", StringComparison.Ordinal) || line.Contains("$password", StringComparison.Ordinal))
            {
                Assert.Matches(
                    @"ROTATION_PW=""\$journal_password"" dit_compose exec|printf '%s\\n' ""\$journal_password"" \| dit_env_set|journal_password=""\$password""|check_password ""\$password""|check_password ""\$journal_password""|!= ""\$journal_password"" \]|write_journal ""\$password""|local password|printf ""ROTATION_ROLE|\[\[ ""\$1"" =~|^\s*check_password|PGPASSWORD=""\$journal_password"" dit_compose exec",
                    line);
            }
        }
    }

    [Fact]
    public void EveryStepOfFinish_MapsItsFailureToExitOne()
    {
        AssertStepFailures(Script());
    }

    [Theory]
    [InlineData("alter_role || fail_step \"setting the password of role $role\"", "alter_role")]
    [InlineData(" || fail_step \"writing $name to .env\"", "")]
    [InlineData(" || fail_step \"writing $name to last-good.env\"", "")]
    [InlineData(" || fail_step \"recreating $service\"", "")]
    [InlineData("  verify_login || fail_step \"logging in as $role with the new password\"\n", "")]
    [InlineData("  wait_for_readiness || fail_step \"waiting for $service\"", "  wait_for_readiness")]
    [InlineData("  dit_log \"$1 failed; the journal stays, run finish again\"\n  exit 1", "  dit_log \"$1 failed; the journal stays, run finish again\"")]
    [InlineData("  verify_login || fail_step \"logging in as $role with the new password\"\n  wait_for_readiness || fail_step \"waiting for $service\"\n", "  wait_for_readiness || fail_step \"waiting for $service\"\n  verify_login || fail_step \"logging in as $role with the new password\"\n")]
    [InlineData("-e PGPASSWORD postgres", "postgres")]
    public void StepFailureCheck_FailsOnAViolation(string from, string to)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertStepFailures(Mutate(from, to)));
    }

    // psql answers 3 to a failed statement and dit_env_set 2 to a rejected value; to the workflow 2 and 3 mean "nothing was
    // changed", so no step may let its own code through. The login proof comes before the liveness wait, and it is a login as
    // the role with the journaled password, not a heartbeat.
    private static void AssertStepFailures(string text)
    {
        string finish = Body(text, "run_finish");
        Assert.Contains("alter_role || fail_step ", finish, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(finish, @"dit_env_set ""\$(?:env_file|good_file)"" ""\$name"" \|\| fail_step "));
        Assert.Matches(@"--no-deps ""\$service"" \|\| fail_step ", finish);
        int login = finish.IndexOf("verify_login || fail_step ", StringComparison.Ordinal);
        int wait = finish.IndexOf("wait_for_readiness || fail_step ", StringComparison.Ordinal);
        Assert.True(login >= 0 && wait > login, "the login proof precedes the liveness wait.");
        Assert.Matches(@"\n  exit 1\n$", Body(text, "fail_step"));
        Assert.Contains("PGPASSWORD=\"$journal_password\" dit_compose exec -T --user postgres -e PGPASSWORD postgres", Body(text, "verify_login"), StringComparison.Ordinal);
        Assert.Contains("-U \"$1\" -d \"$1\"", Body(text, "verify_login"), StringComparison.Ordinal);
    }

    [Fact]
    public void KillPoints_AreInertWithoutBothHookVariables()
    {
        string text = Script();

        Assert.Matches(@"if \[ ""\$\{DIT_TEST_HOOKS:-\}"" = ""1"" \] && \[ ""\$\{DIT_TEST_KILL_AT:-\}"" = ""\$1"" \]; then\s+kill -KILL \$\$", Body(text, "kill_point"));
        Assert.Equal(["after-alter-role", "after-env", "after-last-good", "after-journal"], Regex.Matches(text, @"^\s+kill_point (?<name>[a-z-]+)$", RegexOptions.Multiline).Select(m => m.Groups["name"].Value));
    }
}
