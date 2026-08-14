from __future__ import annotations

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

    package = addin / "bin" / "x64" / "Release" / "ArcGISCodexBridgeAddin.esriAddinX"
    if package.exists():
        with zipfile.ZipFile(package) as archive:
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
