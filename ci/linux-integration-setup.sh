#!/usr/bin/env bash
# Prepares an Ubuntu 24.04 machine (a GitHub-hosted runner, or a developer box) for the real-server
# integration tests in mRemoteNG.Tests.CrossPlatform. Safe to run more than once.
#
# What it does:
#   * installs the apt packages the tests look for (sshd, TigerVNC, x11vnc/Xvfb, ImageMagick, xdotool,
#     socat, inetutils telnetd, FreeRDP 3, xrdp, MariaDB, OpenLDAP slapd, ...);
#   * downloads a pinned OpenBao release (SHA-256 checked) for the Vault/OpenBao tests;
#   * makes sure pwsh is available (Microsoft's apt repository when it is missing);
#   * starts the two servers the tests do not start themselves:
#       - a private MariaDB on 127.0.0.1:3307 (user mrng / mrng-pass, own data directory);
#       - xrdp on 3389 (reused when something already listens there) with a local test user whose
#         session runs a window manager;
#   * exports the test settings (RDP_TEST_*, MRNG_TEST_MYSQL_*, MREMOTENG_BAO_PATH) to $GITHUB_ENV in
#     GitHub Actions and writes them to $MRNG_IT_STATE/env.sh for local use.
#
# sshd, Xvnc, x11vnc, Xvfb, telnetd, slapd and OpenBao are started by the test fixtures themselves.
# Several fixtures (sshd on 2222/2230, the Vault sshd end-to-end test) require the tests to run as root.
#
# Settings (environment variables, all optional):
#   MRNG_IT_STATE        state directory (MariaDB data, logs, env.sh)    [/var/tmp/mrng-integration]
#   MRNG_IT_TOOLS        where the OpenBao binary is installed           [/opt/mrng-integration]
#   RDP_TEST_USER        local account the RDP test logs in with         [mrngrdp]
#   RDP_TEST_PASS        its password                                    [random, kept in $MRNG_IT_STATE]
#   MRNG_IT_SKIP_RDP=1, MRNG_IT_SKIP_MYSQL=1, MRNG_IT_SKIP_BAO=1, MRNG_IT_SKIP_APT=1  leave that part out
#   MRNG_IT_REPLACE_MYSQL=1  replace an installed MySQL with MariaDB (done automatically in GitHub Actions only)

set -euo pipefail

STATE_DIR="${MRNG_IT_STATE:-/var/tmp/mrng-integration}"
TOOLS_DIR="${MRNG_IT_TOOLS:-/opt/mrng-integration}"

OPENBAO_VERSION="2.4.1"
# SHA-256 of bao_<version>_Linux_<arch>.tar.gz, from the release's checksums-linux.txt.
OPENBAO_SHA256_X86_64="b82f5d05d0ab83244340893b8860542dccb9ff25422a30b3b9d60df0e34fb19d"
OPENBAO_SHA256_ARM64="ee8f08d935ecbc75451fd0517e337496b543de6325d69759d1284576c138913d"

MYSQL_PORT=3307
MYSQL_USER=mrng
MYSQL_PASSWORD=mrng-pass

RDP_PORT=3389
POLICY_RC_D=/usr/sbin/policy-rc.d
RDP_USER="${RDP_TEST_USER:-mrngrdp}"

APT_PACKAGES=(
    openssh-server                  # sshd, ssh-keygen (SSH, SFTP, SSH tunnel and Vault end-to-end tests)
    tigervnc-standalone-server      # Xvnc (XvncIntegrationTests, TigerVNC encoding/proxy tests)
    tigervnc-tools                  # vncpasswd
    xvfb                            # Xvfb (x11vnc fixtures, display for the RDP client)
    x11vnc                          # LibVNCServer encodings, reverse connections (VncListenerTests)
    x11-xserver-utils               # xsetroot (test patterns)
    imagemagick                     # convert + display (photo-like test pattern for Tight/JPEG)
    xdotool                         # lets the Xvnc test confirm the pointer really moved
    socat                           # telnetd listener, serial pseudo-terminal
    inetutils-telnetd               # /usr/sbin/telnetd
    bsdutils                        # script(1) (local shell, PowerShell script host)
    freerdp3-x11                    # xfreerdp3
    xrdp xorgxrdp xauth ssl-cert    # the RDP server
    openbox                         # window manager that keeps the RDP session alive
    slapd ldap-utils                # OpenLDAP (Active Directory import, Vault LDAP engine)
    curl ca-certificates            # OpenBao download
)

