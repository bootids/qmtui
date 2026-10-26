#!/usr/bin/env bash
# Repair one directory mode in the pinned upstream framework wrapper package.
# GStreamer 1.28.8 ships its framework root as root:wheel 0700, which prevents
# ordinary users from loading any runtime libraries after a system installation.
# All file bytes, symlinks, component metadata and other directory modes stay intact.
set -euo pipefail

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 expanded-framework-component working-directory" >&2
    exit 2
fi
COMPONENT_DIR="$1"
WORK_DIR="$2"
mkdir -p "$WORK_DIR"

python3 - "$COMPONENT_DIR/PackageInfo" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
assert root.attrib["identifier"] == "org.freedesktop.gstreamer.darwin.GStreamer"
assert root.attrib["version"] == "1.28.8"
assert root.attrib["install-location"].rstrip("/") == "/Library/Frameworks/GStreamer.framework"
assert root.find("scripts") is None, "Unexpected framework scripts: review the new runtime before rebuilding"
PY

pkgutil --flatten "$COMPONENT_DIR" "$WORK_DIR/original.pkg"
pkgutil --expand-full "$WORK_DIR/original.pkg" "$WORK_DIR/original"
PAYLOAD_DIR="$WORK_DIR/original/Payload"
[ -d "$PAYLOAD_DIR" ] && [ ! -L "$PAYLOAD_DIR" ] || {
    echo "Expected an expanded framework Payload directory." >&2
    exit 1
}

# Change only the framework directory itself. Do not recursively chmod files or
# touch /Library on the build host. The framework's published libraries are public
# executable runtime assets; 0755 matches every other runtime directory upstream.
chmod 0755 "$PAYLOAD_DIR"
pkgbuild --root "$PAYLOAD_DIR" \
    --identifier org.freedesktop.gstreamer.darwin.GStreamer \
    --version 1.28.8 --install-location /Library/Frameworks/GStreamer.framework/ \
    --ownership recommended "$WORK_DIR/corrected.pkg"

# Check the rebuilt tiny wrapper before replacing its Payload/Bom. In particular,
# its signed GStreamer framework executable must remain byte-for-byte unchanged.
pkgutil --expand-full "$WORK_DIR/corrected.pkg" "$WORK_DIR/verify"
python3 - "$PAYLOAD_DIR" "$WORK_DIR/verify/Payload" <<'PY'
import hashlib
import os
from pathlib import Path
import stat
import sys


def manifest(root):
    result = {}
    for path in [root, *root.rglob("*")]:
        mode = path.lstat().st_mode
        key = path.relative_to(root).as_posix()
        if stat.S_ISLNK(mode):
            contents = os.readlink(path)
        elif stat.S_ISREG(mode):
            contents = hashlib.sha256(path.read_bytes()).hexdigest()
        elif stat.S_ISDIR(mode):
            contents = None
        else:
            raise ValueError(f"Unexpected framework payload file type: {path}")
        result[key] = (stat.S_IFMT(mode), stat.S_IMODE(mode), contents)
    return result


source = Path(sys.argv[1])
rebuilt = Path(sys.argv[2])
assert stat.S_IMODE(rebuilt.stat().st_mode) == 0o755, "Rebuilt framework root is not 0755"
before, after = manifest(source), manifest(rebuilt)
if before != after:
    changed = sorted(key for key in before.keys() | after.keys() if before.get(key) != after.get(key))
    raise ValueError(f"Framework repack changed more than its root directory mode: {changed}")
print("Verified framework wrapper: root mode 0755; all file bytes and symlinks preserved.")
PY

# Preserve the original PackageInfo (including bundle receipts/install location)
# and empty Scripts archive. Only Payload and its matching Bill of Materials are
# regenerated to record the corrected root directory permission.
pkgutil --expand "$WORK_DIR/corrected.pkg" "$WORK_DIR/corrected"
cp "$WORK_DIR/corrected/Payload" "$COMPONENT_DIR/Payload"
cp "$WORK_DIR/corrected/Bom" "$COMPONENT_DIR/Bom"
