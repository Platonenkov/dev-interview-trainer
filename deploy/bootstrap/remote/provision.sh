#!/usr/bin/env bash
# Server side of the bootstrap. Bootstrap.ps1 uploads it with the host assets (nginx templates, systemd units) and runs
# it as root over SSH; nothing here is meant to be started by hand except for recovery.
#
#   provision.sh --base <vars>                       packages, deploy user, /srv layout, nginx, certificate, backup timer
#   provision.sh --harden <vars>                     sshd drop-in, firewall (run only after a successful reconnect check)
#   provision.sh --has-deploy-key [<public-key>]     exit 0 when a dit-deploy- key (with this key material) is installed, 10 when not
#   provision.sh --add-deploy-key <public-key>       append the key with "restrict"
#   provision.sh --remove-deploy-keys-except <public-key>
#   provision.sh --remove-deploy-key <public-key>    remove the lines with exactly this key material
#   provision.sh --has-env                           exit 0 when the server was deployed before (.env exists), 10 when not
#   provision.sh --has-age-recipient                 exit 0 when backups have a recipient, 10 when not
#   provision.sh --install-age-recipient <file>      replace the backup recipient (public, not secret)
#
# Every step prints "changed <item>" or "unchanged <item>", and the run ends with a count, so a re-run shows what it
# repaired. Host assets change only through a re-run of the bootstrap: there is no version stamp and no drift check.
#
# The vars file is parsed, never sourced, and every value is checked against a pattern.
set -euo pipefail

DEPLOY_USER="deploy"
APP_DIR="/srv/dev-interview-trainer"
ASSETS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Docker publishes the fingerprint of its apt signing key at https://docs.docker.com/engine/install/ubuntu/; the key
# downloaded below must match it before it is trusted.
DOCKER_KEY_FPR="9DC858229FC7DD38854AE2D88D81803C0EBFCD88"

AUTHORIZED_KEYS="/home/$DEPLOY_USER/.ssh/authorized_keys"
SSH_DIR="/etc/ssh"
TEST_MODE=0
# Test hooks: the Pester tests run the key commands on a plain file tree as an ordinary user. They move key and sshd paths
# and switch off the root and ownership steps, so they are read only under DIT_TEST_HOOKS=1, which no workflow or
# bootstrap run sets; a stray variable in root's shell cannot redirect a real run.
if [ "${DIT_TEST_HOOKS:-}" = "1" ]; then
  TEST_MODE=1
  AUTHORIZED_KEYS="${DIT_TEST_AUTHORIZED_KEYS:-$AUTHORIZED_KEYS}"
  SSH_DIR="${DIT_TEST_SSH_DIR:-$SSH_DIR}"
  APP_DIR="${DIT_TEST_APP_DIR:-$APP_DIR}"
fi

DOMAIN="" EMAIL="" ADMIN_USER="" SSH_PORT="22" DEPLOY_PUBLIC_KEY=""
changed=0
unchanged=0

die() {
  printf 'provision: %s\n' "$*" >&2
  exit 1
}

note_changed() {
  printf 'changed %s\n' "$1"
  changed=$((changed + 1))
}

note_unchanged() {
  printf 'unchanged %s\n' "$1"
  unchanged=$((unchanged + 1))
}

note_result() { # <item> <0 = changed | other = unchanged>
  if [ "$2" -eq 0 ]; then note_changed "$1"; else note_unchanged "$1"; fi
}

print_summary() {
  printf 'summary changed=%d unchanged=%d\n' "$changed" "$unchanged"
}

require_root() {
  if [ "$TEST_MODE" != "1" ] && [ "$(id -u)" -ne 0 ]; then
    die "must run as root"
  fi
}

# --- vars file -------------------------------------------------------------------------------------------------------

