#!/usr/bin/env bash
# Runs the parts of provision.sh that can be exercised without a real server inside an ubuntu:24.04 container: the /srv
# layout with its owners and modes (nginx, running as www-data, must be able to stat flags/maintenance) and the sshd
# drop-in against a configuration that carries cloud-init's 50-cloud-init.conf with "PasswordAuthentication yes".
# Packages, the certificate, the firewall and the reload of sshd need a real host and are not covered here.
# Needs Docker; run by deploy-smoke.yml. Exits 0 with a SKIP message when Docker is unavailable.
set -euo pipefail

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    echo "SKIP: docker is not available."
    exit 0
fi

# Git Bash on Windows rewrites arguments that look like POSIX paths (docker cp, docker exec).
export MSYS_NO_PATHCONV=1

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && { pwd -W 2>/dev/null || pwd; })"
container="dit-provision-check-$$"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run -d --name "$container" ubuntu:24.04 sleep 900 >/dev/null
docker exec "$container" mkdir -p /tmp/provision
docker cp "$repo_root/deploy/bootstrap/remote/provision.sh" "$container:/tmp/provision/provision.sh"
docker cp "$repo_root/deploy/nginx" "$container:/tmp/provision/nginx"

docker exec -i "$container" bash -s <<'INNER'
set -euo pipefail

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

ok() {
    echo "ok: $*"
}

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq openssh-server sudo nginx openssl gettext-base >/dev/null

# The file defines functions only when sourced; main runs only when it is executed.
export APP_DIR=/srv/dev-interview-trainer
# shellcheck source=/dev/null
. /tmp/provision/provision.sh

# --- layout ----------------------------------------------------------------------------------------------------------

useradd --create-home deploy
id www-data >/dev/null

out="$(ensure_layout)"
[ "$out" = "changed layout" ] || fail "first ensure_layout printed: $out"
out="$(ensure_layout)"
[ "$out" = "unchanged layout" ] || fail "second ensure_layout printed: $out"
ok "layout is created once and left alone"

expect_dir() { # <path> <owner> <mode>
    local actual
    actual="$(stat -c '%U:%a' "$1")"
    [ "$actual" = "$2:$3" ] || fail "$1 is $actual, expected $2:$3"
}
expect_dir "$APP_DIR" deploy 755
expect_dir "$APP_DIR/flags" deploy 755
expect_dir "$APP_DIR/postgres" deploy 755
expect_dir "$APP_DIR/content/releases" deploy 755
expect_dir "$APP_DIR/backups" deploy 700
expect_dir "$APP_DIR/backup" deploy 750
expect_dir "$APP_DIR/bundles" deploy 755
expect_dir "$APP_DIR/staging" deploy 750
ok "owners and modes"

# A mode drifted by hand is repaired and reported.
chmod 700 "$APP_DIR/flags"
out="$(ensure_layout)"
[ "$out" = "changed layout" ] || fail "a drifted mode was not reported: $out"
expect_dir "$APP_DIR/flags" deploy 755
ok "drifted mode is repaired"

runuser -u deploy -- touch "$APP_DIR/flags/maintenance"
sudo -u www-data test -e "$APP_DIR/flags/maintenance" || fail "www-data cannot stat flags/maintenance"
ok "www-data can stat flags/maintenance"

# Negative control: with the old 700 mode the same check must fail, otherwise it proves nothing.
chmod 700 "$APP_DIR/flags"
if sudo -u www-data test -e "$APP_DIR/flags/maintenance"; then
    fail "the traversal check does not discriminate: www-data saw the flag through a 700 directory"
fi
chmod 755 "$APP_DIR/flags"
ok "a 700 flags directory hides the flag (the check discriminates)"

# --- sshd drop-in ----------------------------------------------------------------------------------------------------

