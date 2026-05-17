"""
Resolve and (if needed) download the koshi-mcp AOT binary.

Resolution order:
  1. ``KOSHI_BIN`` env var (explicit override; version is verified after spawn).
  2. Versioned cache: ``<cache>/koshi/bin/<VERSION>/koshi-mcp-<rid>[.exe]``.
     The path itself is version-pinned, so a hit cannot be stale.
  3. ``shutil.which("koshi-mcp")`` (e.g. installed via ``dotnet tool install``);
     version is verified after spawn.
  4. Download from the matching GitHub release with strict SHA-256
     verification against the hashes embedded in ``_manifest.py``.

Atomicity: downloads write to a temp file, are hash-verified, then renamed
with ``os.replace`` (atomic on every supported OS).

Concurrency: a per-version file lock protects the cache so two processes
constructing ``Client()`` at the same time cannot both fetch the binary.
"""

from __future__ import annotations

import contextlib
import hashlib
import os
import platform
import shutil
import sys
import time
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from ._manifest import EXPECTED_BINARIES, RELEASE_URL_TEMPLATE, VERSION
from .errors import (
    BinaryCorruptedError,
    BinaryNotFoundError,
    UnsupportedPlatformError,
)

_RID_MAP: dict[tuple[str, str], str] = {
    ("Linux", "x86_64"): "linux-x64",
    ("Linux", "aarch64"): "linux-arm64",
    ("Linux", "arm64"): "linux-arm64",
    ("Darwin", "x86_64"): "osx-x64",
    ("Darwin", "arm64"): "osx-arm64",
    ("Windows", "AMD64"): "win-x64",
    ("Windows", "x86_64"): "win-x64",
    ("Windows", "ARM64"): "win-arm64",
}

_DOWNLOAD_TIMEOUT_S = 60
_DOWNLOAD_CHUNK = 1 << 16  # 64 KiB


def detect_rid() -> str:
    """Return the .NET RID for the current OS/CPU, raising on unsupported combos."""
    system = platform.system()
    machine = platform.machine()
    rid = _RID_MAP.get((system, machine))
    if rid is None:
        raise UnsupportedPlatformError(
            f"No published Koshi AOT artifact for {system}/{machine}. "
            f"Install .NET 10 and `dotnet tool install --global Koshi.Mcp` instead, "
            f"then set KOSHI_BIN to the resulting koshi-mcp executable."
        )
    return rid


def _cache_root() -> Path:
    """Return the OS-appropriate cache root (stdlib-only — no platformdirs)."""
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    elif sys.platform == "darwin":
        base = str(Path.home() / "Library" / "Caches")
    else:
        base = os.environ.get("XDG_CACHE_HOME") or str(Path.home() / ".cache")
    return Path(base) / "koshi"


def versioned_cache_dir() -> Path:
    return _cache_root() / "bin" / VERSION


def _binary_filename(rid: str) -> str:
    info = EXPECTED_BINARIES.get(rid)
    if info is not None:
        return info["filename"]
    return f"koshi-mcp-{rid}{'.exe' if rid.startswith('win-') else ''}"


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(_DOWNLOAD_CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


@contextmanager
def _file_lock(lock_path: Path) -> Iterator[None]:
    """Best-effort cross-platform exclusive lock on ``lock_path``."""
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(lock_path), os.O_CREAT | os.O_RDWR, 0o600)
    try:
        if sys.platform == "win32":
            import msvcrt

            for _ in range(600):  # ≤ 60 s
                try:
                    msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(0.1)
            else:
                raise BinaryNotFoundError(
                    f"Timed out acquiring cache lock {lock_path}"
                )
        else:
            import fcntl

            fcntl.flock(fd, fcntl.LOCK_EX)
        yield
    finally:
        try:
            if sys.platform == "win32":
                import msvcrt

                with contextlib.suppress(OSError):
                    msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
            else:
                import fcntl

                fcntl.flock(fd, fcntl.LOCK_UN)
        finally:
            os.close(fd)


