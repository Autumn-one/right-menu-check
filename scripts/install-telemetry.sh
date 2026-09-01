#!/usr/bin/env sh
set -eu

umask 077

repository="${RMC_TELEMETRY_REPOSITORY:-Autumn-one/right-menu-check}"
release_tag="${RMC_TELEMETRY_RELEASE_TAG:-latest}"
server_name="${RMC_TELEMETRY_SERVER_NAME:-}"
admin_allow="${RMC_TELEMETRY_ADMIN_ALLOW:-}"
tls_certificate="${RMC_TELEMETRY_TLS_CERTIFICATE:-}"
tls_key="${RMC_TELEMETRY_TLS_KEY:-}"
asset_url_override="${RMC_TELEMETRY_ASSET_URL:-}"
skip_service_start="${RMC_TELEMETRY_SKIP_SERVICE_START:-0}"
skip_nginx="${RMC_TELEMETRY_SKIP_NGINX:-0}"
allow_insecure_admin="${RMC_TELEMETRY_ALLOW_INSECURE_ADMIN:-0}"
test_mode="${RMC_TELEMETRY_TEST_MODE:-0}"
test_root="${RMC_TELEMETRY_TEST_ROOT:-}"
service_name="rightmenucheck-telemetry"
service_user="rightmenucheck-telemetry"
path_prefix=""
if [ -n "$test_root" ]; then
    [ "$test_mode" = "1" ] || {
        printf 'rightmenucheck-telemetry installer: test root requires test mode\n' >&2
        exit 1
    }
    case "$test_root" in
        /*) ;;
        *) printf 'rightmenucheck-telemetry installer: test root must be absolute\n' >&2; exit 1 ;;
    esac
    case "/$test_root/" in
        */../*|*/./*)
            printf 'rightmenucheck-telemetry installer: test root is not canonical\n' >&2
            exit 1
            ;;
    esac
    [ "$skip_service_start" = "1" ] && [ "$skip_nginx" = "1" ] || {
        printf 'rightmenucheck-telemetry installer: test mode cannot manage services\n' >&2
        exit 1
    }
    path_prefix="$test_root"
fi
[ "$test_mode" = "0" ] || [ -n "$test_root" ] || {
    printf 'rightmenucheck-telemetry installer: test mode requires a test root\n' >&2
    exit 1
}
binary_path="$path_prefix/usr/local/bin/rightmenucheck-telemetry"
state_directory="$path_prefix/var/lib/rightmenucheck-telemetry"
config_directory="$path_prefix/etc/rightmenucheck-telemetry"
environment_path="$config_directory/environment"
unit_path="$path_prefix/etc/systemd/system/$service_name.service"
nginx_path="$path_prefix/etc/nginx/conf.d/$service_name.conf"
temporary_directory=""

fail() {
    printf 'rightmenucheck-telemetry installer: %s\n' "$1" >&2
    exit 1
}

cleanup() {
    if [ -n "$temporary_directory" ] && [ -d "$temporary_directory" ]; then
        rm -rf -- "$temporary_directory"
    fi
}
trap cleanup EXIT HUP INT TERM

[ "$test_mode" = "0" ] || [ "$test_mode" = "1" ] ||
    fail "RMC_TELEMETRY_TEST_MODE must be 0 or 1"
if [ "$test_mode" = "0" ]; then
    [ "$(id -u)" -eq 0 ] || fail "run this installer as root"
fi
[ "$skip_service_start" = "0" ] || [ "$skip_service_start" = "1" ] ||
    fail "RMC_TELEMETRY_SKIP_SERVICE_START must be 0 or 1"
[ "$skip_nginx" = "0" ] || [ "$skip_nginx" = "1" ] ||
    fail "RMC_TELEMETRY_SKIP_NGINX must be 0 or 1"
[ "$allow_insecure_admin" = "0" ] || [ "$allow_insecure_admin" = "1" ] ||
    fail "RMC_TELEMETRY_ALLOW_INSECURE_ADMIN must be 0 or 1"
printf '%s' "$repository" | grep -Eq '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' ||
    fail "repository must use owner/name format"

machine_architecture="$(uname -m)"
case "$machine_architecture" in
    x86_64|amd64) asset_architecture="amd64" ;;
    aarch64|arm64) asset_architecture="arm64" ;;
    *) fail "unsupported Linux architecture: $machine_architecture" ;;
esac

for command_name in curl tar awk sed grep install mktemp openssl dirname; do
    command -v "$command_name" >/dev/null 2>&1 || fail "missing command: $command_name"
done

if command -v sha256sum >/dev/null 2>&1; then
    hash_file() { sha256sum "$1" | awk '{ print $1 }'; }
elif command -v shasum >/dev/null 2>&1; then
    hash_file() { shasum -a 256 "$1" | awk '{ print $1 }'; }
else
    fail "sha256sum or shasum is required"
fi

asset_name="rightmenucheck-telemetry-linux-$asset_architecture.tar.gz"
if [ -n "$asset_url_override" ]; then
    case "$asset_url_override" in
        http://*|https://*) asset_url="$asset_url_override" ;;
        *) fail "RMC_TELEMETRY_ASSET_URL must be an HTTP(S) URL" ;;
    esac
    download_prefixes="DIRECT"
else
    if [ "$release_tag" = "latest" ]; then
        asset_url="https://github.com/$repository/releases/latest/download/$asset_name"
    else
        printf '%s' "$release_tag" | grep -Eq '^v?[0-9A-Za-z][0-9A-Za-z._-]*$' ||
            fail "release tag contains unsupported characters"
        asset_url="https://github.com/$repository/releases/download/$release_tag/$asset_name"
    fi
    download_prefixes="https://ghfast.top/ https://gh-proxy.com/ DIRECT"
fi

temporary_directory="$(mktemp -d)"
archive_path="$temporary_directory/$asset_name"
checksum_path="$archive_path.sha256"
signature_path="$checksum_path.sig"
public_key_path="$temporary_directory/update-public-key.pem"
cat > "$public_key_path" <<'PUBLIC_KEY'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESwMo9w7T5s/zAVmAL07w1ielgu7F
gYvZJ/nI2l/uAsRMZqjXBZevKcdv/rXcBkwJkZPli2OWeCZYrOF1hDOUEg==
-----END PUBLIC KEY-----
PUBLIC_KEY
downloaded=0
for prefix in $download_prefixes; do
    if [ "$prefix" = "DIRECT" ]; then
        candidate_url="$asset_url"
    else
        candidate_url="$prefix$asset_url"
    fi
    rm -f -- "$archive_path" "$checksum_path" "$signature_path"
    if curl -fsSL --retry 2 --connect-timeout 10 --max-time 300 \
            -o "$archive_path" "$candidate_url" &&
       curl -fsSL --retry 2 --connect-timeout 10 --max-time 60 \
            -o "$checksum_path" "$candidate_url.sha256" &&
       curl -fsSL --retry 2 --connect-timeout 10 --max-time 60 \
            -o "$signature_path" "$candidate_url.sha256.sig" &&
       openssl dgst -sha256 -verify "$public_key_path" \
            -signature "$signature_path" "$checksum_path" >/dev/null 2>&1; then
        expected_hash="$(awk 'NR == 1 { print tolower($1) }' "$checksum_path")"
        actual_hash="$(hash_file "$archive_path" | tr 'A-F' 'a-f')"
        if printf '%s' "$expected_hash" | grep -Eq '^[0-9a-f]{64}$' &&
           [ "$expected_hash" = "$actual_hash" ]; then
            downloaded=1
            break
        fi
    fi
done
[ "$downloaded" -eq 1 ] || fail "no release source supplied a valid package and checksum"

package_directory="$temporary_directory/package"
mkdir -p "$package_directory"
archive_listing="$temporary_directory/archive-list.txt"
tar -tzf "$archive_path" > "$archive_listing" || fail "package archive cannot be listed"
if grep -Eq '(^/|(^|/)\.\.(/|$)|\\)' "$archive_listing"; then
    fail "package archive contains an unsafe path"
fi
tar -xzf "$archive_path" --no-same-owner --no-same-permissions -C "$package_directory"
for package_file in \
    rightmenucheck-telemetry \
    rightmenucheck-telemetry.service \
    rightmenucheck-telemetry.nginx.conf.template \
    VERSION; do
    [ -f "$package_directory/$package_file" ] &&
        [ ! -L "$package_directory/$package_file" ] ||
        fail "package is missing or links $package_file"
done
package_version="$(cat "$package_directory/VERSION")"
printf '%s' "$package_version" | grep -Eq \
    '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)([-+][0-9A-Za-z.-]+)?$' ||
    fail "package version is invalid"

previous_binary="$temporary_directory/previous-binary"
previous_unit="$temporary_directory/previous-unit"
had_previous_binary=0
had_previous_unit=0
service_was_active=0
service_was_enabled=0
if [ -f "$binary_path" ]; then
    cp -p -- "$binary_path" "$previous_binary"
    had_previous_binary=1
fi
if [ -f "$unit_path" ]; then
    cp -p -- "$unit_path" "$previous_unit"
    had_previous_unit=1
fi
if [ "$test_mode" = "0" ] && command -v systemctl >/dev/null 2>&1; then
    if systemctl is-active --quiet "$service_name.service"; then
        service_was_active=1
    fi
    if systemctl is-enabled --quiet "$service_name.service"; then
        service_was_enabled=1
    fi
fi

restore_service_install() {
    if [ "$had_previous_binary" -eq 1 ]; then
        cp -p -- "$previous_binary" "$binary_path.restore"
        mv -f -- "$binary_path.restore" "$binary_path"
    else
        rm -f -- "$binary_path"
    fi
    if [ "$had_previous_unit" -eq 1 ]; then
        cp -p -- "$previous_unit" "$unit_path.restore"
        mv -f -- "$unit_path.restore" "$unit_path"
    else
        rm -f -- "$unit_path"
    fi

    systemctl daemon-reload >/dev/null 2>&1 || true
    if [ "$service_was_enabled" -eq 1 ]; then
        systemctl enable "$service_name.service" >/dev/null 2>&1 || true
    else
        systemctl disable "$service_name.service" >/dev/null 2>&1 || true
    fi
    if [ "$service_was_active" -eq 1 ]; then
        systemctl restart "$service_name.service" >/dev/null 2>&1 || true
    else
        systemctl stop "$service_name.service" >/dev/null 2>&1 || true
    fi
}

if [ "$test_mode" = "0" ]; then
    if ! id "$service_user" >/dev/null 2>&1; then
        if command -v useradd >/dev/null 2>&1; then
            useradd --system --home-dir "$state_directory" --shell /usr/sbin/nologin "$service_user"
        elif command -v adduser >/dev/null 2>&1; then
            adduser --system --home "$state_directory" --no-create-home \
                --disabled-login --disabled-password "$service_user"
        else
            fail "useradd or adduser is required"
        fi
    fi
fi

if [ "$test_mode" = "1" ]; then
    mkdir -p "$state_directory" "$config_directory"
else
    install -d -m 0700 -o "$service_user" -g "$service_user" "$state_directory"
    install -d -m 0700 -o root -g root "$config_directory"
fi
if [ ! -f "$environment_path" ]; then
    if command -v openssl >/dev/null 2>&1; then
        admin_token="$(openssl rand -hex 32)"
    else
        admin_token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
    fi
    [ "${#admin_token}" -ge 32 ] || fail "failed to generate an admin token"
    {
        printf 'RMC_TELEMETRY_LISTEN_ADDRESS=127.0.0.1:8787\n'
        printf 'RMC_TELEMETRY_DATABASE_PATH=%s/telemetry.db\n' "$state_directory"
        printf 'RMC_TELEMETRY_ADMIN_TOKEN=%s\n' "$admin_token"
    } > "$environment_path"
    if [ "$test_mode" = "0" ]; then
        chmod 0600 "$environment_path"
    fi
else
    configured_token="$(sed -n 's/^RMC_TELEMETRY_ADMIN_TOKEN=//p' "$environment_path" | head -n 1)"
    [ "${#configured_token}" -ge 32 ] &&
        printf '%s' "$configured_token" | grep -Eq '^[^[:space:]]+$' ||
        fail "existing admin token is missing, too short, or contains whitespace"
fi

new_binary="$binary_path.new"
if [ "$test_mode" = "1" ]; then
    mkdir -p "$(dirname "$binary_path")" "$(dirname "$unit_path")"
    cp -- "$package_directory/rightmenucheck-telemetry" "$new_binary"
else
    install -d -m 0755 "$(dirname "$binary_path")"
    install -d -m 0755 "$(dirname "$unit_path")"
    install -m 0755 -o root -g root "$package_directory/rightmenucheck-telemetry" "$new_binary"
fi
mv -f -- "$new_binary" "$binary_path"
if [ "$test_mode" = "1" ]; then
    cp -- "$package_directory/rightmenucheck-telemetry.service" "$unit_path"
else
    install -m 0644 -o root -g root \
        "$package_directory/rightmenucheck-telemetry.service" "$unit_path"
fi

if [ "$skip_service_start" = "0" ]; then
    command -v systemctl >/dev/null 2>&1 || fail "systemd is required"
    if ! systemctl daemon-reload ||
       ! systemctl enable "$service_name.service" ||
       ! systemctl restart "$service_name.service"; then
        restore_service_install
        fail "service activation failed; the previous installation was restored"
    fi
    healthy=0
    attempt=0
    while [ "$attempt" -lt 30 ]; do
        if curl -fsS --max-time 2 http://127.0.0.1:8787/health >/dev/null; then
            healthy=1
            break
        fi
        attempt=$((attempt + 1))
        sleep 1
    done
    if [ "$healthy" -ne 1 ]; then
        restore_service_install
        fail "service did not become healthy; the previous installation was restored"
    fi
fi

if [ "$skip_nginx" = "0" ]; then
    [ -n "$server_name" ] || fail "RMC_TELEMETRY_SERVER_NAME is required for Nginx"
    printf '%s' "$server_name" | grep -Eq '^[A-Za-z0-9.-]+$' ||
        fail "server name contains unsupported characters"
    if ! command -v nginx >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then
            apt-get update
            DEBIAN_FRONTEND=noninteractive apt-get install -y nginx
        elif command -v dnf >/dev/null 2>&1; then
            dnf install -y nginx
        elif command -v yum >/dev/null 2>&1; then
            yum install -y nginx
        elif command -v apk >/dev/null 2>&1; then
            apk add --no-cache nginx
        else
            fail "install Nginx or set RMC_TELEMETRY_SKIP_NGINX=1"
        fi
    fi

    if [ -n "$admin_allow" ]; then
        printf '%s' "$admin_allow" | grep -Eq '^[0-9A-Fa-f:.]+(/[0-9]{1,3})?$' ||
            fail "RMC_TELEMETRY_ADMIN_ALLOW is not an IP address or CIDR"
        admin_allow_directive="        allow $admin_allow;"
    else
        admin_allow_directive=""
    fi

    if [ -n "$tls_certificate" ] || [ -n "$tls_key" ]; then
        printf '%s\n%s\n' "$tls_certificate" "$tls_key" |
            grep -Eq '^/[A-Za-z0-9_./-]+$' ||
            fail "TLS paths contain unsupported characters"
        [ -f "$tls_certificate" ] && [ -f "$tls_key" ] ||
            fail "both TLS certificate and key must exist"
        listen_directive="listen 443 ssl;"
        tls_directives="    ssl_certificate $tls_certificate;\n    ssl_certificate_key $tls_key;\n    ssl_protocols TLSv1.2 TLSv1.3;"
    else
        if [ -n "$admin_allow" ] && [ "$allow_insecure_admin" != "1" ]; then
            fail "refusing to expose the admin dashboard over plaintext HTTP"
        fi
        listen_directive="listen 80;"
        tls_directives=""
    fi

    generated_nginx="$temporary_directory/$service_name.conf"
    sed \
        -e "s|@@LISTEN_DIRECTIVE@@|$listen_directive|" \
        -e "s|@@SERVER_NAME@@|$server_name|" \
        -e "s|@@ADMIN_ALLOW_DIRECTIVE@@|$admin_allow_directive|" \
        "$package_directory/rightmenucheck-telemetry.nginx.conf.template" |
        awk -v tls="$tls_directives" \
            '{ if ($0 == "@@TLS_DIRECTIVES@@") { print tls } else { print } }' > "$generated_nginx"
    install -d -m 0755 -o root -g root /etc/nginx/conf.d
    previous_nginx=""
    nginx_was_active=0
    nginx_was_enabled=0
    if command -v systemctl >/dev/null 2>&1; then
        if systemctl is-active --quiet nginx; then
            nginx_was_active=1
        fi
        if systemctl is-enabled --quiet nginx; then
            nginx_was_enabled=1
        fi
    fi
    if [ -f "$nginx_path" ]; then
        previous_nginx="$temporary_directory/previous-nginx.conf"
        cp -p -- "$nginx_path" "$previous_nginx"
    fi
    install -m 0644 -o root -g root "$generated_nginx" "$nginx_path"
    if ! nginx -t; then
        if [ -n "$previous_nginx" ]; then
            cp -p -- "$previous_nginx" "$nginx_path"
        else
            rm -f -- "$nginx_path"
        fi
        fail "Nginx rejected the generated configuration"
    fi
    if command -v systemctl >/dev/null 2>&1; then
        if ! systemctl enable --now nginx || ! systemctl reload nginx; then
            if [ -n "$previous_nginx" ]; then
                cp -p -- "$previous_nginx" "$nginx_path"
            else
                rm -f -- "$nginx_path"
            fi
            if [ "$nginx_was_enabled" -eq 1 ]; then
                systemctl enable nginx >/dev/null 2>&1 || true
            else
                systemctl disable nginx >/dev/null 2>&1 || true
            fi
            if [ "$nginx_was_active" -eq 1 ]; then
                systemctl reload nginx >/dev/null 2>&1 || true
            else
                systemctl stop nginx >/dev/null 2>&1 || true
            fi
            fail "Nginx activation failed; the previous configuration was restored"
        fi
    else
        if ! nginx -s reload; then
            if [ -n "$previous_nginx" ]; then
                cp -p -- "$previous_nginx" "$nginx_path"
            else
                rm -f -- "$nginx_path"
            fi
            nginx -s reload >/dev/null 2>&1 || true
            fail "Nginx reload failed; the previous configuration was restored"
        fi
    fi
fi

printf 'RightMenuCheck telemetry %s installed.\n' "$package_version"
printf 'Admin token file: %s\n' "$environment_path"
printf 'Local dashboard: http://127.0.0.1:8787/\n'
