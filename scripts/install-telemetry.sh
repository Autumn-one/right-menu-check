#!/usr/bin/env sh
set -eu
umask 077

repository="${RMC_TELEMETRY_REPOSITORY:-Autumn-one/right-menu-check}"
release_tag="${RMC_TELEMETRY_RELEASE_TAG:-telemetry-v0.1.3}"
port="${RMC_TELEMETRY_PORT:-18787}"
test_mode="${RMC_TELEMETRY_TEST_MODE:-0}"
prefix="${RMC_TELEMETRY_TEST_ROOT:-}"
service=rightmenucheck-telemetry

fail() { printf '%s: %s\n' "$service" "$1" >&2; exit 1; }
case "$port" in ''|*[!0-9]*|0*) fail 'port must be 1-65535' ;; esac
[ "${#port}" -le 5 ] && [ "$port" -le 65535 ] || fail 'port must be 1-65535'
if [ "$test_mode" = 1 ]; then
    case "$prefix" in /*/?*) ;; *) fail 'test mode requires an absolute non-root test directory' ;; esac
    case "$prefix/" in */../*|*/./*) fail 'invalid test directory' ;; esac
else
    [ "$test_mode" = 0 ] && [ -z "$prefix" ] || fail 'invalid test configuration'
    [ "$(id -u)" -eq 0 ] || fail 'run as root'
    command -v systemctl >/dev/null || fail 'systemd is required'
fi
for command_name in curl tar openssl sha256sum awk sed mktemp install; do
    command -v "$command_name" >/dev/null || fail "missing command: $command_name"
done
printf '%s' "$repository" | grep -Eq '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' || fail 'invalid repository'
printf '%s' "$release_tag" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' || fail 'invalid release tag'
case "$(uname -m)" in
    x86_64|amd64) architecture=amd64 ;;
    aarch64|arm64) architecture=arm64 ;;
    *) fail 'unsupported architecture' ;;
esac

binary="$prefix/usr/local/bin/$service"
environment="$prefix/etc/$service/environment"
unit="$prefix/etc/systemd/system/$service.service"
data="$prefix/var/lib/$service"
legacy_proxy="$prefix/etc/nginx/conf.d/$service.conf"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
trap 'exit 1' HUP INT TERM
asset="$service-linux-$architecture.tar.gz"
url="${RMC_TELEMETRY_ASSET_URL:-https://github.com/$repository/releases/download/$release_tag/$asset}"
sources='https://ghfast.top/ https://gh-proxy.com/ DIRECT'
if [ -n "${RMC_TELEMETRY_ASSET_URL:-}" ]; then sources=DIRECT; fi
cat > "$work/public.pem" <<'KEY'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESwMo9w7T5s/zAVmAL07w1ielgu7F
gYvZJ/nI2l/uAsRMZqjXBZevKcdv/rXcBkwJkZPli2OWeCZYrOF1hDOUEg==
-----END PUBLIC KEY-----
KEY
verified=0
for source in $sources; do
    candidate="$source$url"
    if [ "$source" = DIRECT ]; then candidate="$url"; fi
    if curl -fsSL --connect-timeout 8 --max-time 180 -o "$work/package" "$candidate" &&
       curl -fsSL --connect-timeout 8 --max-time 30 -o "$work/checksum" "$candidate.sha256" &&
       curl -fsSL --connect-timeout 8 --max-time 30 -o "$work/signature" "$candidate.sha256.sig" &&
       openssl dgst -sha256 -verify "$work/public.pem" -signature "$work/signature" "$work/checksum" >/dev/null 2>&1; then
        expected="$(awk 'NR==1 {print tolower($1)}' "$work/checksum")"
        actual="$(sha256sum "$work/package" | awk '{print $1}')"
        if [ "$expected" = "$actual" ]; then verified=1; break; fi
    fi
done
[ "$verified" = 1 ] || fail 'download or signature verification failed'
tar -tzf "$work/package" > "$work/entries"
if grep -Eq '(^/|(^|/)\.\.(/|$)|\\)' "$work/entries"; then fail 'unsafe archive path'; fi
mkdir "$work/payload"
tar -xzf "$work/package" --no-same-owner --no-same-permissions -C "$work/payload"
for file in "$service" "$service.service" VERSION; do
    [ -f "$work/payload/$file" ] && [ ! -L "$work/payload/$file" ] || fail 'invalid package contents'
done
version="$(cat "$work/payload/VERSION")"
case "$version" in 0.1.0|0.1.1) fail 'single-port deployment requires telemetry 0.1.2 or newer' ;; esac
if [ "$test_mode" = 0 ]; then
    chmod 0755 "$work/payload/$service"
    [ "$("$work/payload/$service" --version)" = "$version" ] || fail 'binary version mismatch'
fi

# Preserve the previous binary, unit and credentials before migration.
for item in binary environment unit; do
    case "$item" in binary) path="$binary" ;; environment) path="$environment" ;; unit) path="$unit" ;; esac
    [ ! -L "$path" ] || fail 'installation file must not be a symlink'
    if [ -f "$path" ]; then cp -p "$path" "$work/old-$item"; fi
done
was_active=0
was_enabled=0
if [ "$test_mode" = 0 ]; then
    if systemctl is-active --quiet "$service"; then was_active=1; fi
    if systemctl is-enabled --quiet "$service"; then was_enabled=1; fi
    if ! id "$service" >/dev/null 2>&1; then
        useradd --system --user-group --home-dir "$data" --shell /usr/sbin/nologin "$service"
    fi
    install -d -m 0700 -o "$service" -g "$service" "$data"
    install -d -m 0700 -o root -g root "$(dirname "$environment")"
else
    mkdir -p "$data" "$(dirname "$environment")"
fi
if [ -f "$environment" ]; then
    token="$(sed -n 's/^RMC_TELEMETRY_ADMIN_TOKEN=//p' "$environment" | head -n 1 | tr -d '\r')"
    [ "${#token}" -ge 32 ] && printf '%s' "$token" | grep -Eq '^[^[:space:]]+$' || fail 'existing admin token is invalid'
    awk '!/^RMC_TELEMETRY_LISTEN_ADDRESS=/ && !/^RMC_TELEMETRY_ALLOW_PUBLIC_LISTENER=/ && !/^RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN=/' "$environment" > "$work/environment"
else
    token="$(openssl rand -hex 32)"
    printf 'RMC_TELEMETRY_ADMIN_TOKEN=%s\nRMC_TELEMETRY_DATABASE_PATH=%s/telemetry.db\n' "$token" "$data" > "$work/environment"
fi
printf 'RMC_TELEMETRY_LISTEN_ADDRESS=0.0.0.0:%s\n' "$port" >> "$work/environment"

restore() {
    for item in binary environment unit; do
        case "$item" in binary) path="$binary" ;; environment) path="$environment" ;; unit) path="$unit" ;; esac
        if [ -f "$work/old-$item" ]; then
            cp -p "$work/old-$item" "$path.restore"
            mv -f "$path.restore" "$path"
        else
            rm -f "$path"
        fi
    done
    if [ -f "$work/old-proxy" ]; then
        cp -p "$work/old-proxy" "$legacy_proxy"
        if command -v nginx >/dev/null 2>&1 && systemctl is-active --quiet nginx; then
            nginx -t && systemctl reload nginx
        fi
    fi
    systemctl daemon-reload
    if [ "$was_enabled" = 1 ]; then systemctl enable "$service"; else systemctl disable "$service"; fi
    if [ "$was_active" = 1 ]; then systemctl restart "$service"; else systemctl stop "$service"; fi
}

mkdir -p "$(dirname "$binary")" "$(dirname "$unit")"
if [ "$test_mode" = 0 ]; then
    install -m 0755 "$work/payload/$service" "$binary.new"
    install -m 0644 "$work/payload/$service.service" "$unit"
    install -m 0600 "$work/environment" "$environment"
else
    cp "$work/payload/$service" "$binary.new"
    cp "$work/payload/$service.service" "$unit"
    cp "$work/environment" "$environment"
fi
mv -f "$binary.new" "$binary"
if [ "$test_mode" = 0 ]; then
    # This exact file was owned by the previous installer; other sites stay intact.
    if [ -f "$legacy_proxy" ]; then
        [ ! -L "$legacy_proxy" ] || fail 'legacy proxy configuration must not be a symlink'
        cp -p "$legacy_proxy" "$work/old-proxy"
        rm -f "$legacy_proxy"
        if command -v nginx >/dev/null 2>&1; then
            if ! nginx -t; then restore; fail 'legacy proxy removal failed validation'; fi
            if systemctl is-active --quiet nginx && ! systemctl reload nginx; then
                restore
                fail 'legacy proxy reload failed'
            fi
        fi
    fi
    if ! systemctl daemon-reload || ! systemctl enable "$service" || ! systemctl restart "$service"; then
        restore
        fail 'service activation failed; previous installation restored'
    fi
    healthy=0
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        if systemctl is-active --quiet "$service" &&
           curl --noproxy '*' -fsS --max-time 2 "http://127.0.0.1:$port/health" > "$work/health" &&
           grep -q '"status":"ok"' "$work/health"; then healthy=1; break; fi
        sleep 1
    done
    if [ "$healthy" != 1 ]; then
        restore
        fail 'health check failed; previous installation restored'
    fi
fi
printf 'Installed telemetry %s on 0.0.0.0:%s\n' "$version" "$port"
printf 'Admin token remains in %s\n' "$environment"
