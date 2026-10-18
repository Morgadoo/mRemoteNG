#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# mRemoteNG — Flatpak build script
#
# Usage:
#   ./packaging/linux/build-flatpak.sh [--no-install] [--bundle]
#
#   --no-install  build only; don't install the app for the current user
#   --bundle      also export dist/flatpak/mRemoteNG.flatpak (single-file bundle)
#
# Requirements:
#   • flatpak and flatpak-builder (sudo apt install flatpak flatpak-builder)
#   • network access: the runtimes come from Flathub and NuGet packages are restored
#     during the build
#
# The manifest builds the repository's committed HEAD, so commit local changes first.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

INSTALL=1
BUNDLE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --no-install) INSTALL=0; shift ;;
    --bundle) BUNDLE=1; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
MANIFEST="$SCRIPT_DIR/mremoteng.flatpak.yml"
APP_ID="org.mremoteng.mRemoteNG"
BUILD_DIR="$ROOT_DIR/dist/flatpak/build"
REPO_DIR="$ROOT_DIR/dist/flatpak/repo"
STATE_DIR="$ROOT_DIR/dist/flatpak/.flatpak-builder"

for tool in flatpak flatpak-builder; do
  command -v "$tool" >/dev/null || { echo "$tool is not installed" >&2; exit 1; }
done

if [ -n "$(git -C "$ROOT_DIR" status --porcelain --untracked-files=no)" ]; then
  echo "==> Warning: uncommitted changes are not part of the build (the manifest builds HEAD)" >&2
fi

flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo

ARGS=(--user --install-deps-from=flathub --force-clean --state-dir="$STATE_DIR" --repo="$REPO_DIR")
[ "$INSTALL" = 1 ] && ARGS+=(--install)

echo "==> Building $APP_ID"
flatpak-builder "${ARGS[@]}" "$BUILD_DIR" "$MANIFEST"

if [ "$BUNDLE" = 1 ]; then
  OUTPUT="$ROOT_DIR/dist/flatpak/mRemoteNG.flatpak"
  flatpak build-bundle --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
    "$REPO_DIR" "$OUTPUT" "$APP_ID"
  echo "==> Bundle: $OUTPUT"
  ls -lh "$OUTPUT"
fi

[ "$INSTALL" = 1 ] && echo "==> Installed; run it with: flatpak run $APP_ID"
exit 0