load_vars() {
  local file="$1" line key value
  [ -f "$file" ] || die "vars file $file not found"
  while IFS= read -r line || [ -n "$line" ]; do
    [ -n "$line" ] || continue
    key="${line%%=*}"
    value="${line#*=}"
    case "$key" in
      DOMAIN)
        [[ "$value" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$ ]] || die "invalid DOMAIN"
        DOMAIN="$value"
        ;;
      EMAIL)
        [[ "$value" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]] || die "invalid EMAIL"
        EMAIL="$value"
        ;;
      ADMIN_USER)
        [[ "$value" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "invalid ADMIN_USER"
        ADMIN_USER="$value"
        ;;
      SSH_PORT)
        [[ "$value" =~ ^[0-9]{1,5}$ ]] && [ "$value" -ge 1 ] && [ "$value" -le 65535 ] || die "invalid SSH_PORT"
        SSH_PORT="$value"
        ;;
      APP_DIR)
        [[ "$value" =~ ^/[A-Za-z0-9._/-]*$ ]] || die "invalid APP_DIR"
        APP_DIR="$value"
        ;;
      DEPLOY_PUBLIC_KEY)
        [ -z "$value" ] || [[ "$value" =~ ^ssh-ed25519\ [A-Za-z0-9+/]+={0,3}(\ [A-Za-z0-9._-]*)?$ ]] || die "invalid DEPLOY_PUBLIC_KEY"
        DEPLOY_PUBLIC_KEY="$value"
        ;;
      *) die "unknown key in the vars file: $key" ;;
    esac
  done <"$file"
}

# --- small helpers ---------------------------------------------------------------------------------------------------

# Content from stdin; replaced through a temp file and rename. Returns 0 when the file changed, 3 when it already had this content.
write_if_changed() { # <dest> <mode>
  local dest="$1" mode="$2" tmp
  tmp="$(mktemp "$dest.XXXXXX")" || return 1
  if ! cat >"$tmp" || ! chmod "$mode" "$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  if [ -f "$dest" ] && cmp -s "$tmp" "$dest" && [ "$(stat -c %a "$dest")" = "$(stat -c %a "$tmp")" ]; then
    rm -f "$tmp"
    return 3
  fi
  mv -f "$tmp" "$dest" || {
    rm -f "$tmp"
    return 1
  }
}

# Same as write_if_changed for a file taken from the asset bundle.
install_asset() { # <asset relative path> <dest> <mode>
  local rc=0
  [ -f "$ASSETS_DIR/$1" ] || die "asset $1 is missing from the bundle"
  write_if_changed "$2" "$3" <"$ASSETS_DIR/$1" || rc=$?
  [ "$rc" -le 3 ] && [ "$rc" -ne 1 ] || die "cannot write $2"
  return "$rc"
}

# --- packages --------------------------------------------------------------------------------------------------------

package_missing() {
  ! dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q '^install ok installed$'
}

apt_install() { # <item> <package>...
  local item="$1" pkg
  shift
  local -a missing=()
  for pkg in "$@"; do
    if package_missing "$pkg"; then missing+=("$pkg"); fi
  done
  if [ "${#missing[@]}" -eq 0 ]; then
    note_unchanged "$item"
    return 0
  fi
  DEBIAN_FRONTEND=noninteractive apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "${missing[@]}"
  note_changed "$item"
}

# A compromised repository would be a compromised root, so the key is trusted only after its fingerprint matches.
configure_docker_repo() {
  local key=/etc/apt/keyrings/docker.asc sources=/etc/apt/sources.list.d/docker.sources tmp fpr codename arch rc=0
  # shellcheck disable=SC1091
  . /etc/os-release
  [ "${ID:-}" = "ubuntu" ] || die "only Ubuntu is supported (found ${ID:-unknown})"
  codename="${VERSION_CODENAME:?no VERSION_CODENAME in /etc/os-release}"
  arch="$(dpkg --print-architecture)"
  install -d -m 755 /etc/apt/keyrings
  tmp="$(mktemp)"
  curl -fsSL --retry 3 https://download.docker.com/linux/ubuntu/gpg -o "$tmp" || {
    rm -f "$tmp"
    die "cannot download Docker's signing key"
  }
  fpr="$(gpg --show-keys --with-colons "$tmp" | awk -F: '$1 == "fpr" { print $10; exit }')"
  if [ "$fpr" != "$DOCKER_KEY_FPR" ]; then
    rm -f "$tmp"
    die "Docker's signing key has fingerprint ${fpr:-none}, expected $DOCKER_KEY_FPR"
  fi
  write_if_changed "$key" 644 <"$tmp" || rc=$?
  rm -f "$tmp"
  [ "$rc" -ne 1 ] || die "cannot write $key"
  local key_rc="$rc"
  rc=0
  write_if_changed "$sources" 644 <<EOF || rc=$?
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $codename
Components: stable
Architectures: $arch
Signed-By: $key
EOF
  [ "$rc" -ne 1 ] || die "cannot write $sources"
  if [ "$key_rc" -eq 0 ] || [ "$rc" -eq 0 ]; then note_changed docker-apt-repo; else note_unchanged docker-apt-repo; fi
}

