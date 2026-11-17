#!/usr/bin/env bash
# Renders the nginx templates and exercises them in an ubuntu:24.04 container, with the nginx package production gets.
# Fails when the configuration does not load, when a request is routed wrongly, or when any request detail reaches a log file.
# Needs Docker; run by deploy-smoke.yml. Exits 0 with a SKIP message when Docker is unavailable.
set -euo pipefail

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    echo "SKIP: docker is not available."
    exit 0
fi

# Git Bash on Windows rewrites arguments that look like POSIX paths (docker cp, docker exec).
export MSYS_NO_PATHCONV=1

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && { pwd -W 2>/dev/null || pwd; })"
container="dit-nginx-check-$$"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run -d --name "$container" ubuntu:24.04 sleep 600 >/dev/null
docker cp "$repo_root/deploy/nginx" "$container:/tmp/templates"

docker exec -i "$container" bash -s <<'INNER'
set -euo pipefail

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

expect_status() {
    local expected="$1" actual="$2" what="$3"
    [ "$actual" = "$expected" ] || fail "$what: expected HTTP $expected, got $actual"
    echo "ok: $what -> $actual"
}

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq nginx openssl curl gettext-base >/dev/null

export DOMAIN=dit.example.test
export APP_DIR=/srv/dit
mkdir -p "$APP_DIR/flags" /var/www/certbot "/etc/letsencrypt/live/$DOMAIN"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=$DOMAIN" \
    -keyout "/etc/letsencrypt/live/$DOMAIN/privkey.pem" -out "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" 2>/dev/null

nginx -v
render() { envsubst '${DOMAIN} ${APP_DIR}' < "/tmp/templates/$1" > "$2"; }

render nginx.conf.template /etc/nginx/nginx.conf
rm -f /etc/nginx/sites-enabled/default

render dev-interview-trainer.http.conf.template /etc/nginx/sites-enabled/dev-interview-trainer.conf
nginx -t
echo "ok: http-only template loads"

render dev-interview-trainer.conf.template /etc/nginx/sites-enabled/dev-interview-trainer.conf
nginx -t
echo "ok: full site template loads"

rm -f /var/log/nginx/*
nginx

# Nothing listens on 8080 or 3001, so every proxied request is an upstream failure.
site=(curl -sk -o /dev/null -w '%{http_code}' --resolve "$DOMAIN:443:127.0.0.1" --resolve "$DOMAIN:80:127.0.0.1")

expect_status 502 "$("${site[@]}" "https://$DOMAIN/page?q=SENTINEL")" "site request with an unreachable upstream"
expect_status 301 "$("${site[@]}" "http://$DOMAIN/page?q=SENTINEL")" "port 80 redirect"
expect_status 404 "$("${site[@]}" "https://$DOMAIN/internal/ready?q=SENTINEL")" "/internal/"
expect_status 404 "$("${site[@]}" "https://$DOMAIN/INTERNAL/ready?q=SENTINEL")" "/INTERNAL/ (other letter case)"
expect_status 404 "$("${site[@]}" "https://$DOMAIN/stats/other?q=SENTINEL")" "other /stats/ path"
expect_status 404 "$("${site[@]}" "https://$DOMAIN/stats?q=SENTINEL")" "/stats without a slash"
expect_status 502 "$("${site[@]}" "https://$DOMAIN/stats/script.js?q=SENTINEL")" "/stats/script.js"

mkdir -p /var/www/certbot/.well-known/acme-challenge
echo token > /var/www/certbot/.well-known/acme-challenge/probe
expect_status 200 "$("${site[@]}" "http://$DOMAIN/.well-known/acme-challenge/probe")" "ACME challenge on port 80"

hsts="$(curl -sk -D - -o /dev/null --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/page" | tr -d '\r' | grep -i '^strict-transport-security:' || true)"
[ -n "$hsts" ] || fail "Strict-Transport-Security header is missing"
echo "ok: $hsts"

# Rejected host name: the default servers answer 444 (no response) on port 80 and refuse the TLS handshake on 443.
foreign_http="$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: foreign.test' "http://127.0.0.1/page?q=SENTINEL" || true)"
expect_status 000 "$foreign_http" "foreign Host on port 80"
foreign_tls="$(curl -sk -o /dev/null -w '%{http_code}' --resolve foreign.test:443:127.0.0.1 "https://foreign.test/page?q=SENTINEL" || true)"
expect_status 000 "$foreign_tls" "foreign host name on port 443"

# Requests nginx rejects before routing must not leave the query in the error log either.
expect_status 400 "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:443/page?q=SENTINEL")" "plaintext HTTP sent to the TLS port"
exec 3<>/dev/tcp/127.0.0.1/80
printf 'GET /a b?q=SENTINEL c d\r\n\r\n' >&3
malformed="$(head -n 1 <&3 | tr -d '\r')"
exec 3>&-
case "$malformed" in
    *" 400 "*) echo "ok: malformed request line -> 400" ;;
    *) fail "malformed request line: expected 400, got '$malformed'" ;;
esac

head -c 100000 /dev/zero > /tmp/oversized.bin
expect_status 413 "$("${site[@]}" -X POST --data-binary @/tmp/oversized.bin "https://$DOMAIN/stats/api/send?q=SENTINEL")" "oversized /stats/api/send body"

# A parallel burst far above the 10r/s rate and the burst of 20 does not depend on how fast the loop runs.
statuses="$(seq 1 80 | xargs -P 40 -I{} curl -sk -o /dev/null -w '%{http_code}\n' -X POST -d '{}' \
    --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/stats/api/send?q=SENTINEL")"
echo "$statuses" | grep -q '^502$' || fail "no request to /stats/api/send reached the upstream"
echo "$statuses" | grep -q '^429$' || fail "requests over the rate limit were not rejected with 429"
echo "ok: /stats/api/send is rate limited"

touch "$APP_DIR/flags/maintenance"
expect_status 503 "$("${site[@]}" "https://$DOMAIN/page?q=SENTINEL")" "maintenance flag on /"
expect_status 503 "$("${site[@]}" -X POST -d '{}' "https://$DOMAIN/stats/api/send?q=SENTINEL")" "maintenance flag on /stats/api/send"
rm -f "$APP_DIR/flags/maintenance"

nginx -s quit
sleep 1

if grep -rl 'SENTINEL' /var/log/nginx; then
    fail "a request query reached a log file"
fi
if grep -rl '127\.0\.0\.1' /var/log/nginx; then
    fail "a client address reached a log file"
fi
[ -s /var/log/nginx/dev-interview-trainer.access.log ] || fail "the site access log is empty"
echo "ok: logs contain no query string and no address"
echo "nginx-check passed"
INNER
