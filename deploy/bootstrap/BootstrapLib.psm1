#Requires -Version 7.2
# Logic of Bootstrap.ps1. Every native tool is called through a small wrapper (Invoke-Ssh, Invoke-Scp, Invoke-Gh, ...)
# so the Pester tests replace the wrapper and never reach a network, a real gh login or a real key.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SecretAlphabet = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$script:EnvironmentName = 'production'
$script:DefaultAppDir = '/srv/dev-interview-trainer'
$script:FreshConnection = @('-o', 'ControlMaster=no', '-o', 'ControlPath=none', '-o', 'ConnectTimeout=15')
$script:StatelessSecrets = @('DB_PASSWORD', 'CONTENT_IMPORT_TOKEN', 'UMAMI_DB_PASSWORD', 'UMAMI_APP_SECRET')

#region Pure helpers

function Test-DomainName {
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowEmptyString()][string]$Name)

    if ([string]::IsNullOrEmpty($Name) -or $Name.Length -gt 253) { return $false }
    # At least two labels and an alphabetic top-level domain: that also rules out URLs, ports, paths and bare IPv4 addresses.
    return [bool]($Name -cmatch '^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$')
}

function New-RandomSecret {
    [CmdletBinding()]
    [OutputType([string])]
    param([int]$Length = 40)

    if ($Length -lt 1) { throw 'The secret length must be at least 1.' }
    $chars = [char[]]::new($Length)
    for ($i = 0; $i -lt $Length; $i++) {
        # GetInt32 rejects values from the biased tail of the generator range, so there is no modulo bias.
        $chars[$i] = $script:SecretAlphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($script:SecretAlphabet.Length)]
    }
    return [string]::new($chars)
}

function Get-ProductionSettingNames {
    [CmdletBinding()]
    param()

    # The names the workflows read from the production environment (deploy-app.yml, deploy-content.yml, ops-check.yml,
    # rotate-db-password.yml) and render-env.sh renders into the server .env. DEPLOY_KNOWN_HOSTS is a variable, as the
    # ssh-setup action reads it through vars.*: it is a public host key, not a secret.
    return @{
        Secrets   = @(
            'TELEGRAM_OIDC_CLIENT_SECRET', 'DB_PASSWORD', 'CONTENT_IMPORT_TOKEN', 'DEPLOY_SSH_KEY',
            'UMAMI_DB_PASSWORD', 'UMAMI_APP_SECRET', 'BACKUP_S3_ACCESS_KEY_ID', 'BACKUP_S3_SECRET_ACCESS_KEY',
            'SECRETS_ADMIN_TOKEN')
        Variables = @(
            'TELEGRAM_OIDC_CLIENT_ID', 'ADMIN_TELEGRAM_IDS', 'PUBLIC_BASE_URL', 'DEPLOY_HOST', 'DEPLOY_USER',
            'DEPLOY_SSH_PORT', 'DEPLOY_SSH_PUBLIC_KEY', 'DEPLOY_KNOWN_HOSTS', 'APP_DIR', 'UMAMI_WEBSITE_ID', 'BACKUP_S3_PROVIDER', 'BACKUP_S3_ENDPOINT',
            'BACKUP_S3_BUCKET', 'RATE_LIMIT_WRITES_PER_MINUTE', 'RATE_LIMIT_READS_PER_MINUTE',
            'RATE_LIMIT_SEARCH_PER_MINUTE', 'FEEDBACK_REPORTS_PER_DAY')
    }
}

function Get-MissingSecretNames {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([AllowEmptyCollection()][string[]]$Existing = @())

    # Only names that do not exist are generated: a new DB_PASSWORD on a live database would lock the application out.
    return [string[]]@($script:StatelessSecrets | Where-Object { $_ -notin $Existing })
}

function Get-DeployKeyPlan {
    [CmdletBinding()]
    [OutputType([string])]
    param([bool]$ServerHasKey, [bool]$GitHubHasKey, [bool]$Rotate)

    if ($ServerHasKey -and $GitHubHasKey -and -not $Rotate) { return 'keep' }
    return 'create'
}

#endregion

#region Native tool wrappers

function Test-Tool {
    [CmdletBinding()]
    [OutputType([bool])]
    param([string]$Name)

    return [bool](Get-Command $Name -ErrorAction Ignore)
}