install_packages() {
  apt_install base-tools ca-certificates curl gnupg gettext-base
  configure_docker_repo
  apt_install docker docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  apt_install host-packages nginx certbot ufw unattended-upgrades age rclone curl
  if systemctl is-enabled --quiet docker && systemctl is-active --quiet docker; then
    note_unchanged docker-service
  else
    systemctl enable --now docker
    note_changed docker-service
  fi
}

configure_unattended_upgrades() {
  local rc=0
  write_if_changed /etc/apt/apt.conf.d/20auto-upgrades 644 <<'EOF' || rc=$?
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
  note_result unattended-upgrades "$rc"
}

# --- deploy user and keys --------------------------------------------------------------------------------------------

ensure_deploy_user() {
  local rc=1
  if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
    adduser --disabled-password --gecos "" "$DEPLOY_USER" >/dev/null
    rc=0
  fi
  # The docker group is root-equivalent on the host; that is the accepted price of a deploy user without sudo.
  if ! id -nG "$DEPLOY_USER" | tr ' ' '\n' | grep -qx docker; then
    usermod -aG docker "$DEPLOY_USER"
    rc=0
  fi
  note_result deploy-user "$rc"
}

# Sets K_TYPE, K_BLOB and K_COMMENT from an authorized_keys line; returns 1 for comments and lines without a key.
# Options such as "restrict" precede the key type, so the type is found by scanning, not by position.
parse_key_line() {
  K_TYPE="" K_BLOB="" K_COMMENT=""
  local line="$1" i
  local -a fields=()
  case "$line" in '' | '#'*) return 1 ;; esac
  read -r -a fields <<<"$line" || true
  for ((i = 0; i < ${#fields[@]} - 1; i++)); do
    case "${fields[i]}" in
      ssh-ed25519 | ssh-rsa | ecdsa-sha2-* | sk-ssh-ed25519@openssh.com | sk-ecdsa-sha2-nistp256@openssh.com)
        K_TYPE="${fields[i]}"
        K_BLOB="${fields[i + 1]}"
        K_COMMENT="${fields[*]:i+2}"
        return 0
        ;;
    esac
  done
  return 1
}

# Automation keys are recognised by the dit-deploy- comment prefix, but compared by key material: two lines can share a
# comment, and a comment can be edited, while the base64 blob cannot be forged.
is_deploy_key_line() {
  parse_key_line "$1" && [[ "$K_COMMENT" == dit-deploy-* ]]
}

read_public_key() { # <file>: prints "<type> <blob>"
  local file="$1" line
  [ -f "$file" ] || die "public key file $file not found"
  IFS= read -r line <"$file" || [ -n "$line" ] || die "public key file $file is empty"
  parse_key_line "$line" || die "$file does not contain a public key"
  printf '%s %s\n' "$K_TYPE" "$K_BLOB"
}

authorized_keys_has_blob() { # <type> <blob>
  local line
  [ -f "$AUTHORIZED_KEYS" ] || return 1
  while IFS= read -r line || [ -n "$line" ]; do
    if parse_key_line "$line" && [ "$K_TYPE" = "$1" ] && [ "$K_BLOB" = "$2" ]; then
      return 0
    fi
  done <"$AUTHORIZED_KEYS"
  return 1
}

prepare_authorized_keys() {
  local dir
  dir="$(dirname "$AUTHORIZED_KEYS")"
  install -d -m 700 "$dir"
  touch "$AUTHORIZED_KEYS"
  chmod 600 "$AUTHORIZED_KEYS"
  if [ "$TEST_MODE" != "1" ]; then
    chown "$DEPLOY_USER:$DEPLOY_USER" "$dir" "$AUTHORIZED_KEYS"
  fi
}

# Returns 0 when the key was added, 3 when its material was already installed.
add_deploy_key() { # <type> <blob>
  if authorized_keys_has_blob "$1" "$2"; then
    return 3
  fi
  prepare_authorized_keys
  if [ -s "$AUTHORIZED_KEYS" ] && [ "$(tail -c 1 "$AUTHORIZED_KEYS" | wc -l)" -eq 0 ]; then
    printf '\n' >>"$AUTHORIZED_KEYS"
  fi
  printf 'restrict %s %s dit-deploy-%s\n' "$1" "$2" "$(date -u +%Y%m%d)" >>"$AUTHORIZED_KEYS"
}

ensure_deploy_key_from_vars() {
  local rc=0
  if [ -z "$DEPLOY_PUBLIC_KEY" ]; then
    note_unchanged deploy-key
    return 0
  fi
  parse_key_line "$DEPLOY_PUBLIC_KEY" || die "DEPLOY_PUBLIC_KEY holds no key"
  add_deploy_key "$K_TYPE" "$K_BLOB" || rc=$?
  note_result deploy-key "$rc"
}

cmd_has_deploy_key() { # [<public-key-file>]
  local line spec
  if [ "$#" -ge 1 ]; then
    spec="$(read_public_key "$1")"
    # shellcheck disable=SC2086 # the two words are split on purpose
    authorized_keys_has_blob $spec && return 0
    return 10
  fi
  [ -f "$AUTHORIZED_KEYS" ] || return 10
  while IFS= read -r line || [ -n "$line" ]; do
    if is_deploy_key_line "$line"; then return 0; fi
  done <"$AUTHORIZED_KEYS"
  return 10
}

cmd_add_deploy_key() { # <public-key-file>
  local spec rc=0
  spec="$(read_public_key "$1")"
  if [ "$TEST_MODE" != "1" ]; then
    id "$DEPLOY_USER" >/dev/null 2>&1 || die "user $DEPLOY_USER does not exist; run --base first"
  fi
  # shellcheck disable=SC2086 # the two words are split on purpose
  add_deploy_key $spec || rc=$?
  note_result deploy-key "$rc"
}

# Keeps every line that is not an automation key, and the one automation key whose material equals the argument. A file
# without that key is refused: removing every key would lock the pipeline out.
cmd_remove_deploy_keys_except() { # <public-key-file>
  local spec keep_type keep_blob line tmp removed=0 found=0
  spec="$(read_public_key "$1")"
  keep_type="${spec%% *}"
  keep_blob="${spec#* }"
  [ -f "$AUTHORIZED_KEYS" ] || die "$AUTHORIZED_KEYS does not exist"
  authorized_keys_has_blob "$keep_type" "$keep_blob" || die "the given key is not installed; nothing removed"
  tmp="$(mktemp "$AUTHORIZED_KEYS.XXXXXX")"
  while IFS= read -r line || [ -n "$line" ]; do
    if is_deploy_key_line "$line"; then
      if [ "$K_TYPE" = "$keep_type" ] && [ "$K_BLOB" = "$keep_blob" ]; then
        # A duplicate of the kept key is dropped too: exactly one line stays.
        if [ "$found" -eq 1 ]; then removed=$((removed + 1)); continue; fi
        found=1
      else
        removed=$((removed + 1))
        continue
      fi
    fi
    printf '%s\n' "$line" >>"$tmp"
  done <"$AUTHORIZED_KEYS"
  if [ "$removed" -eq 0 ]; then
    rm -f "$tmp"
    note_unchanged deploy-keys
    return 0
  fi
  chmod --reference="$AUTHORIZED_KEYS" "$tmp"
  chown --reference="$AUTHORIZED_KEYS" "$tmp" 2>/dev/null || true
  mv -f "$tmp" "$AUTHORIZED_KEYS"
  note_changed "deploy-keys (removed $removed)"
}

# Removes the automation line(s) with exactly this key material: used to withdraw a key that was installed but never
# stored in GitHub. Other keys and every other line stay.
cmd_remove_deploy_key() { # <public-key-file>
  local spec key_type key_blob line tmp removed=0
  spec="$(read_public_key "$1")"
  key_type="${spec%% *}"
  key_blob="${spec#* }"
  if [ ! -f "$AUTHORIZED_KEYS" ]; then
    note_unchanged deploy-key
    return 0
  fi
  tmp="$(mktemp "$AUTHORIZED_KEYS.XXXXXX")"
  while IFS= read -r line || [ -n "$line" ]; do
    if is_deploy_key_line "$line" && [ "$K_TYPE" = "$key_type" ] && [ "$K_BLOB" = "$key_blob" ]; then
      removed=$((removed + 1))
      continue
    fi
    printf '%s\n' "$line" >>"$tmp"
  done <"$AUTHORIZED_KEYS"
  if [ "$removed" -eq 0 ]; then
    rm -f "$tmp"
    note_unchanged deploy-key
    return 0
  fi
  chmod --reference="$AUTHORIZED_KEYS" "$tmp"
  chown --reference="$AUTHORIZED_KEYS" "$tmp" 2>/dev/null || true
  mv -f "$tmp" "$AUTHORIZED_KEYS"
  note_changed "deploy-key (removed $removed)"
}

# --- layout ----------------------------------------------------------------------------------------------------------

ensure_dir() { # <mode> <path>: returns 0 when the directory, its owner or its mode changed
  local mode="$1" path="$2" before after
  before="$(stat -c '%U:%G:%a' "$path" 2>/dev/null || true)"
  if [ "$TEST_MODE" = "1" ] && ! id "$DEPLOY_USER" >/dev/null 2>&1; then
    install -d -m "$mode" "$path"
  else
    install -d -o "$DEPLOY_USER" -g "$DEPLOY_USER" -m "$mode" "$path"
  fi
  after="$(stat -c '%U:%G:%a' "$path")"
  [ "$before" != "$after" ]
}

# flags, postgres and the application directory are 755 because nginx (www-data) must stat flags/maintenance: an
# unreadable path counts as "no maintenance" and would silently disable the gate. Backups hold dumps and stay private.
# Docker would otherwise create the bind-mounted directories as root on the first "compose up".
ensure_layout() {
  local dir mode rc=1
  while read -r mode dir; do
    if ensure_dir "$mode" "$APP_DIR${dir:+/$dir}"; then rc=0; fi
  done <<'EOF'
755
755 content
755 content/releases
700 backups
750 backup
755 bundles
750 staging
755 flags
755 postgres
EOF
  note_result layout "$rc"
}

# --- nginx and certificate -------------------------------------------------------------------------------------------

render_template() { # <template name> <dest>; returns the write_if_changed status
  local rc=0
  [ -f "$ASSETS_DIR/nginx/$1" ] || die "nginx template $1 is missing from the bundle"
  # Only these two names are ours: the templates also contain nginx's own $variables. envsubst, not the shell, expands them.
  # shellcheck disable=SC2016
  DOMAIN="$DOMAIN" APP_DIR="$APP_DIR" envsubst '${DOMAIN} ${APP_DIR}' <"$ASSETS_DIR/nginx/$1" | write_if_changed "$2" 644 || rc=$?
  [ "$rc" -ne 1 ] || die "cannot render $1"
  return "$rc"
}

# Renders a template into a file nginx reads, proves it with "nginx -t" and reloads. A bad file must never stay in place:
# nginx would refuse to start on the next restart, so the previous content (or no file) comes back and the step fails.
apply_nginx_file() { # <template> <dest> <item>
  local template="$1" dest="$2" item="$3" backup="" rc=0
  if [ -f "$dest" ]; then
    backup="$(mktemp)"
    cp -p "$dest" "$backup"
  fi
  render_template "$template" "$dest" || rc=$?
  if [ "$rc" -eq 3 ]; then
    rm -f "$backup"
    note_unchanged "$item"
    return 0
  fi
  if ! nginx -t; then
    if [ -n "$backup" ]; then mv -f "$backup" "$dest"; else rm -f "$dest"; fi
    die "nginx rejected $template; the previous configuration was restored"
  fi
  # A failed reload gets the same rollback: with the new file left on disk a re-run would see "unchanged" and never retry.
  if ! systemctl reload nginx; then
    if [ -n "$backup" ]; then mv -f "$backup" "$dest"; else rm -f "$dest"; fi
    die "nginx did not reload with $template; the previous configuration was restored"
  fi
  rm -f "$backup"
  note_changed "$item"
}

apply_nginx_site() { # <template> <item>
  apply_nginx_file "$1" /etc/nginx/sites-enabled/dev-interview-trainer.conf "$2"
}

configure_nginx() {
  local rc=0 cert="/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
  # Running before the first reload: a reload of a stopped service fails.
  systemctl enable --now nginx
  apply_nginx_file nginx.conf.template /etc/nginx/nginx.conf nginx-conf
  if [ -e /etc/nginx/sites-enabled/default ]; then
    rm -f /etc/nginx/sites-enabled/default
    systemctl reload nginx
    note_changed nginx-default-site-removed
  fi
  install -d -m 755 /var/www/certbot
  if [ ! -f "$cert" ]; then
    apply_nginx_site dev-interview-trainer.http.conf.template nginx-http-site
    certbot certonly --webroot -w /var/www/certbot -d "$DOMAIN" -m "$EMAIL" --agree-tos --no-eff-email --non-interactive
    note_changed certificate
  else
    note_unchanged certificate
  fi
  apply_nginx_site dev-interview-trainer.conf.template nginx-site
  install -d -m 755 /etc/letsencrypt/renewal-hooks/deploy
  rc=0
  write_if_changed /etc/letsencrypt/renewal-hooks/deploy/dit-reload-nginx.sh 755 <<'EOF' || rc=$?
#!/bin/sh
systemctl reload nginx
EOF
  note_result certbot-deploy-hook "$rc"
}

# --- backup timer ----------------------------------------------------------------------------------------------------

configure_backup_timer() {
  local rc=0 service_rc=0 unit_changed=1
  install_asset systemd/dit-backup.service /etc/systemd/system/dit-backup.service 644 || service_rc=$?
  install_asset systemd/dit-backup.timer /etc/systemd/system/dit-backup.timer 644 || rc=$?
  if [ "$rc" -eq 0 ] || [ "$service_rc" -eq 0 ]; then
    systemctl daemon-reload
    unit_changed=0
  fi
  # ConditionPathExists on the service keeps the timer idle until the first deployment delivers backup.sh.
  if ! systemctl is-enabled --quiet dit-backup.timer || ! systemctl is-active --quiet dit-backup.timer; then
    systemctl enable --now dit-backup.timer
    unit_changed=0
  fi
  note_result backup-timer "$unit_changed"
}

# Whether the server was deployed before: a re-run must not hand out new database passwords for a live database.
cmd_has_env() {
  [ -f "$APP_DIR/.env" ] && return 0
  return 10
}

# --- backup recipient ------------------------------------------------------------------------------------------------

cmd_has_age_recipient() {
  [ -s "$APP_DIR/backup/recipients.txt" ] && return 0
  return 10
}

# Replaces the recipient: a rotation must not leave the old key encrypting new backups.
cmd_install_age_recipient() { # <file>
  local file="$1" recipient rc=0
  [ -f "$file" ] || die "recipient file $file not found"
  IFS= read -r recipient <"$file" || [ -n "$recipient" ] || die "recipient file is empty"
  [[ "$recipient" =~ ^age1[a-z0-9]{58}$ ]] || die "the file does not hold an age recipient"
  ensure_dir 750 "$APP_DIR/backup" || true
  printf '%s\n' "$recipient" | write_if_changed "$APP_DIR/backup/recipients.txt" 640 || rc=$?
  [ "$rc" -ne 1 ] || die "cannot write recipients.txt"
  if [ "$TEST_MODE" != "1" ]; then
    chown "$DEPLOY_USER:$DEPLOY_USER" "$APP_DIR/backup/recipients.txt"
  fi
  note_result age-recipient "$rc"
}

# --- hardening -------------------------------------------------------------------------------------------------------

# 10- sorts before cloud-init's 50-cloud-init.conf, and sshd keeps the first value it reads for a keyword.
sshd_dropin() {
  printf '%s/sshd_config.d/10-dit.conf\n' "$SSH_DIR"
}

expected_root_login() {
  if [ "$ADMIN_USER" = "root" ]; then printf 'prohibit-password\n'; else printf 'no\n'; fi
}

sshd_effective() { # <keyword>
  sshd -T -f "$SSH_DIR/sshd_config" | awk -v k="$1" '$1 == k { print $2; exit }'
}

# OpenSSH reports prohibit-password under its older name, depending on the version.
verify_sshd_settings() {
  local want_root actual
  want_root="$(expected_root_login)"
  [ "$(sshd_effective passwordauthentication)" = "no" ] || return 1
  [ "$(sshd_effective kbdinteractiveauthentication)" = "no" ] || return 1
  actual="$(sshd_effective permitrootlogin)"
  if [ "$want_root" = "prohibit-password" ]; then
    [ "$actual" = "prohibit-password" ] || [ "$actual" = "without-password" ] || return 1
  else
    [ "$actual" = "$want_root" ] || return 1
  fi
}

# Writes the drop-in, proves with "sshd -t" and "sshd -T" that sshd will really use it, and puts the previous state back
# when it will not. Does not reload sshd. Returns 0 when the file changed, 3 when it already was right.
apply_sshd_dropin() {
  local dropin backup="" rc=0
  dropin="$(sshd_dropin)"
  install -d -m 755 "$(dirname "$dropin")" /run/sshd || die "cannot create $(dirname "$dropin")"
  if [ -f "$dropin" ]; then
    backup="$(mktemp)"
    cp -p "$dropin" "$backup"
  fi
  write_if_changed "$dropin" 644 <<EOF || rc=$?
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin $(expected_root_login)
EOF
  [ "$rc" -ne 1 ] || die "cannot write $dropin"
  if ! sshd -t -f "$SSH_DIR/sshd_config" || ! verify_sshd_settings; then
    if [ -n "$backup" ]; then mv -f "$backup" "$dropin"; else rm -f "$dropin"; fi
    die "sshd would not apply $dropin (an earlier file or a missing Include wins); it was reverted and sshd was not reloaded"
  fi
  rm -f "$backup"
  return "$rc"
}

reload_sshd() {
  systemctl reload ssh 2>/dev/null || systemctl reload sshd
}

# The port of the SSH session this very run belongs to. sudo's env_reset drops SSH_CONNECTION, so it is read from the
# ancestors of this process. Prints nothing when the run was not started from an SSH session.
session_ssh_port() {
  local pid="$$" env_file conn=""
  while [ -n "$pid" ] && [ "$pid" -gt 1 ]; do
    env_file="/proc/$pid/environ"
    if [ -r "$env_file" ]; then
      conn="$(tr '\0' '\n' <"$env_file" | sed -n 's/^SSH_CONNECTION=//p' | head -n 1)" || conn=""
      [ -z "$conn" ] || break
    fi
    pid="$(awk '{ print $4 }' "/proc/$pid/stat" 2>/dev/null)" || pid=""
  done
  if [ -n "$conn" ]; then
    awk '{ print $4 }' <<<"$conn"
  fi
}

# Fills SSH_PORTS with every port an admin may be connected on: the session's, the one given to bootstrap, the ones in
# sshd_config and, on Ubuntu 24.04, the ones ssh.socket listens on (socket activation ignores "Port" in sshd_config).
# Dies instead of returning a short list: the firewall must never be enabled from a guess.
collect_ssh_ports() {
  local raw="" out
  out="$(session_ssh_port)" || out=""
  raw+="$out"$'\n'"$SSH_PORT"$'\n'
  out="$(sshd -T -f "$SSH_DIR/sshd_config")" || die "sshd -T failed; the SSH ports are unknown and the firewall was not touched"
  raw+="$(awk '$1 == "port" { print $2 }' <<<"$out")"$'\n'
  if out="$(systemctl show ssh.socket -p Listen --value 2>/dev/null)"; then
    raw+="$(sed -n 's/.*:\([0-9][0-9]*\) (Stream)$/\1/p' <<<"$out")"$'\n'
  fi
  SSH_PORTS=()
  local port
  while read -r port; do
    if [[ "$port" =~ ^[0-9]{1,5}$ ]] && [ "$port" -ge 1 ] && [ "$port" -le 65535 ]; then
      SSH_PORTS+=("$port")
    fi
  done < <(printf '%s' "$raw" | sort -un)
  [ "${#SSH_PORTS[@]}" -gt 0 ] || die "no SSH port found; the firewall was not touched"
}

ssh_ports() {
  collect_ssh_ports
  printf '%s\n' "${SSH_PORTS[@]}"
}

# The SSH ports are allowed before the default policy is set: the other order would cut the running session.
configure_firewall() {
  local before after port
  collect_ssh_ports
  printf 'ssh ports allowed: %s\n' "${SSH_PORTS[*]}"
  before="$(ufw status verbose 2>&1)" || before=""
  for port in "${SSH_PORTS[@]}"; do
    ufw allow "$port/tcp" >/dev/null
  done
  ufw allow 80/tcp >/dev/null
  ufw allow 443/tcp >/dev/null
  ufw default deny incoming >/dev/null
  ufw default allow outgoing >/dev/null
  ufw --force enable >/dev/null
  after="$(ufw status verbose 2>&1)"
  if [ "$before" = "$after" ]; then note_unchanged firewall; else note_changed firewall; fi
}

harden() {
  local rc=0
  apply_sshd_dropin || rc=$?
  case "$rc" in
    0)
      reload_sshd
      note_changed sshd-dropin
      ;;
    3) note_unchanged sshd-dropin ;;
    *) die "sshd drop-in failed" ;;
  esac
  configure_firewall
}

