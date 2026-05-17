"""
Inject AOT binary hashes from manifest.json into the Python wheel.

Run during the `pypi` job of release.yml just before `python -m build`
so the resulting wheel contains a static `_manifest.py` listing the
exact SHA-256 hash of every AOT binary released alongside it. The
auto-downloader verifies every fetched binary against these hashes,
which binds the wheel to the binaries by content — an attacker who
swaps a binary on the GitHub release page is then no longer enough
to compromise users.

This script has no external dependencies and runs on stock Python 3.10+.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


_TEMPLATE = '''\
"""
AUTO-GENERATED at release time by scripts/inject-manifest.py.
DO NOT EDIT BY HAND. Re-run from the release workflow if it needs to change.

Binds this Python wheel to the exact AOT binaries published in the
matching GitHub release. The binary auto-downloader verifies every
fetched binary against EXPECTED_BINARIES; a tampered binary on the
GitHub release alone is not enough to compromise users.
"""

from __future__ import annotations

VERSION: str = "{version}"

RELEASE_URL_TEMPLATE: str = "{template}"

EXPECTED_BINARIES: dict[str, dict[str, str]] = {{
{rows}\
}}
'''


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--version",
        required=False,
        help="Override version from manifest (otherwise uses manifest.version)",
    )
    ap.add_argument("--manifest", required=True, type=Path, help="Path to manifest.json")
    ap.add_argument(
        "--out",
        required=True,
        type=Path,
        help="Output path for _manifest.py inside the wheel source tree",
    )
    args = ap.parse_args()

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    version: str = args.version or manifest["version"]
    binaries: dict[str, dict[str, str]] = manifest["binaries"]
    template: str = manifest.get(
        "release_url_template",
        "https://github.com/jsharma1105/Koshi/releases/download/v{version}/{filename}",
    )

    rows = []
    for rid in sorted(binaries):
        info = binaries[rid]
        rows.append(f'    "{rid}": {{\n')
        rows.append(f'        "filename": "{info["filename"]}",\n')
        rows.append(f'        "sha256":   "{info["sha256"]}",\n')
        rows.append("    },\n")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        _TEMPLATE.format(version=version, template=template, rows="".join(rows)),
        encoding="utf-8",
    )
    print(
        f"Wrote {args.out} for version {version} with {len(binaries)} binaries.",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