log() { printf '\n==> %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
warn() { printf '::warning::%s\n' "$*" >&2; }

if [[ $EUID -eq 0 ]]; then
    SUDO=()
else
    SUDO=(sudo)
fi
as_root() { "${SUDO[@]}" "$@"; }

port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

wait_for() { # wait_for <seconds> <command...>
    local deadline=$((SECONDS + $1)); shift
    until "$@"; do
        ((SECONDS < deadline)) || return 1
        sleep 0.5
    done
}

have_systemd() { [[ -d /run/systemd/system ]] && command -v systemctl >/dev/null; }

# ---------------------------------------------------------------------------------------------
install_packages() {
    [[ "${MRNG_IT_SKIP_APT:-}" == 1 ]] && { log "apt: skipped (MRNG_IT_SKIP_APT=1)"; return; }
    local wanted=() missing=() pkg
    for pkg in "${APT_PACKAGES[@]}"; do wanted+=("$pkg"); done
    # The MariaDB server for the SQL tests, unless a MariaDB is already installed.
    [[ "${MRNG_IT_SKIP_MYSQL:-}" == 1 ]] || command -v mariadbd >/dev/null || [[ -x /usr/sbin/mariadbd ]] \
        || wanted+=(mariadb-server mariadb-client)

    for pkg in "${wanted[@]}"; do
        dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null | grep -q 'install ok installed' || missing+=("$pkg")
    done
    if ((${#missing[@]} == 0)); then
        log "apt: all ${#wanted[@]} packages already installed"
        return
    fi
    # GitHub's Ubuntu image ships MySQL 8, whose packages conflict with MariaDB's: replace it there (or when
    # asked to), never silently on a developer's machine.
    if [[ " ${missing[*]} " == *" mariadb-server "* ]]; then
        local mysql_pkgs
        mysql_pkgs=$(dpkg-query -W -f='${Package} ${Status}\n' 'mysql-server*' 'mysql-client*' 2>/dev/null \
            | awk '/install ok installed/ {print $1}' | tr '\n' ' ' || true)
        if [[ -n "$mysql_pkgs" && ( "${GITHUB_ACTIONS:-}" == true || "${MRNG_IT_REPLACE_MYSQL:-}" == 1 ) ]]; then
            log "apt: removing MySQL packages that conflict with MariaDB: $mysql_pkgs"
            # shellcheck disable=SC2086
            as_root env DEBIAN_FRONTEND=noninteractive apt-get purge -y -q $mysql_pkgs
            # MariaDB cannot use a MySQL 8 data directory; keep it, out of the way.
            if as_root test -d /var/lib/mysql; then as_root mv /var/lib/mysql "/var/lib/mysql.before-mariadb.$$"; fi
        elif [[ -n "$mysql_pkgs" ]]; then
            warn "MySQL is installed ($mysql_pkgs); not replacing it with MariaDB (MRNG_IT_REPLACE_MYSQL=1 allows it). The SQL tests will be skipped."
            local kept=()
            for pkg in "${missing[@]}"; do [[ $pkg == mariadb-* ]] || kept+=("$pkg"); done
            missing=("${kept[@]}")
            ((${#missing[@]} > 0)) || return 0
        fi
    fi
    log "apt: installing ${missing[*]}"
    # In CI, keep the packages' own services (ssh, slapd, mariadb, inetd, ...) from starting: the tests start
    # private instances, and this script starts xrdp itself.
    if [[ "${GITHUB_ACTIONS:-}" == true ]] && ! as_root test -e "$POLICY_RC_D"; then
        printf '#!/bin/sh\nexit 101\n' | as_root tee "$POLICY_RC_D" >/dev/null
        as_root chmod 755 "$POLICY_RC_D"
        trap '"${SUDO[@]}" rm -f "$POLICY_RC_D"' EXIT
    fi
    as_root apt-get update -q
    # With recommended packages (fonts for the X servers, xorgxrdp, ...), as a plain apt-get install would.
    as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y -q "${missing[@]}"
    if [[ -n "$(trap -p EXIT)" ]]; then
        as_root rm -f "$POLICY_RC_D"
        trap - EXIT
    fi
}

# Ubuntu's AppArmor profile for slapd only allows its configuration under /etc/ldap and /var/lib/ldap;
# the test fixtures run slapd with a configuration and database in the temp directory.
relax_apparmor() {
    local profile=/etc/apparmor.d/$1
    [[ -f $profile ]] && as_root test -r /sys/kernel/security/apparmor/profiles || return 0
    if as_root grep -q "^${2} (enforce)" /sys/kernel/security/apparmor/profiles 2>/dev/null; then
        log "AppArmor: disabling the $1 profile (the tests run $2 from a temporary directory)"
        as_root ln -sf "$profile" "/etc/apparmor.d/disable/$1"
        as_root apparmor_parser -R "$profile" \
            || warn "could not unload the AppArmor profile $1: slapd-based tests may be skipped"
    fi
}

# ---------------------------------------------------------------------------------------------
ensure_pwsh() {
    if command -v pwsh >/dev/null; then
        log "pwsh: $(pwsh -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo present)"
        return
    fi
    log "pwsh: not found, installing PowerShell from Microsoft's apt repository"
    local tmp
    tmp=$(mktemp -d)
    # shellcheck disable=SC1091
    if curl -fsSL -o "$tmp/packages-microsoft-prod.deb" \
            "https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release && echo "$VERSION_ID")/packages-microsoft-prod.deb" \
        && as_root dpkg -i "$tmp/packages-microsoft-prod.deb" \
        && as_root apt-get update -q \
        && as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y -q powershell; then
        note "installed pwsh $(pwsh -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
    else
        warn "PowerShell could not be installed: the PowerShell integration tests will be skipped (and reported by ci/check-test-skips.py)."
    fi
    rm -rf "$tmp"
}

# ---------------------------------------------------------------------------------------------
BAO_PATH=""
install_openbao() {
    [[ "${MRNG_IT_SKIP_BAO:-}" == 1 ]] && { log "OpenBao: skipped (MRNG_IT_SKIP_BAO=1)"; return; }
    local arch sha
    case "$(uname -m)" in
        x86_64) arch=x86_64; sha=$OPENBAO_SHA256_X86_64 ;;
        aarch64 | arm64) arch=arm64; sha=$OPENBAO_SHA256_ARM64 ;;
        *) warn "OpenBao: no pinned build for $(uname -m); the Vault/OpenBao tests will be skipped."; return ;;
    esac
    local target="$TOOLS_DIR/openbao-$OPENBAO_VERSION/bao"
    if [[ -x "$target" ]] && "$target" version 2>/dev/null | grep -q "v$OPENBAO_VERSION"; then
        log "OpenBao: $("$target" version) already at $target"
        BAO_PATH=$target
        return
    fi
    log "OpenBao: downloading v$OPENBAO_VERSION ($arch)"
    local tmp archive="bao_${OPENBAO_VERSION}_Linux_${arch}.tar.gz"
    tmp=$(mktemp -d)
    curl -fsSL --retry 3 -o "$tmp/$archive" \
        "https://github.com/openbao/openbao/releases/download/v$OPENBAO_VERSION/$archive"
    echo "$sha  $tmp/$archive" | sha256sum -c --quiet - || { rm -rf "$tmp"; echo "OpenBao checksum mismatch" >&2; exit 1; }
    mkdir "$tmp/x"
    tar -xzf "$tmp/$archive" -C "$tmp/x" bao
    as_root install -D -m 0755 "$tmp/x/bao" "$target"
    rm -rf "$tmp"
    note "$("$target" version)"
    BAO_PATH=$target
}

# ---------------------------------------------------------------------------------------------
MYSQL_DIR="$STATE_DIR/mariadb"
mysql_client() { command -v mariadb || command -v mysql; }
mysql_root() { as_root "$(mysql_client)" --no-defaults --protocol=socket --socket="$MYSQL_DIR/mysqld.sock" -uroot "$@"; }
mysql_as_test_user() {
    "$(mysql_client)" --no-defaults --protocol=tcp -h127.0.0.1 -P"$MYSQL_PORT" -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" \
        -e 'SELECT 1' >/dev/null 2>&1
}

start_mariadb() {
    [[ "${MRNG_IT_SKIP_MYSQL:-}" == 1 ]] && { log "MariaDB: skipped (MRNG_IT_SKIP_MYSQL=1)"; return; }
    if mysql_as_test_user; then
        log "MariaDB: already serving $MYSQL_USER on 127.0.0.1:$MYSQL_PORT"
        return
    fi
    if port_open "$MYSQL_PORT"; then
        warn "MariaDB: port $MYSQL_PORT is taken by something that does not accept $MYSQL_USER; the SQL tests will be skipped."
        return
    fi
    local server
    server=$(command -v mariadbd || echo /usr/sbin/mariadbd)
    [[ -x "$server" ]] || { warn "MariaDB: mariadbd not installed; the SQL tests will be skipped."; return; }

    log "MariaDB: starting a private server on 127.0.0.1:$MYSQL_PORT (data in $MYSQL_DIR)"
    local owner=mysql
    id mysql >/dev/null 2>&1 || owner=root
    as_root mkdir -p "$MYSQL_DIR"
    as_root chown "$owner:" "$MYSQL_DIR"
    if ! as_root test -d "$MYSQL_DIR/data"; then
        # Initialise next to the final location and move it into place, so a failed run starts over.
        as_root rm -rf "$MYSQL_DIR/data.new"
        local output
        output=$(as_root mariadb-install-db --no-defaults --user="$owner" --datadir="$MYSQL_DIR/data.new" \
            --auth-root-authentication-method=socket --skip-test-db --skip-name-resolve 2>&1) \
            || { printf '%s\n' "$output" >&2; exit 1; }
        as_root mv "$MYSQL_DIR/data.new" "$MYSQL_DIR/data"
    fi
    # setsid + nohup: the server outlives this script (and the CI step that runs it).
    as_root setsid nohup "$server" --no-defaults --user="$owner" --datadir="$MYSQL_DIR/data" \
        --port="$MYSQL_PORT" --bind-address=127.0.0.1 --skip-name-resolve \
        --socket="$MYSQL_DIR/mysqld.sock" --pid-file="$MYSQL_DIR/mysqld.pid" \
        --log-error="$MYSQL_DIR/error.log" </dev/null >/dev/null 2>&1 &
    if ! wait_for 60 mysql_root -e 'SELECT 1' >/dev/null 2>&1; then
        as_root cat "$MYSQL_DIR/error.log" >&2 || true
        echo "MariaDB did not start" >&2
        exit 1
    fi
    mysql_root <<SQL
DELETE FROM mysql.global_priv WHERE User = '';
CREATE USER IF NOT EXISTS '$MYSQL_USER'@'%' IDENTIFIED BY '$MYSQL_PASSWORD';
CREATE USER IF NOT EXISTS '$MYSQL_USER'@'localhost' IDENTIFIED BY '$MYSQL_PASSWORD';
ALTER USER '$MYSQL_USER'@'%' IDENTIFIED BY '$MYSQL_PASSWORD';
ALTER USER '$MYSQL_USER'@'localhost' IDENTIFIED BY '$MYSQL_PASSWORD';
GRANT ALL PRIVILEGES ON *.* TO '$MYSQL_USER'@'%';
GRANT ALL PRIVILEGES ON *.* TO '$MYSQL_USER'@'localhost';
FLUSH PRIVILEGES;
SQL
    mysql_as_test_user || { echo "MariaDB: $MYSQL_USER cannot log in" >&2; exit 1; }
    note "$(mysql_root -N -e 'SELECT VERSION()') ready, pid $(as_root cat "$MYSQL_DIR/mysqld.pid")"
}

# ---------------------------------------------------------------------------------------------
RDP_PASS=""
setup_rdp() {
    [[ "${MRNG_IT_SKIP_RDP:-}" == 1 ]] && { log "xrdp: skipped (MRNG_IT_SKIP_RDP=1)"; return; }
    log "xrdp: test user $RDP_USER"
    local pass_file="$STATE_DIR/rdp-password"
    if [[ -n "${RDP_TEST_PASS:-}" ]]; then
        RDP_PASS=$RDP_TEST_PASS
    elif as_root test -s "$pass_file"; then
        RDP_PASS=$(as_root cat "$pass_file")
    else
        local random
        random=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')
        RDP_PASS="Mrng-${random:0:16}"
    fi
    printf '%s\n' "$RDP_PASS" | as_root tee "$pass_file" >/dev/null
    as_root chmod 600 "$pass_file"

    id "$RDP_USER" >/dev/null 2>&1 || as_root useradd --create-home --shell /bin/bash "$RDP_USER"
    printf '%s:%s\n' "$RDP_USER" "$RDP_PASS" | as_root chpasswd
    local home
    home=$(getent passwd "$RDP_USER" | cut -d: -f6)
    # /etc/X11/Xsession runs ~/.xsession (allow-user-xsession); the window manager keeps the session open.
    as_root tee "$home/.xsession" >/dev/null <<'EOF'
#!/bin/sh
# mRemoteNG integration tests: a minimal desktop for the RDP session.
xsetroot -solid "#336699" 2>/dev/null
exec openbox
EOF
    as_root chmod 755 "$home/.xsession"
    as_root chown "$RDP_USER:" "$home/.xsession"

    if port_open "$RDP_PORT"; then
        log "xrdp: something already listens on $RDP_PORT, reusing it"
        return
    fi
    log "xrdp: starting"
    [[ -e /etc/xrdp/cert.pem ]] || as_root make-ssl-cert generate-default-snakeoil --force-overwrite
    if have_systemd; then
        as_root systemctl enable --now xrdp-sesman xrdp >/dev/null
    else
        # What the systemd units do: runtime directories, then sesman as root and xrdp as its own user.
        as_root /bin/sh /usr/share/xrdp/socksetup
        local pidfile
        for pidfile in /run/xrdp/xrdp-sesman.pid /run/xrdp/xrdp.pid; do # left behind by a reboot
            if as_root test -f "$pidfile" && ! as_root kill -0 "$(as_root cat "$pidfile")" 2>/dev/null; then
                as_root rm -f "$pidfile"
            fi
        done
        port_open 3350 || as_root /usr/sbin/xrdp-sesman
        if id xrdp >/dev/null 2>&1; then as_root runuser -u xrdp -- /usr/sbin/xrdp; else as_root /usr/sbin/xrdp; fi
    fi
    wait_for 30 port_open "$RDP_PORT" || { echo "xrdp did not start listening on $RDP_PORT" >&2; exit 1; }
    note "listening on $RDP_PORT"
}

# ---------------------------------------------------------------------------------------------
check_test_ports() {
    local port busy=()
    # Fixed ports of the self-starting fixtures: sshd, tunnel sshd, Xvnc, TigerVNC, x11vnc, reverse VNC,
    # OpenBao (+cluster), Vault sshd, Vault slapd, AD slapd.
    for port in 2222 2230 5961 5964 5965 5501 8210 8211 8222 8390 1389; do
        port_open "$port" && busy+=("$port")
    done
    ((${#busy[@]} == 0)) || warn "ports used by the test fixtures are busy: ${busy[*]} (those tests will be skipped)"
}

export_env() {
    local file="$STATE_DIR/env.sh" lines=()
    lines+=("MRNG_TEST_MYSQL_HOST=127.0.0.1:$MYSQL_PORT" "MRNG_TEST_MYSQL_USER=$MYSQL_USER" "MRNG_TEST_MYSQL_PASSWORD=$MYSQL_PASSWORD")
    [[ -n "$BAO_PATH" ]] && lines+=("MREMOTENG_BAO_PATH=$BAO_PATH")
    if [[ -n "$RDP_PASS" ]]; then
        lines+=("RDP_TEST_HOST=127.0.0.1" "RDP_TEST_PORT=$RDP_PORT" "RDP_TEST_USER=$RDP_USER" "RDP_TEST_PASS=$RDP_PASS" "RDP_TEST_NLA=false")
    fi
    if [[ -n "${GITHUB_ENV:-}" ]]; then
        [[ -n "$RDP_PASS" ]] && echo "::add-mask::$RDP_PASS"
        printf '%s\n' "${lines[@]}" >>"$GITHUB_ENV"
    fi
    printf 'export %q\n' "${lines[@]}" | as_root tee "$file" >/dev/null
    as_root chmod 600 "$file"
    log "Test environment (also in $file; source it as root before 'dotnet test'):"
    local line
    for line in "${lines[@]}"; do
        if [[ $line == RDP_TEST_PASS=* && -n "${GITHUB_ACTIONS:-}" ]]; then
            note "RDP_TEST_PASS=***"
        else
            note "$line"
        fi
    done
}

# ---------------------------------------------------------------------------------------------
main() {
    as_root mkdir -p "$STATE_DIR"
    as_root chmod 755 "$STATE_DIR"
    install_packages
    relax_apparmor usr.sbin.slapd /usr/sbin/slapd
    ensure_pwsh
    install_openbao
    start_mariadb
    setup_rdp
    check_test_ports
    export_env
    log "Done."
}

# Sourcing the script (e.g. to try one step) only defines the functions.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