mkdir -p /etc/ssh/sshd_config.d
printf 'PasswordAuthentication yes\n' >/etc/ssh/sshd_config.d/50-cloud-init.conf
mkdir -p /run/sshd
[ "$(sshd_effective passwordauthentication)" = "yes" ] || fail "the baseline does not reproduce cloud-init's override"

ADMIN_USER=root
rc=0
apply_sshd_dropin || rc=$?
[ "$rc" -eq 0 ] || fail "apply_sshd_dropin returned $rc on a first run"
[ "$(sshd_effective passwordauthentication)" = "no" ] || fail "passwordauthentication is not no"
[ "$(sshd_effective kbdinteractiveauthentication)" = "no" ] || fail "kbdinteractiveauthentication is not no"
case "$(sshd_effective permitrootlogin)" in
    prohibit-password | without-password) ;;
    *) fail "permitrootlogin is $(sshd_effective permitrootlogin)" ;;
esac
ok "10-dit.conf wins over 50-cloud-init.conf (root admin: key-only root login)"

rc=0
apply_sshd_dropin || rc=$?
[ "$rc" -eq 3 ] || fail "apply_sshd_dropin returned $rc on a repeat"
ok "repeat run changes nothing"

ADMIN_USER=admin
rc=0
apply_sshd_dropin || rc=$?
[ "$rc" -eq 0 ] || fail "apply_sshd_dropin returned $rc after the admin changed"
[ "$(sshd_effective permitrootlogin)" = "no" ] || fail "root login stays enabled for a non-root admin"
ok "non-root admin: root login is off"

# An earlier file with the same keyword wins over 10-dit.conf: the check must refuse and put the old content back.
printf 'PasswordAuthentication yes\n' >/etc/ssh/sshd_config.d/05-early.conf
before="$(cat /etc/ssh/sshd_config.d/10-dit.conf)"
ADMIN_USER=root
if (apply_sshd_dropin) 2>/dev/null; then
    fail "apply_sshd_dropin accepted a configuration that keeps password login on"
fi
[ "$(cat /etc/ssh/sshd_config.d/10-dit.conf)" = "$before" ] || fail "the previous drop-in was not restored"
rm -f /etc/ssh/sshd_config.d/05-early.conf
ok "an overriding earlier file is detected and the drop-in is reverted"

# --- firewall ports --------------------------------------------------------------------------------------------------

SSH_PORT=2222
ports=" $(ssh_ports | tr '\n' ' ')"
case "$ports" in
    *" 22 "*) ;;
    *) fail "ssh_ports lost the listening port: $ports" ;;
esac
case "$ports" in
    *" 2222 "*) ;;
    *) fail "ssh_ports lost the configured port: $ports" ;;
esac
ok "firewall ports include the listening and the configured SSH port"

# The match must be anchored: a list that holds only 2222 does not hold 22.
only=" 2222 "
case "$only" in
    *" 22 "*) fail "the port match is not anchored" ;;
esac

# Negative case: with sshd on 2200 and the bootstrap port 2222, port 22 must not appear out of nowhere.
sshd() { echo "port 2200"; }
ports=" $(ssh_ports | tr '\n' ' ')"
case "$ports" in
    *" 22 "*) fail "ssh_ports invented port 22: $ports" ;;
esac
case "$ports" in
    *" 2200 "*) ;;
    *) fail "ssh_ports lost the sshd port: $ports" ;;
esac
case "$ports" in
    *" 2222 "*) ;;
    *) fail "ssh_ports lost the bootstrap port: $ports" ;;
esac
ok "ports are matched exactly (2222 is not 22)"

# Ubuntu 24.04 socket activation: the port comes from ssh.socket, not from sshd_config.
systemctl() { if [ "$1" = show ]; then printf '[::]:2300 (Stream)\n0.0.0.0:2300 (Stream)\n'; fi; }
ports=" $(ssh_ports | tr '\n' ' ')"
case "$ports" in
    *" 2300 "*) ;;
    *) fail "the ssh.socket port is missing: $ports" ;;
esac
unset -f systemctl
ok "ssh.socket listen ports are included"

