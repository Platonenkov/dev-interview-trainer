using System.Text.RegularExpressions;
using Trainer.TestSupport;

namespace Trainer.Deploy.Tests;

public sealed partial class BackupScriptTests
{
    private static readonly string DeployRoot = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([DeployRoot, .. parts]));

    private static Dictionary<string, string> UnitSettings(string unit) =>
        Read("systemd", unit)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith('[') && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .GroupBy(pair => pair[0])
            .ToDictionary(group => group.Key, group => group.Last()[1]);

    [GeneratedRegex(@"^\s*(?:[A-Za-z_]+=\S+\s+)*rclone\b.*$", RegexOptions.Multiline)]
    private static partial Regex RcloneInvocation();

    [Fact]
    public void BackupTimer_RunsDailyAtHalfPastThreeUtcAndCatchesUp()
    {
        Dictionary<string, string> timer = UnitSettings("dit-backup.timer");

        Assert.Equal("*-*-* 03:30:00 UTC", timer["OnCalendar"]);
        Assert.Equal("true", timer["Persistent"]);
        Assert.Equal("10m", timer["RandomizedDelaySec"]);
        Assert.Equal("dit-backup.service", timer["Unit"]);
        Assert.Equal("timers.target", timer["WantedBy"]);
    }

    [Fact]
    public void BackupService_RunsTheBundledScriptAsDeployOnlyOnceItExists()
    {
        Dictionary<string, string> service = UnitSettings("dit-backup.service");
        const string script = "/srv/dev-interview-trainer/current-bundle/remote/backup.sh";

        Assert.Equal(script, service["ConditionPathExists"]);
        Assert.Equal($"{script} daily", service["ExecStart"]);
        Assert.Equal("deploy", service["User"]);
        Assert.Equal("oneshot", service["Type"]);
        Assert.Equal("APP_DIR=/srv/dev-interview-trainer", service["Environment"]);
    }

    [Theory]
    [InlineData("backup.sh")]
    [InlineData("restore.sh")]
    [InlineData("ops-check.sh")]
    public void EntryScripts_StartWithABashShebang(string name)
    {
        Assert.StartsWith("#!/usr/bin/env bash\n", Read("remote", name), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup.sh")]
    [InlineData("restore.sh")]
    [InlineData("ops-check.sh")]
    public void Scripts_DoNotTrustAnInheritedLockFlag(string name)
    {
        // A bare DIT_LOCKED proves nothing; dit_lock decides from the inherited descriptor.
        Assert.DoesNotContain("DIT_LOCKED=1", Read("remote", name), StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_KeepsOffsiteCredentialsOffTheCommandLine()
    {
        string script = Read("remote", "backup.sh");

        foreach (Match call in RcloneInvocation().Matches(script))
        {
            Assert.DoesNotContain("ACCESS_KEY", call.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", call.Value, StringComparison.Ordinal);
        }

        Assert.Contains("RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--dump", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-vv", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_ProceedsOnlyOverTheGuardsThatLeaveTheTrainerDatabaseConsistentAndNeverDeletesWithoutAMarker()
    {
        string script = Read("remote", "backup.sh");

        // flags/maintenance and .deploy-in-progress still refuse; a pending rotation and an Umami recovery may last days.
        Assert.Matches(@"(?m)^dit_refuse_guards --allow rotation umami-recovery$", script);
        Assert.Single(Regex.Matches(script, @"dit_refuse_guards"));
        Assert.DoesNotContain("--allow restore", script, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted-deploy", script, StringComparison.Ordinal);
        Assert.Contains("dit_backup_prune_candidates", script, StringComparison.Ordinal);
        Assert.DoesNotContain("find \"$backups\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_SkipsTheUmamiDumpWhileTheRecoveryGuardExists()
    {
        string script = Read("remote", "backup.sh");
        int skip = script.IndexOf("[ -e \"$APP_DIR/.umami-recovery\" ]", StringComparison.Ordinal);

        Assert.True(skip > 0, "the umami dump must be skipped while .umami-recovery exists.");
        Assert.Contains("skipping the umami dump", script[skip..(skip + 300)], StringComparison.Ordinal);
        Assert.True(skip < script.IndexOf("dump_database \"$db\" \"$ts\")\"; then", StringComparison.Ordinal), "the skip must come before the dump of the database.");
    }

    [Fact]
    public void Backup_NeverReadsAnObjectBecauseTheServerKeyMayOnlyListAndWrite()
    {
        string script = Read("remote", "backup.sh");

        // Without these rclone looks the destination object up before the upload and HEADs it after, and a key that cannot
        // read gets a 403 on both: no upload would ever succeed.
        Assert.Contains("RCLONE_CONFIG_OFFSITE_NO_HEAD=true", script, StringComparison.Ordinal);
        Assert.Contains("RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET=true", script, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(script, @"rclone_offsite copyto --no-check-dest "));
        Assert.DoesNotContain("rclone_offsite copyto \"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rclone_offsite sync", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rclone_offsite check", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDatabases_SilencesStatementLoggingBeforeThePasswordStatement()
    {
        string script = Read("remote", "ensure-databases.sh");
        int errorLog = script.IndexOf("SET log_min_error_statement = panic;", StringComparison.Ordinal);
        int statementLog = script.IndexOf("SET log_statement = 'none';", StringComparison.Ordinal);
        int getenv = script.IndexOf("\\getenv pw PW", StringComparison.Ordinal);
        int create = script.IndexOf("CREATE ROLE", StringComparison.Ordinal);

        // psql substitutes the password client-side, so a failing statement would be logged with it in plain text.
        Assert.True(errorLog > 0 && statementLog > 0 && getenv > 0 && create > 0, "all four lines must be present.");
        Assert.True(errorLog < getenv && statementLog < getenv && getenv < create, "the SET lines must precede the password statement.");
        Assert.Equal(2, Regex.Count(script, @"SET log_"));
    }

    [Fact]
    public void DeployApp_SettlesAPendingImportBeforeItRestartsAnything()
    {
        string script = Read("remote", "deploy-app.sh");
        int settle = script.IndexOf("dit_revert_import_pending || exit 2", StringComparison.Ordinal);

        Assert.True(settle > 0);
        Assert.True(settle < script.IndexOf("\nrecover_interrupted_run\n", StringComparison.Ordinal), "the pending import must be settled before the repair restarts the stack.");
        Assert.True(settle < script.IndexOf("phase=active", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_SettlesAPendingImportForTrainerAndRepairsTheRecordsBeforeUmamiStarts()
    {
        string script = Read("remote", "restore.sh");
        int pending = script.IndexOf("dit_revert_import_pending || exit 2", StringComparison.Ordinal);
        int repair = script.IndexOf("if ! dit_restore_last_good_records", StringComparison.Ordinal);
        int flag = script.IndexOf("flag_tmp=\"$flag.new\"", StringComparison.Ordinal);

        Assert.True(pending > 0 && repair > 0);
        Assert.True(pending < flag && repair < flag, "both must happen before the maintenance flag and any start.");
        Assert.Contains("dit_clear_umami_recovery", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_ReadsTheIdentityFromStdinAndAllowsOnlyTheUmamiGuards()
    {
        string script = Read("remote", "restore.sh");

        Assert.Contains("identity=\"$(cat)\"", script, StringComparison.Ordinal);
        Assert.Contains("dit_refuse_guards --allow umami-recovery interrupted-deploy", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, @"dit_refuse_guards --allow"));
        Assert.Contains("dit_clear_umami_recovery", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -f \"$APP_DIR/.umami-recovery\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-i /dev/stdin", script, StringComparison.Ordinal);
        Assert.DoesNotContain("import-pending", string.Join('\n', script.Split('\n').Where(line => !line.StartsWith('#'))), StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_NeverRenamesDatabasesBackAutomatically()
    {
        string script = Read("remote", "restore.sh");
        string beforeHelp = script[..script.IndexOf("print_interrupted_help() {", StringComparison.Ordinal)];
        string afterHelp = script[script.IndexOf("# --- replace ---", StringComparison.Ordinal)..];

        // Only the forward swap is executed; the way back exists as text for the operator.
        Assert.Equal(2, Regex.Count(afterHelp, @"dit_psql postgres -c ""ALTER DATABASE"));
        Assert.DoesNotContain("ALTER DATABASE", beforeHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployApp_RequiresThePreDeployBackup()
    {
        string script = Read("remote", "deploy-app.sh");

        Assert.DoesNotContain("is not part of this bundle", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[ -f \"$here/backup.sh\" ]", script, StringComparison.Ordinal);
        int backup = script.IndexOf("bash \"$here/backup.sh\" predeploy", StringComparison.Ordinal);
        Assert.True(backup > 0);
        Assert.True(backup < script.IndexOf("phase=active", StringComparison.Ordinal), "the backup must run before anything is activated.");
    }

    [Theory]
    [InlineData("deploy/remote/backup.sh")]
    [InlineData("deploy/remote/restore.sh")]
    [InlineData("deploy/remote/ops-check.sh")]
    [InlineData("deploy/remote/deploy-app.sh")]
    [InlineData("deploy/remote/ensure-databases.sh")]
    public async Task EntryScripts_AreCommittedExecutable(string path)
    {
        // systemd executes backup.sh directly, and a mode lost in packaging would break the timer silently.
        System.Diagnostics.ProcessStartInfo info = new("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ContentWorkspace.RepositoryRoot(),
        };
        info.ArgumentList.Add("ls-files");
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(path);

        string output;
        int exitCode;
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            output = await stdout;
            exitCode = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Assert.Fail($"git is not available, so the file modes cannot be checked ({exception.Message}).");
            return;
        }

        Assert.True(exitCode == 0, $"'git ls-files' failed (exit {exitCode}): the repository root '{ContentWorkspace.RepositoryRoot()}' is not a git checkout, so the file modes cannot be checked.");
        Assert.True(output.Length > 0, $"{path} is not tracked by git (or the root is not a checkout), so its mode cannot be checked.");
        Assert.StartsWith("100755 ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_FailsIntoTheInterruptedStateWhenTheKeyRingIsGoneAfterTheStart()
    {
        string script = Read("remote", "restore.sh");
        int check = script.IndexOf("ring_now=\"$(dit_psql trainer", StringComparison.Ordinal);
        int removal = script.IndexOf("rm -f \"$flag\"", StringComparison.Ordinal);

        // A failing query (the table is gone) or a smaller count must stop the script while the phase is "replacing",
        // so the flag stays and the interrupted-restore help is printed; the removal of the flag comes only after it.
        Assert.True(check > 0 && check < removal, "the key ring check must come before the flag is removed.");
        Assert.DoesNotContain("|| true", script[check..(check + 140)], StringComparison.Ordinal);
        Assert.Contains("if ! dit_count_not_below", script[check..removal], StringComparison.Ordinal);
        Assert.Contains("exit 1", script[check..removal], StringComparison.Ordinal);
        Assert.True(script.IndexOf("phase=replacing", StringComparison.Ordinal) < check);
    }

    [Fact]
    public void Restore_RemovesItsTempFlagWhenWritingFails()
    {
        string script = Read("remote", "restore.sh");
        int write = script.IndexOf("printf '%s\\n%s\\n%s\\n' \"$db\" \"$old\" \"$scratch\" >\"$flag_tmp\"", StringComparison.Ordinal);

        Assert.True(write > 0);
        Assert.StartsWith("if ! ", script[(write - 5)..], StringComparison.Ordinal);
        Assert.Contains("rm -f \"$flag_tmp\"", script[write..(write + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_SwitchesTracingOffBeforeTheIdentityIsReadAndDropsItAfterUse()
    {
        string script = Read("remote", "restore.sh");

        Assert.True(script.IndexOf("{ set +x; } 2>/dev/null", StringComparison.Ordinal) < script.IndexOf("identity=\"$(cat)\"", StringComparison.Ordinal));
        Assert.Contains("identity=\"${identity//$'\\r'/}\"", script, StringComparison.Ordinal);
        Assert.Contains("unset identity", script, StringComparison.Ordinal);
    }
}
