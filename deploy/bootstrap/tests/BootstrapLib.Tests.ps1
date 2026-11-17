#Requires -Version 7.2
# Pester 5. The functions that talk to a server or to GitHub are mocked at the module's native-command wrappers
# (Invoke-Ssh, Invoke-Scp, Invoke-Gh, ...), so no test touches a network, a real gh login or a real ssh key.

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
    $script:Provision = Join-Path $script:RepoRoot 'deploy' 'bootstrap' 'remote' 'provision.sh'
    Import-Module (Join-Path $PSScriptRoot '..' 'BootstrapLib.psm1') -Force
}

Describe 'Test-DomainName' {
    It 'accepts <_>' -ForEach 'trainer.example.com', 'a-b.example.dev', 'x.co' {
        Test-DomainName $_ | Should -BeTrue
    }

    It 'rejects <_>' -ForEach 'https://x.dev', 'x.dev/path', 'localhost', 'x..dev', '-x.dev', 'x.dev:443', 'a b.dev', '', '1.2.3.4' {
        Test-DomainName $_ | Should -BeFalse
    }
}

Describe 'New-RandomSecret' {
    It 'returns 40 alphanumeric characters' {
        New-RandomSecret -Length 40 | Should -Match '^[A-Za-z0-9]{40}$'
    }

    It 'does not repeat itself' {
        (New-RandomSecret -Length 40) | Should -Not -Be (New-RandomSecret -Length 40)
    }

    It 'uses the whole alphabet' {
        $seen = [System.Collections.Generic.HashSet[char]]::new()
        foreach ($c in (New-RandomSecret -Length 4000).ToCharArray()) { [void]$seen.Add($c) }
        $seen.Count | Should -Be 62
    }

    It 'refuses a length below 1' {
        { New-RandomSecret -Length 0 } | Should -Throw
    }
}

Describe 'Get-MissingSecretNames' {
    It 'skips names that exist' {
        Get-MissingSecretNames -Existing @('DB_PASSWORD') | Should -Not -Contain 'DB_PASSWORD'
    }

    It 'lists the four generated secrets when nothing exists' {
        Get-MissingSecretNames -Existing @() | Should -Be @('DB_PASSWORD', 'CONTENT_IMPORT_TOKEN', 'UMAMI_DB_PASSWORD', 'UMAMI_APP_SECRET')
    }

    It 'never lists a secret that is entered by hand' {
        Get-MissingSecretNames -Existing @() | Should -Not -Contain 'TELEGRAM_OIDC_CLIENT_SECRET'
    }
}

Describe 'Get-DeployKeyPlan' {
    It 'keeps an established key on a re-run' {
        Get-DeployKeyPlan $true $true $false | Should -Be 'keep'
    }

    It 'creates when either side is missing' {
        Get-DeployKeyPlan $true $false $false | Should -Be 'create'
        Get-DeployKeyPlan $false $true $false | Should -Be 'create'
    }

    It 'creates when rotation is asked for' {
        Get-DeployKeyPlan $true $true $true | Should -Be 'create'
    }
}

Describe 'Get-ProductionSettingNames' {
    It 'matches render-env.sh' {
        $text = Get-Content -Raw (Join-Path $script:RepoRoot 'deploy' 'scripts' 'render-env.sh')
        $names = foreach ($block in 'required', 'optional') {
            $m = [regex]::Match($text, "(?s)$block=\((.*?)\)")
            $m.Success | Should -BeTrue
            $m.Groups[1].Value -split '\s+' | Where-Object { $_ }
        }
        # APP_IMAGE is supplied by the workflow itself.
        $expected = $names | Where-Object { $_ -ne 'APP_IMAGE' } | Sort-Object

        $actual = Get-ProductionSettingNames
        # DEPLOY_* and SECRETS_ADMIN_TOKEN are read by the workflows, never rendered into the server .env.
        $rendered = @($actual.Secrets) + @($actual.Variables) |
            Where-Object { $_ -notlike 'DEPLOY_*' -and $_ -ne 'SECRETS_ADMIN_TOKEN' } |
            Sort-Object

        $rendered | Should -Be $expected
    }

    It 'lists every name the workflows read from the production environment' {
        $actual = Get-ProductionSettingNames
        $all = @($actual.Secrets) + @($actual.Variables)
        $used = Get-ChildItem (Join-Path $script:RepoRoot '.github' 'workflows') -Filter *.yml |
            ForEach-Object { [regex]::Matches((Get-Content -Raw $_.FullName), '\b(?:secrets|vars)\.([A-Z][A-Z0-9_]+)') } |
            ForEach-Object { $_.Groups[1].Value } |
            Where-Object { $_ -ne 'GITHUB_TOKEN' } |
            Sort-Object -Unique
        $used | Where-Object { $_ -notin $all } | Should -BeNullOrEmpty
    }

    It 'keeps secrets and variables apart' {
        $actual = Get-ProductionSettingNames
        $actual.Secrets | Should -Contain 'DEPLOY_SSH_KEY'
        $actual.Secrets | Should -Contain 'SECRETS_ADMIN_TOKEN'
        $actual.Variables | Should -Contain 'DEPLOY_KNOWN_HOSTS'
        $actual.Variables | Should -Not -Contain 'DB_PASSWORD'
        @($actual.Secrets | Where-Object { $_ -in $actual.Variables }) | Should -BeNullOrEmpty
    }
}