# A failing sshd -T must stop the run before ufw is touched.
sshd() { return 1; }
ufw() { fail "ufw was called although the SSH ports are unknown"; }
if (configure_firewall) 2>/dev/null; then
    fail "configure_firewall went on without a port list"
fi
unset -f sshd ufw
ok "no port list, no firewall change"

# --- nginx -----------------------------------------------------------------------------------------------------------

DOMAIN=dit.example.test
EMAIL=admin@example.com
mkdir -p /var/www/certbot "/etc/letsencrypt/live/$DOMAIN"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=$DOMAIN" \
    -keyout "/etc/letsencrypt/live/$DOMAIN/privkey.pem" -out "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" 2>/dev/null
rm -f /etc/nginx/sites-enabled/default
calls=/tmp/systemctl.calls
systemctl() { printf '%s\n' "$*" >>"$calls"; }

: >"$calls"
out="$(configure_nginx)"
grep -qx "changed nginx-conf" <<<"$out" || fail "first run did not install nginx.conf: $out"
grep -qx "changed nginx-site" <<<"$out" || fail "first run did not install the site: $out"
nginx -t 2>/dev/null || fail "the installed configuration does not load"
ok "nginx configuration is installed and loads"

: >"$calls"
out="$(configure_nginx)"
grep -qx "unchanged nginx-conf" <<<"$out" || fail "a repeat changed nginx.conf: $out"
grep -qx "unchanged nginx-site" <<<"$out" || fail "a repeat changed the site: $out"
if grep -q reload "$calls"; then fail "a repeat reloaded nginx"; fi
ok "a repeat changes and reloads nothing"

# A changed nginx.conf alone must reach the running server.
printf '# changed by the check\n' >>/tmp/provision/nginx/nginx.conf.template
: >"$calls"
out="$(configure_nginx)"
grep -qx "changed nginx-conf" <<<"$out" || fail "the new nginx.conf was not installed: $out"
grep -qx "unchanged nginx-site" <<<"$out" || fail "the site changed with it: $out"
[ "$(grep -c '^reload nginx$' "$calls")" -eq 1 ] || fail "a changed nginx.conf must cause exactly one reload; calls: $(tr '\n' ';' <"$calls")"
ok "a changed nginx.conf is reloaded"

# A valid file that nginx fails to load must not stay either, or a re-run would call it "unchanged" and never retry.
before="$(cat /etc/nginx/nginx.conf)"
printf '# valid but never loaded\n' >>/tmp/provision/nginx/nginx.conf.template
systemctl() { if [ "$1" = reload ]; then return 1; fi; printf '%s\n' "$*" >>"$calls"; }
if (configure_nginx) >/dev/null 2>&1; then
    fail "a failed reload was reported as success"
fi
[ "$(cat /etc/nginx/nginx.conf)" = "$before" ] || fail "the previous nginx.conf was not restored after the failed reload"
systemctl() { printf '%s\n' "$*" >>"$calls"; }
out="$(configure_nginx)"
grep -qx "changed nginx-conf" <<<"$out" || fail "the re-run did not see the change again: $out"
ok "a failed reload is rolled back and retried by the next run"

# A rejected nginx.conf must not stay in place.
before="$(cat /etc/nginx/nginx.conf)"
printf 'this is not a directive;\n' >>/tmp/provision/nginx/nginx.conf.template
: >"$calls"
if (configure_nginx) >/dev/null 2>&1; then
    fail "a broken nginx.conf was accepted"
fi
[ "$(cat /etc/nginx/nginx.conf)" = "$before" ] || fail "the previous nginx.conf was not restored"
nginx -t 2>/dev/null || fail "the restored configuration does not load"
if grep -q reload "$calls"; then fail "nginx was reloaded with a rejected file"; fi
unset -f systemctl
ok "a rejected nginx.conf is rolled back and not reloaded"

echo "provision-check: all checks passed"
INNER
