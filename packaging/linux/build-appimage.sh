#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# mRemoteNG — Linux AppImage packaging script
#
# Usage:
#   chmod +x packaging/linux/build-appimage.sh
#   ./packaging/linux/build-appimage.sh [--arch x64|arm64] [--version 1.78.2]
#
# Requirements:
#   • .NET SDK 10.0+
#   • appimagetool (https://github.com/AppImage/AppImageKit/releases)
#     Place appimagetool-x86_64.AppImage in PATH or same directory.
#   • FUSE (for AppImage mounting): sudo apt install libfuse2
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

ARCH="${1:-x64}"
VERSION="${2:-1.78.2-dev}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="$ROOT_DIR/dist/linux"
APPDIR="$OUT_DIR/mRemoteNG.AppDir"

echo "==> Building mRemoteNG AppImage for Linux/$ARCH (v$VERSION)"

# 1. Publish self-contained binary
echo "==> Publishing .NET app (self-contained, linux-$ARCH)…"
dotnet publish "$ROOT_DIR/mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj" \
  -c Release \
  -r "linux-$ARCH" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishReadyToRun=true \
  -o "$APPDIR/usr/bin" \
  -p:Version="$VERSION"

# 2. Build AppDir structure
echo "==> Building AppDir structure…"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$APPDIR/usr/share/metainfo"

# Desktop entry
cat > "$APPDIR/mRemoteNG.desktop" << 'DESKTOP'
[Desktop Entry]
Type=Application
Name=mRemoteNG
GenericName=Remote Connection Manager
Comment=Multi-protocol remote connection manager (SSH, RDP, VNC, Telnet)
Exec=mRemoteNG.Avalonia %F
Icon=mremoteng
Categories=Network;RemoteAccess;
Keywords=ssh;rdp;vnc;telnet;remote;connection;
StartupNotify=true
StartupWMClass=mRemoteNG.Avalonia
MimeType=application/x-mremoteng;
DESKTOP

cp "$APPDIR/mRemoteNG.desktop" "$APPDIR/usr/share/applications/"

# Icon (copy from assets if available)
if [ -f "$ROOT_DIR/mRemoteNG.Avalonia/Assets/Icons/mRemoteNG.ico" ]; then
  # Convert .ico to .png (requires imagemagick)
  if command -v convert &> /dev/null; then
    convert "$ROOT_DIR/mRemoteNG.Avalonia/Assets/Icons/mRemoteNG.ico[0]" \
      -resize 256x256 \
      "$APPDIR/usr/share/icons/hicolor/256x256/apps/mremoteng.png"
    cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/mremoteng.png" \
       "$APPDIR/mremoteng.png"
  fi
fi

# AppStream metadata
cat > "$APPDIR/usr/share/metainfo/org.mremoteng.mRemoteNG.metainfo.xml" << 'XML'
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>org.mremoteng.mRemoteNG</id>
  <name>mRemoteNG</name>
  <summary>Multi-protocol remote connection manager</summary>
  <description>
    <p>mRemoteNG is an open source, multi-tab remote connection manager.
    It allows you to store and manage connections to remote hosts using
    SSH, RDP, VNC, Telnet, HTTP/HTTPS, and more — all in one place.</p>
  </description>
  <url type="homepage">https://mremoteng.org</url>
  <url type="bugtracker">https://github.com/mRemoteNG/mRemoteNG/issues</url>
  <metadata_license>MIT</metadata_license>
  <project_license>GPL-2.0-or-later</project_license>
  <releases>
    <release version="1.78.2-dev" date="2026-03-10"/>
  </releases>
</component>
XML

# AppRun entrypoint
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/bash
SELF="$(readlink -f "$0")"
HERE="$(dirname "$SELF")"
export PATH="$HERE/usr/bin:$PATH"
export LD_LIBRARY_PATH="$HERE/usr/lib:$LD_LIBRARY_PATH"
exec "$HERE/usr/bin/mRemoteNG.Avalonia" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# 3. Build AppImage
echo "==> Running appimagetool…"
mkdir -p "$OUT_DIR"

APPIMAGETOOL="$(command -v appimagetool 2>/dev/null || echo "$SCRIPT_DIR/appimagetool-x86_64.AppImage")"
if [ ! -f "$APPIMAGETOOL" ] && ! command -v appimagetool &> /dev/null; then
  echo "WARNING: appimagetool not found. Downloading…"
  wget -q "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage" \
    -O "$SCRIPT_DIR/appimagetool-x86_64.AppImage"
  chmod +x "$SCRIPT_DIR/appimagetool-x86_64.AppImage"
  APPIMAGETOOL="$SCRIPT_DIR/appimagetool-x86_64.AppImage"
fi

OUTPUT_FILE="$OUT_DIR/mRemoteNG-$VERSION-linux-$ARCH.AppImage"
ARCH="$ARCH" "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_FILE"

echo "==> AppImage built: $OUTPUT_FILE"
ls -lh "$OUTPUT_FILE"
