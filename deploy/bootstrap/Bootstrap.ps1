#Requires -Version 7.2
<#
.SYNOPSIS
First-time, idempotent provisioning of the Dev Interview Trainer VPS from the administrator's PC.

.DESCRIPTION
Needs PowerShell 7.2+, the OpenSSH client, age and age-keygen (winget install FiloSottile.age, brew install age, or
apt install age) and, optionally, an authenticated gh. The remote part is deploy/bootstrap/remote/provision.sh, uploaded
with the nginx templates and systemd units and run in two phases (--base, then --harden after a reconnect check).

The OIDC client secret and the S3 access key pair are read with Read-Host -AsSecureString and are never parameters.
Nothing is written to disk on this PC except a private temporary directory that is deleted at the end.

.PARAMETER HostName
SSH host of the server.

.PARAMETER AdminUser
Administrator account on the server (key login, sudo or root).

.PARAMETER Domain
Public domain name (no scheme, port or path).

.PARAMETER AdminTelegramIds
Telegram user ids that receive the Admin role on their first sign-in.

.PARAMETER LetsEncryptEmail
Contact e-mail for the Let's Encrypt certificate.

.PARAMETER SshPort
Port sshd listens on (default: what ssh -G reports for the host). The firewall allows it in addition to the port of the current session, and it is stored as DEPLOY_SSH_PORT for the deploy workflows.

.PARAMETER TelegramClientId
Telegram OIDC client id; asked for when it is neither given nor already stored.

.PARAMETER Repository
GitHub repository as owner/name; detected through gh when omitted.

.PARAMETER RotateDeployKey
Creates a new deploy key pair, proves it, stores it, then removes the older keys.

.PARAMETER RotateAgeIdentity
Creates a new backup identity and replaces the recipient. Keep the old identity: older backups need it.

.PARAMETER RotateSecret
Regenerates CONTENT_IMPORT_TOKEN or UMAMI_APP_SECRET in GitHub and stops; re-run deploy-app afterwards.
Database passwords are rotated by the rotate-db-password.yml workflow.
#>
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
    [string]$RotateSecret
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'BootstrapLib.psm1') -Force
Invoke-Bootstrap @PSBoundParameters