def _atomic_download(url: str, dest: Path, expected_sha256: str | None) -> None:
    """Download ``url`` to ``dest`` atomically. Raise on hash mismatch."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.parent / f".{dest.name}.{os.getpid()}.{int(time.time_ns())}.tmp"

    headers = {"User-Agent": f"koshi-py/{VERSION}", "Accept": "application/octet-stream"}
    request = Request(url, headers=headers)
    try:
        with urlopen(request, timeout=_DOWNLOAD_TIMEOUT_S) as resp:
            if resp.status != 200:
                raise BinaryNotFoundError(
                    f"Failed to download {url}: HTTP {resp.status}"
                )
            with tmp.open("wb") as f:
                while True:
                    chunk = resp.read(_DOWNLOAD_CHUNK)
                    if not chunk:
                        break
                    f.write(chunk)
    except HTTPError as e:
        tmp.unlink(missing_ok=True)
        raise BinaryNotFoundError(f"HTTP {e.code} fetching {url}: {e.reason}") from e
    except URLError as e:
        tmp.unlink(missing_ok=True)
        raise BinaryNotFoundError(f"Network error fetching {url}: {e.reason}") from e

    try:
        if expected_sha256 is not None:
            actual = _sha256_file(tmp)
            if actual.lower() != expected_sha256.lower():
                raise BinaryCorruptedError(
                    f"Downloaded binary hash mismatch for {dest.name}: "
                    f"expected {expected_sha256}, got {actual}"
                )

        if os.name != "nt":
            os.chmod(tmp, 0o755)

        os.replace(tmp, dest)
    finally:
        # If replace() above succeeded, tmp no longer exists; this is a safety net.
        with contextlib.suppress(FileNotFoundError):
            tmp.unlink()


def _download_to_cache(rid: str) -> Path:
    """Download the matching AOT binary into the versioned cache."""
    expected = EXPECTED_BINARIES.get(rid)
    if expected is None:
        raise BinaryNotFoundError(
            f"This dev build of koshi has no manifest entry for {rid}. "
            f"Either install koshi-mcp via `dotnet tool install --global Koshi.Mcp`, "
            f"or set KOSHI_BIN to a built koshi-mcp binary, or install koshi from PyPI."
        )

    filename = expected["filename"]
    sha256 = expected["sha256"]
    url = RELEASE_URL_TEMPLATE.format(version=VERSION, filename=filename)

    cache_dir = versioned_cache_dir()
    dest = cache_dir / filename
    lock = cache_dir / ".lock"

    with _file_lock(lock):
        # Another process may have completed the download while we waited.
        if dest.exists() and _sha256_file(dest) == sha256:
            return dest
        _atomic_download(url, dest, expected_sha256=sha256)
    return dest


def resolve_binary() -> Path:
    """
    Resolve the koshi-mcp binary by walking the resolution chain.

    Version-matching is enforced at spawn time by :class:`koshi.Client` via
    the ``serverInfo.version`` returned during MCP initialize, not here.
    This function's job is only to return *some* path that exists.
    """
    rid = detect_rid()

    env_bin = os.environ.get("KOSHI_BIN")
    if env_bin:
        p = Path(env_bin)
        if not p.exists():
            raise BinaryNotFoundError(
                f"KOSHI_BIN is set to '{env_bin}' but that path does not exist."
            )
        return p

    cached = versioned_cache_dir() / _binary_filename(rid)
    if cached.exists():
        info = EXPECTED_BINARIES.get(rid)
        if info is not None:
            if _sha256_file(cached) == info["sha256"]:
                return cached
            with contextlib.suppress(OSError):
                cached.unlink()
        else:
            return cached

    on_path = shutil.which("koshi-mcp") or shutil.which("koshi-mcp.exe")
    if on_path:
        return Path(on_path)

    return _download_to_cache(rid)
