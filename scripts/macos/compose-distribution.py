#!/usr/bin/env python3
"""Add qmtui to the unmodified GStreamer runtime component packages.

The upstream Distribution retains its component IDs, dependencies, choices and
license resources. No installation script downloads or installs another package.
"""

import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


QMTUI_PACKAGE_ID = "io.github.bootids.qmtui"
QMTUI_PACKAGE_NAME = "qmtui-component.pkg"
REQUIRED_RUNTIME_IDS = (
    "org.freedesktop.gstreamer.darwin.GStreamer",
    "org.freedesktop.gstreamer.darwin.base-system-1.0",
    "org.freedesktop.gstreamer.darwin.base-crypto",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-core",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-system",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-playback",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-codecs",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-net",
    "org.freedesktop.gstreamer.darwin.gstreamer-1.0-libav",
)


def compose(source: Path, packages: Path, output: Path, version: str) -> None:
    tree = ET.parse(source)
    root = tree.getroot()
    if root.tag != "installer-gui-script":
        raise ValueError("Unexpected GStreamer Distribution format")

    # The pinned runtime must be completely local: productbuild must never leave
    # a remote package URL in what is advertised as an offline installer.
    package_ids = set()
    for ref in root.findall("pkg-ref"):
        name = (ref.text or "").strip()
        if not name:
            continue
        if not name.startswith("#") or Path(name[1:]).name != name[1:]:
            raise ValueError(f"Unexpected non-local component reference: {name}")
        if not (packages / name[1:]).is_file():
            raise FileNotFoundError(f"Missing runtime component: {name[1:]}")
        package_ids.add(ref.attrib["id"])

    missing = set(REQUIRED_RUNTIME_IDS) - package_ids
    if missing:
        raise ValueError(f"Missing required runtime package IDs: {sorted(missing)}")
    if not (packages / QMTUI_PACKAGE_NAME).is_file():
        raise FileNotFoundError(f"Missing {QMTUI_PACKAGE_NAME}")

    options = root.find("options")
    if options is None:
        options = ET.SubElement(root, "options")
    options.set("hostArchitectures", "arm64")
    options.set("customize", "allow")
    options.set("rootVolumeOnly", "true")

    domains = root.find("domains")
    if domains is None:
        domains = ET.SubElement(root, "domains")
    domains.set("enable_anywhere", "false")
    domains.set("enable_currentUserHome", "false")
    domains.set("enable_localSystem", "true")

    title = root.find("title")
    if title is None:
        title = ET.SubElement(root, "title")
    title.text = f"qmtui {version} for Apple Silicon"

    # .NET 10's currently supported macOS floor. This also matches the native
    # macos-15 CI build host; M5 Macs run a newer macOS version.
    volume_check = root.find("volume-check")
    if volume_check is None:
        volume_check = ET.SubElement(root, "volume-check")
    allowed = volume_check.find("allowed-os-versions")
    if allowed is None:
        allowed = ET.SubElement(volume_check, "allowed-os-versions")
    for child in list(allowed):
        allowed.remove(child)
    ET.SubElement(allowed, "os-version", {"min": "15.0"})

    for name, filename in (
        ("welcome", "qmtui-welcome.txt"),
        ("conclusion", "qmtui-conclusion.txt"),
    ):
        element = root.find(name)
        if element is None:
            element = ET.SubElement(root, name)
        element.set("file", filename)
        element.set("uti", "public.plain-text")

    outline = root.find("choices-outline")
    if outline is None:
        raise ValueError("GStreamer Distribution has no choices-outline")
    outline.insert(0, ET.Element("line", {"choice": QMTUI_PACKAGE_ID}))
    choice = ET.SubElement(
        root,
        "choice",
        {
            "id": QMTUI_PACKAGE_ID,
            "title": "qmtui (Apple Silicon)",
            "description": "Terminal QQ Music player and required audio runtime",
            "start_selected": "true",
            "start_enabled": "false",
            "selected": "true",
        },
    )
    ET.SubElement(choice, "pkg-ref", {"id": QMTUI_PACKAGE_ID})
    # Keep playback dependencies installed even if users customize unrelated
    # optional upstream components. The original upstream choices are retained.
    for package_id in REQUIRED_RUNTIME_IDS:
        ET.SubElement(choice, "pkg-ref", {"id": package_id})
    ref = ET.SubElement(root, "pkg-ref", {"id": QMTUI_PACKAGE_ID, "version": version})
    ref.text = f"#{QMTUI_PACKAGE_NAME}"

    ET.indent(tree, space="    ")
    tree.write(output, encoding="utf-8", xml_declaration=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("packages", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    compose(args.source, args.packages, args.output, args.version)
