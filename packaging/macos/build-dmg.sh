#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# mRemoteNG — macOS .app bundle + DMG packaging script
#
# Usage:
#   ./packaging/macos/build-dmg.sh [--arch x64|arm64|universal] [--version 1.78.2]
#
# Requirements:
#   • .NET SDK 10.0+ (macOS)
#   • Xcode Command Line Tools: xcode-select --install
#   • create-dmg (brew install create-dmg) — optional, falls back to hdiutil
#   • codesign / notarytool (for distribution; requires Apple Developer account)
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

ARCH="${1:-arm64}"
VERSION="${2:-1.78.2-dev}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="$ROOT_DIR/dist/macos"
APP_NAME="mRemoteNG"
APP_BUNDLE="$OUT_DIR/$APP_NAME.app"

echo "==> Building mRemoteNG macOS .app for $ARCH (v$VERSION)"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# 1. Publish
if [ "$ARCH" = "universal" ]; then
  echo "==> Publishing universal binary (x64 + arm64)…"
  TMP_X64="$OUT_DIR/tmp-x64"
  TMP_ARM64="$OUT_DIR/tmp-arm64"

  dotnet publish "$ROOT_DIR/mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj" \
    -c Release -r osx-x64 --self-contained true -o "$TMP_X64" -p:Version="$VERSION"

  dotnet publish "$ROOT_DIR/mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj" \
    -c Release -r osx-arm64 --self-contained true -o "$TMP_ARM64" -p:Version="$VERSION"

  # Lipo merge main executable
  lipo -create \
    "$TMP_X64/mRemoteNG.Avalonia" \
    "$TMP_ARM64/mRemoteNG.Avalonia" \
    -output "$APP_BUNDLE/Contents/MacOS/mRemoteNG"

  # Copy remaining files from arm64 build
  cp -a "$TMP_ARM64/"* "$APP_BUNDLE/Contents/MacOS/"
  mv "$APP_BUNDLE/Contents/MacOS/mRemoteNG.Avalonia" "$APP_BUNDLE/Contents/MacOS/mRemoteNG_arm64"
  # Replace with lipo'd binary
  cp -f "$APP_BUNDLE/Contents/MacOS/mRemoteNG" "$APP_BUNDLE/Contents/MacOS/mRemoteNG.Avalonia"
else
  echo "==> Publishing .NET app (osx-$ARCH)…"
  dotnet publish "$ROOT_DIR/mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj" \
    -c Release \
    -r "osx-$ARCH" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$APP_BUNDLE/Contents/MacOS" \
    -p:Version="$VERSION"
fi

# 2. Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>mRemoteNG</string>
    <key>CFBundleDisplayName</key>
    <string>mRemoteNG</string>
    <key>CFBundleIdentifier</key>
    <string>org.mremoteng.mRemoteNG</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>mRemoteNG.Avalonia</string>
    <key>CFBundleIconFile</key>
    <string>mRemoteNG</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2008–2026 mRemoteNG Contributors. GPL-2.0-or-later.</string>
    <!-- Entitlements for notarization -->
    <key>com.apple.security.network.client</key>
    <true/>
    <key>com.apple.security.files.user-selected.read-write</key>
    <true/>
</dict>
</plist>
PLIST

# 3. Code sign (if APPLE_IDENTITY is set in environment)
if [ -n "${APPLE_IDENTITY:-}" ]; then
  echo "==> Code signing with identity: $APPLE_IDENTITY"
  find "$APP_BUNDLE/Contents/MacOS" -type f -exec \
    codesign --force --sign "$APPLE_IDENTITY" \
      --options runtime \
      --entitlements "$SCRIPT_DIR/entitlements.plist" {} \;

  codesign --force --sign "$APPLE_IDENTITY" \
    --options runtime \
    --entitlements "$SCRIPT_DIR/entitlements.plist" \
    "$APP_BUNDLE"

  echo "==> Verifying signature…"
  codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"
  spctl --assess --verbose=4 --type exec "$APP_BUNDLE" || echo "WARNING: spctl check failed (expected if not notarized yet)"
else
  echo "WARNING: APPLE_IDENTITY not set. Skipping code signing."
  echo "         Set APPLE_IDENTITY='Developer ID Application: Your Name (TEAM_ID)' to sign."
fi

# 4. Build DMG
mkdir -p "$OUT_DIR"
DMG_OUTPUT="$OUT_DIR/mRemoteNG-$VERSION-macos-$ARCH.dmg"

if command -v create-dmg &> /dev/null; then
  echo "==> Building DMG with create-dmg…"
  create-dmg \
    --volname "mRemoteNG $VERSION" \
    --volicon "$ROOT_DIR/mRemoteNG.Avalonia/Assets/Icons/mRemoteNG.ico" \
    --window-pos 200 120 \
    --window-size 800 400 \
    --icon-size 100 \
    --icon "$APP_NAME.app" 200 190 \
    --hide-extension "$APP_NAME.app" \
    --app-drop-link 600 185 \
    "$DMG_OUTPUT" \
    "$OUT_DIR/"
else
  echo "==> Building DMG with hdiutil (basic)…"
  TMP_DMG="$OUT_DIR/tmp.dmg"
  hdiutil create -size 500m -fs HFS+ -volname "mRemoteNG $VERSION" "$TMP_DMG"
  hdiutil attach "$TMP_DMG" -mountpoint /Volumes/mRemoteNG
  cp -a "$APP_BUNDLE" /Volumes/mRemoteNG/
  ln -s /Applications /Volumes/mRemoteNG/Applications
  hdiutil detach /Volumes/mRemoteNG
  hdiutil convert "$TMP_DMG" -format UDZO -o "$DMG_OUTPUT"
  rm -f "$TMP_DMG"
fi

echo "==> DMG built: $DMG_OUTPUT"
ls -lh "$DMG_OUTPUT"

# 5. Notarize (if credentials are set)
if [ -n "${APPLE_NOTARIZE_KEYCHAIN_PROFILE:-}" ]; then
  echo "==> Notarizing DMG with profile: $APPLE_NOTARIZE_KEYCHAIN_PROFILE"
  xcrun notarytool submit "$DMG_OUTPUT" \
    --keychain-profile "$APPLE_NOTARIZE_KEYCHAIN_PROFILE" \
    --wait
  xcrun stapler staple "$DMG_OUTPUT"
  echo "==> Notarization complete."
fi
