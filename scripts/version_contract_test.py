from __future__ import annotations

import argparse
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import VERSION


def require(pattern: str, text: str, source: str) -> str:
    match = re.search(pattern, text)
    if not match:
        raise RuntimeError(f"Could not parse version from {source}")
    return match.group(1)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--package",
        type=Path,
        help="Optional built .esriAddinX to validate in addition to source versions.",
    )
    args = parser.parse_args()
    addin = ROOT / "addin" / "ArcGISCodexBridgeAddin"
    bridge_version = require(
        r'ProtocolVersion\s*=\s*"([^"]+)"',
        (addin / "BridgeSupport.cs").read_text(encoding="utf-8"),
        "BridgeSupport.cs",
    )
    daml_version = require(
        r'<AddInInfo[^>]+version="([^"]+)"',
        (addin / "Config.daml").read_text(encoding="utf-8"),
        "Config.daml",
    )
    project_version = require(
        r"<Version>([^<]+)</Version>",
        (addin / "ArcGISCodexBridgeAddin.csproj").read_text(encoding="utf-8"),
        "ArcGISCodexBridgeAddin.csproj",
    )
    versions = {
        "mcp": VERSION,
        "bridge_protocol": bridge_version,
        "daml": daml_version,
        "project": project_version,
    }
    if len(set(versions.values())) != 1:
        raise RuntimeError(f"Version mismatch: {versions}")

    package = args.package
    if package is not None:
        package = package.resolve()
        if not package.is_file():
            raise FileNotFoundError(package)
        with zipfile.ZipFile(package) as archive:
            entries = set(archive.namelist())
            required_entries = {
                "Config.daml",
                "Install/ArcGISCodexBridgeAddin.dll",
            }
            missing_entries = sorted(required_entries - entries)
            if missing_entries:
                raise RuntimeError(
                    f"Built package is missing required entries: {missing_entries}"
                )
            debug_entries = sorted(
                entry for entry in entries if entry.casefold().endswith(".pdb")
            )
            if debug_entries:
                raise RuntimeError(
                    f"Release package contains debug symbols: {debug_entries}"
                )
            package_daml = archive.read("Config.daml").decode("utf-8")
        package_version = require(
            r'<AddInInfo[^>]+version="([^"]+)"',
            package_daml,
            str(package),
        )
        if package_version != VERSION:
            raise RuntimeError(
                f"Built package version {package_version} does not match source {VERSION}"
            )
        versions["package"] = package_version

    print(f"version contract ok: {versions}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