# Arguments go through ProcessStartInfo.ArgumentList, so nothing is re-quoted (an empty "-N ''" survives on Windows).
# Without -Capture the child inherits the console: an interactive sudo prompt is visible and nothing is buffered.
function Invoke-Native {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$InputText,
        [switch]$Capture,
        [string]$WorkingDirectory
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $hasInput = $PSBoundParameters.ContainsKey('InputText')
    if ($hasInput) {
        $psi.RedirectStandardInput = $true
        $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    }
    if ($Capture) {
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
    }
    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        if ($hasInput) {
            $process.StandardInput.Write($InputText)
            $process.StandardInput.Close()
        }
        $lines = @()
        if ($Capture) {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $lines = @(($stdout.Result + "`n" + $stderr.Result) -split "`r?`n")
        }
        else {
            $process.WaitForExit()
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $lines }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-Ssh { param([string[]]$Arguments, [string]$WorkingDirectory) (Invoke-Native -FilePath ssh -Arguments $Arguments -WorkingDirectory $WorkingDirectory).ExitCode }
function Invoke-Scp { param([string[]]$Arguments, [string]$WorkingDirectory) (Invoke-Native -FilePath scp -Arguments $Arguments -WorkingDirectory $WorkingDirectory).ExitCode }
function Invoke-Tar { param([string[]]$Arguments, [string]$WorkingDirectory) (Invoke-Native -FilePath tar -Arguments $Arguments -WorkingDirectory $WorkingDirectory).ExitCode }
function Invoke-SshKeygen { param([string[]]$Arguments) Invoke-Native -FilePath ssh-keygen -Arguments $Arguments -Capture }

function Invoke-SshConfig { param([string[]]$Arguments) Invoke-Native -FilePath ssh -Arguments (@('-G', '--') + $Arguments) -Capture }

function Invoke-Gh {
    param([string[]]$Arguments, [string]$InputText)
    if ($PSBoundParameters.ContainsKey('InputText')) {
        return Invoke-Native -FilePath gh -Arguments $Arguments -InputText $InputText -Capture
    }
    return Invoke-Native -FilePath gh -Arguments $Arguments -Capture
}

function Invoke-AgeKeygen {
    param([Parameter(Mandatory)][string]$OutFile)
    $r = Invoke-Native -FilePath age-keygen -Arguments @('-o', $OutFile) -Capture
    $recipient = [regex]::Match(($r.Output -join "`n"), 'age1[a-z0-9]{58}').Value
    return [pscustomobject]@{ ExitCode = $r.ExitCode; Recipient = $recipient }
}

#endregion

#region Exported building blocks

function New-TemporaryDirectory {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('dit-bootstrap-' + [guid]::NewGuid().ToString('N'))
    if ($IsWindows) {
        [void][System.IO.Directory]::CreateDirectory($path)
        $acl = [System.Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                $user, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        Set-Acl -Path $path -AclObject $acl
    }
    else {
        # Created with the mode, never created open and tightened afterwards.
        [void][System.IO.Directory]::CreateDirectory($path, [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute')
    }
    return $path
}

function Set-GitHubValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('secret', 'variable')][string]$Kind,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][securestring]$Value,
        [Parameter(Mandatory)][string]$Repository
    )

    if ($Name -cnotmatch '^[A-Z][A-Z0-9_]*$') { throw "Invalid setting name '$Name'." }
    if ($Repository -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') { throw "Invalid repository '$Repository'; expected owner/name." }

    $ghArgs = @($Kind, 'set', $Name, '--env', $script:EnvironmentName, '--repo', $Repository)
    # The value reaches gh on stdin only; an argument would show up in the process list.
    $plain = [System.Net.NetworkCredential]::new('', $Value).Password
    # gh's own messages never contain the value, so they can name the reason (permissions, repository) in the error.
    $output = ($plain | & gh @ghArgs 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "gh $Kind set $Name failed with exit code $LASTEXITCODE. $output" }
}

function ConvertTo-KnownHostsLine {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$HostName, [int]$Port = 22)

    # The admin's own known_hosts holds the key that person already accepted on first connect; ssh-keyscan would only
    # report whatever answers on the network right now.
    $pattern = if ($Port -eq 22) { $HostName } else { "[$HostName]:$Port" }
    $result = Invoke-SshKeygen @('-F', $pattern)
    $lines = @($result.Output | Where-Object { $_ -and $_ -notmatch '^\s*#' })
    if ($result.ExitCode -ne 0 -or $lines.Count -eq 0) {
        throw "No entry for $pattern in your known_hosts. Connect to the server once with ssh and accept its host key, then re-run."
    }
    return ($lines -join "`n")
}

function Test-AgeIdentity {
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$Recipient, [Parameter(Mandatory)][securestring]$Identity)

    # age reads the identity from a file, so it lives for the length of one round trip in a private directory.
    $dir = New-TemporaryDirectory
    try {
        $challenge = New-RandomSecret -Length 32
        $cipher = Join-Path $dir 'challenge.age'
        $identityFile = Join-Path $dir 'identity.txt'
        [System.IO.File]::WriteAllText($identityFile, [System.Net.NetworkCredential]::new('', $Identity).Password + "`n")
        $encrypt = Invoke-Native -FilePath age -Arguments @('-r', $Recipient, '-o', $cipher) -InputText $challenge -Capture
        if ($encrypt.ExitCode -ne 0) { return $false }
        $decrypt = Invoke-Native -FilePath age -Arguments @('-d', '-i', $identityFile, $cipher) -Capture
        if ($decrypt.ExitCode -ne 0) { return $false }
        return (($decrypt.Output -join "`n").Trim() -ceq $challenge)
    }
    finally {
        Remove-Item -Recurse -Force $dir -ErrorAction Ignore
    }
}

#endregion

#region Bootstrap steps