# gh is replaced by a script on PATH that records its arguments and its stdin, so the test sees what a real process would:
# the value must be in the second file and nowhere in the first.
Describe 'Set-GitHubValue' -Skip:$IsWindows {
    BeforeAll {
        $script:FakeDir = Join-Path ([System.IO.Path]::GetTempPath()) ('fake-gh-' + [guid]::NewGuid().ToString('N'))
        $bin = Join-Path $script:FakeDir 'bin'
        New-Item -ItemType Directory $bin | Out-Null
        $gh = Join-Path $bin 'gh'
        [System.IO.File]::WriteAllText($gh, "#!/bin/sh`nprintf '%s\n' `"`$@`" > `"`$GH_FAKE_DIR/args`"`ncat > `"`$GH_FAKE_DIR/stdin`"`n[ -z `"`$GH_FAKE_ERR`" ] || echo `"`$GH_FAKE_ERR`" >&2`nexit `"`${GH_FAKE_EXIT:-0}`"`n")
        & chmod +x $gh
        $script:OldPath = $env:PATH
        $env:PATH = "$bin$([System.IO.Path]::PathSeparator)$env:PATH"
        $env:GH_FAKE_DIR = $script:FakeDir
    }

    AfterAll {
        $env:PATH = $script:OldPath
        Remove-Item Env:GH_FAKE_DIR, Env:GH_FAKE_EXIT -ErrorAction Ignore
        Remove-Item -Recurse -Force $script:FakeDir -ErrorAction Ignore
    }

    BeforeEach {
        Remove-Item (Join-Path $script:FakeDir 'args'), (Join-Path $script:FakeDir 'stdin') -ErrorAction Ignore
        Remove-Item Env:GH_FAKE_EXIT -ErrorAction Ignore
    }

    It 'passes the value on stdin only' {
        Set-GitHubValue secret DB_PASSWORD (ConvertTo-SecureString 's3cr3t' -AsPlainText -Force) 'o/r'

        $argText = (Get-Content -Raw (Join-Path $script:FakeDir 'args'))
        $argText | Should -Not -Match 's3cr3t'
        $argText | Should -Match 'secret\nset\nDB_PASSWORD\n--env\nproduction\n--repo\no/r'
        (Get-Content -Raw (Join-Path $script:FakeDir 'stdin')) | Should -Match 's3cr3t'
    }

    It 'sets a variable through the same stdin path' {
        Set-GitHubValue variable APP_DIR (ConvertTo-SecureString '/srv/x' -AsPlainText -Force) 'o/r'

        (Get-Content -Raw (Join-Path $script:FakeDir 'args')) | Should -Match 'variable\nset\nAPP_DIR'
        (Get-Content -Raw (Join-Path $script:FakeDir 'stdin')) | Should -Match '/srv/x'
    }

    It 'fails without echoing the value' {
        $env:GH_FAKE_EXIT = '1'
        $env:GH_FAKE_ERR = 'HTTP 403: Resource not accessible by personal access token'
        $message = ''
        try { Set-GitHubValue secret DB_PASSWORD (ConvertTo-SecureString 's3cr3t' -AsPlainText -Force) 'o/r' }
        catch { $message = $_.Exception.Message }
        finally { Remove-Item Env:GH_FAKE_ERR -ErrorAction Ignore }

        $message | Should -Match 'DB_PASSWORD'
        $message | Should -Match 'HTTP 403'
        $message | Should -Not -Match 's3cr3t'
    }

    It 'rejects a bad repository or name before running gh' {
        $v = ConvertTo-SecureString 'x' -AsPlainText -Force
        { Set-GitHubValue secret 'bad name' $v 'o/r' } | Should -Throw
        { Set-GitHubValue secret DB_PASSWORD $v 'not-a-repo' } | Should -Throw
        Test-Path (Join-Path $script:FakeDir 'args') | Should -BeFalse
    }
}

