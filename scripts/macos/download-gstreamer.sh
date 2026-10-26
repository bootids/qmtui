#!/usr/bin/env bash
# Print the absolute path of a verified, pinned macOS runtime package.
# All progress goes to stderr, so callers can use command substitution.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
GSTREAMER_VERSION="1.28.8"
GSTREAMER_FILENAME="gstreamer-1.0-${GSTREAMER_VERSION}-universal.pkg"
GSTREAMER_URL="https://gstreamer.freedesktop.org/data/pkg/osx/${GSTREAMER_VERSION}/${GSTREAMER_FILENAME}"
# Published at ${GSTREAMER_URL}.sha256sum. Keep the checksum pinned with the version.
GSTREAMER_SHA256="d371ab04a7632713626836c2fc234e863cc98484c74c473eba99b0fbd9b3a3fe"
CACHE_DIR="${1:-$REPO_DIR/bin/macos-runtime}"

if [ "$#" -gt 1 ]; then
    echo "Usage: $0 [cache-directory]" >&2
    exit 2
fi

if [ -n "${GSTREAMER_PKG:-}" ]; then
    PACKAGE_PATH="$GSTREAMER_PKG"
    if [ ! -f "$PACKAGE_PATH" ]; then
        echo "GSTREAMER_PKG does not exist: $PACKAGE_PATH" >&2
        exit 1
    fi
else
    mkdir -p "$CACHE_DIR"
    CACHE_DIR="$(cd "$CACHE_DIR" && pwd)"
    PACKAGE_PATH="$CACHE_DIR/$GSTREAMER_FILENAME"
    if [ ! -f "$PACKAGE_PATH" ]; then
        command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
        PARTIAL_PATH="$(mktemp "$CACHE_DIR/.gstreamer-download.XXXXXX")"
        trap 'rm -f "$PARTIAL_PATH"' EXIT
        echo "Downloading official GStreamer ${GSTREAMER_VERSION} runtime..." >&2
        curl --fail --location --retry 3 --connect-timeout 30 --max-time 900 \
            --output "$PARTIAL_PATH" "$GSTREAMER_URL" >&2
        PACKAGE_PATH="$PARTIAL_PATH"
    fi
fi

command -v shasum >/dev/null || { echo "shasum is required." >&2; exit 1; }
ACTUAL_SHA256="$(shasum -a 256 "$PACKAGE_PATH" | awk '{print $1}')"
if [ "$ACTUAL_SHA256" != "$GSTREAMER_SHA256" ]; then
    echo "GStreamer checksum mismatch for $PACKAGE_PATH" >&2
    echo "Expected: $GSTREAMER_SHA256" >&2
    echo "Actual:   $ACTUAL_SHA256" >&2
    exit 1
fi

if [ -n "${PARTIAL_PATH:-}" ]; then
    PACKAGE_PATH="$CACHE_DIR/$GSTREAMER_FILENAME"
    mv "$PARTIAL_PATH" "$PACKAGE_PATH"
    trap - EXIT
fi

PACKAGE_PATH="$(cd "$(dirname "$PACKAGE_PATH")" && pwd)/$(basename "$PACKAGE_PATH")"
echo "Verified GStreamer ${GSTREAMER_VERSION} SHA-256." >&2
printf '%s\n' "$PACKAGE_PATH"