function Assert-BootstrapInputs {
    param([string]$Domain, [long[]]$AdminTelegramIds, [string]$LetsEncryptEmail, [int]$SshPort, [string]$AdminUser, [string]$HostName)

    if (-not (Test-DomainName $Domain)) { throw "'$Domain' is not a valid domain name (no scheme, port or path)." }
    if (-not $AdminTelegramIds -or @($AdminTelegramIds | Where-Object { $_ -le 0 }).Count -gt 0) { throw 'Every admin Telegram id must be a positive number.' }
    if ($LetsEncryptEmail -notmatch '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$') { throw "'$LetsEncryptEmail' is not a valid e-mail address." }
    if ($SshPort -lt 1 -or $SshPort -gt 65535) { throw 'The SSH port must be between 1 and 65535.' }
    if ($AdminUser -cnotmatch '^[a-z_][a-z0-9_-]{0,31}$') { throw "'$AdminUser' is not a valid Unix user name." }
    # A leading dash would reach ssh as an option (-oProxyCommand=...), so the first character must be alphanumeric.
    if ($HostName -notmatch '^[A-Za-z0-9][A-Za-z0-9.:-]*$') { throw "'$HostName' is not a valid host name or address." }
}

function Assert-Tools {
    param([bool]$NeedsServer)

    $missing = @('ssh', 'scp', 'ssh-keygen', 'tar', 'age', 'age-keygen') | Where-Object { -not (Test-Tool $_) }
    if ($NeedsServer -and $missing) {
        throw "Missing tools: $($missing -join ', '). Install the OpenSSH client and age (winget install FiloSottile.age, brew install age, or apt install age)."
    }
}

# What ssh itself would connect to. A ~/.ssh/config alias is fine for the connection, but DEPLOY_HOST and the known_hosts
# entry must name the real host: the runner knows nothing of the alias.
function Resolve-SshTarget {
    param([string]$HostName)

    $r = Invoke-SshConfig @($HostName)
    if ($r.ExitCode -ne 0) { throw "ssh -G $HostName failed: $($r.Output -join ' ')" }
    $resolvedHost = ($r.Output | ForEach-Object { if ($_ -match '^hostname\s+(\S+)\s*$') { $Matches[1] } } | Select-Object -First 1)
    $resolvedPort = ($r.Output | ForEach-Object { if ($_ -match '^port\s+(\d+)\s*$') { [int]$Matches[1] } } | Select-Object -First 1)
    if (-not $resolvedHost) { throw "ssh -G $HostName reported no host name." }
    return [pscustomobject]@{ HostName = $resolvedHost; Port = $(if ($resolvedPort) { $resolvedPort } else { 22 }) }
}

# provision.sh answers a query with 0 (yes) or 10 (no); anything else is a failure, never a "no".
function ConvertTo-QueryAnswer {
    param([int]$ExitCode, [string]$Query)

    switch ($ExitCode) {
        0 { return $true }
        10 { return $false }
        default { throw "provision.sh $Query failed with exit code $ExitCode." }
    }
}

