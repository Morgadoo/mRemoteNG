#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# mRemoteNG — Debian/Ubuntu .deb packaging script
#
# Usage:
#   ./packaging/linux/build-deb.sh [--arch amd64|arm64] [--version 1.78.2]
#
# Requirements:
#   • .NET SDK 10.0+
#   • dpkg-deb (sudo apt install dpkg-dev)
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DEB_ARCH="amd64"
VERSION="1.78.2"
while [ $# -gt 0 ]; do
  case "$1" in
    --arch) DEB_ARCH="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$DEB_ARCH" in
  amd64) DOTNET_ARCH="x64" ;;
  arm64) DOTNET_ARCH="arm64" ;;
  *) echo "Unsupported architecture: $DEB_ARCH (use amd64 or arm64)" >&2; exit 2 ;;
esac
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STAGING="$ROOT_DIR/dist/deb/staging"
OUT_DIR="$ROOT_DIR/dist/deb"

echo "==> Building mRemoteNG .deb for $DEB_ARCH (v$VERSION)"

rm -rf "$STAGING"

# Debian directory structure
INSTALL_DIR="$STAGING/usr"
mkdir -p "$INSTALL_DIR/bin"
mkdir -p "$INSTALL_DIR/share/applications"
mkdir -p "$INSTALL_DIR/share/icons/hicolor/256x256/apps"
mkdir -p "$INSTALL_DIR/share/doc/mremoteng"
mkdir -p "$STAGING/DEBIAN"

# 1. Publish
echo "==> Publishing .NET app…"
dotnet publish "$ROOT_DIR/mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj" \
  -c Release \
  -r "linux-$DOTNET_ARCH" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o "$INSTALL_DIR/lib/mremoteng" \
  -p:Version="$VERSION"

# Icon
cp "$SCRIPT_DIR/mremoteng.png" "$INSTALL_DIR/share/icons/hicolor/256x256/apps/mremoteng.png"

# 2. Wrapper script
cat > "$INSTALL_DIR/bin/mremoteng" << 'WRAPPER'
#!/bin/bash
exec /usr/lib/mremoteng/mRemoteNG.Avalonia "$@"
WRAPPER
chmod +x "$INSTALL_DIR/bin/mremoteng"

# 3. Desktop entry
cat > "$INSTALL_DIR/share/applications/mremoteng.desktop" << 'DESKTOP'
[Desktop Entry]
Type=Application
Name=mRemoteNG
GenericName=Remote Connection Manager
Comment=Multi-protocol remote connection manager
Exec=mremoteng %F
Icon=mremoteng
Categories=Network;RemoteAccess;
Keywords=ssh;rdp;vnc;telnet;remote;
StartupNotify=true
MimeType=application/x-mremoteng;
DESKTOP

# 4. Copyright / changelog
cat > "$INSTALL_DIR/share/doc/mremoteng/copyright" << 'COPYRIGHT'
mRemoteNG — Multi-protocol remote connection manager
Copyright (C) 2008–2026 mRemoteNG Contributors
License: GPL-2.0-or-later <https://www.gnu.org/licenses/gpl-2.0.html>
Homepage: https://mremoteng.org
COPYRIGHT

# 5. Control file
INSTALLED_SIZE=$(du -sk "$INSTALL_DIR" | awk '{print $1}')
cat > "$STAGING/DEBIAN/control" << CONTROL
Package: mremoteng
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: mRemoteNG Team <info@mremoteng.org>
Installed-Size: $INSTALLED_SIZE
Depends: libx11-6, libxrandr2, libfontconfig1, libdbus-1-3
Recommends: xfreerdp3, xclip | wl-clipboard, libnotify-bin
Section: net
Priority: optional
Homepage: https://mremoteng.org
Description: Multi-protocol remote connection manager
 mRemoteNG is an open source, multi-tab remote connection manager that
 supports SSH, RDP (via FreeRDP), VNC, Telnet, Rlogin, HTTP/HTTPS,
 PowerShell, Serial, and external application protocols.
 .
 It is a cross-platform port of the original Windows-only mRemoteNG,
 built with Avalonia UI and .NET 10.
CONTROL

# 6. Post-install script
cat > "$STAGING/DEBIAN/postinst" << 'POSTINST'
#!/bin/bash
set -e
update-desktop-database /usr/share/applications || true
gtk-update-icon-cache -f /usr/share/icons/hicolor || true
POSTINST
chmod 755 "$STAGING/DEBIAN/postinst"

# 7. Build .deb
OUTPUT="$OUT_DIR/mremoteng_${VERSION}_${DEB_ARCH}.deb"
mkdir -p "$OUT_DIR"
dpkg-deb --build "$STAGING" "$OUTPUT"

echo "==> .deb built: $OUTPUT"
ls -lh "$OUTPUT"
