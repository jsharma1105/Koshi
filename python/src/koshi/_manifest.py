"""
DEFAULT (development) manifest. Overwritten at release time by
scripts/inject-manifest.py before the wheel is built. When running from
a source checkout, EXPECTED_BINARIES is empty and hash verification is
skipped — meaning auto-download is *not* safe from a dev install.

End users always get the release-time version of this file because the
PyPI wheel is built with the injected manifest baked in.
"""

from __future__ import annotations

VERSION: str = "0.4.1"

RELEASE_URL_TEMPLATE: str = (
    "https://github.com/jsharma1105/Koshi/releases/download/v{version}/{filename}"
)

# When empty, binary.py refuses to auto-download (would be unverifiable).
# Users running from a source checkout must set KOSHI_BIN, or install
# `koshi-mcp` via `dotnet tool install --global Koshi.Mcp` so PATH lookup
# can find it.
EXPECTED_BINARIES: dict[str, dict[str, str]] = {}
