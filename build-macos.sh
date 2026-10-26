#!/usr/bin/env bash
# Build an offline Apple Silicon Installer package plus a CLI-only archive.
# Usage: ./build-macos.sh VERSION [output-directory]
# Optional: GSTREAMER_PKG=/path/to/verified/official-runtime.pkg
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:-0.4.5}"
OUTPUT_DIR="${2:-$REPO_DIR/bin/macos-dist}"
HELPER_DIR="$REPO_DIR/scripts/macos"

fail() { echo "Error: $*" >&2; exit 1; }

[ "$#" -le 2 ] || fail "Usage: $0 VERSION [output-directory]"
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || fail "VERSION must be a numeric dotted version, for example 0.4.5."
[ "$(uname -s)" = "Darwin" ] || fail "Build on macOS: Native AOT cannot cross-compile macOS from Linux."
[ "$(uname -m)" = "arm64" ] || fail "Use a native Apple Silicon shell, not an Intel/Rosetta shell."
MACOS_MAJOR="$(sw_vers -productVersion | cut -d. -f1)"
[ "$MACOS_MAJOR" -ge 15 ] || fail "macOS 15 or newer is required."

for tool in dotnet python3 pkgbuild productbuild pkgutil codesign lipo shasum ditto; do
    command -v "$tool" >/dev/null || fail "Missing build tool: $tool"
done
[[ "$(dotnet --version)" = 10.* ]] || fail ".NET 10 SDK is required."

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/qmtui-macos.XXXXXX")"
trap 'rm -rf "$STAGE_DIR"' EXIT
RUNTIME_PKG="$("$HELPER_DIR/download-gstreamer.sh")"

echo "==> Expanding the verified GStreamer runtime..."
pkgutil --expand "$RUNTIME_PKG" "$STAGE_DIR/runtime"
[ -f "$STAGE_DIR/runtime/Distribution" ] || fail "Runtime installer has no Distribution."
[ -f "$STAGE_DIR/runtime/Resources/license.txt" ] || fail "Runtime installer license is missing."
mkdir -p "$STAGE_DIR/packages"
COMPONENT_COUNT=0
for component in "$STAGE_DIR/runtime/"*.pkg; do
    [ -d "$component" ] || fail "Expected an expanded runtime component directory: $component"
    [ -f "$component/PackageInfo" ] || fail "Runtime component has no PackageInfo: $component"
    if [ "$(basename "$component")" = "osx-framework-1.28.8-universal.pkg" ]; then
        echo "==> Correcting upstream framework root permissions (0700 -> 0755)..."
        bash "$HELPER_DIR/normalize-framework-component.sh" "$component" "$STAGE_DIR/framework-fix"
    fi
    pkgutil --flatten "$component" "$STAGE_DIR/packages/$(basename "$component")"
    COMPONENT_COUNT=$((COMPONENT_COUNT + 1))
done
[ "$COMPONENT_COUNT" -gt 0 ] || fail "Runtime installer has no component packages."

echo "==> Publishing qmtui ${VERSION} for osx-arm64..."
cd "$REPO_DIR"
MACOSX_DEPLOYMENT_TARGET=15.0 dotnet publish QmTui.csproj \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishAot=true -p:Version="$VERSION" -o "$STAGE_DIR/publish"
BINARY="$STAGE_DIR/publish/qmtui"
[ -x "$BINARY" ] || fail "Native executable was not produced."
[ "$(lipo -archs "$BINARY")" = "arm64" ] || fail "Published executable is not native arm64."
codesign --force --sign - "$BINARY"
codesign --verify --strict "$BINARY"
ACTUAL_VERSION="$("$BINARY" --version)"
[ "$ACTUAL_VERSION" = "qmtui $VERSION" ] || fail "Binary version '$ACTUAL_VERSION' does not match requested version '$VERSION'."
"$BINARY" --help