Describe 'Test-AgeIdentity' -Skip:(-not ((Get-Command age -ErrorAction Ignore) -and (Get-Command age-keygen -ErrorAction Ignore))) {
    BeforeAll {
        function New-Pair {
            $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("age-test-" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory $dir | Out-Null
            $file = Join-Path $dir 'id.txt'
            $out = age-keygen -o $file 2>&1 | Out-String
            $recipient = [regex]::Match($out, 'age1[a-z0-9]{58}').Value
            $identity = (Get-Content $file | Where-Object { $_ -like 'AGE-SECRET-KEY-*' } | Select-Object -First 1)
            Remove-Item -Recurse -Force $dir
            [pscustomobject]@{ Recipient = $recipient; Identity = (ConvertTo-SecureString $identity -AsPlainText -Force) }
        }
        $script:A = New-Pair
        $script:B = New-Pair
    }

    It 'accepts the identity that belongs to the recipient' {
        Test-AgeIdentity $script:A.Recipient $script:A.Identity | Should -BeTrue
    }

    It 'rejects an identity that does not match the recipient' {
        Test-AgeIdentity $script:A.Recipient $script:B.Identity | Should -BeFalse
    }

    It 'rejects a malformed identity' {
        Test-AgeIdentity $script:A.Recipient (ConvertTo-SecureString 'not an identity' -AsPlainText -Force) | Should -BeFalse
    }
}

Describe 'ConvertTo-KnownHostsLine' {
    It 'asks the admin known_hosts for the host, with the bracketed form for a non-default port' {
        Mock Invoke-SshKeygen {
            $global:keygenArgs = $Arguments
            [pscustomobject]@{ ExitCode = 0; Output = @('# Host [h.example]:2222 found: line 3', '[h.example]:2222 ssh-ed25519 AAAAC3', '') }
        } -ModuleName BootstrapLib

        $line = ConvertTo-KnownHostsLine 'h.example' 2222

        $line | Should -Be '[h.example]:2222 ssh-ed25519 AAAAC3'
        InModuleScope BootstrapLib { $global:keygenArgs -join ' ' } | Should -Be '-F [h.example]:2222'
    }

    It 'throws when the admin never trusted the host' {
        Mock Invoke-SshKeygen { [pscustomobject]@{ ExitCode = 1; Output = @() } } -ModuleName BootstrapLib
        { ConvertTo-KnownHostsLine 'h.example' 22 } | Should -Throw '*known_hosts*'
    }
}

Describe 'provision.sh deploy-key handling' {
    BeforeAll {
        $script:Work = Join-Path ([System.IO.Path]::GetTempPath()) ("prov-test-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $script:Work | Out-Null
        $script:KeyA = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        $script:KeyB = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'

        function Invoke-Prov {
            param([string]$AuthorizedKeys, [string[]]$Arguments)
            $env:DIT_TEST_HOOKS = '1'
            $env:DIT_TEST_AUTHORIZED_KEYS = $AuthorizedKeys
            try {
                $out = & bash $script:Provision @Arguments 2>&1 | Out-String
                [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
            }
            finally {
                Remove-Item Env:DIT_TEST_HOOKS, Env:DIT_TEST_AUTHORIZED_KEYS -ErrorAction Ignore
            }
        }

        function New-Fixture {
            param([string[]]$Lines)
            $f = Join-Path $script:Work ([guid]::NewGuid().ToString('N'))
            [System.IO.File]::WriteAllText($f, (($Lines -join "`n") + "`n"))
            $f
        }

        function Write-Pub { param([string]$Key) $f = Join-Path $script:Work ([guid]::NewGuid().ToString('N') + '.pub'); [System.IO.File]::WriteAllText($f, "$Key ignored-comment`n"); $f }
    }

    AfterAll { Remove-Item -Recurse -Force $script:Work -ErrorAction Ignore }

    It 'keeps only the line with the new key blob even when two keys share a comment' {
        $auth = New-Fixture @(
            '# admin note',
            "restrict $($script:KeyA) dit-deploy-20261116",
            "restrict $($script:KeyB) dit-deploy-20261116",
            'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC someone@laptop')
        $pub = Write-Pub $script:KeyB

        $r = Invoke-Prov $auth @('--remove-deploy-keys-except', $pub)

        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'changed deploy-keys \(removed 1\)'
        $remaining = Get-Content $auth
        $remaining | Should -Contain "restrict $($script:KeyB) dit-deploy-20261116"
        $remaining | Should -Not -Contain "restrict $($script:KeyA) dit-deploy-20261116"
        $remaining | Should -Contain '# admin note'
        ($remaining | Where-Object { $_ -like '*someone@laptop' }) | Should -HaveCount 1
    }

    It 'is idempotent' {
        $auth = New-Fixture @("restrict $($script:KeyA) dit-deploy-20261116")
        $r = Invoke-Prov $auth @('--remove-deploy-keys-except', (Write-Pub $script:KeyA))
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'unchanged deploy-keys'
    }

    It 'refuses to remove everything when the given key is not installed' {
        $auth = New-Fixture @("restrict $($script:KeyA) dit-deploy-20261116")
        $r = Invoke-Prov $auth @('--remove-deploy-keys-except', (Write-Pub $script:KeyB))
        $r.ExitCode | Should -Not -Be 0
        Get-Content $auth | Should -Contain "restrict $($script:KeyA) dit-deploy-20261116"
    }

    It 'does not treat a line without the dit-deploy- prefix as an automation key' {
        $auth = New-Fixture @(
            "restrict $($script:KeyA) dit-deploy-20261116",
            "$($script:KeyB) someone@laptop")
        Invoke-Prov $auth @('--remove-deploy-keys-except', (Write-Pub $script:KeyA)) | Out-Null
        Get-Content $auth | Should -Contain "$($script:KeyB) someone@laptop"
    }

    It 'has-deploy-key says yes by blob and no for another key' {
        $auth = New-Fixture @("restrict $($script:KeyA) dit-deploy-20261116")
        (Invoke-Prov $auth @('--has-deploy-key')).ExitCode | Should -Be 0
        (Invoke-Prov $auth @('--has-deploy-key', (Write-Pub $script:KeyA))).ExitCode | Should -Be 0
        (Invoke-Prov $auth @('--has-deploy-key', (Write-Pub $script:KeyB))).ExitCode | Should -Be 10
    }

    It 'has-deploy-key ignores keys that are not automation keys' {
        $auth = New-Fixture @("$($script:KeyA) someone@laptop")
        (Invoke-Prov $auth @('--has-deploy-key')).ExitCode | Should -Be 10
    }

    It 'add-deploy-key appends with restrict once, even when the blob is already there under another comment' {
        $auth = New-Fixture @("restrict $($script:KeyA) dit-deploy-19990101")
        $r = Invoke-Prov $auth @('--add-deploy-key', (Write-Pub $script:KeyA))
        $r.Output | Should -Match 'unchanged deploy-key'

        $r = Invoke-Prov $auth @('--add-deploy-key', (Write-Pub $script:KeyB))
        $r.Output | Should -Match 'changed deploy-key'
        $lines = Get-Content $auth
        $lines | Should -HaveCount 2
        $lines[1] | Should -Match "^restrict $([regex]::Escape($script:KeyB)) dit-deploy-\d{8}$"
    }

    It 'remove-deploy-key removes exactly the given key material and nothing else' {
        $auth = New-Fixture @(
            "restrict $($script:KeyA) dit-deploy-20261116",
            "restrict $($script:KeyB) dit-deploy-20261116",
            "$($script:KeyA) someone@laptop")
        $r = Invoke-Prov $auth @('--remove-deploy-key', (Write-Pub $script:KeyB))
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'changed deploy-key \(removed 1\)'
        $lines = Get-Content $auth
        $lines | Should -HaveCount 2
        $lines | Should -Contain "restrict $($script:KeyA) dit-deploy-20261116"
        (Invoke-Prov $auth @('--remove-deploy-key', (Write-Pub $script:KeyB))).Output | Should -Match 'unchanged deploy-key'
    }

    It 'has-env follows the .env file of the application directory' {
        $app = Join-Path $script:Work 'app'
        New-Item -ItemType Directory $app -Force | Out-Null
        $env:DIT_TEST_APP_DIR = $app
        try {
            (Invoke-Prov (Join-Path $script:Work 'x') @('--has-env')).ExitCode | Should -Be 10
            [System.IO.File]::WriteAllText((Join-Path $app '.env'), "A='b'`n")
            (Invoke-Prov (Join-Path $script:Work 'x') @('--has-env')).ExitCode | Should -Be 0
        }
        finally { Remove-Item Env:DIT_TEST_APP_DIR -ErrorAction Ignore }
    }

    It 'ignores every test hook unless DIT_TEST_HOOKS=1' {
        # Without the guard the redirected key file would be honoured and, with the root check off, be modified.
        $auth = New-Fixture @("restrict $($script:KeyA) dit-deploy-20261116", "restrict $($script:KeyB) dit-deploy-20261116")
        $env:DIT_TEST_AUTHORIZED_KEYS = $auth
        $env:DIT_TEST_APP_DIR = $script:Work
        try {
            $out = & bash $script:Provision --remove-deploy-keys-except (Write-Pub $script:KeyA) 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            Get-Content $auth | Should -Contain "restrict $($script:KeyB) dit-deploy-20261116"
            & bash $script:Provision --has-env 2>&1 | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
        }
        finally { Remove-Item Env:DIT_TEST_AUTHORIZED_KEYS, Env:DIT_TEST_APP_DIR -ErrorAction Ignore }
    }

    It 'rejects a file that is not a public key' {
        $auth = New-Fixture @('')
        $bad = Join-Path $script:Work 'bad.pub'
        [System.IO.File]::WriteAllText($bad, "not a key`n")
        (Invoke-Prov $auth @('--add-deploy-key', $bad)).ExitCode | Should -Not -Be 0
    }

    It 'rejects an unknown vars key and a bad domain' {
        $vars = Join-Path $script:Work 'vars'
        [System.IO.File]::WriteAllText($vars, "DOMAIN=ok.example.com`nSHELL=evil`n")
        (Invoke-Prov (Join-Path $script:Work 'x') @('--harden', $vars)).ExitCode | Should -Not -Be 0
        [System.IO.File]::WriteAllText($vars, "DOMAIN=x;rm -rf /`n")
        (Invoke-Prov (Join-Path $script:Work 'x') @('--base', $vars)).ExitCode | Should -Not -Be 0
    }
}

Describe 'Invoke-Bootstrap' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bs-tmp-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $script:TempRoot | Out-Null
        $script:OldTmp = $env:TMPDIR
        $script:OldTmpWin = $env:TMP
        $script:OldTempWin = $env:TEMP
        $env:TMPDIR = $script:TempRoot
        $env:TMP = $script:TempRoot
        $env:TEMP = $script:TempRoot

        InModuleScope BootstrapLib {
            $global:SshCalls = [System.Collections.Generic.List[string]]::new()
            $global:ScpCalls = [System.Collections.Generic.List[string]]::new()
            $global:GhCalls = [System.Collections.Generic.List[string]]::new()
            $global:SshFailOn = $null
        }

        Mock Test-Tool { $true } -ModuleName BootstrapLib
        Mock Write-Host {} -ModuleName BootstrapLib
        Mock Write-Warning {} -ModuleName BootstrapLib
        Mock ConvertTo-KnownHostsLine { 'h.example ssh-ed25519 AAAAHOST' } -ModuleName BootstrapLib
        Mock Invoke-Tar {
            Set-Content -Path (Join-Path $WorkingDirectory $Arguments[1]) -Value 'tgz'
            0
        } -ModuleName BootstrapLib

        Mock Invoke-Ssh {
            $text = $Arguments -join ' '
            $global:SshCalls.Add($text)
            if ($global:SshFailOn -and $text -match $global:SshFailOn) { return 255 }
            # Queries: no deploy key and no age recipient on the server yet.
            if ($text -match '--has-deploy-key') { return 10 }
            if ($text -match '--has-age-recipient') { return 10 }
            if ($text -match '--has-env') { return 10 }
            0
        } -ModuleName BootstrapLib
        Mock Invoke-Scp { $global:ScpCalls.Add(($Arguments -join ' ')); 0 } -ModuleName BootstrapLib

        Mock Invoke-SshKeygen {
            $f = $Arguments[$Arguments.IndexOf('-f') + 1]
            Set-Content -Path $f -Value 'PRIVATE-KEY-BODY'
            Set-Content -Path "$f.pub" -Value 'ssh-ed25519 AAAANEWKEY dit-deploy-test'
            [pscustomobject]@{ ExitCode = 0; Output = @() }
        } -ModuleName BootstrapLib

        Mock Invoke-AgeKeygen {
            Set-Content -Path $OutFile -Value @('# created', 'AGE-SECRET-KEY-1FAKEFAKEFAKE')
            [pscustomobject]@{ ExitCode = 0; Recipient = ('age1' + ('q' * 58)) }
        } -ModuleName BootstrapLib

        Mock Test-AgeIdentity { $true } -ModuleName BootstrapLib
        Mock Invoke-SshConfig { [pscustomobject]@{ ExitCode = 0; Output = @('user a', 'hostname h.example', 'port 22') } } -ModuleName BootstrapLib

        Mock Read-Host {
            if ($AsSecureString) { ConvertTo-SecureString 'typed-secret' -AsPlainText -Force } else { 'yes' }
        } -ModuleName BootstrapLib

        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $global:GhCalls.Add($text)
            $out = @()
            if ($text -match '^repo view') { $out = @('o/r') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib

        Mock Set-GitHubValue {
            $global:GhCalls.Add("set $Kind $Name")
        } -ModuleName BootstrapLib

        $script:Params = @{
            HostName         = 'h.example'
            AdminUser        = 'admin'
            Domain           = 'trainer.example.com'
            AdminTelegramIds = @(42)
            LetsEncryptEmail = 'me@example.com'
            Repository       = 'o/r'
            RepositoryRoot   = $script:RepoRoot
        }
    }

    AfterEach {
        $env:TMPDIR = $script:OldTmp
        $env:TMP = $script:OldTmpWin
        $env:TEMP = $script:OldTempWin
        Remove-Item -Recurse -Force $script:TempRoot -ErrorAction Ignore
    }

    It 'removes its temporary directory on success' {
        Invoke-Bootstrap @script:Params
        Get-ChildItem $script:TempRoot | Should -BeNullOrEmpty
    }

    It 'removes its temporary directory on failure' {
        Mock Invoke-Scp { throw 'scp exploded' } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*scp exploded*'
        Get-ChildItem $script:TempRoot | Should -BeNullOrEmpty
    }

    It 'keeps the temporary directory private to the current user' {
        if ($IsWindows) { Set-ItResult -Skipped -Because 'POSIX modes only'; return }
        $dir = New-TemporaryDirectory
        try { (& stat -c %a $dir) | Should -Be '700' }
        finally { Remove-Item -Recurse -Force $dir }
    }

    It 'does not run --harden when the reconnect check fails' {
        InModuleScope BootstrapLib { $global:SshFailOn = '-o PasswordAuthentication=no' }
        { Invoke-Bootstrap @script:Params } | Should -Throw '*reconnect*'
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Not -Match '--harden'
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Match '--base'
    }

    It 'does not run --harden when the new deploy key cannot log in' {
        InModuleScope BootstrapLib { $global:SshFailOn = 'deploy@h\.example' }
        { Invoke-Bootstrap @script:Params } | Should -Throw '*deploy*'
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Not -Match '--harden'
    }

    It 'runs --base, the reconnect check, --harden, and a second reconnect check in that order' {
        Invoke-Bootstrap @script:Params
        $calls = [string[]]@($global:SshCalls)
        $base = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '--base' })
        $harden = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '--harden' })
        $base | Should -BeGreaterOrEqual 0
        $harden | Should -BeGreaterThan $base
        ($calls[($base + 1)..($harden - 1)] | Where-Object { $_ -match 'BatchMode=yes' }) | Should -Not -BeNullOrEmpty
        ($calls[($harden + 1)..($calls.Count - 1)] | Where-Object { $_ -match 'BatchMode=yes' }) | Should -Not -BeNullOrEmpty
    }

    It 'runs the interactive phases with a terminal and the queries without one' {
        Invoke-Bootstrap @script:Params
        $calls = [string[]]@($global:SshCalls)
        ($calls | Where-Object { $_ -match '--base' }) | Should -Match '^-t '
        ($calls | Where-Object { $_ -match '--harden' }) | Should -Match '^-t '
        ($calls | Where-Object { $_ -match '--has-deploy-key' } | Select-Object -First 1) | Should -Not -Match '^-t '
    }

    It 'does not install the age recipient when the pasted identity does not decrypt the challenge' {
        Mock Test-AgeIdentity { $false } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*identity*'
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Not -Match '--install-age-recipient'
        InModuleScope BootstrapLib { $global:ScpCalls -join "`n" } | Should -Not -Match 'age-recipient'
    }

    It 'installs the age recipient once the identity proves itself' {
        Invoke-Bootstrap @script:Params
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Match '--install-age-recipient'
    }

    It 'leaves an installed age recipient alone unless rotation is asked for' {
        Mock Invoke-Ssh {
            $global:SshCalls.Add(($Arguments -join ' '))
            if (($Arguments -join ' ') -match '--has-deploy-key') { return 10 }
            0
        } -ModuleName BootstrapLib
        Invoke-Bootstrap @script:Params
        Should -Invoke Invoke-AgeKeygen -ModuleName BootstrapLib -Times 0

        Invoke-Bootstrap @script:Params -RotateAgeIdentity
        Should -Invoke Invoke-AgeKeygen -ModuleName BootstrapLib -Times 1
    }

    It 'generates secrets only for names that do not exist yet' {
        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $global:GhCalls.Add($text)
            $out = @()
            if ($text -match '^secret list') { $out = @('DB_PASSWORD', 'DEPLOY_SSH_KEY', 'TELEGRAM_OIDC_CLIENT_SECRET', 'BACKUP_S3_ACCESS_KEY_ID', 'BACKUP_S3_SECRET_ACCESS_KEY') }
            if ($text -match '^variable list') { $out = @('DEPLOY_SSH_PUBLIC_KEY', 'DEPLOY_KNOWN_HOSTS', 'BACKUP_S3_PROVIDER', 'BACKUP_S3_ENDPOINT', 'BACKUP_S3_BUCKET') }
            if ($text -match '^variable get DEPLOY_SSH_PUBLIC_KEY') { $out = @('ssh-ed25519 AAAAKEPT dit-deploy-old') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib
        Mock Invoke-Ssh {
            $global:SshCalls.Add(($Arguments -join ' '))
            0
        } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        $sets = InModuleScope BootstrapLib { $global:GhCalls | Where-Object { $_ -like 'set *' } }
        $sets | Should -Not -Contain 'set secret DB_PASSWORD'
        $sets | Should -Contain 'set secret CONTENT_IMPORT_TOKEN'
        $sets | Should -Contain 'set secret UMAMI_DB_PASSWORD'
        $sets | Should -Contain 'set secret UMAMI_APP_SECRET'
        # The stored public key is installed on the server, so the key is kept: no new key pair.
        $sets | Should -Not -Contain 'set secret DEPLOY_SSH_KEY'
        $sets | Should -Not -Contain 'set variable DEPLOY_SSH_PUBLIC_KEY'
        Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 0
        # The host key and the port are refreshed from the admin's own trust on every run.
        $sets | Should -Contain 'set variable DEPLOY_KNOWN_HOSTS'
        $sets | Should -Contain 'set variable DEPLOY_SSH_PORT'
        ([string[]]@($global:SshCalls) -join "`n") | Should -Match '--has-deploy-key /tmp/dit-bootstrap-[0-9a-f]+/github-deploy\.pub'
    }

    It 'restricts the production environment to main before it writes anything' {
        Invoke-Bootstrap @script:Params
        $calls = [string[]]@($global:GhCalls)
        $envIndex = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match 'environments/production' })
        $setIndex = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -like 'set *' })
        $envIndex | Should -BeGreaterOrEqual 0
        $setIndex | Should -BeGreaterThan $envIndex
        ($calls -join "`n") | Should -Match 'deployment-branch-policies'
    }

    It 'writes nothing when the owner declines the list' {
        Mock Read-Host { if ($AsSecureString) { ConvertTo-SecureString 'typed' -AsPlainText -Force } else { 'no' } } -ModuleName BootstrapLib
        Invoke-Bootstrap @script:Params
        InModuleScope BootstrapLib { $global:GhCalls | Where-Object { $_ -like 'set *' } } | Should -BeNullOrEmpty
    }

    It 'removes older deploy keys only after DEPLOY_SSH_KEY is stored' {
        Invoke-Bootstrap @script:Params
        $events = InModuleScope BootstrapLib {
            @($global:GhCalls | Where-Object { $_ -eq 'set secret DEPLOY_SSH_KEY' }) + @($global:SshCalls | Where-Object { $_ -match '--remove-deploy-keys-except' })
        }
        $events | Should -HaveCount 2
        $events[0] | Should -Be 'set secret DEPLOY_SSH_KEY'
    }

    It 'leaves older keys in place when DEPLOY_SSH_KEY could not be stored' {
        Mock Set-GitHubValue { if ($Name -eq 'DEPLOY_SSH_KEY') { throw 'gh failed' } } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*gh failed*'
        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Not -Match '--remove-deploy-keys-except'
    }

    It 'without gh prints the checklist and leaves old keys in place' {
        Mock Test-Tool { $Name -ne 'gh' } -ModuleName BootstrapLib
        InModuleScope BootstrapLib { $global:Printed = [System.Collections.Generic.List[string]]::new() }
        Mock Write-Host { $global:Printed.Add(($Object -join ' ')) } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        InModuleScope BootstrapLib { $global:SshCalls -join "`n" } | Should -Not -Match '--remove-deploy-keys-except'
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 0
        (InModuleScope BootstrapLib { $global:Printed -join "`n" }) | Should -Match 'DB_PASSWORD'
        (InModuleScope BootstrapLib { $global:Printed -join "`n" }) | Should -Match 'signin-telegram'
    }

    It 'prints the BotFather redirect URI' {
        InModuleScope BootstrapLib { $global:Printed = [System.Collections.Generic.List[string]]::new() }
        Mock Write-Host { $global:Printed.Add(($Object -join ' ')) } -ModuleName BootstrapLib
        Invoke-Bootstrap @script:Params
        (InModuleScope BootstrapLib { $global:Printed -join "`n" }) | Should -Match 'https://trainer\.example\.com/signin-telegram'
    }

    It 'never puts a secret into an ssh, scp or gh argument' {
        Invoke-Bootstrap @script:Params
        $all = InModuleScope BootstrapLib { ($global:SshCalls + $global:ScpCalls + $global:GhCalls) -join "`n" }
        $all | Should -Not -Match 'typed-secret'
        $all | Should -Not -Match 'PRIVATE-KEY-BODY'
        $all | Should -Not -Match 'AGE-SECRET-KEY'
    }

    It 'refuses an invalid domain before touching the network' {
        $p = $script:Params.Clone(); $p.Domain = 'https://x.dev'
        { Invoke-Bootstrap @p } | Should -Throw '*domain*'
        Should -Invoke Invoke-Ssh -ModuleName BootstrapLib -Times 0
    }

    It 'refuses a non-positive Telegram id' {
        $p = $script:Params.Clone(); $p.AdminTelegramIds = @(0)
        { Invoke-Bootstrap @p } | Should -Throw '*Telegram*'
    }

    It 'stops when a required tool is missing' {
        Mock Test-Tool { $Name -ne 'age-keygen' } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*age-keygen*'
        Should -Invoke Invoke-Ssh -ModuleName BootstrapLib -Times 0
    }

    It 'rotates a stateless secret without touching the server' {
        Invoke-Bootstrap @script:Params -RotateSecret CONTENT_IMPORT_TOKEN
        Should -Invoke Invoke-Ssh -ModuleName BootstrapLib -Times 0
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter { $Name -eq 'CONTENT_IMPORT_TOKEN' }
    }

    It 'refuses to rotate a database password and points to the workflow' {
        { Invoke-Bootstrap @script:Params -RotateSecret DB_PASSWORD } | Should -Throw '*rotate-db-password.yml*'
        { Invoke-Bootstrap @script:Params -RotateSecret UMAMI_DB_PASSWORD } | Should -Throw '*rotate-db-password.yml*'
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 0
    }

    It 'refuses a secret that is not stateless' {
        { Invoke-Bootstrap @script:Params -RotateSecret DEPLOY_SSH_KEY } | Should -Throw
    }

    It 'rotates the deploy key: new key proven, stored, then older keys removed' {
        Mock Invoke-Ssh {
            $text = $Arguments -join ' '
            $global:SshCalls.Add($text)
            if ($text -match '--has-deploy-key') { return 0 }
            0
        } -ModuleName BootstrapLib
        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $global:GhCalls.Add($text)
            $out = @()
            if ($text -match '^secret list') { $out = @('DEPLOY_SSH_KEY', 'DB_PASSWORD', 'CONTENT_IMPORT_TOKEN', 'UMAMI_DB_PASSWORD', 'UMAMI_APP_SECRET') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params -RotateDeployKey

        $ssh = [string[]]@($global:SshCalls)
        $add = [array]::FindIndex($ssh, [Predicate[string]] { param($c) $c -match '--add-deploy-key' })
        $prove = [array]::FindIndex($ssh, [Predicate[string]] { param($c) $c -match '-i .*deploy@h\.example' })
        $remove = [array]::FindIndex($ssh, [Predicate[string]] { param($c) $c -match '--remove-deploy-keys-except' })
        $add | Should -BeGreaterOrEqual 0
        $prove | Should -BeGreaterThan $add
        $remove | Should -BeGreaterThan $prove
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter { $Name -eq 'DEPLOY_SSH_KEY' }
    }

    It 'stores a moved SSH port and the matching known_hosts entry for the deploy workflows' {
        Invoke-Bootstrap @script:Params -SshPort 2222

        Should -Invoke ConvertTo-KnownHostsLine -ModuleName BootstrapLib -ParameterFilter { $Port -eq 2222 }
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter {
            $Kind -eq 'variable' -and $Name -eq 'DEPLOY_SSH_PORT' -and [System.Net.NetworkCredential]::new('', $Value).Password -eq '2222'
        }
        ([string[]]@($global:SshCalls) | Where-Object { $_ -match '--base' }) | Should -Match '-p 2222 '
    }

    It 'checks every reconnect with a fresh connection, ignoring multiplexing, with a timeout' {
        Invoke-Bootstrap @script:Params
        $checks = [string[]]@($global:SshCalls | Where-Object { $_ -match 'BatchMode=yes' })
        # Admin and deploy key, before and after hardening.
        $checks | Should -HaveCount 4
        foreach ($c in $checks) {
            $c | Should -Match '-o ControlMaster=no'
            $c | Should -Match '-o ControlPath=none'
            $c | Should -Match '-o ConnectTimeout=15'
        }
    }

    It 'withdraws the key it just installed when the owner declines the GitHub write' {
        Mock Read-Host { if ($AsSecureString) { ConvertTo-SecureString 'typed' -AsPlainText -Force } else { 'no' } } -ModuleName BootstrapLib
        InModuleScope BootstrapLib { $global:Printed = [System.Collections.Generic.List[string]]::new() }
        Mock Write-Host { $global:Printed.Add(($Object -join ' ')) } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        ([string[]]@($global:SshCalls) -join "`n") | Should -Match '--remove-deploy-key /tmp/dit-bootstrap-[0-9a-f]+/deploy_key\.pub'
        $summary = InModuleScope BootstrapLib { $global:Printed -join "`n" }
        $summary | Should -Match 'Deploy key: discarded'
        $summary | Should -Not -Match 'Deploy key: create'
    }

    It 'does not touch server keys on a declined write when no key was created' {
        Mock Invoke-Ssh {
            $global:SshCalls.Add(($Arguments -join ' '))
            if (($Arguments -join ' ') -match '--has-env') { return 10 }
            0
        } -ModuleName BootstrapLib
        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $out = @()
            if ($text -match '^secret list') { $out = @('DEPLOY_SSH_KEY') }
            if ($text -match '^variable list') { $out = @('DEPLOY_SSH_PUBLIC_KEY') }
            if ($text -match '^variable get DEPLOY_SSH_PUBLIC_KEY') { $out = @('ssh-ed25519 AAAAKEPT dit-deploy-old') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib
        Mock Read-Host { if ($AsSecureString) { ConvertTo-SecureString 'typed' -AsPlainText -Force } else { 'no' } } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        ([string[]]@($global:SshCalls) -join "`n") | Should -Not -Match '--remove-deploy-key'
    }

    It 'uses the resolved host name for DEPLOY_HOST and the known_hosts lookup when given an ssh alias' {
        Mock Invoke-SshConfig { [pscustomobject]@{ ExitCode = 0; Output = @('user a', 'hostname real.example', 'port 22') } } -ModuleName BootstrapLib
        $p = $script:Params.Clone(); $p.HostName = 'myvps'

        Invoke-Bootstrap @p

        Should -Invoke ConvertTo-KnownHostsLine -ModuleName BootstrapLib -ParameterFilter { $HostName -eq 'real.example' }
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter {
            $Name -eq 'DEPLOY_HOST' -and [System.Net.NetworkCredential]::new('', $Value).Password -eq 'real.example'
        }
        # The connection itself still goes through the alias, so its ssh config applies.
        ([string[]]@($global:SshCalls) | Where-Object { $_ -match '--base' }) | Should -Match 'admin@myvps '
    }

    It 'stops before phase 1 when the host has no entry in known_hosts' {
        Mock ConvertTo-KnownHostsLine { throw 'No entry for h.example in your known_hosts.' } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*known_hosts*'
        ([string[]]@($global:SshCalls) -join "`n") | Should -Not -Match '--base'
    }

    Context 'branch policies of the production environment' {
        BeforeEach {
            Mock Invoke-Gh {
                $text = $Arguments -join ' '
                $global:GhCalls.Add($text)
                $out = @()
                if ($text -eq 'api repos/o/r/environments/production') { $out = @('{"deployment_branch_policy":{"custom_branch_policies":true}}') }
                if ($text -eq 'api repos/o/r/environments/production/deployment-branch-policies') { $out = @($global:PoliciesJson) }
                [pscustomobject]@{ ExitCode = 0; Output = $out }
            } -ModuleName BootstrapLib
        }

        AfterEach { Remove-Variable PoliciesJson -Scope Global -ErrorAction Ignore }

        It 'keeps only the branch policy named main, compared by exact name and type' {
            $global:PoliciesJson = '{"branch_policies":[{"id":1,"name":"Main","type":"branch"},{"id":2,"name":"main","type":"tag"},{"id":3,"name":"main","type":"branch"}]}'
            Invoke-Bootstrap @script:Params
            $gh = [string[]]@($global:GhCalls) -join "`n"
            $gh | Should -Match 'DELETE repos/o/r/environments/production/deployment-branch-policies/1'
            $gh | Should -Match 'DELETE repos/o/r/environments/production/deployment-branch-policies/2'
            $gh | Should -Not -Match 'deployment-branch-policies/3'
            $gh | Should -Not -Match 'POST'
        }

        It 'adds the main branch policy when only a tag policy of that name exists' {
            $global:PoliciesJson = '{"branch_policies":[{"id":9,"name":"main","type":"tag"}]}'
            Invoke-Bootstrap @script:Params
            $gh = [string[]]@($global:GhCalls) -join "`n"
            $gh | Should -Match 'DELETE repos/o/r/environments/production/deployment-branch-policies/9'
            $gh | Should -Match 'POST repos/o/r/environments/production/deployment-branch-policies -f name=main -f type=branch'
        }
    }

    Context 'without gh' {
        BeforeEach {
            Mock Test-Tool { $Name -ne 'gh' } -ModuleName BootstrapLib
            InModuleScope BootstrapLib { $global:Printed = [System.Collections.Generic.List[string]]::new() }
            Mock Write-Host { $global:Printed.Add(($Object -join ' ')) } -ModuleName BootstrapLib
        }

        It 'on a server that was deployed before generates no secrets and keeps the installed deploy key' {
            Mock Invoke-Ssh {
                $global:SshCalls.Add(($Arguments -join ' '))
                0
            } -ModuleName BootstrapLib

            Invoke-Bootstrap @script:Params

            $printed = InModuleScope BootstrapLib { $global:Printed -join "`n" }
            $printed | Should -Not -Match 'Secret\s+DB_PASSWORD'
            $printed | Should -Not -Match 'Secret\s+UMAMI_DB_PASSWORD'
            $printed | Should -Not -Match 'Secret\s+CONTENT_IMPORT_TOKEN'
            $printed | Should -Match 'were not generated'
            $printed | Should -Match 'Deploy key: keep'
            Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 0
            ([string[]]@($global:SshCalls) -join "`n") | Should -Not -Match '--add-deploy-key|--remove-deploy-key'
        }

        It 'on a fresh server generates the secrets and a deploy key and says what to set' {
            Invoke-Bootstrap @script:Params

            $printed = InModuleScope BootstrapLib { $global:Printed -join "`n" }
            $printed | Should -Match 'Secret\s+DB_PASSWORD'
            $printed | Should -Match 'Secret\s+DEPLOY_SSH_KEY'
            $printed | Should -Match 'Variable DEPLOY_SSH_PUBLIC_KEY'
            $printed | Should -Match 'Variable DEPLOY_SSH_PORT = 22'
            Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 1
        }

        It 'rotates the key of a deployed server only when asked' {
            Mock Invoke-Ssh {
                $global:SshCalls.Add(($Arguments -join ' '))
                0
            } -ModuleName BootstrapLib

            Invoke-Bootstrap @script:Params -RotateDeployKey

            Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 1
            ([string[]]@($global:SshCalls) -join "`n") | Should -Match '--add-deploy-key'
        }
    }

    It 'creates a new key when the stored public key is not installed on the server' {
        Mock Invoke-Ssh {
            $text = $Arguments -join ' '
            $global:SshCalls.Add($text)
            if ($text -match '--has-deploy-key .*github-deploy') { return 10 }
            if ($text -match '--has-deploy-key') { return 0 }
            0
        } -ModuleName BootstrapLib
        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $global:GhCalls.Add($text)
            $out = @()
            if ($text -match '^secret list') { $out = @('DEPLOY_SSH_KEY') }
            if ($text -match '^variable list') { $out = @('DEPLOY_SSH_PUBLIC_KEY') }
            if ($text -match '^variable get DEPLOY_SSH_PUBLIC_KEY') { $out = @('ssh-ed25519 AAAAGONE dit-deploy-old') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 1
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter { $Name -eq 'DEPLOY_SSH_KEY' }
        Should -Invoke Set-GitHubValue -ModuleName BootstrapLib -Times 1 -ParameterFilter { $Name -eq 'DEPLOY_SSH_PUBLIC_KEY' }
    }

    It 'rejects the host name <_> before any network call' -ForEach '-oProxyCommand=x', '-v', 'h ost', "h`tost", "h`nost", 'h;ost', '' {
        $p = $script:Params.Clone(); $p.HostName = $_
        { Invoke-Bootstrap @p } | Should -Throw
        Should -Invoke Invoke-Ssh -ModuleName BootstrapLib -Times 0
        Should -Invoke Invoke-SshConfig -ModuleName BootstrapLib -Times 0
    }

    It 'rejects the admin user <_> before any network call' -ForEach '-x', 'a b', 'A', '' {
        $p = $script:Params.Clone(); $p.AdminUser = $_
        { Invoke-Bootstrap @p } | Should -Throw
        Should -Invoke Invoke-Ssh -ModuleName BootstrapLib -Times 0
    }

    It 'ends the options before the destination of every ssh and scp call' {
        Invoke-Bootstrap @script:Params
        foreach ($c in [string[]]@($global:SshCalls)) { $c | Should -Match ' -- (admin|deploy)@' }
        foreach ($c in [string[]]@($global:ScpCalls)) { $c | Should -Match ' -- ' }
    }

    It 'withdraws the new deploy key when the run fails after phase 1' {
        Mock Test-AgeIdentity { $false } -ModuleName BootstrapLib
        { Invoke-Bootstrap @script:Params } | Should -Throw '*identity*'
        $calls = [string[]]@($global:SshCalls)
        $remove = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '--remove-deploy-key /tmp/dit-bootstrap-[0-9a-f]+/deploy_key\.pub' })
        $cleanup = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match 'rm -rf /tmp/dit-bootstrap-' })
        $remove | Should -BeGreaterOrEqual 0
        $cleanup | Should -BeGreaterThan $remove
    }

    It 'does not withdraw a deploy key that was handed over' {
        Invoke-Bootstrap @script:Params
        ([string[]]@($global:SshCalls) -join "`n") | Should -Not -Match '--remove-deploy-key /'
    }

    It 'does not withdraw anything when no key was created' {
        Mock Invoke-Ssh {
            $global:SshCalls.Add(($Arguments -join ' '))
            if (($Arguments -join ' ') -match '--harden') { return 255 }
            if (($Arguments -join ' ') -match '--has-age-recipient') { return 0 }
            if (($Arguments -join ' ') -match '--has-env') { return 10 }
            0
        } -ModuleName BootstrapLib
        Mock Invoke-Gh {
            $text = $Arguments -join ' '
            $out = @()
            if ($text -match '^secret list') { $out = @('DEPLOY_SSH_KEY') }
            if ($text -match '^variable list') { $out = @('DEPLOY_SSH_PUBLIC_KEY') }
            if ($text -match '^variable get DEPLOY_SSH_PUBLIC_KEY') { $out = @('ssh-ed25519 AAAAKEPT dit-deploy-old') }
            [pscustomobject]@{ ExitCode = 0; Output = $out }
        } -ModuleName BootstrapLib

        { Invoke-Bootstrap @script:Params } | Should -Throw
        ([string[]]@($global:SshCalls) -join "`n") | Should -Not -Match '--remove-deploy-key /'
    }

    Context 'without gh, on a server that has a key but was never deployed' {
        BeforeEach {
            Mock Test-Tool { $Name -ne 'gh' } -ModuleName BootstrapLib
            Mock Invoke-Ssh {
                $text = $Arguments -join ' '
                $global:SshCalls.Add($text)
                if ($text -match '--has-env') { return 10 }
                0
            } -ModuleName BootstrapLib
        }

        It 'plans a new key and replaces the stale one once the new one is proven' {
            Invoke-Bootstrap @script:Params

            Should -Invoke Invoke-SshKeygen -ModuleName BootstrapLib -Times 1
            $calls = [string[]]@($global:SshCalls)
            $prove = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '-i .*deploy@h\.example' })
            $remove = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '--remove-deploy-keys-except' })
            $prove | Should -BeGreaterOrEqual 0
            $remove | Should -BeGreaterThan $prove
        }
    }

    It 'says plainly that DEPLOY_SSH_KEY must already be stored when the server key is kept without gh' {
        Mock Test-Tool { $Name -ne 'gh' } -ModuleName BootstrapLib
        InModuleScope BootstrapLib { $global:Printed = [System.Collections.Generic.List[string]]::new() }
        Mock Write-Host { $global:Printed.Add(($Object -join ' ')) } -ModuleName BootstrapLib
        Mock Invoke-Ssh {
            $global:SshCalls.Add(($Arguments -join ' '))
            0
        } -ModuleName BootstrapLib

        Invoke-Bootstrap @script:Params

        $printed = InModuleScope BootstrapLib { $global:Printed -join "`n" }
        $printed | Should -Match 'DEPLOY_SSH_KEY must already be stored'
        $printed | Should -Match '-RotateDeployKey'
        $printed | Should -Not -Match 'kept \(keep\)'
    }
}