# --- entry -----------------------------------------------------------------------------------------------------------

usage() {
  printf 'usage: provision.sh --base <vars> | --harden <vars> | --has-deploy-key [<key>] | --add-deploy-key <key>\n' >&2
  printf '       | --remove-deploy-keys-except <key> | --remove-deploy-key <key> | --has-env\n' >&2
  printf '       | --has-age-recipient | --install-age-recipient <file>\n' >&2
  exit 2
}

main() {
  [ "$#" -ge 1 ] || usage
  local command="$1"
  shift
  require_root
  case "$command" in
    --base)
      [ "$#" -eq 1 ] || usage
      load_vars "$1"
      [ -n "$DOMAIN" ] && [ -n "$EMAIL" ] || die "the vars file needs DOMAIN and EMAIL"
      install_packages
      configure_unattended_upgrades
      ensure_deploy_user
      ensure_deploy_key_from_vars
      ensure_layout
      configure_nginx
      configure_backup_timer
      print_summary
      ;;
    --harden)
      [ "$#" -eq 1 ] || usage
      load_vars "$1"
      [ -n "$ADMIN_USER" ] || die "the vars file needs ADMIN_USER"
      harden
      print_summary
      ;;
    --has-deploy-key) cmd_has_deploy_key "$@" ;;
    --add-deploy-key)
      [ "$#" -eq 1 ] || usage
      cmd_add_deploy_key "$1"
      print_summary
      ;;
    --remove-deploy-keys-except)
      [ "$#" -eq 1 ] || usage
      cmd_remove_deploy_keys_except "$1"
      print_summary
      ;;
    --has-env) cmd_has_env ;;
    --remove-deploy-key)
      [ "$#" -eq 1 ] || usage
      cmd_remove_deploy_key "$1"
      print_summary
      ;;
    --has-age-recipient) cmd_has_age_recipient ;;
    --install-age-recipient)
      [ "$#" -eq 1 ] || usage
      cmd_install_age_recipient "$1"
      print_summary
      ;;
    *) usage ;;
  esac
}

# Sourced by the container checks, which call the functions above directly.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