echo "==> Building the qmtui component..."
PAYLOAD="$STAGE_DIR/payload"
mkdir -p "$PAYLOAD/usr/local/bin" "$PAYLOAD/usr/local/share/qmtui" "$PAYLOAD/Applications"
install -m 755 "$BINARY" "$PAYLOAD/usr/local/bin/qmtui"
ln -s qmtui "$PAYLOAD/usr/local/bin/qqmusic-tui"
install -m 755 "$HELPER_DIR/qmtui.command" "$PAYLOAD/Applications/qmtui.command"
install -m 644 "$REPO_DIR/LICENSE" "$PAYLOAD/usr/local/share/qmtui/LICENSE"
install -m 644 "$HELPER_DIR/INSTALL.txt" "$PAYLOAD/usr/local/share/qmtui/INSTALL.txt"
install -m 644 "$HELPER_DIR/THIRD-PARTY-NOTICES.txt" "$PAYLOAD/usr/local/share/qmtui/THIRD-PARTY-NOTICES.txt"
install -m 644 "$STAGE_DIR/runtime/Resources/license.txt" "$PAYLOAD/usr/local/share/qmtui/GSTREAMER-LICENSE.txt"
pkgbuild --root "$PAYLOAD" --identifier io.github.bootids.qmtui \
    --version "$VERSION" --install-location / --ownership recommended \
    "$STAGE_DIR/packages/qmtui-component.pkg"

echo "==> Composing the offline installer (${COMPONENT_COUNT} upstream runtime components)..."
ditto "$STAGE_DIR/runtime/Resources" "$STAGE_DIR/resources"
cat > "$STAGE_DIR/resources/qmtui-welcome.txt" <<EOF
qmtui ${VERSION} — Apple Silicon (arm64)

本安装包适用于 macOS 15 或更新版本，包含 qmtui 和官方 GStreamer 1.28.8 音频运行时。
无需另外安装 .NET、Homebrew 或 Rosetta；安装过程不需要联网。

安装完成后，在终端输入 qmtui，或在“应用程序”中双击 qmtui.command。
GStreamer 将安装到官方共享位置 /Library/Frameworks/GStreamer.framework。
EOF
cp "$HELPER_DIR/INSTALL.txt" "$STAGE_DIR/resources/qmtui-conclusion.txt"
python3 "$HELPER_DIR/compose-distribution.py" \
    "$STAGE_DIR/runtime/Distribution" "$STAGE_DIR/packages" \
    "$STAGE_DIR/Distribution" "$VERSION"
INSTALLER_NAME="qmtui-${VERSION}-macos-arm64.pkg"
productbuild --distribution "$STAGE_DIR/Distribution" \
    --resources "$STAGE_DIR/resources" --package-path "$STAGE_DIR/packages" \
    "$OUTPUT_DIR/$INSTALLER_NAME"
[ -s "$OUTPUT_DIR/$INSTALLER_NAME" ] || fail "Offline installer was not produced."

echo "==> Packaging the secondary CLI-only archive..."
ARCHIVE_DIR="$STAGE_DIR/archive/qmtui-${VERSION}-macos-arm64"
mkdir -p "$ARCHIVE_DIR"
install -m 755 "$BINARY" "$ARCHIVE_DIR/qmtui"
ln -s qmtui "$ARCHIVE_DIR/qqmusic-tui"
cp "$REPO_DIR/LICENSE" "$HELPER_DIR/INSTALL.txt" "$ARCHIVE_DIR/"
ARCHIVE_NAME="qmtui-${VERSION}-macos-arm64.tar.gz"
COPYFILE_DISABLE=1 tar -czf "$OUTPUT_DIR/$ARCHIVE_NAME" -C "$STAGE_DIR/archive" "qmtui-${VERSION}-macos-arm64"
cp "$HELPER_DIR/INSTALL.txt" "$OUTPUT_DIR/INSTALL-macOS.txt"
(
    cd "$OUTPUT_DIR"
    shasum -a 256 "$INSTALLER_NAME" "$ARCHIVE_NAME" INSTALL-macOS.txt > SHA256SUMS
)

echo "==> Built and verified native arm64 executable; installer is unsigned/unnotarized."
echo "    Offline installer: $OUTPUT_DIR/$INSTALLER_NAME"
echo "    CLI-only archive:  $OUTPUT_DIR/$ARCHIVE_NAME"
echo "    Checksums:         $OUTPUT_DIR/SHA256SUMS"