function Get-GitHubNames {
    param([string]$Kind, [string]$Repository)

    $r = Invoke-Gh @($Kind, 'list', '--env', $script:EnvironmentName, '--repo', $Repository, '--json', 'name', '--jq', '.[].name')
    if ($r.ExitCode -ne 0) { throw "gh $Kind list failed: $($r.Output -join ' ')" }
    return [string[]]@($r.Output | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# Creates the environment with a custom branch policy and leaves only "main" in it. An environment that already has custom
# policies is not rewritten, so reviewers or timers someone added by hand are not reset.
function Initialize-ProductionEnvironment {
    param([string]$Repository)

    $base = "repos/$Repository/environments/$($script:EnvironmentName)"
    $current = Invoke-Gh @('api', $base)
    $custom = $false
    if ($current.ExitCode -eq 0 -and $current.Output) {
        try { $custom = [bool](($current.Output -join "`n") | ConvertFrom-Json).deployment_branch_policy.custom_branch_policies } catch { $custom = $false }
    }
    if (-not $custom) {
        $body = '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
        $put = Invoke-Gh @('api', '-X', 'PUT', $base, '--input', '-') -InputText $body
        if ($put.ExitCode -ne 0) { throw "Creating the environment failed: $($put.Output -join ' ')" }
    }

    $list = Invoke-Gh @('api', "$base/deployment-branch-policies")
    $policies = @()
    if ($list.ExitCode -eq 0 -and $list.Output) {
        try { $policies = @((($list.Output -join "`n") | ConvertFrom-Json).branch_policies) } catch { $policies = @() }
    }
    # Exactly a branch policy named main stays; a policy named Main, or a tag policy named main, would not admit main.
    $isMain = { param($p) $p.name -ceq 'main' -and (-not $p.PSObject.Properties['type'] -or $p.type -eq 'branch') }
    foreach ($p in $policies) {
        if (-not (& $isMain $p)) {
            $del = Invoke-Gh @('api', '-X', 'DELETE', "$base/deployment-branch-policies/$($p.id)")
            if ($del.ExitCode -ne 0) { throw "Removing the branch policy '$($p.name)' failed." }
        }
    }
    if (-not ($policies | Where-Object { & $isMain $_ })) {
        $add = Invoke-Gh @('api', '-X', 'POST', "$base/deployment-branch-policies", '-f', 'name=main', '-f', 'type=branch')
        if ($add.ExitCode -ne 0) { throw "Adding the branch policy 'main' failed: $($add.Output -join ' ')" }
    }
}

# Text files are rewritten with LF so a checkout with CRLF cannot break the scripts on the server.
function Build-HostAssets {
    param([string]$RepositoryRoot, [string]$TempDirectory)

    $stage = Join-Path $TempDirectory 'assets'
    [void](New-Item -ItemType Directory -Path (Join-Path $stage 'nginx'), (Join-Path $stage 'systemd') -Force)
    $copy = {
        param([string]$From, [string]$To)
        $text = [System.IO.File]::ReadAllText($From).Replace("`r`n", "`n")
        [System.IO.File]::WriteAllText($To, $text, [System.Text.UTF8Encoding]::new($false))
    }
    & $copy (Join-Path $RepositoryRoot 'deploy' 'bootstrap' 'remote' 'provision.sh') (Join-Path $stage 'provision.sh')
    $nginx = @(Get-ChildItem (Join-Path $RepositoryRoot 'deploy' 'nginx') -Filter '*.template')
    $systemd = @(Get-ChildItem (Join-Path $RepositoryRoot 'deploy' 'systemd') -File)
    if ($nginx.Count -eq 0 -or $systemd.Count -eq 0) { throw 'deploy/nginx or deploy/systemd holds no assets to upload.' }
    foreach ($f in $nginx) { & $copy $f.FullName (Join-Path $stage 'nginx' $f.Name) }
    foreach ($f in $systemd) { & $copy $f.FullName (Join-Path $stage 'systemd' $f.Name) }

    # Relative names and a working directory: GNU tar reads "C:\..." as a remote host.
    $code = Invoke-Tar -Arguments @('-czf', 'host-assets.tgz', '-C', 'assets', '.') -WorkingDirectory $TempDirectory
    if ($code -ne 0) { throw "tar failed with exit code $code." }
}

function Invoke-Remote {
    param($Context, [string]$Command, [switch]$Tty, [string[]]$Options = @())

    $sshArgs = @()
    if ($Tty) { $sshArgs += '-t' }
    $sshArgs += $Options
    $sshArgs += @('-p', [string]$Context.Port, '--', "$($Context.AdminUser)@$($Context.HostName)", $Command)
    return Invoke-Ssh -Arguments $sshArgs
}

function Send-ToServer {
    param($Context, [string[]]$FileNames)

    $scpArgs = @('-q', '-P', [string]$Context.Port, '--') + $FileNames + @("$($Context.AdminUser)@$($Context.HostName):$($Context.RemoteDir)/")
    $code = Invoke-Scp -Arguments $scpArgs -WorkingDirectory $Context.Temp
    if ($code -ne 0) { throw "scp failed with exit code $code." }
}

# Queries and single changes run without a terminal. A non-root admin without passwordless sudo cannot answer there, so one
# retry with a terminal lets sudo ask; every provision.sh command is idempotent, which makes the repeat safe.
function Invoke-Provision {
    param($Context, [string[]]$ProvisionArguments, [switch]$Interactive)

    $script = "bash $($Context.RemoteDir)/provision.sh $($ProvisionArguments -join ' ')"
    if ($Interactive) {
        return Invoke-Remote $Context ($Context.Sudo + $script) -Tty
    }
    $noPassword = if ($Context.Sudo) { 'sudo -n ' } else { '' }
    $code = Invoke-Remote $Context ($noPassword + $script)
    if ($Context.Sudo -and $code -notin 0, 10) {
        $code = Invoke-Remote $Context ($Context.Sudo + $script) -Tty
    }
    return $code
}

function Test-Reconnect {
    param($Context, [string]$DeployKeyPath)

    # A multiplexed master from phase 1 would answer instead of a new login, and a port dropped by the firewall would hang
    # until the OS timeout: only a fresh connection with a short timeout is an honest check.
    $code = Invoke-Remote $Context 'true' -Options ($script:FreshConnection + @('-o', 'PasswordAuthentication=no', '-o', 'BatchMode=yes'))
    if ($code -ne 0) {
        throw "Reconnect check failed for $($Context.AdminUser)@$($Context.HostName): a new non-interactive login with your key does not work."
    }
    if ($DeployKeyPath) {
        $deploySsh = @('-i', $DeployKeyPath, '-o', 'IdentitiesOnly=yes') + $script:FreshConnection + @('-o', 'BatchMode=yes', '-p', [string]$Context.Port, '--', "deploy@$($Context.HostName)", 'true')
        if ((Invoke-Ssh -Arguments $deploySsh) -ne 0) {
            throw "Deploy key check failed: logging in as deploy@$($Context.HostName) with the new key does not work."
        }
    }
}

function Install-BackupRecipient {
    param($Context, [switch]$Rotate)

    $has = Invoke-Provision $Context @('--has-age-recipient')
    if ($has -notin 0, 10) { throw "provision.sh --has-age-recipient failed with exit code $has." }
    if ($has -eq 0 -and -not $Rotate) {
        Write-Host 'Backup recipient: already installed.'
        return 'kept'
    }

    $identityFile = Join-Path $Context.Temp 'age-identity.txt'
    $generated = Invoke-AgeKeygen -OutFile $identityFile
    if ($generated.ExitCode -ne 0 -or -not $generated.Recipient) { throw 'age-keygen failed.' }
    $identity = @(Get-Content $identityFile | Where-Object { $_ -like 'AGE-SECRET-KEY-*' })[0]

    Write-Host ''
    Write-Host 'Backup identity (shown once, it is not stored anywhere). Store it in your password manager now:'
    Write-Host $identity
    Write-Host 'Without it the encrypted backups cannot be restored.'

    $proved = $false
    for ($attempt = 1; $attempt -le 3 -and -not $proved; $attempt++) {
        $pasted = Read-Host 'Paste the identity back to prove it is saved' -AsSecureString
        $proved = Test-AgeIdentity -Recipient $generated.Recipient -Identity $pasted
        if (-not $proved) { Write-Warning 'That identity does not decrypt the test message.' }
    }
    if (-not $proved) {
        throw 'The pasted identity never matched the new recipient, so the recipient was not installed and backups stay refused. Re-run the bootstrap.'
    }

    Set-Content -Path (Join-Path $Context.Temp 'age-recipient.txt') -Value $generated.Recipient -Encoding ascii
    Send-ToServer $Context @('age-recipient.txt')
    $code = Invoke-Provision $Context @('--install-age-recipient', "$($Context.RemoteDir)/age-recipient.txt")
    if ($code -ne 0) { throw "provision.sh --install-age-recipient failed with exit code $code." }
    Remove-Item $identityFile -Force -ErrorAction Ignore
    if ($Rotate) { Write-Host 'Older backups stay readable only with the previous identity: keep both.' }
    return 'installed'
}

function Read-OptionalSecret {
    param([string]$Prompt)
    $value = Read-Host "$Prompt (Enter to skip)" -AsSecureString
    if ($value.Length -eq 0) { return $null }
    return $value
}

function Assert-RotatableSecret {
    param([string]$Name)

    if ($Name -in 'DB_PASSWORD', 'UMAMI_DB_PASSWORD') {
        throw "$Name is a database password: changing it in GitHub does not change the database. Run the rotate-db-password.yml workflow instead."
    }
    if ($Name -notin 'CONTENT_IMPORT_TOKEN', 'UMAMI_APP_SECRET') {
        throw "-RotateSecret accepts CONTENT_IMPORT_TOKEN or UMAMI_APP_SECRET, not '$Name'."
    }
}

function Invoke-SecretRotation {
    param([string]$Name, [string]$Repository)

    Assert-RotatableSecret -Name $Name
    if (-not $Repository) { throw 'Pass -Repository owner/name (or run inside the repository with gh authenticated).' }
    Set-GitHubValue secret $Name (ConvertTo-SecureString (New-RandomSecret -Length 40) -AsPlainText -Force) $Repository
    Write-Host "$Name was regenerated and stored. Re-run the deploy-app workflow so the server picks it up."
}

#endregion

function Invoke-Bootstrap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][string]$AdminUser,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][long[]]$AdminTelegramIds,
        [Parameter(Mandatory)][string]$LetsEncryptEmail,
        [int]$SshPort = 22,
        [string]$TelegramClientId,
        [string]$Repository,
        [switch]$RotateDeployKey,
        [switch]$RotateAgeIdentity,
        [string]$RotateSecret,
        [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    )

    # 1. Inputs and tools.
    Assert-BootstrapInputs -Domain $Domain -AdminTelegramIds $AdminTelegramIds -LetsEncryptEmail $LetsEncryptEmail -SshPort $SshPort -AdminUser $AdminUser -HostName $HostName
    # Refused before anything else: a rotation needs no server, only a name that is safe to regenerate.
    if ($RotateSecret) { Assert-RotatableSecret -Name $RotateSecret }
    Assert-Tools -NeedsServer $true

    $hasGh = $false
    if (Test-Tool gh) {
        $hasGh = (Invoke-Gh @('auth', 'status')).ExitCode -eq 0
        if (-not $hasGh) { Write-Warning 'gh is installed but not authenticated; the checklist will be printed instead of writing to GitHub.' }
    }
    if ($hasGh -and -not $Repository) {
        $view = Invoke-Gh @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')
        if ($view.ExitCode -eq 0) { $Repository = ($view.Output | Where-Object { $_ } | Select-Object -First 1).Trim() }
    }
    if ($hasGh -and $Repository -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') { throw 'Pass -Repository owner/name; it could not be detected.' }

    if ($RotateSecret) {
        if (-not $hasGh) { throw '-RotateSecret writes to GitHub and needs an authenticated gh.' }
        Invoke-SecretRotation -Name $RotateSecret -Repository $Repository
        return
    }

    $target = Resolve-SshTarget -HostName $HostName
    $deployHost = $target.HostName
    if (-not $PSBoundParameters.ContainsKey('SshPort')) { $SshPort = $target.Port }
    if ($hasGh) { Initialize-ProductionEnvironment -Repository $Repository }

    # 2. One private temporary directory for everything below.
    $temp = New-TemporaryDirectory
    $randomBytes = [byte[]]::new(4)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    $context = [pscustomobject]@{
        HostName  = $HostName
        AdminUser = $AdminUser
        Port      = $SshPort
        Temp      = $temp
        RemoteDir = '/tmp/dit-bootstrap-' + [System.Convert]::ToHexString($randomBytes).ToLowerInvariant()
        Sudo      = $(if ($AdminUser -eq 'root') { '' } else { 'sudo ' })
    }
    $remoteCreated = $false
    # True from the moment the new public key may be on the server until its private half is in GitHub or on the owner's
    # screen. A failure or Ctrl-C in between would leave a key nobody can use, so the finally block withdraws it.
    $pendingKeyRemoval = $false
    try {
        # 3. Host assets.
        Build-HostAssets -RepositoryRoot $RepositoryRoot -TempDirectory $temp
        $code = Invoke-Remote $context "mkdir -m 700 $($context.RemoteDir)"
        if ($code -ne 0) { throw "Creating $($context.RemoteDir) on the server failed (ssh exit code $code)." }
        $remoteCreated = $true
        Send-ToServer $context @('host-assets.tgz')
        $code = Invoke-Remote $context "cd $($context.RemoteDir) && tar -xzf host-assets.tgz"
        if ($code -ne 0) { throw "Unpacking the host assets failed (ssh exit code $code)." }

        # The first connection above showed the host key to the admin; without a trusted entry nothing may change on the server.
        $knownHosts = ConvertTo-KnownHostsLine -HostName $deployHost -Port $SshPort

        # 4. Deploy key, only when needed. The decision compares key material, not names.
        $serverHasAnyKey = ConvertTo-QueryAnswer (Invoke-Provision $context @('--has-deploy-key')) '--has-deploy-key'
        $serverDeployed = ConvertTo-QueryAnswer (Invoke-Provision $context @('--has-env')) '--has-env'
        $existingSecrets = @()
        $existingVariables = @()
        $githubKeyInstalled = $false
        if ($hasGh) {
            $existingSecrets = Get-GitHubNames -Kind secret -Repository $Repository
            $existingVariables = Get-GitHubNames -Kind variable -Repository $Repository
            if ($existingVariables -contains 'DEPLOY_SSH_PUBLIC_KEY') {
                $stored = Invoke-Gh @('variable', 'get', 'DEPLOY_SSH_PUBLIC_KEY', '--env', $script:EnvironmentName, '--repo', $Repository)
                $storedKey = if ($stored.ExitCode -eq 0) { ($stored.Output | Where-Object { $_ } | Select-Object -First 1) } else { $null }
                if ($storedKey) {
                    Set-Content -Path (Join-Path $temp 'github-deploy.pub') -Value $storedKey.Trim() -Encoding ascii
                    Send-ToServer $context @('github-deploy.pub')
                    $githubKeyInstalled = ConvertTo-QueryAnswer (Invoke-Provision $context @('--has-deploy-key', "$($context.RemoteDir)/github-deploy.pub")) '--has-deploy-key <key>'
                }
            }
            $plan = Get-DeployKeyPlan -ServerHasKey $githubKeyInstalled -GitHubHasKey ($existingSecrets -contains 'DEPLOY_SSH_KEY') -Rotate $RotateDeployKey.IsPresent
        }
        else {
            # GitHub cannot be asked. A deployed server necessarily works with a key the owner stored, so it is kept; on a server
            # that was never deployed an installed key may come from a failed run whose private half is gone, so it is replaced.
            $plan = Get-DeployKeyPlan -ServerHasKey ($serverHasAnyKey -and $serverDeployed) -GitHubHasKey $true -Rotate $RotateDeployKey.IsPresent
        }
        $deployOutcome = $plan
        $newKeyPath = $null
        $newPublicKey = $null
        if ($plan -eq 'create') {
            $comment = 'dit-deploy-{0}-{1}' -f [datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), (New-RandomSecret -Length 8).ToLowerInvariant()
            $newKeyPath = Join-Path $temp 'deploy_key'
            $kg = Invoke-SshKeygen @('-t', 'ed25519', '-N', '', '-C', $comment, '-f', $newKeyPath)
            if ($kg.ExitCode -ne 0) { throw 'ssh-keygen failed.' }
            $newPublicKey = (Get-Content "$newKeyPath.pub" | Select-Object -First 1).Trim()
        }

        $vars = @("DOMAIN=$Domain", "EMAIL=$LetsEncryptEmail", "ADMIN_USER=$AdminUser", "SSH_PORT=$SshPort", "APP_DIR=$script:DefaultAppDir")
        # A server without any automation key gets the new one with --base; a rotation appends it afterwards.
        if ($newPublicKey -and -not $serverHasAnyKey) { $vars += "DEPLOY_PUBLIC_KEY=$newPublicKey" }
        [System.IO.File]::WriteAllText((Join-Path $temp 'vars'), ($vars -join "`n") + "`n")
        $toSend = @('vars')
        if ($newKeyPath) { $toSend += 'deploy_key.pub' }
        Send-ToServer $context $toSend

        # 5. Phase 1.
        Write-Host 'Phase 1: packages, deploy user, directories, nginx, certificate, backup timer.'
        if ($newKeyPath) { $pendingKeyRemoval = $true }
        $code = Invoke-Provision $context @('--base', "$($context.RemoteDir)/vars") -Interactive
        if ($code -ne 0) { throw "provision.sh --base failed with exit code $code. It is idempotent: fix the cause and re-run." }
        if ($newKeyPath -and $serverHasAnyKey) {
            $code = Invoke-Provision $context @('--add-deploy-key', "$($context.RemoteDir)/deploy_key.pub")
            if ($code -ne 0) { throw "provision.sh --add-deploy-key failed with exit code $code." }
        }

        # 6. Reconnect check before anything locks the server down.
        Test-Reconnect -Context $context -DeployKeyPath $newKeyPath

        # 7. Phase 2.
        Write-Host 'Phase 2: sshd hardening and firewall.'
        $code = Invoke-Provision $context @('--harden', "$($context.RemoteDir)/vars") -Interactive
        if ($code -ne 0) {
            throw "provision.sh --harden failed with exit code $code. If you are locked out, use the provider's web console and remove /etc/ssh/sshd_config.d/10-dit.conf, then run 'ufw disable' and 'systemctl reload ssh'."
        }
        try { Test-Reconnect -Context $context -DeployKeyPath $newKeyPath }
        catch {
            throw "$($_.Exception.Message) Recovery: use the provider's web console, remove /etc/ssh/sshd_config.d/10-dit.conf, run 'ufw disable' and 'systemctl reload ssh', then re-run the bootstrap."
        }

        # 8. Backup identity.
        $ageResult = Install-BackupRecipient -Context $context -Rotate:$RotateAgeIdentity

        # 9. Secrets: only names that do not exist yet.
        $values = [System.Collections.Generic.List[pscustomobject]]::new()
        $addValue = { param($Kind, $Name, [securestring]$Value) $values.Add([pscustomobject]@{ Kind = $Kind; Name = $Name; Value = $Value }) }
        # Without gh the stored names are unknown. A server that was deployed before already holds live values, and a new
        # database password in the checklist would lock the application out of its own database.
        $generated = @()
        if ($hasGh) { $generated = Get-MissingSecretNames -Existing $existingSecrets }
        elseif (-not $serverDeployed) { $generated = Get-MissingSecretNames -Existing @() }
        foreach ($name in $generated) {
            & $addValue 'secret' $name (ConvertTo-SecureString (New-RandomSecret -Length 40) -AsPlainText -Force)
        }
        $manual = [System.Collections.Generic.List[string]]::new()
        if ($newKeyPath) {
            & $addValue 'secret' 'DEPLOY_SSH_KEY' (ConvertTo-SecureString (Get-Content -Raw $newKeyPath) -AsPlainText -Force)
        }

        $plainVariables = [ordered]@{
            ADMIN_TELEGRAM_IDS = ($AdminTelegramIds -join ',')
            PUBLIC_BASE_URL    = "https://$Domain"
            DEPLOY_HOST        = $deployHost
            DEPLOY_USER        = 'deploy'
            DEPLOY_SSH_PORT    = [string]$SshPort
            DEPLOY_KNOWN_HOSTS = $knownHosts
            APP_DIR            = $script:DefaultAppDir
        }
        if ($newPublicKey) { $plainVariables['DEPLOY_SSH_PUBLIC_KEY'] = $newPublicKey }
        if ($TelegramClientId) { $plainVariables['TELEGRAM_OIDC_CLIENT_ID'] = $TelegramClientId }

        if ($hasGh) {
            # Entered by hand, once; a re-run does not ask for what GitHub already holds.
            foreach ($name in 'TELEGRAM_OIDC_CLIENT_SECRET', 'BACKUP_S3_ACCESS_KEY_ID', 'BACKUP_S3_SECRET_ACCESS_KEY') {
                if ($existingSecrets -contains $name) { continue }
                $entered = Read-OptionalSecret $name
                if ($entered) { & $addValue 'secret' $name $entered } else { $manual.Add($name) }
            }
            foreach ($name in 'BACKUP_S3_PROVIDER', 'BACKUP_S3_ENDPOINT', 'BACKUP_S3_BUCKET') {
                if ($existingVariables -contains $name) { continue }
                $entered = Read-Host "$name (Enter to skip)"
                if ($entered) { $plainVariables[$name] = $entered } else { $manual.Add($name) }
            }
            if (-not $TelegramClientId -and $existingVariables -notcontains 'TELEGRAM_OIDC_CLIENT_ID') {
                $entered = Read-Host 'TELEGRAM_OIDC_CLIENT_ID (Enter to skip)'
                if ($entered) { $plainVariables['TELEGRAM_OIDC_CLIENT_ID'] = $entered } else { $manual.Add('TELEGRAM_OIDC_CLIENT_ID') }
            }
        }
        foreach ($name in $plainVariables.Keys) {
            & $addValue 'variable' $name (ConvertTo-SecureString ([string]$plainVariables[$name]) -AsPlainText -Force)
        }

        # 10. GitHub, or a checklist.
        if ($hasGh) {
            Write-Host ''
            Write-Host "These values will be written to the '$($script:EnvironmentName)' environment of ${Repository}:"
            foreach ($v in $values) { Write-Host ('  {0,-8} {1}' -f $v.Kind, $v.Name) }
            $answer = Read-Host 'Type yes to write them'
            if ($answer -ne 'yes') {
                Write-Warning 'Nothing was written to GitHub. Re-run the bootstrap to try again.'
                if ($newKeyPath) {
                    # The private half dies with the temporary directory, so the installed public half would be an orphan.
                    $code = Invoke-Provision $context @('--remove-deploy-key', "$($context.RemoteDir)/deploy_key.pub")
                    if ($code -ne 0) { throw "provision.sh --remove-deploy-key failed with exit code $code; the discarded deploy key is still installed." }
                    $deployOutcome = 'discarded'
                    $pendingKeyRemoval = $false
                }
            }
            else {
                foreach ($v in ($values | Sort-Object { if ($_.Name -eq 'DEPLOY_SSH_KEY') { 0 } else { 1 } })) {
                    Set-GitHubValue -Kind $v.Kind -Name $v.Name -Value $v.Value -Repository $Repository
                    if ($v.Name -eq 'DEPLOY_SSH_KEY') {
                        $pendingKeyRemoval = $false
                        # The new key is stored, so the older ones are no longer needed.
                        $code = Invoke-Provision $context @('--remove-deploy-keys-except', "$($context.RemoteDir)/deploy_key.pub")
                        if ($code -ne 0) { throw "provision.sh --remove-deploy-keys-except failed with exit code $code; older keys are still installed." }
                        $deployOutcome = 'stored'
                    }
                }
            }
        }
        else {
            Write-Host ''
            Write-Host 'No authenticated gh: set these by hand in the production environment (each secret is shown once).'
            foreach ($v in $values | Where-Object Kind -EQ 'secret') {
                Write-Host ("Secret   {0}" -f $v.Name)
                Write-Host ([System.Net.NetworkCredential]::new('', $v.Value).Password)
            }
            foreach ($v in $values | Where-Object Kind -EQ 'variable') {
                Write-Host ("Variable {0} = {1}" -f $v.Name, [System.Net.NetworkCredential]::new('', $v.Value).Password)
            }
            Write-Host 'Enter yourself: TELEGRAM_OIDC_CLIENT_SECRET, BACKUP_S3_ACCESS_KEY_ID, BACKUP_S3_SECRET_ACCESS_KEY and the BACKUP_S3_* variables.'
            if ($serverDeployed) {
                Write-Host 'This server was deployed before, so DB_PASSWORD, UMAMI_DB_PASSWORD, CONTENT_IMPORT_TOKEN and UMAMI_APP_SECRET were not generated: keep the values you stored earlier.'
            }
            if ($newKeyPath) {
                $deployOutcome = 'printed'
                $pendingKeyRemoval = $false
                Write-Host 'Store DEPLOY_SSH_KEY and DEPLOY_SSH_PUBLIC_KEY above.'
                if ($serverHasAnyKey -and -not $serverDeployed) {
                    # Nothing was ever deployed with an older key, so it is a leftover of a failed run, and the new key is proven.
                    $code = Invoke-Provision $context @('--remove-deploy-keys-except', "$($context.RemoteDir)/deploy_key.pub")
                    if ($code -ne 0) { throw "provision.sh --remove-deploy-keys-except failed with exit code $code; older keys are still installed." }
                }
                elseif ($serverHasAnyKey) {
                    Write-Host 'The previous deploy key stays valid until you store the new one; then remove its dit-deploy- line from /home/deploy/.ssh/authorized_keys, or re-run with gh.'
                }
            }
            else {
                Write-Host 'The deploy key on the server was kept: DEPLOY_SSH_KEY must already be stored in the production environment from the earlier run. If it is lost, re-run with -RotateDeployKey.'
            }
        }
        Write-Host 'Still to set by hand later: SECRETS_ADMIN_TOKEN (rotate-db-password.yml) and UMAMI_WEBSITE_ID (after Umami first runs).'
        if ($manual.Count -gt 0) { Write-Host "Skipped now: $($manual -join ', ')." }

        # 11. Summary.
        Write-Host ''
        Write-Host "Register this redirect URI with BotFather: https://$Domain/signin-telegram"
        Write-Host "Backup identity: $ageResult. Deploy key: $deployOutcome. Each provisioning phase printed its own changed/unchanged list above."
    }
    finally {
        if ($remoteCreated -and $pendingKeyRemoval) {
            try { [void](Invoke-Provision $context @('--remove-deploy-key', "$($context.RemoteDir)/deploy_key.pub")) }
            catch { Write-Warning "The unused deploy key could not be withdrawn: $($_.Exception.Message)" }
        }
        if ($remoteCreated) {
            # Nothing secret is left there, but nothing should stay either.
            try { [void](Invoke-Remote $context "rm -rf $($context.RemoteDir)") } catch { Write-Verbose "Remote cleanup failed: $($_.Exception.Message)" }
        }
        Remove-Item -Recurse -Force $temp -ErrorAction Ignore
    }
}

Export-ModuleMember -Function Test-DomainName, New-RandomSecret, Get-ProductionSettingNames, Get-MissingSecretNames,
Set-GitHubValue, ConvertTo-KnownHostsLine, Test-AgeIdentity, Get-DeployKeyPlan, New-TemporaryDirectory, Invoke-Bootstrap
